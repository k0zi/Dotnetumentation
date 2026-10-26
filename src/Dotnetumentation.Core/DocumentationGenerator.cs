using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Dotnetumentation.Core.Analysis;
using Dotnetumentation.Core.Generators;
using Dotnetumentation.Core.Graph;
using Dotnetumentation.Core.Incremental;
using Dotnetumentation.Core.Loading;
using Dotnetumentation.Core.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Dotnetumentation.Core;

public sealed record GenerationProgress(string Message, int Completed = 0, int Total = 0);

/// <summary>How the documentation of a project was made in a run.</summary>
public enum GenerationMode
{
    /// <summary>Analyzed and generated completely.</summary>
    Full,
    /// <summary>Generated again, keeping the method diagrams whose source files did not change.</summary>
    Incremental,
    /// <summary>Nothing changed since the last generation: the files were left as they are.</summary>
    Skipped,
}

public sealed record ProjectResult(string Name, string OutputDirectory, int Written, int Unchanged, int Deleted, IReadOnlyList<string> Files,
    GenerationMode Mode = GenerationMode.Full);

/// <param name="SolutionGraph">The graph database of the whole solution; null when no graph is generated.</param>
public sealed record GenerationResult(IReadOnlyList<ProjectResult> Projects, IReadOnlyList<string> Warnings, TimeSpan Elapsed,
    string? SolutionGraph = null);

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
        generators.Add(new StaticDocumentationGenerator());
        generators.Add(new GraphDocumentationGenerator());
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
        if (options.IsIncremental && options.Clean)
            throw new ArgumentException("An incremental update cannot delete the output folders first (clean).", nameof(options));

        var stopwatch = Stopwatch.StartNew();
        progress?.Report(new GenerationProgress($"Opening {Path.GetFileName(options.InputPath)}"));
        using var loaded = await SolutionLoader.LoadAsync(options.InputPath,
            new Progress<string>(m => progress?.Report(new GenerationProgress(m))), cancellationToken);
        var warnings = new ConcurrentBag<string>(loaded.Diagnostics);

        var projects = loaded.Projects.Where(p => Matches(SolutionLoader.ProjectName(p), options)).ToList();
        if (projects.Count == 0)
            warnings.Add("No C# project matches the include / exclude patterns.");

        // The git state is saved with every run, so a later run can update the documentation incrementally.
        var solutionDirectory = Path.GetDirectoryName(Path.GetFullPath(options.InputPath))!;
        var repository = GitRepository.TryOpen(solutionDirectory);
        var head = repository?.Head();
        var source = new SourcePaths(repository?.Root ?? solutionDirectory);
        var dirty = head is null ? [] : repository!.Dirty().Where(GenerationState.IsSource).Order(GenerationState.PathComparer).ToList();
        var fingerprint = GenerationState.FingerprintOf(options);
        var plans = Plan(loaded, projects, options, repository, head, fingerprint, warnings, progress);

        // Only the updated projects and the projects they reference (and follow calls into) are compiled.
        var dependencies = loaded.Solution.GetProjectDependencyGraph();
        var needed = projects.Where(p => plans[p].Mode != GenerationMode.Skipped)
            .SelectMany(p => dependencies.GetProjectsThatThisProjectTransitivelyDependsOn(p.Id).Append(p.Id))
            .ToHashSet();
        var compileAll = plans.Values.All(p => p.Mode != GenerationMode.Skipped);

        progress?.Report(new GenerationProgress("Compiling", 0, projects.Count));
        var compilations = new Dictionary<SyntaxTree, Compilation>();
        var projectByAssembly = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var project in loaded.Solution.Projects.Where(p => p.Language == LanguageNames.CSharp))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!compileAll && !needed.Contains(project.Id))
            {
                projectByAssembly.TryAdd(project.AssemblyName, SolutionLoader.ProjectName(project));
                continue;
            }
            if (await project.GetCompilationAsync(cancellationToken) is not { } compilation)
                continue;
            foreach (var tree in compilation.SyntaxTrees)
                compilations.TryAdd(tree, compilation);
            projectByAssembly.TryAdd(compilation.AssemblyName ?? project.AssemblyName, SolutionLoader.ProjectName(project));
        }

        // Skipped projects referenced by updated ones are analyzed too: their types are named in the updated documentation.
        var analyses = new List<(Project Project, Compilation Compilation, ProjectModel Model, TypeAnalyzer Analyzer)>();
        foreach (var project in projects.Where(p => compileAll || needed.Contains(p.Id)))
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
                NamespaceDependencies = options.Diagrams.HasFlag(DiagramKinds.Component) && plans[project].Mode != GenerationMode.Skipped
                    ? analyzer.NamespaceDependencies(projectByAssembly, cancellationToken)
                    : [],
            };
            analyses.Add((project, compilation, model, analyzer));
        }

        var solutionTypes = analyses.SelectMany(a => a.Model.Types).DistinctBy(t => t.Id).ToDictionary(t => t.Id);
        var results = new ConcurrentBag<ProjectResult>();
        var completed = 0;
        foreach (var project in projects.Where(p => plans[p].Mode == GenerationMode.Skipped))
        {
            var plan = plans[project];
            var outputDirectory = OutputDirectory(project, options);
            plan.State?.With(head, dirty).Save(outputDirectory);
            var result = OutputWriter.Skip(SolutionLoader.ProjectName(project), outputDirectory);
            results.Add(result);
            progress?.Report(new GenerationProgress($"{result.Name}: up to date", Interlocked.Increment(ref completed), projects.Count));
        }

        await Parallel.ForEachAsync(analyses.Where(a => plans[a.Project].Mode != GenerationMode.Skipped), cancellationToken, (analysis, ct) =>
        {
            var plan = plans[analysis.Project];
            var outputDirectory = Path.Combine(analysis.Model.Directory, options.OutputDirectoryName);
            var reuse = plan.Mode == GenerationMode.Incremental ? new Reuse(plan.State!, plan.Changed!, outputDirectory) : null;
            var methods = AnalyzeMethods(analysis.Analyzer, compilations, options, source, reuse, ct);
            var context = new GeneratorContext(analysis.Model, methods.Activities, methods.Sequences, solutionTypes, options.Diagrams)
            {
                SequenceLinks = methods.SequenceStates.Select(Link).ToList(),
                ActivityLinks = methods.ActivityStates.Select(Link).ToList(),
            };
            if (options.Graph)
                context = context with
                {
                    Graph = new GraphAnalyzer(analysis.Compilation, analysis.Analyzer, projectByAssembly, outputDirectory, solutionDirectory)
                        .Build(context, ct),
                };
            var files = GeneratorsFor(options.Diagrams).SelectMany(g => g.Generate(context)).ToList();
            var result = WriteGraph(OutputWriter.Write(analysis.Model.Name, outputDirectory, files, options.Clean, methods.Kept, plan.Mode),
                context.Graph);
            new GenerationState
            {
                Fingerprint = fingerprint,
                Commit = head,
                Dirty = dirty,
                Sequences = methods.SequenceStates,
                Activities = methods.ActivityStates,
            }.Save(outputDirectory);
            results.Add(result);
            var kept = methods.Kept.Count > 0 ? $", {methods.Kept.Count} diagrams kept" : "";
            progress?.Report(new GenerationProgress(
                $"{analysis.Model.Name}: {result.Written} written, {result.Unchanged} unchanged, {result.Deleted} deleted{kept}",
                Interlocked.Increment(ref completed), projects.Count));
            return ValueTask.CompletedTask;
        });

        // Skipped projects keep their graph database, so the solution graph is always made from every project's database.
        var solutionGraph = Path.Combine(solutionDirectory, Path.GetFileNameWithoutExtension(options.InputPath) + GraphDatabase.SolutionFileSuffix);
        var projectGraphs = projects.Select(p => Path.Combine(OutputDirectory(p, options), GraphDatabase.FileName)).Where(File.Exists).Distinct().ToList();
        if (options.Graph && projectGraphs.Count > 0)
        {
            progress?.Report(new GenerationProgress($"Graph database of the solution: {Path.GetFileName(solutionGraph)}"));
            GraphDatabase.Merge(solutionGraph, projectGraphs, new Dictionary<string, string>
            {
                ["schema_version"] = GraphDatabase.SchemaVersion.ToString(),
                ["tool_version"] = GraphAnalyzer.ToolVersion,
                ["scope"] = "solution",
                ["solution"] = Path.GetFileNameWithoutExtension(options.InputPath),
            });
        }
        else if (!options.Graph)
            File.Delete(solutionGraph);

        return new GenerationResult(results.OrderBy(r => r.Name, StringComparer.Ordinal).ToList(),
            warnings.Distinct().Order(StringComparer.Ordinal).ToList(), stopwatch.Elapsed,
            File.Exists(solutionGraph) ? solutionGraph : null);
    }

    /// <summary>Writes (or, when it is not generated, deletes) the graph database of a project and counts it in the result.</summary>
    private static ProjectResult WriteGraph(ProjectResult result, CodeGraph? graph)
    {
        var path = Path.GetFullPath(Path.Combine(result.OutputDirectory, GraphDatabase.FileName));
        if (graph is null)
        {
            if (!File.Exists(path))
                return result;
            File.Delete(path);
            return result with { Deleted = result.Deleted + 1 };
        }
        var written = GraphDatabase.Write(path, graph);
        return result with
        {
            Written = result.Written + (written ? 1 : 0),
            Unchanged = result.Unchanged + (written ? 0 : 1),
            Files = result.Files.Append(path).Order(StringComparer.Ordinal).ToList(),
        };
    }

    private static MethodDiagram Link(MethodDiagramState state) => new(state.Namespace, state.TypeName, state.MethodId);

    private static string OutputDirectory(Project project, GeneratorOptions options) =>
        Path.Combine(Path.GetDirectoryName(project.FilePath!) ?? ".", options.OutputDirectoryName);

    /// <summary>How a project is documented in this run; <see cref="Changed"/> are the changed source files of an incremental update.</summary>
    private sealed record ProjectPlan(GenerationMode Mode, GenerationState? State = null, IReadOnlySet<string>? Changed = null);

    /// <summary>
    /// Decides per project whether it is generated from scratch, updated or skipped. A project is updated when a source
    /// file of it or of a project it references (transitively) changed since the commit of its last generation.
    /// </summary>
    private static Dictionary<Project, ProjectPlan> Plan(LoadedSolution loaded, IReadOnlyList<Project> projects, GeneratorOptions options,
        GitRepository? repository, string? head, string fingerprint, ConcurrentBag<string> warnings, IProgress<GenerationProgress>? progress)
    {
        var full = projects.ToDictionary(p => p, _ => new ProjectPlan(GenerationMode.Full));
        if (!options.IsIncremental)
            return full;
        if (repository is null || head is null)
        {
            warnings.Add("The solution is not in a git repository with commits: the whole documentation is generated.");
            return full;
        }
        var since = options.Since is null ? null
            : repository.ResolveCommit(options.Since) ?? throw new ArgumentException($"Unknown git commit or ref: '{options.Since}'.", nameof(options));

        var source = new SourcePaths(repository.Root);
        var owners = new SourceOwners(loaded.Solution, source);
        var dependencies = loaded.Solution.GetProjectDependencyGraph();
        var changesSince = new Dictionary<string, IReadOnlySet<string>?>(StringComparer.Ordinal);
        var plans = new Dictionary<Project, ProjectPlan>();
        foreach (var project in projects)
        {
            var name = SolutionLoader.ProjectName(project);
            var outputDirectory = OutputDirectory(project, options);
            var state = GenerationState.Load(outputDirectory) is { } loadedState && loadedState.Fingerprint == fingerprint ? loadedState : null;
            var baseCommit = since ?? (state?.Commit is { } commit ? repository.ResolveCommit(commit) : null);
            if (baseCommit is null || !File.Exists(Path.Combine(outputDirectory, DocumentationPaths.Index))
                || options.Graph && !File.Exists(Path.Combine(outputDirectory, GraphDatabase.FileName)))
            {
                progress?.Report(new GenerationProgress($"{name}: no earlier generation with these options, generated from scratch"));
                plans[project] = new ProjectPlan(GenerationMode.Full);
                continue;
            }
            if (!changesSince.TryGetValue(baseCommit, out var changes))
                changesSince[baseCommit] = changes = repository.ChangedSince(baseCommit);
            if (changes is null)
            {
                warnings.Add($"{name}: the git changes since {baseCommit[..7]} could not be read; the project is generated from scratch.");
                plans[project] = new ProjectPlan(GenerationMode.Full);
                continue;
            }

            var changed = changes.Concat(state?.Dirty ?? []).Where(GenerationState.IsSource).ToHashSet(GenerationState.PathComparer);
            var scope = dependencies.GetProjectsThatThisProjectTransitivelyDependsOn(project.Id).Append(project.Id)
                .Select(id => loaded.Solution.GetProject(id)?.FilePath).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
            var relevant = changed.Where(f => GenerationState.IsSolutionLevel(f) || owners.Of(f).Any(scope.Contains)).ToList();
            var ownProjectFile = source.Relative(project.FilePath!);
            ProjectPlan plan;
            if (relevant.Count == 0)
                plan = new ProjectPlan(GenerationMode.Skipped, state);
            else if (state is null || relevant.Any(f => GenerationState.IsSolutionLevel(f) || GenerationState.PathComparer.Equals(f, ownProjectFile)))
                plan = new ProjectPlan(GenerationMode.Full);
            else
                plan = new ProjectPlan(GenerationMode.Incremental, state, changed);
            plans[project] = plan;
            if (plan.Mode != GenerationMode.Skipped)
                progress?.Report(new GenerationProgress($"{name}: {relevant.Count} changed source file(s) since {baseCommit[..7]}"
                                                        + (plan.Mode == GenerationMode.Full ? ", generated from scratch" : "")));
        }
        progress?.Report(new GenerationProgress(
            $"Incremental update: {plans.Values.Count(p => p.Mode != GenerationMode.Skipped)} of {projects.Count} project(s) to update"));
        return plans;
    }

    /// <summary>The diagrams of the last run that are still valid: none of their source files changed and the file is still there.</summary>
    private sealed class Reuse(GenerationState state, IReadOnlySet<string> changed, string outputDirectory)
    {
        private readonly Dictionary<MethodDiagram, MethodDiagramState> _sequences = Index(state.Sequences);
        private readonly Dictionary<MethodDiagram, MethodDiagramState> _activities = Index(state.Activities);

        public MethodDiagramState? Sequence(MethodDiagram method) =>
            Find(_sequences, method, DocumentationPaths.Sequence(method.TypeName, method.Namespace, method.MethodId));

        public MethodDiagramState? Activity(MethodDiagram method) =>
            Find(_activities, method, DocumentationPaths.Activity(method.TypeName, method.Namespace, method.MethodId));

        private MethodDiagramState? Find(Dictionary<MethodDiagram, MethodDiagramState> diagrams, MethodDiagram method, string path) =>
            diagrams.TryGetValue(method, out var diagram) && !diagram.Sources.Any(changed.Contains)
                                                          && File.Exists(OutputWriter.FullPath(outputDirectory, path))
                ? diagram
                : null;

        private static Dictionary<MethodDiagram, MethodDiagramState> Index(IReadOnlyList<MethodDiagramState> diagrams) =>
            diagrams.DistinctBy(Link).ToDictionary(Link);
    }

    /// <summary>The method diagrams of a project: the analyzed models and, for every diagram, the state saved for the next run.</summary>
    private sealed class MethodDiagrams
    {
        public List<ActivityModel> Activities { get; } = [];
        public List<SequenceModel> Sequences { get; } = [];
        public List<MethodDiagramState> ActivityStates { get; } = [];
        public List<MethodDiagramState> SequenceStates { get; } = [];
        /// <summary>Diagram files of the last run that are left as they are.</summary>
        public HashSet<string> Kept { get; } = new(StringComparer.Ordinal);
    }

    private static MethodDiagrams AnalyzeMethods(TypeAnalyzer analyzer, IReadOnlyDictionary<SyntaxTree, Compilation> compilations,
        GeneratorOptions options, SourcePaths source, Reuse? reuse, CancellationToken cancellationToken)
    {
        var result = new MethodDiagrams();
        var wantActivities = options.Diagrams.HasFlag(DiagramKinds.Activity);
        var wantSequences = options.Diagrams.HasFlag(DiagramKinds.Sequence);
        if (!wantActivities && !wantSequences)
            return result;

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

                var key = new MethodDiagram(Symbols.NamespaceName(type), type.ToDisplayString(Symbols.TypeNameFormat), TypeAnalyzer.MethodId(method));
                // A method without a kept diagram is analyzed again: that is cheap when it had no diagram before.
                if (wantActivities)
                {
                    if (reuse?.Activity(key) is { } kept)
                        Keep(result.ActivityStates, kept, DocumentationPaths.Activity(key.TypeName, key.Namespace, key.MethodId));
                    else if (ControlFlowAnalyzer.Analyze(declaration, !method.ReturnsVoid) is { } body && ControlFlowAnalyzer.IsInteresting(body))
                    {
                        result.Activities.Add(new ActivityModel(key.Namespace, key.TypeName, key.MethodId, body));
                        result.ActivityStates.Add(new MethodDiagramState(key.Namespace, key.TypeName, key.MethodId,
                            [source.Relative(declaration.SyntaxTree.FilePath)]));
                    }
                }
                if (wantSequences)
                {
                    if (reuse?.Sequence(key) is { } kept)
                        Keep(result.SequenceStates, kept, DocumentationPaths.Sequence(key.TypeName, key.Namespace, key.MethodId));
                    else if (callGraph.Analyze(method, cancellationToken) is { } sequence)
                    {
                        result.Sequences.Add(sequence);
                        result.SequenceStates.Add(new MethodDiagramState(key.Namespace, key.TypeName, key.MethodId,
                            sequence.SourceFiles.Select(source.Relative).Distinct(GenerationState.PathComparer).Order(StringComparer.Ordinal).ToList()));
                    }
                }
            }
        }
        return result;

        void Keep(List<MethodDiagramState> states, MethodDiagramState state, string path)
        {
            states.Add(state);
            result.Kept.Add(path);
        }
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
