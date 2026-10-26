using System.Diagnostics;
using Dotnetumentation.Core.Graph;
using Dotnetumentation.Core.Incremental;

namespace Dotnetumentation.Core.Tests;

/// <summary>The sample solution in a git repository: the restored sources are the first commit, the documentation is untracked.</summary>
public sealed class GitSampleSolutionFixture : IDisposable
{
    private readonly SampleSolutionFixture _sample = new();

    public string Directory => _sample.Directory;

    public string SolutionPath => _sample.SolutionPath;

    public GitSampleSolutionFixture()
    {
        File.WriteAllText(Path.Combine(Directory, ".gitignore"), "bin/\nobj/\n");
        Git("init", "-q");
        Commit("Sample");
    }

    /// <summary>Back to the first commit, without the generated documentation.</summary>
    public void Reset()
    {
        Git("reset", "-q", "--hard", Git("rev-list", "--max-parents=0", "HEAD").Trim());
        Git("clean", "-q", "-fd");
    }

    public void Commit(string message)
    {
        Git("add", "-A");
        Git("-c", "user.name=Test", "-c", "user.email=test@example.com", "commit", "-q", "-m", message);
    }

    public string Git(params string[] args)
    {
        var start = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("-C");
        start.ArgumentList.Add(Directory);
        foreach (var arg in args)
            start.ArgumentList.Add(arg);
        using var git = Process.Start(start)!;
        var error = git.StandardError.ReadToEndAsync();
        var output = git.StandardOutput.ReadToEnd();
        git.WaitForExit();
        if (git.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed:\n{error.Result}");
        return output;
    }

    public void Dispose() => _sample.Dispose();
}

public class IncrementalGenerationTests : IClassFixture<GitSampleSolutionFixture>
{
    private readonly GitSampleSolutionFixture _sample;

    public IncrementalGenerationTests(GitSampleSolutionFixture sample)
    {
        _sample = sample;
        sample.Reset();
    }

    private string App => Path.Combine(_sample.Directory, "Sample.App");
    private string Domain => Path.Combine(_sample.Directory, "Sample.Domain");
    private string AppDocs => Path.Combine(App, "docs.net");
    private string OrderService => Path.Combine(App, "OrderService.cs");

    private Task<GenerationResult> Run(bool incremental = false, string? since = null, bool clean = false, DiagramKinds diagrams = DiagramKinds.All) =>
        new DocumentationGenerator().GenerateAsync(new GeneratorOptions
        {
            InputPath = _sample.SolutionPath,
            Incremental = incremental,
            Since = since,
            Clean = clean,
            Diagrams = diagrams,
        });

    private static Dictionary<string, GenerationMode> Modes(GenerationResult result) => result.Projects.ToDictionary(p => p.Name, p => p.Mode);

    /// <summary>Every file of the documentation folders, the state files included.</summary>
    private Dictionary<string, string> Snapshot() =>
        System.IO.Directory.EnumerateFiles(_sample.Directory, "*", SearchOption.AllDirectories)
            .Where(f => f.Contains($"{Path.DirectorySeparatorChar}docs.net{Path.DirectorySeparatorChar}"))
            .ToDictionary(f => Path.GetRelativePath(_sample.Directory, f), File.ReadAllText);

    private async Task AssertSameAsFullRun()
    {
        var incremental = Snapshot();
        await Run(clean: true);
        var full = Snapshot();
        Assert.Equal(full.Keys.Order(), incremental.Keys.Order());
        foreach (var (path, content) in full)
            Assert.True(content == incremental[path], $"{path} differs from a full generation.");
    }

    private static async Task Edit(string path, string oldText, string newText)
    {
        var text = await File.ReadAllTextAsync(path);
        Assert.Contains(oldText, text);
        await File.WriteAllTextAsync(path, text.Replace(oldText, newText));
    }

    private const string CountMethod = "    public int Count() => repository.All().Count();\n\n    public decimal Place(";

    [Fact]
    public async Task Every_run_saves_the_git_state()
    {
        await Run();

        var head = _sample.Git("rev-parse", "HEAD").Trim();
        var app = GenerationState.Load(AppDocs)!;
        Assert.Equal(head, app.Commit);
        Assert.Empty(app.Dirty);
        var place = Assert.Single(app.Sequences, s => s.MethodId == "Place(Order)");
        Assert.Equal(["Sample.App/OrderService.cs", "Sample.Domain/Model.cs"], place.Sources);
        Assert.Equal(head, GenerationState.Load(Path.Combine(Domain, "docs.net"))!.Commit);
    }

    [Fact]
    public async Task Without_source_changes_every_project_is_skipped()
    {
        await Run();
        await File.WriteAllTextAsync(Path.Combine(_sample.Directory, "NOTES.md"), "not code");

        var result = await Run(incremental: true);

        Assert.All(result.Projects, p => Assert.Equal(GenerationMode.Skipped, p.Mode));
        Assert.All(result.Projects, p => Assert.Equal(0, p.Written));
        Assert.All(result.Projects, p => Assert.NotEmpty(p.Files));
    }

