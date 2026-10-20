using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Dotnetumentation.Core.Analysis;
using Dotnetumentation.Core.Generators;
using Dotnetumentation.Core.Loading;
using Dotnetumentation.Core.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Dotnetumentation.Core;

public sealed record GenerationProgress(string Message, int Completed = 0, int Total = 0);

public sealed record ProjectResult(string Name, string OutputDirectory, int Written, int Unchanged, int Deleted, IReadOnlyList<string> Files);

public sealed record GenerationResult(IReadOnlyList<ProjectResult> Projects, IReadOnlyList<string> Warnings, TimeSpan Elapsed);

/// <summary>Loads a solution, analyzes its C# projects and writes the diagrams into each project's output folder.</summary>
public sealed class DocumentationGenerator
{
    public static IReadOnlyList<IDiagramGenerator> GeneratorsFor(DiagramKinds kinds)
    {
        var generators = new List<IDiagramGenerator>();
        if (kinds.HasFlag(DiagramKinds.Class))
            generators.Add(new ClassDiagramGenerator());
        if (kinds.HasFlag(DiagramKinds.Component))
            generators.Add(new ComponentDiagramGenerator());
        if (kinds.HasFlag(DiagramKinds.Sequence))
            generators.Add(new SequenceDiagramGenerator());
        if (kinds.HasFlag(DiagramKinds.Activity))
            generators.Add(new ActivityDiagramGenerator());
        generators.Add(new MarkdownIndexGenerator());
        return generators;
    }

