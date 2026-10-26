using System.Diagnostics;
using KD.Avalonia.Rice.Controls.Uml;

namespace Dotnetumentation.Core.Tests;

/// <summary>The sample solution copied to a temporary folder and restored once for all tests.</summary>
public sealed class SampleSolutionFixture : IDisposable
{
    public string Directory { get; } = Path.Combine(Path.GetTempPath(), "dotnetumentation-tests-" + Guid.NewGuid().ToString("N"));

    public string SolutionPath => Path.Combine(Directory, "Sample.slnx");

    public SampleSolutionFixture()
    {
        CopyDirectory(Path.Combine(AppContext.BaseDirectory, "TestData", "SampleSolution"), Directory);
        // The workspace needs the restored project.assets.json to resolve the framework references. Without node reuse:
        // a reused MSBuild node would inherit the output pipe and keep it open after the restore exited.
        using var restore = Process.Start(new ProcessStartInfo("dotnet", ["restore", SolutionPath, "--disable-build-servers", "-nodeReuse:false"])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        var error = restore.StandardError.ReadToEndAsync();
        var output = restore.StandardOutput.ReadToEnd() + error.Result;
        restore.WaitForExit();
        if (restore.ExitCode != 0)
            throw new InvalidOperationException("dotnet restore failed:\n" + output);
    }

    private static void CopyDirectory(string source, string target)
    {
        System.IO.Directory.CreateDirectory(target);
        foreach (var file in System.IO.Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        foreach (var dir in System.IO.Directory.EnumerateDirectories(source))
            CopyDirectory(dir, Path.Combine(target, Path.GetFileName(dir)));
    }

    public void Dispose()
    {
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temporary folder is harmless.
        }
    }
}

public class GeneratorTests(SampleSolutionFixture sample) : IClassFixture<SampleSolutionFixture>
{
    private Task<GenerationResult> GenerateAsync(bool clean = false) =>
        Run(new GeneratorOptions { InputPath = sample.SolutionPath, Clean = clean });

    private static Task<GenerationResult> Run(GeneratorOptions options) => new DocumentationGenerator().GenerateAsync(options);

    [Fact]
    public async Task Every_project_gets_its_documentation_folder()
    {
        var result = await GenerateAsync(clean: true);

        Assert.Equal(["Sample.App", "Sample.Domain"], result.Projects.Select(p => p.Name));
        var app = Path.Combine(sample.Directory, "Sample.App", "docs.net");
        var domain = Path.Combine(sample.Directory, "Sample.Domain", "docs.net");
        foreach (var file in (string[])["README.md", "dependencies.md", "documentation-coverage.md", "component.puml", "packages.puml", "class/Sample.App.puml",
                     "sequence/Sample.App.OrderService/Place_Order.puml", "activity/Sample.App.OrderService/Place_Order.puml"])
            Assert.True(File.Exists(Path.Combine(app, file)), file);
        Assert.True(File.Exists(Path.Combine(domain, "class", "Sample.Domain.puml")));
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(domain, "api"), "*.md"));
        var dependencyDocs = await File.ReadAllTextAsync(Path.Combine(app, "dependencies.md"));
        Assert.Contains("Sample.Domain", dependencyDocs);

        var component = await File.ReadAllTextAsync(Path.Combine(app, "component.puml"));
        Assert.Contains("project_Sample_App --> project_Sample_Domain", component);
        var readme = await File.ReadAllTextAsync(Path.Combine(domain, "README.md"));
        Assert.Contains("| `Order` | class | An order of a Customer. |", readme);
    }

    [Fact]
    public async Task Every_diagram_is_readable_by_rice()
    {
        var result = await GenerateAsync();

        var files = result.Projects.SelectMany(p => p.Files).Where(f => f.EndsWith(".puml")).ToList();
        Assert.NotEmpty(files);
        foreach (var file in files)
        {
            var kind = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(file))) switch
            {
                "sequence" => UmlDiagramKind.Sequence,
                "activity" => UmlDiagramKind.Activity,
                _ => Path.GetFileName(file) switch
                {
                    "component.puml" => UmlDiagramKind.Component,
                    "packages.puml" => UmlDiagramKind.Package,
                    _ => UmlDiagramKind.Class,
                },
            };
            RiceAssert.Parses(await File.ReadAllTextAsync(file), kind);
        }
    }

    [Fact]
    public async Task A_second_run_changes_nothing_and_removes_stale_files()
    {
        await GenerateAsync();
        var stale = Path.Combine(sample.Directory, "Sample.Domain", "docs.net", "class", "Removed.Namespace.puml");
        await File.WriteAllTextAsync(stale, "@startuml\n@enduml\n");

        var result = await GenerateAsync();

        Assert.All(result.Projects, p => Assert.Equal(0, p.Written));
        Assert.Equal(1, result.Projects.Sum(p => p.Deleted));
        Assert.False(File.Exists(stale));
    }

    [Theory]
    [InlineData("Sample.App", true)]
    [InlineData("Sample.Domain", false)]
    public void Include_and_exclude_patterns_select_projects(string project, bool expected)
    {
        var options = new GeneratorOptions { InputPath = "x.slnx", Include = ["Sample.*"], Exclude = ["*.Domain"] };
        Assert.Equal(expected, DocumentationGenerator.Matches(project, options));
    }

    [Fact]
    public async Task Without_git_an_incremental_update_generates_everything()
    {
        var result = await Run(new GeneratorOptions { InputPath = sample.SolutionPath, Incremental = true });

        Assert.All(result.Projects, p => Assert.Equal(GenerationMode.Full, p.Mode));
        Assert.Contains(result.Warnings, w => w.Contains("not in a git repository"));
    }

    [Fact]
    public async Task An_incremental_update_cannot_clean()
    {
        var options = new GeneratorOptions { InputPath = sample.SolutionPath, Incremental = true, Clean = true };
        await Assert.ThrowsAsync<ArgumentException>(() => Run(options));
    }

    [Fact]
    public async Task Output_folder_must_be_a_plain_name()
    {
        var options = new GeneratorOptions { InputPath = sample.SolutionPath, OutputDirectoryName = "..", Clean = true };
        await Assert.ThrowsAsync<ArgumentException>(() => Run(options));
    }
}
