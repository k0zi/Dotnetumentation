using System.Reflection;
using Dotnetumentation.Core.Analysis;
using Dotnetumentation.Core.Generators;
using Dotnetumentation.Core.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using TypeKind = Dotnetumentation.Core.Model.TypeKind;

namespace Dotnetumentation.Core.Graph;

/// <summary>
/// Builds the graph database of one project: its namespaces, files, types and members (the documented ones, after the
/// visibility filter), their relations, the direct calls of the members and the documentation pages of each node.
/// </summary>
/// <param name="databaseDirectory">The folder of the database: the stored paths are relative to it.</param>
/// <param name="solutionDirectory">File ids (<c>file:</c>) are relative to it, so they are the same in every graph of the solution.</param>
internal sealed class GraphAnalyzer(Compilation compilation, TypeAnalyzer analyzer, IReadOnlyDictionary<string, string> projectByAssembly,
    string databaseDirectory, string solutionDirectory)
{
    private readonly Dictionary<string, GraphNode> _nodes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, GraphNode> _stubs = new(StringComparer.Ordinal);
    private readonly HashSet<GraphEdge> _edges = [];
    private readonly HashSet<GraphDocument> _documents = [];
    private readonly Dictionary<SyntaxTree, SemanticModel> _models = [];

    public static string ProjectId(string name) => "project:" + name;
    public static string PackageId(string name) => "package:" + name;
    public static string NamespaceId(string ns) => "N:" + DocumentationPaths.NamespaceTitle(ns);
    public static string TypeId(string typeId) => "T:" + typeId.Replace('+', '.');

    public CodeGraph Build(GeneratorContext context, CancellationToken cancellationToken)
    {
        var project = context.Project;
        var projectId = ProjectId(project.Name);
        Add(new GraphNode
        {
            Id = projectId, Kind = NodeKinds.Project, Name = project.Name, FullName = project.Name, Project = project.Name,
            Signature = project.TargetFramework, File = DatabasePath(project.FilePath),
        });
        foreach (var reference in project.ProjectReferences)
        {
            Stub(new GraphNode { Id = ProjectId(reference), Kind = NodeKinds.Project, Name = reference, FullName = reference, Project = reference, IsStub = true });
            Edge(projectId, ProjectId(reference), EdgeKinds.ProjectReference);
        }
        foreach (var package in project.PackageReferences)
        {
            Add(new GraphNode
            {
                Id = PackageId(package.Name), Kind = NodeKinds.Package, Name = package.Name, FullName = package.Name,
                Signature = package.Version, IsExternal = true,
            });
            Edge(projectId, PackageId(package.Name), EdgeKinds.PackageReference, package.Version ?? "");
        }
        foreach (var ns in project.Namespaces)
        {
            Add(new GraphNode
            {
                Id = NamespaceId(ns), Kind = NodeKinds.Namespace, Name = DocumentationPaths.NamespaceTitle(ns),
                FullName = DocumentationPaths.NamespaceTitle(ns), Project = project.Name, Namespace = ns.Length == 0 ? null : ns,
            });
            Edge(projectId, NamespaceId(ns), EdgeKinds.Contains);
        }
        foreach (var dependency in project.NamespaceDependencies)
        {
            Stub(new GraphNode
            {
                Id = NamespaceId(dependency.To), Kind = NodeKinds.Namespace, Name = DocumentationPaths.NamespaceTitle(dependency.To),
                FullName = DocumentationPaths.NamespaceTitle(dependency.To), Project = dependency.ToProject ?? project.Name, IsStub = true,
            });
            Edge(NamespaceId(dependency.From), NamespaceId(dependency.To), EdgeKinds.UsesNamespace);
        }

        var symbols = new Dictionary<string, ISymbol>(StringComparer.Ordinal);
        foreach (var type in analyzer.SourceTypes())
        {
            if (type.GetDocumentationCommentId() is { } typeId)
                symbols.TryAdd(typeId, type);
            foreach (var member in type.GetMembers())
                if (member.GetDocumentationCommentId() is { } memberId)
                    symbols.TryAdd(memberId, member);
        }

        var documentedTypes = project.Types.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        var sequences = context.SequenceLinks.ToHashSet();
        var activities = context.ActivityLinks.ToHashSet();
        foreach (var type in project.Types)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var id = type.SymbolId ?? TypeId(type.Id);
            var symbol = symbols.GetValueOrDefault(id) as INamedTypeSymbol;
            var location = Location(symbol);
            Add(new GraphNode
            {
                Id = id, Kind = KindName(type.Kind), Name = type.Name, FullName = type.FullName, Project = project.Name,
                Namespace = type.Namespace.Length == 0 ? null : type.Namespace, Visibility = Lower(type.Visibility),
                Declaration = type.Declaration, Summary = type.Documentation.Summary ?? type.Summary,
                File = location?.File, Line = location?.Line,
            });
            // A nested type belongs to its documented outer type, any other type to its namespace.
            var outer = symbol?.ContainingType is { } containing && documentedTypes.Contains(Symbols.TypeId(containing))
                ? containing.GetDocumentationCommentId()
                : null;
            Edge(outer ?? NamespaceId(type.Namespace), id, EdgeKinds.Contains);
            DeclaredIn(id, symbol, project.Name);
            Document(id, DocumentKinds.Api, DocumentationPaths.TypeReference(type.Id));
            if (context.Diagrams.HasFlag(DiagramKinds.Class))
                Document(id, DocumentKinds.Class, DocumentationPaths.ClassDiagram(type.Namespace));

            foreach (var relation in type.Relations)
            {
                var target = TypeId(relation.ToId);
                Edge(id, target, RelationKindName(relation.Kind), relation.Label ?? "");
                if (context.SolutionTypes.GetValueOrDefault(relation.ToId) is { } known)
                    Stub(new GraphNode
                    {
                        Id = target, Kind = KindName(known.Kind), Name = known.Name, FullName = known.FullName,
                        Project = ProjectOf(compilation.GetTypeByMetadataName(relation.ToId)),
                        Namespace = known.Namespace.Length == 0 ? null : known.Namespace, IsStub = true,
                    });
                else if (compilation.GetTypeByMetadataName(relation.ToId) is { } targetSymbol)
                    StubFor(targetSymbol);
            }

            var implemented = symbol is null ? [] : ImplementedMembers(symbol);
            foreach (var member in type.Members)
            {
                if (member.SymbolId is not { } memberId)
                    continue;
                var memberSymbol = symbols.GetValueOrDefault(memberId);
                var memberLocation = Location(memberSymbol);
                Add(new GraphNode
                {
                    Id = memberId, Kind = KindName(member.Kind), Name = member.Name, FullName = $"{type.FullName}.{member.Id ?? member.Name}",
                    Project = project.Name, Namespace = type.Namespace.Length == 0 ? null : type.Namespace,
                    Visibility = Lower(member.Visibility), Signature = member.Signature, Declaration = member.Declaration,
                    Summary = member.Documentation.Summary, File = memberLocation?.File, Line = memberLocation?.Line,
                });
                Edge(id, memberId, EdgeKinds.Contains);
                DeclaredIn(memberId, memberSymbol, project.Name);
                Document(memberId, DocumentKinds.Api, DocumentationPaths.TypeReference(type.Id));
                if (member.Id is { } methodId)
                {
                    var method = new MethodDiagram(type.Namespace, type.Name, methodId);
                    if (sequences.Contains(method))
                        Document(memberId, DocumentKinds.Sequence, DocumentationPaths.Sequence(type.Name, type.Namespace, methodId));
                    if (activities.Contains(method))
                        Document(memberId, DocumentKinds.Activity, DocumentationPaths.Activity(type.Name, type.Namespace, methodId));
                }
                if (memberSymbol is null)
                    continue;

                if (Overridden(memberSymbol) is { } overridden && StubFor(overridden) is { } overriddenId)
                    Edge(memberId, overriddenId, EdgeKinds.Overrides);
                foreach (var interfaceMember in implemented.GetValueOrDefault(memberSymbol) ?? [])
                    if (StubFor(interfaceMember) is { } interfaceMemberId)
                        Edge(memberId, interfaceMemberId, EdgeKinds.ImplementsMember);
                Calls(memberId, memberSymbol, cancellationToken);
            }
        }

        foreach (var (stubId, stub) in _stubs)
            _nodes.TryAdd(stubId, stub);

        var meta = new Dictionary<string, string>
        {
            ["schema_version"] = GraphDatabase.SchemaVersion.ToString(),
            ["tool_version"] = ToolVersion,
            ["scope"] = "project",
            ["project"] = project.Name,
            ["assembly"] = project.AssemblyName,
        };
        if (project.TargetFramework is not null)
            meta["target_framework"] = project.TargetFramework;
        return new CodeGraph(meta, _nodes.Values.ToList(), _edges.ToList(), _documents.ToList());
    }

    public static string ToolVersion { get; } =
        typeof(GraphAnalyzer).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";

    /// <summary>The methods a member calls and the constructors it calls with <c>new</c>; lambdas and local functions included.</summary>
    private void Calls(string memberId, ISymbol member, CancellationToken cancellationToken)
    {
        foreach (var reference in member.DeclaringSyntaxReferences)
        {
            var syntax = reference.GetSyntax(cancellationToken);
            if (ModelOf(syntax.SyntaxTree) is not { } model)
                continue;
            // A primary constructor is declared by the whole type: only its base constructor call is its own code.
            var nodes = syntax is TypeDeclarationSyntax type
                ? type.BaseList?.Types.OfType<PrimaryConstructorBaseTypeSyntax>().SelectMany(b => b.DescendantNodesAndSelf()) ?? []
                : syntax.DescendantNodes();
            foreach (var node in nodes)
            {
                if (node is not (InvocationExpressionSyntax or BaseObjectCreationExpressionSyntax or ConstructorInitializerSyntax
                    or PrimaryConstructorBaseTypeSyntax))
                    continue;
                var info = model.GetSymbolInfo(node, cancellationToken);
                if ((info.Symbol ?? info.CandidateSymbols.FirstOrDefault()) is not IMethodSymbol called)
                    continue;
                called = (called.ReducedFrom ?? called).OriginalDefinition;
                if (called.MethodKind is MethodKind.DelegateInvoke or MethodKind.LocalFunction or MethodKind.AnonymousFunction || called.ContainingType is null)
                    continue;
                if (StubFor(called) is { } calledId)
                    Edge(memberId, calledId, node is BaseObjectCreationExpressionSyntax ? EdgeKinds.Creates : EdgeKinds.Calls);
            }
        }
    }

    /// <summary>For every member of the type, the interface members it implements.</summary>
    private static Dictionary<ISymbol, List<ISymbol>> ImplementedMembers(INamedTypeSymbol type)
    {
        var result = new Dictionary<ISymbol, List<ISymbol>>(SymbolEqualityComparer.Default);
        foreach (var @interface in type.AllInterfaces)
        foreach (var interfaceMember in @interface.GetMembers())
        {
            if (type.FindImplementationForInterfaceMember(interfaceMember) is not { } implementation
                || !SymbolEqualityComparer.Default.Equals(implementation.ContainingType, type))
                continue;
            if (!result.TryGetValue(implementation, out var list))
                result[implementation] = list = [];
            list.Add(interfaceMember.OriginalDefinition);
        }
        return result;
    }

    private static ISymbol? Overridden(ISymbol member) => member switch
    {
        IMethodSymbol m => m.OverriddenMethod,
        IPropertySymbol p => p.OverriddenProperty,
        IEventSymbol e => e.OverriddenEvent,
        _ => null,
    };

    /// <summary>Adds a name-only node for a symbol defined elsewhere (unless it is defined here); null when it has no id.</summary>
    private string? StubFor(ISymbol symbol)
    {
        symbol = symbol.OriginalDefinition;
        if (symbol.GetDocumentationCommentId() is not { } id)
            return null;
        if (_nodes.ContainsKey(id) || _stubs.ContainsKey(id))
            return id;
        var type = symbol as INamedTypeSymbol ?? symbol.ContainingType;
        var typeName = type?.ToDisplayString(Symbols.TypeNameFormat);
        var ns = type is null ? "" : Symbols.NamespaceName(type);
        var qualifiedType = ns.Length == 0 ? typeName : $"{ns}.{typeName}";
        var (kind, name, fullName) = symbol switch
        {
            INamedTypeSymbol named => (KindName(Symbols.KindOf(named)), typeName!, qualifiedType!),
            IMethodSymbol method => (method.MethodKind == MethodKind.Constructor ? NodeKinds.Constructor : NodeKinds.Method,
                method.MethodKind == MethodKind.Constructor ? method.ContainingType.Name : method.Name,
                $"{qualifiedType}.{TypeAnalyzer.MethodId(method)}"),
            IPropertySymbol => (NodeKinds.Property, symbol.Name, $"{qualifiedType}.{symbol.Name}"),
            IEventSymbol => (NodeKinds.Event, symbol.Name, $"{qualifiedType}.{symbol.Name}"),
            IFieldSymbol => (NodeKinds.Field, symbol.Name, $"{qualifiedType}.{symbol.Name}"),
            _ => (symbol.Kind.ToString().ToLowerInvariant(), symbol.Name, symbol.ToDisplayString()),
        };
        var location = Location(symbol);
        Stub(new GraphNode
        {
            Id = id, Kind = kind, Name = name, FullName = fullName, Project = ProjectOf(symbol),
            Namespace = ns.Length == 0 ? null : ns, Signature = symbol is IMethodSymbol m ? m.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat) : null,
            File = location?.File, Line = location?.Line,
            IsExternal = !Symbols.IsSolutionType(type), IsStub = true,
        });
        return id;
    }

    private string? ProjectOf(ISymbol? symbol) =>
        symbol?.ContainingAssembly is { } assembly && Symbols.IsSolutionType(symbol as INamedTypeSymbol ?? symbol.ContainingType)
            ? projectByAssembly.GetValueOrDefault(assembly.Name, assembly.Name)
            : null;

    private void DeclaredIn(string id, ISymbol? symbol, string project)
    {
        foreach (var path in SourceFiles(symbol))
        {
            var relative = Path.GetRelativePath(solutionDirectory, path).Replace('\\', '/');
            var fileId = "file:" + relative;
            Add(new GraphNode
            {
                Id = fileId, Kind = NodeKinds.File, Name = Path.GetFileName(path), FullName = relative,
                Project = project, File = DatabasePath(path),
            });
            Edge(ProjectId(project), fileId, EdgeKinds.Contains);
            Edge(id, fileId, EdgeKinds.DeclaredIn);
        }
    }

    private static IEnumerable<string> SourceFiles(ISymbol? symbol) =>
        (symbol?.Locations ?? []).Where(l => l.IsInSource).Select(l => l.SourceTree!.FilePath)
            .Where(p => p.Length > 0 && !Symbols.IsGeneratedFile(p)).Distinct().Order(StringComparer.Ordinal);

    /// <summary>The first declaration in source; partial declarations are ordered by file, so the choice is stable.</summary>
    private (string File, int Line)? Location(ISymbol? symbol)
    {
        var location = symbol?.Locations.Where(l => l.IsInSource && l.SourceTree!.FilePath.Length > 0)
            .OrderBy(l => Symbols.IsGeneratedFile(l.SourceTree!.FilePath))
            .ThenBy(l => l.SourceTree!.FilePath, StringComparer.Ordinal).ThenBy(l => l.SourceSpan.Start).FirstOrDefault();
        return location is null ? null : (DatabasePath(location.SourceTree!.FilePath), location.GetLineSpan().StartLinePosition.Line + 1);
    }

    private string DatabasePath(string path) => Path.GetRelativePath(databaseDirectory, path).Replace('\\', '/');

    private SemanticModel? ModelOf(SyntaxTree tree)
    {
        if (_models.TryGetValue(tree, out var model))
            return model;
        return compilation.SyntaxTrees.Contains(tree) ? _models[tree] = compilation.GetSemanticModel(tree) : null;
    }

    private void Add(GraphNode node) => _nodes.TryAdd(node.Id, node);

    private void Stub(GraphNode node)
    {
        if (!_nodes.ContainsKey(node.Id))
            _stubs.TryAdd(node.Id, node);
    }

    private void Edge(string source, string target, string kind, string label = "") => _edges.Add(new GraphEdge(source, target, kind, label));

    private void Document(string node, string kind, string path) => _documents.Add(new GraphDocument(node, kind, path));

    private static string? Lower(Visibility visibility) => visibility.ToString().ToLowerInvariant();

    public static string KindName(TypeKind kind) => kind switch
    {
        TypeKind.RecordStruct => NodeKinds.RecordStruct,
        _ => kind.ToString().ToLowerInvariant(),
    };

    public static string KindName(MemberKind kind) => kind switch
    {
        MemberKind.EnumValue => NodeKinds.EnumValue,
        _ => kind.ToString().ToLowerInvariant(),
    };

    private static string RelationKindName(RelationKind kind) => kind switch
    {
        RelationKind.Generalization => EdgeKinds.Inherits,
        RelationKind.Realization => EdgeKinds.Implements,
        RelationKind.Association => EdgeKinds.Association,
        RelationKind.Aggregation => EdgeKinds.Aggregation,
        _ => EdgeKinds.Dependency,
    };
}
