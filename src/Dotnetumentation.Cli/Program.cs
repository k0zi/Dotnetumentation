using System.CommandLine;
using Dotnetumentation.Core;
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
        var verbose = new Option<bool>("--verbose")
        {
            Description = "Show every progress message and the load warnings.",
        };

        var generate = new Command("generate", "Generate the PlantUML documentation of every C# project of a solution.")
        {
            input, diagrams, visibility, depth, maxMessages, include, exclude, outputName, clean, verbose,
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
        }, result.GetValue(verbose), cancellationToken));

        return new RootCommand("Dotnetumentation: static UML (PlantUML) documentation of .NET solutions.") { generate };
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
            Console.WriteLine($"{project.Name}: {project.Files.Count} files in {project.OutputDirectory}");
        if (result.Warnings.Count > 0)
        {
            Console.Error.WriteLine($"{result.Warnings.Count} warning(s){(verbose ? ":" : " (use --verbose to show them).")}");
            if (verbose)
                foreach (var warning in result.Warnings)
                    Console.Error.WriteLine("  " + warning);
        }
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