    [Fact]
    public async Task A_change_updates_its_project_only_and_keeps_the_diagrams_of_unchanged_files()
    {
        await Run();
        // A kept diagram is not written again: the marker survives.
        var place = Path.Combine(AppDocs, "sequence", "Sample.App.OrderService", "Place_Order.puml");
        await File.AppendAllTextAsync(place, "' marker\n");
        await File.WriteAllTextAsync(Path.Combine(App, "Report.cs"),
            "using Sample.Domain;\n\nnamespace Sample.App;\n\npublic class Report(IRepository<Order> orders)\n{\n    public int Count() => orders.All().Count();\n}\n");

        var result = await Run(incremental: true);

        Assert.Equal(GenerationMode.Incremental, Modes(result)["Sample.App"]);
        Assert.Equal(GenerationMode.Skipped, Modes(result)["Sample.Domain"]);
        Assert.True(File.Exists(Path.Combine(AppDocs, "sequence", "Sample.App.Report", "Count.puml")));
        Assert.EndsWith("' marker\n", await File.ReadAllTextAsync(place));
        Assert.Contains("Sample.App.Report", await File.ReadAllTextAsync(Path.Combine(AppDocs, "README.md")));

        // The skipped project keeps its graph, and the solution graph is merged from it again.
        Assert.Contains(result.Projects.Single(p => p.Name == "Sample.Domain").Files, f => f.EndsWith(GraphDatabase.FileName));
        using var graph = GraphIndex.Open(GraphIndex.Locate(_sample.SolutionPath));
        Assert.NotNull(graph.Node("T:Sample.App.Report"));
        Assert.False(graph.Node("T:Sample.Domain.Order")!.IsStub);
    }

    [Fact]
    public async Task An_updated_project_matches_a_full_generation()
    {
        await Run();
        await Edit(OrderService, "    public decimal Place(", CountMethod);

        var result = await Run(incremental: true);

        Assert.Equal(GenerationMode.Incremental, Modes(result)["Sample.App"]);
        Assert.True(File.Exists(Path.Combine(AppDocs, "sequence", "Sample.App.OrderService", "Count.puml")));
        await AssertSameAsFullRun();
    }

    [Fact]
    public async Task A_change_of_a_referenced_project_updates_the_projects_using_it()
    {
        await Run();
        await Edit(Path.Combine(Domain, "Model.cs"), "    public OrderState State { get; set; }",
            "    public OrderState State { get; set; }\n    public string? Note { get; set; }");
        _sample.Commit("Note");

        var result = await Run(incremental: true);

        Assert.Equal(GenerationMode.Incremental, Modes(result)["Sample.Domain"]);
        Assert.Equal(GenerationMode.Incremental, Modes(result)["Sample.App"]);
        Assert.Equal(_sample.Git("rev-parse", "HEAD").Trim(), GenerationState.Load(AppDocs)!.Commit);
        await AssertSameAsFullRun();
    }

    [Fact]
    public async Task The_diagrams_of_a_renamed_method_are_replaced()
    {
        await Run();
        await Edit(OrderService, "public decimal Place(", "public decimal PlaceOrder(");

        await Run(incremental: true);

        Assert.False(File.Exists(Path.Combine(AppDocs, "sequence", "Sample.App.OrderService", "Place_Order.puml")));
        Assert.False(File.Exists(Path.Combine(AppDocs, "activity", "Sample.App.OrderService", "Place_Order.puml")));
        Assert.True(File.Exists(Path.Combine(AppDocs, "sequence", "Sample.App.OrderService", "PlaceOrder_Order.puml")));
        await AssertSameAsFullRun();
    }

    [Fact]
    public async Task A_reverted_uncommitted_change_is_generated_again()
    {
        await Edit(OrderService, "    public decimal Place(", CountMethod);
        await Run();
        Assert.Equal(["Sample.App/OrderService.cs"], GenerationState.Load(AppDocs)!.Dirty);
        _sample.Git("checkout", "--", ".");

        var result = await Run(incremental: true);

        Assert.Equal(GenerationMode.Incremental, Modes(result)["Sample.App"]);
        Assert.False(File.Exists(Path.Combine(AppDocs, "sequence", "Sample.App.OrderService", "Count.puml")));
        await AssertSameAsFullRun();
    }

    [Fact]
    public async Task Other_options_generate_everything_again()
    {
        await Run();

        var result = await Run(incremental: true, diagrams: DiagramKinds.Class);

        Assert.All(result.Projects, p => Assert.Equal(GenerationMode.Full, p.Mode));
        Assert.False(System.IO.Directory.Exists(Path.Combine(AppDocs, "sequence")));
    }

    [Fact]
    public async Task Since_compares_with_the_given_ref()
    {
        await Run();
        await Edit(OrderService, "    public decimal Place(", CountMethod);
        _sample.Commit("Count");

        var result = await Run(since: "HEAD");

        Assert.All(result.Projects, p => Assert.Equal(GenerationMode.Skipped, p.Mode));
        await Assert.ThrowsAsync<ArgumentException>(() => Run(since: "no-such-branch"));
    }

    [Fact]
    public async Task A_build_file_change_generates_everything_again()
    {
        await Run();
        await File.WriteAllTextAsync(Path.Combine(_sample.Directory, "Directory.Build.props"), "<Project />\n");

        var result = await Run(incremental: true);

        Assert.All(result.Projects, p => Assert.Equal(GenerationMode.Full, p.Mode));
    }
}
