using Dotnetumentation.Core.Graph;

namespace Dotnetumentation.Core.Tests;

public class GraphTests(SampleSolutionFixture sample) : IClassFixture<SampleSolutionFixture>
{
    private const string OrderService = "T:Sample.App.OrderService";
    private const string Place = "M:Sample.App.OrderService.Place(Sample.Domain.Order)";

    private string AppGraph => Path.Combine(sample.Directory, "Sample.App", "docs.net", GraphDatabase.FileName);
    private string DomainGraph => Path.Combine(sample.Directory, "Sample.Domain", "docs.net", GraphDatabase.FileName);
    private string SolutionGraph => Path.Combine(sample.Directory, "Sample" + GraphDatabase.SolutionFileSuffix);

    private Task<GenerationResult> Run(bool graph = true) =>
        new DocumentationGenerator().GenerateAsync(new GeneratorOptions { InputPath = sample.SolutionPath, Graph = graph });

    [Fact]
    public async Task Every_project_and_the_solution_get_a_graph_database_and_its_description()
    {
        var result = await Run();

        Assert.Equal(SolutionGraph, result.SolutionGraph);
        Assert.True(File.Exists(AppGraph));
        Assert.True(File.Exists(DomainGraph));
        Assert.Contains(result.Projects, p => p.Files.Contains(AppGraph));
        var docs = Path.Combine(sample.Directory, "Sample.App", "docs.net");
        Assert.Contains("(graph.md)", await File.ReadAllTextAsync(Path.Combine(docs, "README.md")));
        var description = await File.ReadAllTextAsync(Path.Combine(docs, "graph.md"));
        Assert.Contains("CREATE TABLE nodes", description);
        Assert.Contains("| calls |", description);
    }

    [Fact]
    public async Task Types_and_members_are_nodes_with_source_and_documents()
    {
        await Run();
        using var graph = GraphDatabase.Open(AppGraph);

        var type = graph.Node(OrderService)!;
        Assert.Equal(NodeKinds.Class, type.Kind);
        Assert.Equal("Sample.App", type.Project);
        Assert.False(type.IsStub);
        Assert.Contains("Places orders", type.Summary);
        Assert.Equal(Path.Combine(sample.Directory, "Sample.App", "OrderService.cs"), type.File);
        Assert.True(type.Line > 1);

        var method = graph.Node(Place)!;
        Assert.Equal(NodeKinds.Method, method.Kind);
        Assert.Equal("Sample.App.OrderService.Place(Order)", method.FullName);
        var documents = graph.Documents(Place);
        Assert.Equal([DocumentKinds.Activity, DocumentKinds.Api, DocumentKinds.Sequence], documents.Select(d => d.Kind));
        Assert.All(documents, d => Assert.True(File.Exists(d.Path), d.Path));
        Assert.Contains(graph.Relations(OrderService), r => r.Edge.Kind == EdgeKinds.Contains && r.Other.Id == Place);
        Assert.Contains(graph.Relations(OrderService), r => r.Edge.Kind == EdgeKinds.DeclaredIn && r.Other.Kind == NodeKinds.File);
    }

