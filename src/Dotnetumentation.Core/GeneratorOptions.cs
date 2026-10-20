using Dotnetumentation.Core.Model;

namespace Dotnetumentation.Core;

[Flags]
public enum DiagramKinds
{
    None = 0,
    Class = 1,
    Component = 2,
    Sequence = 4,
    Activity = 8,
    All = Class | Component | Sequence | Activity,
}

public sealed record GeneratorOptions
{
    /// <summary>The <c>.sln</c>, <c>.slnx</c> or <c>.csproj</c> file to document.</summary>
    public required string InputPath { get; init; }

    public DiagramKinds Diagrams { get; init; } = DiagramKinds.All;

    /// <summary>Types and members less visible than this are left out of the diagrams.</summary>
    public Visibility MinVisibility { get; init; } = Visibility.Internal;

    /// <summary>
    /// How deep the sequence diagrams follow calls into other methods of the solution; null follows them to the end of
    /// the own code. Calls into .NET and other libraries are always shown, but never followed.
    /// </summary>
    public int? SequenceDepth { get; init; }

    /// <summary>
    /// A sequence diagram is cut after this many messages, as a very large one is not readable; 0 means no limit.
    /// </summary>
    public int MaxSequenceMessages { get; init; } = DefaultMaxSequenceMessages;

    /// <summary>Project name patterns (<c>*</c>, <c>?</c>) to document; empty means every project.</summary>
    public IReadOnlyList<string> Include { get; init; } = [];

    /// <summary>Project name patterns to skip.</summary>
    public IReadOnlyList<string> Exclude { get; init; } = [];

    /// <summary>Name of the folder created next to each project file.</summary>
    public string OutputDirectoryName { get; init; } = DefaultOutputDirectoryName;

    /// <summary>Deletes the output folder of a project before writing it, so diagrams of removed code disappear.</summary>
    public bool Clean { get; init; }

    public const string DefaultOutputDirectoryName = "docs.net";
    public const int DefaultMaxSequenceMessages = 500;
}
