namespace Dotnetumentation.Core.Model;

/// <summary>
/// A method body as a tree of control flow statements, the input of an activity diagram.
/// <paramref name="MethodId"/> is the method name with parameter types, unique within the type.
/// </summary>
public sealed record ActivityModel(string Namespace, string TypeName, string MethodId, IReadOnlyList<ActivityNode> Body);

public abstract record ActivityNode;

public sealed record ActionNode(string Text) : ActivityNode;

/// <summary><c>if</c> / <c>else if</c> chain; a branch with a null condition is the <c>else</c>.</summary>
public sealed record IfNode(IReadOnlyList<ActivityBranch> Branches) : ActivityNode;

public sealed record ActivityBranch(string? Condition, string? Label, IReadOnlyList<ActivityNode> Body);

/// <summary>A loop that tests before the body (<c>while</c>, <c>for</c>, <c>foreach</c>).</summary>
public sealed record WhileNode(string Condition, string? Label, IReadOnlyList<ActivityNode> Body) : ActivityNode;

/// <summary>A loop that tests after the body (<c>do … while</c>).</summary>
public sealed record RepeatNode(string Condition, IReadOnlyList<ActivityNode> Body) : ActivityNode;

public sealed record TryNode(
    IReadOnlyList<ActivityNode> Body,
    IReadOnlyList<ActivityBranch> Catches,
    IReadOnlyList<ActivityNode>? Finally) : ActivityNode;

/// <summary>Normal end of the flow (<c>return</c>).</summary>
public sealed record StopNode(string? Text) : ActivityNode;

/// <summary>Abnormal end of the flow (<c>throw</c>).</summary>
public sealed record EndNode(string? Text) : ActivityNode;

/// <summary>Call graph of a method, the input of a sequence diagram.</summary>
public sealed record SequenceModel(
    string Namespace,
    string TypeName,
    string MethodId,
    IReadOnlyList<SequenceParticipant> Participants,
    IReadOnlyList<SequenceStep> Steps)
{
    /// <summary>The source files the diagram depends on: the walked method bodies and the declarations of the called methods and participants.</summary>
    public IReadOnlyList<string> SourceFiles { get; init; } = [];
}

/// <summary>A method with a sequence or activity diagram, as linked from the index and the reference pages.</summary>
public sealed record MethodDiagram(string Namespace, string TypeName, string MethodId);

public enum ParticipantKind
{
    Participant,
    Interface,
    /// <summary>A type of a referenced assembly (.NET, NuGet package): its calls are shown, not followed.</summary>
    External,
}

public sealed record SequenceParticipant(string Id, string Name, ParticipantKind Kind);

public abstract record SequenceStep;

/// <summary>A call (<see cref="IsReturn"/> false) or the value returned from a call.</summary>
public sealed record SequenceMessage(string? From, string To, string Text, bool IsReturn = false, bool IsAsync = false, bool IsCreate = false)
    : SequenceStep;

public enum FragmentKind
{
    Alt,
    Opt,
    Loop,
    Group,
}

/// <summary><c>alt</c>/<c>loop</c>/… block; the sections after the first are the <c>else</c> parts.</summary>
public sealed record SequenceFragment(FragmentKind Kind, IReadOnlyList<SequenceSection> Sections) : SequenceStep;

public sealed record SequenceSection(string? Label, IReadOnlyList<SequenceStep> Steps);
