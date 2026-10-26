namespace Dotnetumentation.Core.Graph;

/// <summary>
/// A project, package, namespace, file, type or member in the graph database. Ids are documentation comment ids
/// (<c>T:Ns.Type`1</c>, <c>M:Ns.Type.Place(Ns.Order)</c>) or <c>N:</c>, <c>project:</c>, <c>package:</c>, <c>file:</c>
/// followed by the name.
/// </summary>
public sealed record GraphNode
{
    public required string Id { get; init; }
    /// <summary>One of <see cref="NodeKinds"/>.</summary>
    public required string Kind { get; init; }
    public required string Name { get; init; }
    public required string FullName { get; init; }
    /// <summary>The project defining the node; null for .NET and package code.</summary>
    public string? Project { get; init; }
    public string? Namespace { get; init; }
    public string? Visibility { get; init; }
    /// <summary>UML style signature of a member (<c>Place(order : Order) : decimal</c>); the version of a package.</summary>
    public string? Signature { get; init; }
    public string? Declaration { get; init; }
    public string? Summary { get; init; }
    /// <summary>
    /// Source file relative to the folder of the database file (<c>/</c> separators). The reading API of
    /// <see cref="GraphDatabase"/> returns it as a full path.
    /// </summary>
    public string? File { get; init; }
    /// <summary>1-based line of the declaration.</summary>
    public int? Line { get; init; }
    /// <summary>Not code of the solution: .NET or a package.</summary>
    public bool IsExternal { get; init; }
    /// <summary>Only referenced here, defined in another project or assembly: the node has its name only.</summary>
    public bool IsStub { get; init; }
}

/// <summary>A directed relation; <see cref="Label"/> is the member name of an association, the version of a package or empty.</summary>
public sealed record GraphEdge(string Source, string Target, string Kind, string Label = "");

/// <summary>A documentation page or diagram of a node; the path is relative to the folder of the database file.</summary>
public sealed record GraphDocument(string Node, string Kind, string Path);

/// <summary>The content of a graph database.</summary>
public sealed record CodeGraph(
    IReadOnlyDictionary<string, string> Meta,
    IReadOnlyList<GraphNode> Nodes,
    IReadOnlyList<GraphEdge> Edges,
    IReadOnlyList<GraphDocument> Documents);

/// <summary>A relation of a node as read from the database: the edge, its direction and the node at the other end.</summary>
public sealed record GraphRelation(GraphEdge Edge, bool IsOutgoing, GraphNode Other);

public static class NodeKinds
{
    public const string Project = "project";
    public const string Package = "package";
    public const string Namespace = "namespace";
    public const string File = "file";
    public const string Class = "class";
    public const string Record = "record";
    public const string Struct = "struct";
    public const string RecordStruct = "record struct";
    public const string Interface = "interface";
    public const string Enum = "enum";
    public const string Delegate = "delegate";
    public const string Method = "method";
    public const string Constructor = "constructor";
    public const string Property = "property";
    public const string Field = "field";
    public const string Event = "event";
    public const string EnumValue = "enum value";

    public static readonly IReadOnlyList<string> Types = [Class, Record, Struct, RecordStruct, Interface, Enum, Delegate];
    public static readonly IReadOnlyList<string> Members = [Method, Constructor, Property, Field, Event, EnumValue];
}

public static class EdgeKinds
{
    /// <summary>Project → namespace and file, namespace → type, type → member and nested type.</summary>
    public const string Contains = "contains";
    /// <summary>Type or member → the file declaring it.</summary>
    public const string DeclaredIn = "declared_in";
    public const string Inherits = "inherits";
    public const string Implements = "implements";
    /// <summary>A field, property or captured constructor parameter of the target type; label: the member.</summary>
    public const string Association = "association";
    /// <summary>A collection of the target type; label: the member.</summary>
    public const string Aggregation = "aggregation";
    /// <summary>The target type is used in a method signature.</summary>
    public const string Dependency = "dependency";
    /// <summary>Method, constructor or property → a method it calls.</summary>
    public const string Calls = "calls";
    /// <summary>Method, constructor or property → a constructor it calls with <c>new</c>.</summary>
    public const string Creates = "creates";
    /// <summary>Member → the base class member it overrides.</summary>
    public const string Overrides = "overrides";
    /// <summary>Member → the interface member it implements.</summary>
    public const string ImplementsMember = "implements_member";
    public const string ProjectReference = "project_reference";
    /// <summary>Project → package; label: the version.</summary>
    public const string PackageReference = "package_reference";
    /// <summary>Namespace → a namespace whose types its code uses.</summary>
    public const string UsesNamespace = "uses_namespace";
}

public static class DocumentKinds
{
    public const string Api = "api";
    public const string Class = "class";
    public const string Sequence = "sequence";
    public const string Activity = "activity";
}

/// <summary>Readable names of the relations, as seen from one node: <c>calls</c> on the caller, <c>called by</c> on the callee.</summary>
public static class RelationNames
{
    public static string Of(GraphRelation relation) => Of(relation.Edge.Kind, relation.IsOutgoing);

    public static string Of(string edgeKind, bool outgoing) => (edgeKind, outgoing) switch
    {
        (EdgeKinds.Contains, true) => "contains",
        (EdgeKinds.Contains, false) => "contained in",
        (EdgeKinds.DeclaredIn, true) => "declared in",
        (EdgeKinds.DeclaredIn, false) => "declares",
        (EdgeKinds.Inherits, true) => "inherits from",
        (EdgeKinds.Inherits, false) => "inherited by",
        (EdgeKinds.Implements, true) => "implements",
        (EdgeKinds.Implements, false) => "implemented by",
        (EdgeKinds.Association, true) => "references",
        (EdgeKinds.Association, false) => "referenced by",
        (EdgeKinds.Aggregation, true) => "holds collections of",
        (EdgeKinds.Aggregation, false) => "collected by",
        (EdgeKinds.Dependency, true) => "uses in signatures",
        (EdgeKinds.Dependency, false) => "used in signatures of",
        (EdgeKinds.Calls, true) => "calls",
        (EdgeKinds.Calls, false) => "called by",
        (EdgeKinds.Creates, true) => "creates",
        (EdgeKinds.Creates, false) => "created by",
        (EdgeKinds.Overrides, true) => "overrides",
        (EdgeKinds.Overrides, false) => "overridden by",
        (EdgeKinds.ImplementsMember, true) => "implements member",
        (EdgeKinds.ImplementsMember, false) => "implemented by member",
        (EdgeKinds.ProjectReference or EdgeKinds.PackageReference, true) => "references",
        (EdgeKinds.ProjectReference or EdgeKinds.PackageReference, false) => "referenced by",
        (EdgeKinds.UsesNamespace, true) => "uses namespace",
        (EdgeKinds.UsesNamespace, false) => "used by namespace",
        _ => outgoing ? edgeKind : edgeKind + " (incoming)",
    };

    /// <summary>The node kinds of a filter word: <c>type</c>, <c>member</c> or a node kind (<c>record-struct</c> for <c>record struct</c>).</summary>
    public static IReadOnlyList<string> KindsOf(string filter) => filter.Trim().ToLowerInvariant() switch
    {
        "type" or "types" => NodeKinds.Types,
        "member" or "members" => NodeKinds.Members,
        var kind => [kind.Replace('-', ' ')],
    };
}