    public async Task<GenerationResult> GenerateAsync(GeneratorOptions options, IProgress<GenerationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        // The folder is deleted with --clean: it must be a plain sub folder, never the project folder itself.
        var outputName = options.OutputDirectoryName;
        if (string.IsNullOrWhiteSpace(outputName) || outputName is "." or ".."
            || outputName.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, ..Path.GetInvalidFileNameChars()]) >= 0)
            throw new ArgumentException($"Invalid output folder name: '{outputName}'.", nameof(options));

        var stopwatch = Stopwatch.StartNew();
        progress?.Report(new GenerationProgress($"Opening {Path.GetFileName(options.InputPath)}"));
        using var loaded = await SolutionLoader.LoadAsync(options.InputPath,
            new Progress<string>(m => progress?.Report(new GenerationProgress(m))), cancellationToken);
        var warnings = new ConcurrentBag<string>(loaded.Diagnostics);

        var projects = loaded.Projects.Where(p => Matches(SolutionLoader.ProjectName(p), options)).ToList();
        if (projects.Count == 0)
            warnings.Add("No C# project matches the include / exclude patterns.");

        // Every compilation of the solution is needed: calls are followed into projects that are not documented, too.
        progress?.Report(new GenerationProgress("Compiling", 0, projects.Count));
        var compilations = new Dictionary<SyntaxTree, Compilation>();
        var projectByAssembly = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var project in loaded.Solution.Projects.Where(p => p.Language == LanguageNames.CSharp))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await project.GetCompilationAsync(cancellationToken) is not { } compilation)
                continue;
            foreach (var tree in compilation.SyntaxTrees)
                compilations.TryAdd(tree, compilation);
            projectByAssembly.TryAdd(compilation.AssemblyName ?? project.AssemblyName, SolutionLoader.ProjectName(project));
        }

        var analyses = new List<(Project Project, Compilation Compilation, ProjectModel Model, TypeAnalyzer Analyzer)>();
        foreach (var project in projects)
        {
            if (await project.GetCompilationAsync(cancellationToken) is not { } compilation)
            {
                warnings.Add($"{project.Name}: the project could not be compiled.");
                continue;
            }
            var analyzer = new TypeAnalyzer(compilation, options.MinVisibility);
            var model = new ProjectModel(
                SolutionLoader.ProjectName(project),
                project.FilePath!,
                TargetFramework(project),
                project.AssemblyName,
                project.ProjectReferences.Select(r => loaded.Solution.GetProject(r.ProjectId)).OfType<Project>()
                    .Select(SolutionLoader.ProjectName).Distinct().Order(StringComparer.Ordinal).ToList(),
                PackageReferenceReader.Read(project.FilePath!),
                analyzer.Analyze(cancellationToken))
            {
                NamespaceDependencies = options.Diagrams.HasFlag(DiagramKinds.Component)
                    ? analyzer.NamespaceDependencies(projectByAssembly, cancellationToken)
                    : [],
            };
            analyses.Add((project, compilation, model, analyzer));
        }

        var solutionTypes = analyses.SelectMany(a => a.Model.Types).DistinctBy(t => t.Id).ToDictionary(t => t.Id);
        var results = new ConcurrentBag<ProjectResult>();
        var completed = 0;
        await Parallel.ForEachAsync(analyses, cancellationToken, (analysis, ct) =>
        {
            var (activities, sequences) = AnalyzeMethods(analysis.Analyzer, compilations, options, ct);
            var context = new GeneratorContext(analysis.Model, activities, sequences, solutionTypes, options.Diagrams);
            var files = GeneratorsFor(options.Diagrams).SelectMany(g => g.Generate(context)).ToList();
            var outputDirectory = Path.Combine(analysis.Model.Directory, options.OutputDirectoryName);
            var result = OutputWriter.Write(analysis.Model.Name, outputDirectory, files, options.Clean);
            results.Add(result);
            progress?.Report(new GenerationProgress(
                $"{analysis.Model.Name}: {result.Written} written, {result.Unchanged} unchanged, {result.Deleted} deleted",
                Interlocked.Increment(ref completed), analyses.Count));
            return ValueTask.CompletedTask;
        });

        return new GenerationResult(results.OrderBy(r => r.Name, StringComparer.Ordinal).ToList(),
            warnings.Distinct().Order(StringComparer.Ordinal).ToList(), stopwatch.Elapsed);
    }

    private static (List<ActivityModel>, List<SequenceModel>) AnalyzeMethods(TypeAnalyzer analyzer,
        IReadOnlyDictionary<SyntaxTree, Compilation> compilations, GeneratorOptions options, CancellationToken cancellationToken)
    {
        var activities = new List<ActivityModel>();
        var sequences = new List<SequenceModel>();
        var wantActivities = options.Diagrams.HasFlag(DiagramKinds.Activity);
        var wantSequences = options.Diagrams.HasFlag(DiagramKinds.Sequence);
        if (!wantActivities && !wantSequences)
            return (activities, sequences);

        var models = new Dictionary<SyntaxTree, SemanticModel>();
        SemanticModel? ModelOf(SyntaxTree tree)
        {
            if (models.TryGetValue(tree, out var model))
                return model;
            if (!compilations.TryGetValue(tree, out var compilation))
                return null;
            return models[tree] = compilation.GetSemanticModel(tree);
        }

        var callGraph = new CallGraphAnalyzer(ModelOf, options.SequenceDepth, options.MaxSequenceMessages);
        foreach (var type in analyzer.SourceTypes().OrderBy(Symbols.TypeId, StringComparer.Ordinal))
        {
            foreach (var method in TypeAnalyzer.SourceOrder(type.GetMembers()).OfType<IMethodSymbol>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (method.IsImplicitlyDeclared || method.MethodKind is not (MethodKind.Ordinary or MethodKind.Constructor)
                    || Symbols.VisibilityOf(method) < options.MinVisibility || Symbols.IsGenerated(method))
                    continue;
                var declaration = method.DeclaringSyntaxReferences.Select(r => r.GetSyntax(cancellationToken))
                    .OfType<BaseMethodDeclarationSyntax>().FirstOrDefault(m => m.Body is not null || m.ExpressionBody is not null);
                if (declaration is null)
                    continue;

                var ns = Symbols.NamespaceName(type);
                var typeName = type.ToDisplayString(Symbols.TypeNameFormat);
                if (wantActivities && ControlFlowAnalyzer.Analyze(declaration, !method.ReturnsVoid) is { } body
                                   && ControlFlowAnalyzer.IsInteresting(body))
                    activities.Add(new ActivityModel(ns, typeName, TypeAnalyzer.MethodId(method), body));
                if (wantSequences && callGraph.Analyze(method, cancellationToken) is { } sequence)
                    sequences.Add(sequence);
            }
        }
        return (activities, sequences);
    }

    private static string? TargetFramework(Project project)
    {
        // Multi-targeted projects are named "Name(net8.0)" by the workspace.
        var match = Regex.Match(project.Name, @"\((?<tfm>[^)]+)\)$");
        if (match.Success)
            return match.Groups["tfm"].Value;
        return project.ParseOptions?.PreprocessorSymbolNames
            .Where(s => Regex.IsMatch(s, @"^NET\d+_\d+$|^NETSTANDARD\d+_\d+$|^NET\d{2,3}$"))
            .OrderByDescending(s => s, StringComparer.Ordinal)
            .Select(s => s.ToLowerInvariant().Replace('_', '.'))
            .FirstOrDefault();
    }

    /// <summary>The project matches an include pattern (or there are none) and no exclude pattern.</summary>
    public static bool Matches(string projectName, GeneratorOptions options) =>
        (options.Include.Count == 0 || options.Include.Any(p => Glob(p).IsMatch(projectName)))
        && !options.Exclude.Any(p => Glob(p).IsMatch(projectName));

    private static Regex Glob(string pattern) =>
        new("^" + Regex.Escape(pattern.Trim()).Replace(@"\*", ".*").Replace(@"\?", ".") + "$", RegexOptions.IgnoreCase);
}
