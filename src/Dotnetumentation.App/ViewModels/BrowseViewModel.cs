using System.Collections.ObjectModel;
using System.Net;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dotnetumentation.Core;
using Dotnetumentation.Core.Graph;

namespace Dotnetumentation.App.ViewModels;

/// <summary>A project, folder or documentation file in the browser tree.</summary>
public sealed class DocNode(string name, string fullPath, bool isFile, IReadOnlyList<DocNode> children)
{
    public string Name { get; } = name;
    public string FullPath { get; } = fullPath;
    public bool IsFile { get; } = isFile;
    public IReadOnlyList<DocNode> Children { get; } = children;

    public bool IsDiagram => IsFile && FullPath.EndsWith(".puml", StringComparison.OrdinalIgnoreCase);
    public bool IsMarkdown => IsFile && FullPath.EndsWith(".md", StringComparison.OrdinalIgnoreCase);

    public IEnumerable<DocNode> Descendants() => Children.SelectMany(c => c.Descendants().Prepend(c));
}

public sealed partial class BrowseViewModel : ObservableObject
{
    /// <summary>Folders that never hold project documentation; skipping them keeps the scan fast.</summary>
    private static readonly HashSet<string> SkippedFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", ".git", ".vs", ".idea", "node_modules", "packages", "artifacts", "TestResults",
    };

    private const string SymbolScheme = "symbol";

    private List<DocNode> _allNodes = [];

    /// <summary>
    /// The graph databases of the opened folder. They are opened for each query only, so a generation running at the
    /// same time can replace them.
    /// </summary>
    private IReadOnlyList<string> _graphs = [];

    [ObservableProperty] private string? _rootPath;
    [ObservableProperty] private string _outputDirectoryName = GeneratorOptions.DefaultOutputDirectoryName;
    [ObservableProperty] private string _filter = "";
    [ObservableProperty] private DocNode? _selectedNode;
    [ObservableProperty] private string _symbolQuery = "";
    [ObservableProperty] private GraphNode? _selectedSymbol;

    [ObservableProperty] private string? _diagramSource;
    [ObservableProperty] private string? _markdown;
    [ObservableProperty] private Uri? _markdownBaseUri;
    [ObservableProperty] private string _title = "No documentation opened";
    [ObservableProperty] private string _status = "";

    public ObservableCollection<DocNode> Nodes { get; } = [];

    /// <summary>The classes, functions and other nodes of the graph database matching <see cref="SymbolQuery"/>.</summary>
    public ObservableCollection<GraphNode> Symbols { get; } = [];

    public bool HasGraph => _graphs.Count > 0;

    public bool ShowsDiagram => DiagramSource is not null;
    public bool ShowsMarkdown => Markdown is not null;
    public bool ShowsNothing => DiagramSource is null && Markdown is null;

    partial void OnDiagramSourceChanged(string? value) => NotifyContent();
    partial void OnMarkdownChanged(string? value) => NotifyContent();

    private void NotifyContent()
    {
        OnPropertyChanged(nameof(ShowsDiagram));
        OnPropertyChanged(nameof(ShowsMarkdown));
        OnPropertyChanged(nameof(ShowsNothing));
    }

    partial void OnFilterChanged(string value) => ApplyFilter();

    partial void OnSymbolQueryChanged(string value) => SearchSymbols();

    partial void OnSelectedSymbolChanged(GraphNode? value)
    {
        if (value is not null)
            ShowSymbol(value.Id);
    }

    partial void OnSelectedNodeChanged(DocNode? value)
    {
        if (value is { IsFile: true })
            Open(value.FullPath);
    }

    /// <summary>Shows the documentation of the solution or project file (or folder) at <paramref name="path"/>.</summary>
    public void Load(string path)
    {
        RootPath = File.Exists(path) ? Path.GetDirectoryName(Path.GetFullPath(path)) : Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        Refresh();
    }

    [RelayCommand]
    private void Refresh()
    {
        if (RootPath is null || !Directory.Exists(RootPath))
        {
            _allNodes = [];
            ApplyFilter();
            Status = "Folder not found.";
            return;
        }

        var selected = SelectedNode?.FullPath;
        _allNodes = FindProjects(RootPath).ToList();
        ApplyFilter();
        _graphs = GraphIndex.Locate(RootPath, OutputDirectoryName);
        OnPropertyChanged(nameof(HasGraph));
        SearchSymbols();
        Title = RootPath;
        Status = _allNodes.Count == 0
            ? $"No '{OutputDirectoryName}' folder below {RootPath}: generate the documentation first."
            : $"{_allNodes.Count} documented project(s).";

        // Keep the open file after a refresh, otherwise start with the first project's overview.
        var all = _allNodes.SelectMany(n => n.Descendants().Prepend(n)).ToList();
        SelectedNode = all.FirstOrDefault(n => n.FullPath == selected)
                       ?? all.FirstOrDefault(n => n.IsMarkdown && Path.GetFileName(n.FullPath).Equals("README.md", StringComparison.OrdinalIgnoreCase))
                       ?? all.FirstOrDefault(n => n.IsMarkdown);
        if (SelectedNode is null)
        {
            DiagramSource = null;
            Markdown = null;
        }
    }

    /// <summary>Follows local documentation links, including links relative to the current Markdown page.</summary>
    public bool OpenLink(Uri link)
    {
        if (link.IsAbsoluteUri && link.Scheme == SymbolScheme)
        {
            ShowSymbol(Uri.UnescapeDataString(link.OriginalString[(SymbolScheme.Length + 1)..]));
            return true;
        }
        if (!link.IsAbsoluteUri)
        {
            if (MarkdownBaseUri is null)
                return false;
            link = new Uri(MarkdownBaseUri, link);
        }
        if (!link.IsFile || !IsDocumentationFile(link.LocalPath))
            return false;
        Open(link.LocalPath);
        return true;
    }

    /// <summary>Opens a documentation file, e.g. from a link of the Markdown overview.</summary>
    public void Open(string path)
    {
        path = Path.GetFullPath(path);
        if (!IsDocumentationFile(path))
        {
            Status = "Select a Markdown document or a PlantUML diagram.";
            return;
        }
        if (!File.Exists(path))
        {
            Status = $"File not found: {path}";
            return;
        }
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Status = e.Message;
            return;
        }

        Title = RootPath is null ? path : Path.GetRelativePath(RootPath, path);
        if (path.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            DiagramSource = null;
            MarkdownBaseUri = new Uri(path);
            Markdown = text;
            Status = "Documentation page";
        }
        else
        {
            Markdown = null;
            MarkdownBaseUri = null;
            DiagramSource = text;
            Status = "UML diagram";
        }

        var node = _allNodes.SelectMany(n => n.Descendants()).FirstOrDefault(n => n.FullPath == path);
        if (node is not null && !ReferenceEquals(node, SelectedNode))
            SelectedNode = node;
    }

    private void SearchSymbols()
    {
        Symbols.Clear();
        if (_graphs.Count == 0 || string.IsNullOrWhiteSpace(SymbolQuery))
            return;
        try
        {
            using var index = GraphIndex.Open(_graphs);
            foreach (var node in index.Search(SymbolQuery, limit: 100))
                Symbols.Add(node);
            Status = Symbols.Count == 0 ? "No class or function matches." : $"{Symbols.Count} match(es).";
        }
        catch (Exception e) when (e is IOException or Microsoft.Data.Sqlite.SqliteException)
        {
            Status = "The graph database cannot be read: " + e.Message;
        }
    }

    /// <summary>Shows a node of the graph database as a page: its declaration, relations and documentation links.</summary>
    public void ShowSymbol(string id)
    {
        GraphNode? node;
        string page;
        try
        {
            using var index = GraphIndex.Open(_graphs);
            node = index.Node(id);
            if (node is null)
            {
                Status = $"Not in the graph database: {id}";
                return;
            }
            page = SymbolPage(node, index.Relations(id), index.Documents(id));
        }
        catch (Exception e) when (e is IOException or Microsoft.Data.Sqlite.SqliteException)
        {
            Status = "The graph database cannot be read: " + e.Message;
            return;
        }

        DiagramSource = null;
        MarkdownBaseUri = null;
        Markdown = page;
        Title = node.FullName;
        Status = $"{node.Kind} in the graph database";
        if (SelectedSymbol?.Id != id)
            SelectedSymbol = Symbols.FirstOrDefault(s => s.Id == id);
    }

    private string SymbolPage(GraphNode node, IReadOnlyList<GraphRelation> relations, IReadOnlyList<GraphDocument> documents)
    {
        var md = new StringBuilder($"# {Text(node.FullName)}\n\n");
        var facts = new List<string> { node.Kind };
        if (node.Visibility is { } visibility)
            facts.Add(visibility);
        if (node.Project is { } project)
            facts.Add($"project {Code(project)}");
        if (node.IsExternal)
            facts.Add("external (.NET or package)");
        else if (node.IsStub)
            facts.Add("defined in a project whose graph is not opened");
        md.Append(string.Join(" · ", facts)).Append("\n\n");
        if ((node.Declaration ?? node.Signature) is { } declaration)
            md.Append($"{Code(declaration)}\n\n");
        if (node.Summary is { } summary)
            md.Append($"{Text(summary)}\n\n");
        if (node.File is { } file)
            md.Append($"Source: {Code(RootPath is null ? file : Path.GetRelativePath(RootPath, file).Replace('\\', '/'))}"
                      + (node.Line is { } line ? $", line {line}" : "") + "\n\n");

        if (documents.Count > 0)
        {
            md.Append("## Documentation\n\n");
            foreach (var document in documents)
                md.Append($"- [{DocumentName(document.Kind)}]({new Uri(document.Path).AbsoluteUri})\n");
            md.Append('\n');
        }

        foreach (var group in relations.Where(r => r.Edge.Kind != EdgeKinds.DeclaredIn)
                     .GroupBy(RelationNames.Of).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            md.Append($"## {char.ToUpperInvariant(group.Key[0])}{group.Key[1..]}\n\n");
            foreach (var relation in group.OrderBy(r => r.Other.FullName, StringComparer.Ordinal))
            {
                var label = relation.Edge.Label.Length > 0 ? $" ({Code(relation.Edge.Label)})" : "";
                var external = relation.Other.IsExternal ? " · external" : "";
                md.Append($"- [{Text(relation.Other.FullName)}]({SymbolScheme}:{Uri.EscapeDataString(relation.Other.Id)}){label} · {relation.Other.Kind}{external}\n");
            }
            md.Append('\n');
        }
        return md.ToString();
    }

    private static string DocumentName(string kind) => kind switch
    {
        DocumentKinds.Api => "API reference",
        DocumentKinds.Class => "Class diagram",
        DocumentKinds.Sequence => "Sequence diagram",
        DocumentKinds.Activity => "Activity diagram",
        _ => kind,
    };

    // Names and summaries are content, never Markdown instructions.
    private static string Text(string value) => WebUtility.HtmlEncode(value)
        .Replace("\\", "\\\\").Replace("`", "\\`").Replace("*", "\\*").Replace("_", "\\_")
        .Replace("[", "\\[").Replace("]", "\\]").Replace("\r", " ").Replace("\n", " ");

    private static string Code(string value)
    {
        var fence = "`";
        while (value.Contains(fence, StringComparison.Ordinal))
            fence += "`";
        return $"{fence} {value.Replace('\r', ' ').Replace('\n', ' ')} {fence}";
    }

    private IEnumerable<DocNode> FindProjects(string root)
    {
        // The folder picker can select the generated folder itself, not just its parent project.
        if (Path.GetFileName(root).Equals(OutputDirectoryName, StringComparison.OrdinalIgnoreCase))
            return [new DocNode(Path.GetFileName(Path.GetDirectoryName(root)) ?? Path.GetFileName(root), root, false, Folder(root))];

        var pending = new Stack<string>();
        pending.Push(root);
        var found = new List<DocNode>();
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            IEnumerable<string> children;
            try
            {
                children = Directory.EnumerateDirectories(dir).ToList();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (var child in children)
            {
                var name = Path.GetFileName(child);
                if (name.Equals(OutputDirectoryName, StringComparison.OrdinalIgnoreCase))
                    found.Add(new DocNode(Path.GetFileName(dir), child, false, Folder(child)));
                else if (!SkippedFolders.Contains(name) && !name.StartsWith('.'))
                    pending.Push(child);
            }
        }
        return found.OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The files of a documentation folder: the overview first, then the sub folders, then the diagrams.</summary>
    private static IReadOnlyList<DocNode> Folder(string dir)
    {
        var folders = Directory.EnumerateDirectories(dir)
            .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
            .Select(d => new DocNode(Path.GetFileName(d), d, false, Folder(d)));
        var files = Directory.EnumerateFiles(dir)
            .Where(IsDocumentationFile)
            .OrderBy(f => Path.GetFileName(f).Equals("README.md", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(f => f, StringComparer.OrdinalIgnoreCase)
            .Select(f => new DocNode(DocumentTitle(f), f, true, []))
            .ToList();
        return [.. files.Where(f => f.IsMarkdown), .. folders, .. files.Where(f => !f.IsMarkdown)];
    }

    private static bool IsDocumentationFile(string path) =>
        path.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".puml", StringComparison.OrdinalIgnoreCase);

    private static string DocumentTitle(string path)
    {
        if (path.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            if (Path.GetFileName(path).Equals("README.md", StringComparison.OrdinalIgnoreCase))
                return "Overview";
            try
            {
                // Generated API filenames include an identity hash; show the page heading instead.
                var heading = File.ReadLines(path).Take(32).FirstOrDefault(l => l.StartsWith("# ", StringComparison.Ordinal));
                if (heading is not null)
                    return WebUtility.HtmlDecode(heading[2..].Trim())
                        .Replace("\\_", "_").Replace("\\*", "*").Replace("\\[", "[").Replace("\\]", "]").Replace("\\`", "`");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // The file can disappear while refreshing; retain a usable tree label.
            }
        }
        return Path.GetFileNameWithoutExtension(path);
    }

    private void ApplyFilter()
    {
        Nodes.Clear();
        foreach (var node in string.IsNullOrWhiteSpace(Filter) ? _allNodes : _allNodes.Select(n => Filtered(n, Filter.Trim())).OfType<DocNode>())
            Nodes.Add(node);
    }

    /// <summary>The node with only the files whose name contains <paramref name="text"/>; null when nothing matches.</summary>
    private static DocNode? Filtered(DocNode node, string text)
    {
        if (node.IsFile)
            return node.Name.Contains(text, StringComparison.OrdinalIgnoreCase)
                   || Path.GetFileName(node.FullPath).Contains(text, StringComparison.OrdinalIgnoreCase) ? node : null;
        var children = node.Children.Select(c => Filtered(c, text)).OfType<DocNode>().ToList();
        return children.Count == 0 ? null : new DocNode(node.Name, node.FullPath, false, children);
    }
}
