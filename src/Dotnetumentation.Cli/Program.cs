using System.CommandLine;
using Dotnetumentation.Core;
using Dotnetumentation.Core.Graph;
using Dotnetumentation.Core.Model;

namespace Dotnetumentation.Cli;

internal static class Program
{
    private const int Success = 0;
    private const int LoadFailed = 1;
    private const int CompletedWithWarnings = 2;

    public static Task<int> Main(string[] args) => BuildCommand().Parse(args).InvokeAsync();

    private static RootCommand BuildCommand()
    {
        var input = new Argument<FileInfo>("input")
        {
            Description = "The .sln, .slnx or .csproj file to document.",
        };
        var diagrams = new Option<DiagramKinds[]>("--diagrams", "-d")
        {
            Description = "Diagram types to generate (class, component, sequence, activity); default: all.",
            AllowMultipleArgumentsPerToken = true,
            CustomParser = result => result.Tokens
                .SelectMany(t => t.Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .Select(v => Enum.TryParse<DiagramKinds>(v, ignoreCase: true, out var kind) ? kind : Fail(result, v))
                .ToArray(),
        };
        var visibility = new Option<Visibility>("--visibility", "-v")
        {
            Description = "Least visibility of the documented types and members (public, protected, internal, private).",
            DefaultValueFactory = _ => Visibility.Internal,
        };
        var depth = new Option<int?>("--sequence-depth")
        {
            Description = "How deep sequence diagrams follow calls into the solution: a number, or 'all' (default) to follow "
                          + "them to the end of the own code. Calls into .NET and other libraries are shown, but not followed.",
            CustomParser = result =>
            {
                var value = result.Tokens.Count == 0 ? "all" : result.Tokens[0].Value;
                if (value.Equals("all", StringComparison.OrdinalIgnoreCase))
                    return null;
                if (int.TryParse(value, out var number) && number >= 0)
                    return number;
                result.AddError($"Invalid sequence depth '{value}'. Use a number (0 or more) or 'all'.");
                return null;
            },
        };
        var maxMessages = new Option<int>("--max-messages")
        {
            Description = "Cut a sequence diagram after this many messages; 0 means no limit.",
            DefaultValueFactory = _ => GeneratorOptions.DefaultMaxSequenceMessages,
        };
        var include = new Option<string[]>("--include")
        {
            Description = "Only document projects matching these name patterns (* and ? wildcards).",
            AllowMultipleArgumentsPerToken = true,
        };
        var exclude = new Option<string[]>("--exclude")
        {
            Description = "Skip projects matching these name patterns (* and ? wildcards).",
            AllowMultipleArgumentsPerToken = true,
        };
        var outputName = new Option<string>("--output-dir-name")
        {
            Description = "Name of the folder created next to each project file.",
            DefaultValueFactory = _ => GeneratorOptions.DefaultOutputDirectoryName,
        };
        var clean = new Option<bool>("--clean")
        {
            Description = "Delete each output folder before writing it.",
        };
        var incremental = new Option<bool>("--incremental", "-i")
        {
            Description = "Only update what changed (git) since the last generation: unchanged projects are skipped, "
                          + "diagrams of unchanged code are kept. Without a git repository everything is generated.",
        };
        var since = new Option<string>("--since")
        {
            Description = "Incremental update compared with this git commit, branch or tag instead of the last generation.",
        };
        var noGraph = new Option<bool>("--no-graph")
        {
            Description = "Do not write the graph databases (graph.db) of the classes, functions and calls.",
        };
        var verbose = new Option<bool>("--verbose")
        {
            Description = "Show every progress message and the load warnings.",
        };

        var generate = new Command("generate", "Generate the PlantUML documentation of every C# project of a solution.")
        {
            input, diagrams, visibility, depth, maxMessages, include, exclude, outputName, clean, incremental, since, noGraph, verbose,
        };
        generate.SetAction((result, cancellationToken) => GenerateAsync(new GeneratorOptions
        {
            InputPath = result.GetValue(input)!.FullName,
            Diagrams = result.GetValue(diagrams) is { Length: > 0 } kinds ? kinds.Aggregate(DiagramKinds.None, (a, k) => a | k) : DiagramKinds.All,
            MinVisibility = result.GetValue(visibility),
            SequenceDepth = result.GetValue(depth),
            MaxSequenceMessages = Math.Max(0, result.GetValue(maxMessages)),
            Include = result.GetValue(include) ?? [],
            Exclude = result.GetValue(exclude) ?? [],
            OutputDirectoryName = result.GetValue(outputName) ?? GeneratorOptions.DefaultOutputDirectoryName,
            Clean = result.GetValue(clean),
            Incremental = result.GetValue(incremental),
            Since = result.GetValue(since),
            Graph = !result.GetValue(noGraph),
        }, result.GetValue(verbose), cancellationToken));

        return new RootCommand("Dotnetumentation: static UML (PlantUML) documentation of .NET solutions.") { generate, BuildQueryCommand() };
    }

    private static Command BuildQueryCommand()
    {
        var path = new Argument<string>("path")
        {
            Description = "A graph database, a solution or project file, a documentation folder or a folder above them.",
        };
        var text = new Argument<string>("text")
        {
            Description = "Words of the name, full name or summary; a word matches the start of a word (\"order serv\").",
        };
        var kind = new Option<string[]>("--kind", "-k")
        {
            Description = "Only these node kinds: type, member, or a kind (class, interface, method, property, record-struct, …).",
            AllowMultipleArgumentsPerToken = true,
        };
        var limit = new Option<int>("--limit", "-n")
        {
            Description = "Show at most this many matches.",
            DefaultValueFactory = _ => 20,
        };
        var relations = new Option<bool>("--relations", "-r")
        {
            Description = "Show the relations (calls, called by, implements, …) and documents of every match; always shown for a single match.",
        };
        var outputName = new Option<string>("--output-dir-name")
        {
            Description = "Name of the documentation folders to look for graph databases in.",
            DefaultValueFactory = _ => GeneratorOptions.DefaultOutputDirectoryName,
        };

        var query = new Command("query", "Search the classes and functions in the graph database of generated documentation.")
        {
            path, text, kind, limit, relations, outputName,
        };
        query.SetAction(result => Query(result.GetValue(path)!, result.GetValue(text)!,
            (result.GetValue(kind) ?? []).SelectMany(k => k.Split(',', StringSplitOptions.RemoveEmptyEntries)).SelectMany(RelationNames.KindsOf).ToList(),
            Math.Max(1, result.GetValue(limit)), result.GetValue(relations),
            result.GetValue(outputName) ?? GeneratorOptions.DefaultOutputDirectoryName));
        return query;
    }

    private static int Query(string path, string text, IReadOnlyList<string> kinds, int limit, bool relations, string outputDirectoryName)
    {
        var databases = GraphIndex.Locate(path, outputDirectoryName);
        if (databases.Count == 0)
        {
            Console.Error.WriteLine($"No graph database found at {Path.GetFullPath(path)}: generate the documentation first.");
            return LoadFailed;
        }

        using var index = GraphIndex.Open(databases);
        var matches = index.Search(text, kinds, limit);
        if (matches.Count == 0)
        {
            Console.WriteLine("No match.");
            return Success;
        }
        var kindWidth = matches.Max(m => m.Kind.Length);
        var nameWidth = Math.Min(80, matches.Max(m => m.FullName.Length));
        foreach (var match in matches)
        {
            Console.WriteLine($"{match.Kind.PadRight(kindWidth)}  {match.FullName.PadRight(nameWidth)}  {Location(match)}".TrimEnd());
            if (!relations && matches.Count > 1)
                continue;
            if (match.Summary is { } summary)
                Console.WriteLine($"    {summary}");
            foreach (var group in index.Relations(match.Id).Where(r => r.Edge.Kind != EdgeKinds.DeclaredIn)
                         .GroupBy(RelationNames.Of).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                Console.WriteLine($"    {group.Key}:");
                foreach (var relation in group.OrderBy(r => r.Other.FullName, StringComparer.Ordinal))
                {
                    var label = relation.Edge.Label.Length > 0 ? $" ({relation.Edge.Label})" : "";
                    var origin = relation.Other.IsExternal ? "  [external]" : "";
                    Console.WriteLine($"      {relation.Other.FullName}{label}{origin}");
                }
            }
            foreach (var document in index.Documents(match.Id))
                Console.WriteLine($"    {document.Kind}: {Display(document.Path)}");
        }
        return Success;
    }

    private static string Location(GraphNode node) =>
        node.File is null ? node.IsExternal ? "[external]" : "" : $"{Display(node.File)}{(node.Line is { } line ? $":{line}" : "")}";

    /// <summary>Paths below the current folder are shown relative to it.</summary>
    private static string Display(string path)
    {
        var relative = Path.GetRelativePath(Environment.CurrentDirectory, path);
        return relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative) ? path : relative;
    }

