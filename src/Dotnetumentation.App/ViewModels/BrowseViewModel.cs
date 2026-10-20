using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dotnetumentation.Core;

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

    private List<DocNode> _allNodes = [];

    [ObservableProperty] private string? _rootPath;
    [ObservableProperty] private string _outputDirectoryName = GeneratorOptions.DefaultOutputDirectoryName;
    [ObservableProperty] private string _filter = "";
    [ObservableProperty] private DocNode? _selectedNode;

    [ObservableProperty] private string? _diagramSource;
    [ObservableProperty] private string? _markdown;
    [ObservableProperty] private Uri? _markdownBaseUri;
    [ObservableProperty] private string _title = "No documentation opened";
    [ObservableProperty] private string _status = "";

    public ObservableCollection<DocNode> Nodes { get; } = [];

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

    partial void OnSelectedNodeChanged(DocNode? value)
    {
        if (value is { IsFile: true })
            Open(value.FullPath);
    }

    /// <summary>Shows the documentation of the solution or project file (or folder) at <paramref name="path"/>.</summary>
    public void Load(string path)
    {
        RootPath = File.Exists(path) ? Path.GetDirectoryName(Path.GetFullPath(path)) : Path.GetFullPath(path);
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
        Title = RootPath;
        Status = _allNodes.Count == 0
            ? $"No '{OutputDirectoryName}' folder below {RootPath}: generate the documentation first."
            : $"{_allNodes.Count} documented project(s).";

        // Keep the open file after a refresh, otherwise start with the first project's overview.
        var all = _allNodes.SelectMany(n => n.Descendants().Prepend(n)).ToList();
        SelectedNode = all.FirstOrDefault(n => n.FullPath == selected)
                       ?? all.FirstOrDefault(n => n.IsMarkdown);
        if (SelectedNode is null)
        {
            DiagramSource = null;
            Markdown = null;
        }
    }

    /// <summary>Opens a documentation file, e.g. from a link of the Markdown overview.</summary>
    public void Open(string path)
    {
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
        }
        else
        {
            Markdown = null;
            DiagramSource = text;
        }

        var node = _allNodes.SelectMany(n => n.Descendants()).FirstOrDefault(n => n.FullPath == path);
        if (node is not null && !ReferenceEquals(node, SelectedNode))
            SelectedNode = node;
    }

    private IEnumerable<DocNode> FindProjects(string root)
    {
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
            .Where(f => f.EndsWith(".puml", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .Select(f => new DocNode(Path.GetFileNameWithoutExtension(f), f, true, []))
            .ToList();
        return [.. files.Where(f => f.IsMarkdown), .. folders, .. files.Where(f => !f.IsMarkdown)];
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
            return node.Name.Contains(text, StringComparison.OrdinalIgnoreCase) ? node : null;
        var children = node.Children.Select(c => Filtered(c, text)).OfType<DocNode>().ToList();
        return children.Count == 0 ? null : new DocNode(node.Name, node.FullPath, false, children);
    }
}
