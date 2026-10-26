namespace Dotnetumentation.Core.Model;

/// <summary>Accessibility of a type or member, ordered from the least to the most visible.</summary>
public enum Visibility
{
    Private,
    Internal,
    Protected,
    Public,
}

public enum TypeKind
{
    Class,
    Record,
    Struct,
    RecordStruct,
    Interface,
    Enum,
    Delegate,
}

public enum MemberKind
{
    Field,
    Property,
    Event,
    Constructor,
    Method,
    EnumValue,
}

public enum RelationKind
{
    /// <summary>Inheritance from a base class (or a base interface).</summary>
    Generalization,
    /// <summary>A class implementing an interface.</summary>
    Realization,
    /// <summary>A field or property of the target type.</summary>
    Association,
    /// <summary>A field or property holding a collection of the target type.</summary>
    Aggregation,
    /// <summary>The target type used in a method signature.</summary>
    Dependency,
}

/// <summary>A C# project of the analyzed solution with its types.</summary>
public sealed record ProjectModel(
    string Name,
    string FilePath,
    string? TargetFramework,
    string AssemblyName,
    IReadOnlyList<string> ProjectReferences,
    IReadOnlyList<PackageReferenceModel> PackageReferences,
    IReadOnlyList<TypeModel> Types)
{
    /// <summary>Namespace pairs where code in the first namespace uses a type of the second one.</summary>
    public IReadOnlyList<NamespaceDependency> NamespaceDependencies { get; init; } = [];

    public string Directory => Path.GetDirectoryName(FilePath) ?? ".";

    public IEnumerable<string> Namespaces => Types.Select(t => t.Namespace).Distinct().Order(StringComparer.Ordinal);
}

/// <summary><paramref name="ToProject"/> is null when the target namespace belongs to the same project.</summary>
public sealed record NamespaceDependency(string From, string To, string? ToProject);

public sealed record PackageReferenceModel(string Name, string? Version);

public sealed record NamedDocumentation(string Name, string Text);

public sealed record DocumentationComment
{
    public string? Summary { get; init; }
    public string? Remarks { get; init; }
    public string? Returns { get; init; }
    public string? Value { get; init; }
    public IReadOnlyList<NamedDocumentation> Parameters { get; init; } = [];
    public IReadOnlyList<NamedDocumentation> TypeParameters { get; init; } = [];
    public IReadOnlyList<NamedDocumentation> Exceptions { get; init; } = [];
}

public sealed record TypeModel
{
    /// <summary>Unique id: the fully qualified metadata name (<c>Ns.Outer+Inner`1</c>).</summary>
    public required string Id { get; init; }
    /// <summary>The documentation comment id (<c>T:Ns.Outer.Inner`1</c>), the id in the graph database.</summary>
    public string? SymbolId { get; init; }
    /// <summary>Display name with generic parameters and outer types (<c>Outer.Inner&lt;T&gt;</c>).</summary>
    public required string Name { get; init; }
    /// <summary>Containing namespace, empty for the global namespace.</summary>
    public required string Namespace { get; init; }
    public required TypeKind Kind { get; init; }
    public required Visibility Visibility { get; init; }
    public bool IsAbstract { get; init; }
    public bool IsStatic { get; init; }
    public bool IsSealed { get; init; }
    public string? Summary { get; init; }
    public DocumentationComment Documentation { get; init; } = new();
    public string? Declaration { get; init; }
    public string? SourceFile { get; init; }
    public IReadOnlyList<MemberModel> Members { get; init; } = [];
    public IReadOnlyList<RelationModel> Relations { get; init; } = [];

    public string FullName => Namespace.Length == 0 ? Name : $"{Namespace}.{Name}";
}

public sealed record MemberModel
{
    public required MemberKind Kind { get; init; }
    public required string Name { get; init; }
    /// <summary>UML style text: <c>Name : Type</c> or <c>Name(p : T) : R</c>.</summary>
    public required string Signature { get; init; }
    public required Visibility Visibility { get; init; }
    public bool IsStatic { get; init; }
    public bool IsAbstract { get; init; }
    public DocumentationComment Documentation { get; init; } = new();
    public string? Declaration { get; init; }
    /// <summary>Unique within its type, used for file names of the method diagrams.</summary>
    public string? Id { get; init; }
    /// <summary>The documentation comment id (<c>M:Ns.Type.Place(Ns.Order)</c>), the id in the graph database.</summary>
    public string? SymbolId { get; init; }
}

public sealed record RelationModel(string FromId, string ToId, RelationKind Kind, string? Label = null);