    private static DiagramKinds Fail(System.CommandLine.Parsing.ArgumentResult result, string value)
    {
        result.AddError($"Unknown diagram type '{value}'. Use class, component, sequence or activity.");
        return DiagramKinds.None;
    }

    private static async Task<int> GenerateAsync(GeneratorOptions options, bool verbose, CancellationToken cancellationToken)
    {
        // Progress<T> would post to the thread pool and mix up the order of the lines.
        var progress = new SynchronousProgress<GenerationProgress>(p =>
        {
            if (verbose || p.Total > 0 && p.Completed > 0)
                Console.WriteLine(p.Total > 0 && p.Completed > 0 ? $"[{p.Completed}/{p.Total}] {p.Message}" : p.Message);
        });

        GenerationResult result;
        try
        {
            result = await new DocumentationGenerator().GenerateAsync(options, progress, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Cancelled.");
            return LoadFailed;
        }
        catch (Exception e) when (e is IOException or ArgumentException or InvalidOperationException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Error: {e.Message}");
            return LoadFailed;
        }

        foreach (var project in result.Projects)
            Console.WriteLine($"{project.Name}: {project.Files.Count} files in {project.OutputDirectory}" + project.Mode switch
            {
                GenerationMode.Skipped => " (up to date)",
                GenerationMode.Incremental => " (updated)",
                _ => "",
            });
        if (result.Warnings.Count > 0)
        {
            Console.Error.WriteLine($"{result.Warnings.Count} warning(s){(verbose ? ":" : " (use --verbose to show them).")}");
            if (verbose)
                foreach (var warning in result.Warnings)
                    Console.Error.WriteLine("  " + warning);
        }
        if (result.SolutionGraph is not null)
            Console.WriteLine($"Graph database of the solution: {result.SolutionGraph}");
        Console.WriteLine($"Done in {result.Elapsed.TotalSeconds:0.0} s.");
        return result.Projects.Count == 0 ? LoadFailed : result.Warnings.Count > 0 ? CompletedWithWarnings : Success;
    }

    private sealed class SynchronousProgress<T>(Action<T> handler) : IProgress<T>
    {
        private readonly Lock _gate = new();

        public void Report(T value)
        {
            lock (_gate)
                handler(value);
        }
    }
}
