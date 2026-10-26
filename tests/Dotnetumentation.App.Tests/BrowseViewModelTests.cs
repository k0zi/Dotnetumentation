using Dotnetumentation.App.ViewModels;
using Dotnetumentation.Core.Graph;

namespace Dotnetumentation.App.Tests;

public sealed class BrowseViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dotnetumentation-browse-" + Guid.NewGuid().ToString("N"));
    private string Docs => Path.Combine(_root, "Project", "docs.net");
    private const string ApiFile = "api/Writer-0123456789ABCDEF.md";

    public BrowseViewModelTests()
    {
        Directory.CreateDirectory(Path.Combine(Docs, "api"));
        File.WriteAllText(Path.Combine(Docs, "README.md"), "# Project\n\n[API](" + ApiFile + ")");
        File.WriteAllText(Path.Combine(Docs, "dependencies.md"), "# Dependencies\n\nProject references.");
        File.WriteAllText(Path.Combine(Docs, "documentation-coverage.md"), "# Documentation summaries\n\nMissing summaries.");
        File.WriteAllText(Path.Combine(Docs, ApiFile), "# Demo.Writer&lt;T&gt;\n\n[Overview](../README.md)");
        File.WriteAllText(Path.Combine(Docs, "component.puml"), "@startuml\n@enduml");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Opening_solution_or_documentation_folder_lists_all_pages_and_selects_overview(bool direct)
    {
        var vm = new BrowseViewModel();
        vm.Load(direct ? Docs + Path.DirectorySeparatorChar : _root);

        var project = Assert.Single(vm.Nodes);
        Assert.Equal("Overview", project.Children[0].Name);
        Assert.Equal(Path.Combine(Docs, "README.md"), vm.SelectedNode?.FullPath);
        Assert.True(vm.ShowsMarkdown);
        Assert.False(vm.ShowsDiagram);
        Assert.Contains(project.Descendants(), n => n.Name == "Demo.Writer<T>");
        Assert.Contains(project.Children, n => n.Name == "Dependencies");
        Assert.Contains(project.Children, n => n.Name == "Documentation summaries");
    }

    [Fact]
    public void Relative_links_open_api_pages_and_return_to_overview()
    {
        var vm = new BrowseViewModel();
        vm.Load(Docs);
        Assert.True(vm.OpenLink(new Uri(ApiFile, UriKind.Relative)));
        Assert.Contains("Demo.Writer", vm.Markdown);
        Assert.Equal("Demo.Writer<T>", vm.SelectedNode?.Name);
        Assert.Equal(new Uri(Path.Combine(Docs, ApiFile)), vm.MarkdownBaseUri);
        Assert.True(vm.OpenLink(new Uri("../README.md", UriKind.Relative)));
        Assert.Equal("Overview", vm.SelectedNode?.Name);
    }

    [Fact]
    public void Documents_and_diagrams_switch_renderers_without_stale_content()
    {
        var vm = new BrowseViewModel();
        vm.Load(Docs);
        Assert.True(vm.OpenLink(new Uri(Path.Combine(Docs, "component.puml"))));
        Assert.True(vm.ShowsDiagram);
        Assert.Null(vm.Markdown);
        vm.Open(Path.Combine(Docs, "dependencies.md"));
        Assert.True(vm.ShowsMarkdown);
        Assert.Null(vm.DiagramSource);
        Assert.Contains("Project references", vm.Markdown);
    }

    [Fact]
    public void Filter_matches_page_titles_and_refresh_keeps_the_open_page()
    {
        var vm = new BrowseViewModel();
        vm.Load(Docs);
        vm.Filter = "Writer<T>";
        var page = Assert.Single(vm.Nodes.SelectMany(n => n.Descendants()), n => n.IsFile);
        vm.SelectedNode = page;
        vm.RefreshCommand.Execute(null);
        Assert.Equal(page.FullPath, vm.SelectedNode?.FullPath);
        Assert.Contains("Demo.Writer", vm.Markdown);
    }

    [Fact]
    public void Missing_links_report_error_without_replacing_current_content_and_external_links_are_not_intercepted()
    {
        var vm = new BrowseViewModel();
        vm.Load(Docs);
        var previous = vm.Markdown;
        Assert.True(vm.OpenLink(new Uri("missing.md", UriKind.Relative)));
        Assert.Contains("File not found", vm.Status);
        Assert.Equal(previous, vm.Markdown);
        Assert.False(vm.OpenLink(new Uri("https://example.com/docs")));
        Assert.False(vm.OpenLink(new Uri("other.txt", UriKind.Relative)));
    }

    private const string Writer = "T:Demo.Writer`1";
    private const string Write = "M:Demo.Writer`1.Write(`0)";

    private void WriteGraph() => GraphDatabase.Write(Path.Combine(Docs, GraphDatabase.FileName), new CodeGraph(
        new Dictionary<string, string> { ["scope"] = "project" },
        [
            new GraphNode { Id = Writer, Kind = NodeKinds.Class, Name = "Writer<T>", FullName = "Demo.Writer<T>", Project = "Project",
                Summary = "Writes values.", File = "../Writer.cs", Line = 3 },
            new GraphNode { Id = Write, Kind = NodeKinds.Method, Name = "Write", FullName = "Demo.Writer<T>.Write(T)", Project = "Project",
                Signature = "Write(value : T) : void" },
            new GraphNode { Id = "M:System.Console.WriteLine(System.String)", Kind = NodeKinds.Method, Name = "WriteLine",
                FullName = "System.Console.WriteLine(string)", IsExternal = true, IsStub = true },
        ],
        [
            new GraphEdge(Writer, Write, EdgeKinds.Contains),
            new GraphEdge(Write, "M:System.Console.WriteLine(System.String)", EdgeKinds.Calls),
        ],
        [new GraphDocument(Writer, DocumentKinds.Api, ApiFile)]));

    [Fact]
    public void Symbols_are_searched_in_the_graph_and_their_pages_link_relations_and_documents()
    {
        var vm = new BrowseViewModel();
        vm.Load(_root);
        Assert.False(vm.HasGraph);

        WriteGraph();
        vm.RefreshCommand.Execute(null);
        Assert.True(vm.HasGraph);
        vm.SymbolQuery = "writ";
        Assert.Equal([Write, "M:System.Console.WriteLine(System.String)", Writer], vm.Symbols.Select(s => s.Id).Order(StringComparer.Ordinal));

        vm.SelectedSymbol = vm.Symbols.Single(s => s.Id == Writer);
        Assert.True(vm.ShowsMarkdown);
        Assert.Contains("# Demo.Writer&lt;T&gt;", vm.Markdown);
        Assert.Contains("Writes values.", vm.Markdown);
        Assert.Contains("## Contains", vm.Markdown);
        var methodLink = "symbol:" + Uri.EscapeDataString(Write);
        Assert.Contains($"]({methodLink})", vm.Markdown);

        // A relation link opens the other symbol, a document link the reference page.
        Assert.True(vm.OpenLink(new Uri(methodLink)));
        Assert.Contains("## Calls", vm.Markdown);
        Assert.Contains("external", vm.Markdown);
        Assert.Equal(Write, vm.SelectedSymbol?.Id);
        vm.ShowSymbol(Writer);
        Assert.True(vm.OpenLink(new Uri(Path.Combine(Docs, ApiFile))));
        Assert.Equal("Demo.Writer<T>", vm.SelectedNode?.Name);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
