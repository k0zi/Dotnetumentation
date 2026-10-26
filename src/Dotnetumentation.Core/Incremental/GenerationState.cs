using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dotnetumentation.Core.Incremental;

/// <summary>A sequence or activity diagram of the last run and the source files it was made from.</summary>
public sealed record MethodDiagramState(string Namespace, string TypeName, string MethodId, IReadOnlyList<string> Sources);

/// <summary>
/// What the last generation of a project was made from, saved next to its documentation, so the next run can update
/// only what changed. Source paths are relative to the git repository root, with <c>/</c> separators.
/// </summary>
public sealed record GenerationState
{
    public const string FileName = ".dotnetumentation-state.json";
    public const int CurrentVersion = 1;

    public int Version { get; init; } = CurrentVersion;

    /// <summary>The options and the tool version: documentation made with other ones is generated again.</summary>
    public required string Fingerprint { get; init; }

    /// <summary>The checked out commit at the generation; null outside a git repository.</summary>
    public string? Commit { get; init; }

    /// <summary>Source files that differed from <see cref="Commit"/> at the generation.</summary>
    public IReadOnlyList<string> Dirty { get; init; } = [];

    public IReadOnlyList<MethodDiagramState> Sequences { get; init; } = [];
    public IReadOnlyList<MethodDiagramState> Activities { get; init; } = [];

    /// <summary>Git compares paths by case, Windows file systems do not.</summary>
    public static StringComparer PathComparer { get; } = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static string FingerprintOf(GeneratorOptions options)
    {
        var version = typeof(GenerationState).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return $"{version};{options.Diagrams};{options.MinVisibility};{options.SequenceDepth?.ToString() ?? "all"};{options.MaxSequenceMessages};{(options.Graph ? "graph" : "")}";
    }

    /// <summary>Null when there is no state file or it cannot be read.</summary>
    public static GenerationState? Load(string outputDirectory)
    {
        try
        {
            var path = Path.Combine(outputDirectory, FileName);
            if (!File.Exists(path))
                return null;
            var state = JsonSerializer.Deserialize(File.ReadAllText(path), GenerationStateJson.Default.GenerationState);
            return state?.Version == CurrentVersion ? state : null;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            // A broken state file only means the project is generated from scratch.
            return null;
        }
    }

    /// <summary>The same documentation, now matching another commit and working tree.</summary>
    public GenerationState With(string? commit, IReadOnlyList<string> dirty) => this with { Commit = commit, Dirty = dirty };

    /// <summary>Writes the state file, unless it already has this content.</summary>
    public void Save(string outputDirectory)
    {
        var path = Path.Combine(outputDirectory, FileName);
        var json = JsonSerializer.Serialize(this, GenerationStateJson.Default.GenerationState) + "\n";
        if (File.Exists(path) && File.ReadAllText(path) == json)
            return;
        Directory.CreateDirectory(outputDirectory);
        File.WriteAllText(path, json);
    }

    /// <summary>Files that can change the documentation: C# sources and MSBuild files. Everything else is ignored.</summary>
    public static bool IsSource(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".cs" or ".csproj" || IsSolutionLevel(path);

    /// <summary>Build files that can change every project: a change of one of them generates everything again.</summary>
    public static bool IsSolutionLevel(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".sln" or ".slnx" or ".props" or ".targets"
        || Path.GetFileName(path).ToLowerInvariant() is "global.json" or "nuget.config";
}

[JsonSourceGenerationOptions(WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(GenerationState))]
internal sealed partial class GenerationStateJson : JsonSerializerContext;