    [Fact]
    public async Task Calls_creations_and_implementations_are_edges()
    {
        await Run();
        using var graph = GraphDatabase.Open(AppGraph);

        var place = graph.Relations(Place).Where(r => r.IsOutgoing).ToList();
        Assert.Contains(place, r => r.Edge.Kind == EdgeKinds.Calls && r.Other.Id == "M:Sample.Domain.IRepository`1.Add(`0)" && r.Other is { IsStub: true, IsExternal: false });
        Assert.Contains(place, r => r.Edge.Kind == EdgeKinds.Calls && r.Other.Id == "M:Sample.App.OrderService.Validate(Sample.Domain.OrderLine)");
        Assert.Contains(place, r => r.Edge.Kind == EdgeKinds.Creates && r.Other.Id == "M:System.ArgumentException.#ctor(System.String)" && r.Other.IsExternal);
        // Who uses a library: the incoming edges of the external node.
        Assert.Contains(graph.Relations("M:System.ArgumentException.#ctor(System.String)"),
            r => !r.IsOutgoing && r.Edge.Kind == EdgeKinds.Creates && r.Other.Id == Place);
        // Private members are below the visibility filter of the documentation, but in the graph, with their calls.
        var log = graph.Node("M:Sample.App.OrderService.Log(System.String)")!;
        Assert.Equal("private", log.Visibility);
        Assert.False(log.IsStub);
        Assert.Empty(graph.Documents(log.Id));
        Assert.Contains(graph.Relations("M:System.Console.WriteLine(System.String)"),
            r => !r.IsOutgoing && r.Edge.Kind == EdgeKinds.Calls && r.Other.Id == log.Id);
        // The primary constructor is declared by the whole class, but the calls of the methods are not its own.
        Assert.DoesNotContain(graph.Relations("M:Sample.App.OrderService.#ctor(Sample.Domain.IRepository{Sample.Domain.Order})"),
            r => r.IsOutgoing && r.Edge.Kind is EdgeKinds.Calls or EdgeKinds.Creates);

        Assert.Contains(graph.Relations("T:Sample.App.InMemoryRepository`1"),
            r => r.IsOutgoing && r.Edge.Kind == EdgeKinds.Implements && r.Other.Id == "T:Sample.Domain.IRepository`1");
        Assert.Contains(graph.Relations("M:Sample.App.InMemoryRepository`1.Add(`0)"),
            r => r.IsOutgoing && r.Edge.Kind == EdgeKinds.ImplementsMember && r.Other.Id == "M:Sample.Domain.IRepository`1.Add(`0)");
        Assert.Contains(graph.Relations(OrderService),
            r => r.IsOutgoing && r.Edge is { Kind: EdgeKinds.Association, Label: "repository" } && r.Other.Id == "T:Sample.Domain.IRepository`1");
        Assert.Contains(graph.Relations("project:Sample.App"),
            r => r.IsOutgoing && r.Edge.Kind == EdgeKinds.ProjectReference && r.Other.Id == "project:Sample.Domain");
    }

    [Fact]
    public async Task Search_finds_names_by_their_words()
    {
        await Run();
        using var index = GraphIndex.Open(GraphIndex.Locate(sample.SolutionPath));

        Assert.Contains(index.Search("service"), n => n.Id == OrderService);
        Assert.Equal("T:Sample.Domain.Order", index.Search("order", NodeKinds.Types.ToList())[0].Id);
        Assert.Contains(index.Search("places orders"), n => n.Id == OrderService);
        Assert.Empty(index.Search("  "));
    }

    [Fact]
    public async Task The_solution_graph_holds_every_project_with_its_own_rows()
    {
        await Run();
        Assert.Equal([SolutionGraph], GraphIndex.Locate(sample.SolutionPath));
        Assert.Equal([AppGraph], GraphIndex.Locate(Path.Combine(sample.Directory, "Sample.App")));
        using var graph = GraphDatabase.Open(SolutionGraph);

        var order = graph.Node("T:Sample.Domain.Order")!;
        Assert.False(order.IsStub);
        Assert.Equal(Path.Combine(sample.Directory, "Sample.Domain", "Model.cs"), order.File);
        Assert.All(graph.Documents(order.Id), d => Assert.True(File.Exists(d.Path), d.Path));
        Assert.False(graph.Node("M:Sample.Domain.IRepository`1.Add(`0)")!.IsStub);
        Assert.NotNull(graph.Node(Place));
        Assert.Equal("solution", graph.Meta()["scope"]);
    }

    [Fact]
    public async Task The_same_code_gives_the_same_bytes()
    {
        await Run();
        var app = await File.ReadAllBytesAsync(AppGraph);
        var solution = await File.ReadAllBytesAsync(SolutionGraph);

        var result = await Run();

        Assert.All(result.Projects, p => Assert.Equal(0, p.Written));
        Assert.Equal(app, await File.ReadAllBytesAsync(AppGraph));
        Assert.Equal(solution, await File.ReadAllBytesAsync(SolutionGraph));
    }

    [Fact]
    public async Task Without_the_graph_option_the_databases_are_removed()
    {
        await Run();

        var result = await Run(graph: false);

        Assert.Null(result.SolutionGraph);
        Assert.False(File.Exists(AppGraph));
        Assert.False(File.Exists(SolutionGraph));
        Assert.False(File.Exists(Path.Combine(sample.Directory, "Sample.App", "docs.net", "graph.md")));
        await Run();
    }
}
