using Dotnetumentation.Core.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Dotnetumentation.Core.Analysis;

/// <summary>Reads the types, members and type relations of one compiled project.</summary>
internal sealed class TypeAnalyzer(Compilation compilation, Visibility minVisibility)
{
    public IReadOnlyList<TypeModel> Analyze(CancellationToken cancellationToken)
    {
        var result = new List<TypeModel>();
        foreach (var type in SourceTypes())
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.Add(CreateType(type));
        }
        return result.OrderBy(t => t.Namespace, StringComparer.Ordinal).ThenBy(t => t.Name, StringComparer.Ordinal).ToList();
    }

    /// <summary>The documented types of the project, nested types included.</summary>
    public IEnumerable<INamedTypeSymbol> SourceTypes()
    {
        var stack = new Stack<INamespaceOrTypeSymbol>();
        stack.Push(compilation.Assembly.GlobalNamespace);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (current is INamespaceSymbol ns)
                foreach (var member in ns.GetMembers())
                    stack.Push(member);
            if (current is not INamedTypeSymbol type)
                continue;
            foreach (var nested in type.GetTypeMembers())
                stack.Push(nested);
            if (IsDocumented(type))
                yield return type;
        }
    }

    private bool IsDocumented(INamedTypeSymbol type) =>
        !type.IsImplicitlyDeclared
        && type.CanBeReferencedByName
        && SymbolEqualityComparer.Default.Equals(type.ContainingAssembly, compilation.Assembly)
        && Symbols.IsSolutionType(type)
        && Symbols.EffectiveVisibility(type) >= minVisibility;

    private TypeModel CreateType(INamedTypeSymbol type) => new()
    {
        Id = Symbols.TypeId(type),
        SymbolId = type.GetDocumentationCommentId(),
        Name = type.ToDisplayString(Symbols.TypeNameFormat),
        Namespace = Symbols.NamespaceName(type),
        Kind = Symbols.KindOf(type),
        Visibility = Symbols.VisibilityOf(type),
        IsAbstract = type.IsAbstract && type.TypeKind == Microsoft.CodeAnalysis.TypeKind.Class,
        IsStatic = type.IsStatic,
        IsSealed = type.IsSealed && type.TypeKind == Microsoft.CodeAnalysis.TypeKind.Class,
        Summary = Symbols.Summary(type),
        Documentation = XmlDocumentation.Read(type),
        Declaration = type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
        SourceFile = type.Locations.FirstOrDefault(l => l.IsInSource)?.SourceTree?.FilePath,
        Members = Members(type),
        Relations = Relations(type),
    };

    private List<MemberModel> Members(INamedTypeSymbol type)
    {
        var members = new List<MemberModel>();
        if (type.TypeKind == Microsoft.CodeAnalysis.TypeKind.Enum)
        {
            foreach (var field in type.GetMembers().OfType<IFieldSymbol>().Where(f => f.HasConstantValue))
                members.Add(new MemberModel { Kind = MemberKind.EnumValue, Name = field.Name, Signature = field.Name, Visibility = Visibility.Public, Documentation = XmlDocumentation.Read(field), Declaration = field.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat), SymbolId = field.GetDocumentationCommentId() });
            return members;
        }
        if (type.TypeKind == Microsoft.CodeAnalysis.TypeKind.Delegate)
        {
            if (type.DelegateInvokeMethod is { } invoke)
                members.Add(Method(invoke, "Invoke"));
            return members;
        }

        foreach (var member in SourceOrder(type.GetMembers()))
        {
            if (member.IsImplicitlyDeclared || !member.CanBeReferencedByName && member is not IMethodSymbol { MethodKind: MethodKind.Constructor }
                || Symbols.VisibilityOf(member) < minVisibility || Symbols.IsGenerated(member))
                continue;
            var model = member switch
            {
                IFieldSymbol field => new MemberModel
                {
                    Kind = MemberKind.Field, Name = field.Name, Signature = $"{field.Name} : {Symbols.ShortName(field.Type)}",
                    Visibility = Symbols.VisibilityOf(field), IsStatic = field.IsStatic || field.IsConst,
                },
                IPropertySymbol property => new MemberModel
                {
                    Kind = MemberKind.Property, Name = property.Name,
                    Signature = property.IsIndexer
                        ? $"this[{string.Join(", ", property.Parameters.Select(p => Symbols.ShortName(p.Type)))}] : {Symbols.ShortName(property.Type)}"
                        : $"{property.Name} : {Symbols.ShortName(property.Type)}",
                    Visibility = Symbols.VisibilityOf(property), IsStatic = property.IsStatic,
                    IsAbstract = property.IsAbstract && type.TypeKind != Microsoft.CodeAnalysis.TypeKind.Interface,
                },
                IEventSymbol @event => new MemberModel
                {
                    Kind = MemberKind.Event, Name = @event.Name, Signature = $"<<event>> {@event.Name} : {Symbols.ShortName(@event.Type)}",
                    Visibility = Symbols.VisibilityOf(@event), IsStatic = @event.IsStatic,
                },
                IMethodSymbol { MethodKind: MethodKind.Constructor } ctor => Method(ctor, type.Name),
                IMethodSymbol { MethodKind: MethodKind.Ordinary or MethodKind.UserDefinedOperator or MethodKind.Conversion } method =>
                    Method(method, method.Name),
                _ => null,
            };
            if (model is not null)
                members.Add(model with
                {
                    Documentation = XmlDocumentation.Read(member),
                    Declaration = member.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                    SymbolId = member.GetDocumentationCommentId(),
                });
        }
        return members.OrderBy(m => m.Kind).ToList();
    }

    /// <summary>
    /// Members in source order. The order of <see cref="INamespaceOrTypeSymbol.GetMembers()"/> follows the order of the
    /// files of partial types, which may change between runs.
    /// </summary>
    public static IEnumerable<ISymbol> SourceOrder(IEnumerable<ISymbol> members) =>
        members.Select(m => (Member: m, Location: m.Locations.FirstOrDefault(l => l.IsInSource)))
            .OrderBy(x => x.Location?.SourceTree?.FilePath, StringComparer.Ordinal)
            .ThenBy(x => x.Location?.SourceSpan.Start ?? 0)
            .ThenBy(x => x.Member.Name, StringComparer.Ordinal)
            .Select(x => x.Member);

    private static MemberModel Method(IMethodSymbol method, string name)
    {
        var generics = method.TypeParameters.Length == 0 ? "" : $"<{string.Join(", ", method.TypeParameters.Select(t => t.Name))}>";
        var parameters = string.Join(", ", method.Parameters.Select(p => $"{p.Name} : {Symbols.ShortName(p.Type)}"));
        var signature = $"{name}{generics}({parameters})";
        if (method.MethodKind != MethodKind.Constructor)
            signature += $" : {Symbols.ShortName(method.ReturnType)}";
        return new MemberModel
        {
            Kind = method.MethodKind == MethodKind.Constructor ? MemberKind.Constructor : MemberKind.Method,
            Name = name,
            Signature = signature,
            Visibility = Symbols.VisibilityOf(method),
            IsStatic = method.IsStatic,
            IsAbstract = method.IsAbstract && method.ContainingType.TypeKind != Microsoft.CodeAnalysis.TypeKind.Interface,
            Id = MethodId(method),
            SymbolId = method.GetDocumentationCommentId(),
            Documentation = XmlDocumentation.Read(method),
            Declaration = method.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
        };
    }

    /// <summary><c>Name(int, Order)</c>: unique within the type, also for overloads.</summary>
    public static string MethodId(IMethodSymbol method)
    {
        var name = method.MethodKind == MethodKind.Constructor ? method.ContainingType.Name : method.Name;
        if (method.Arity > 0)
            name += "`" + method.Arity;
        return $"{name}({string.Join(", ", method.Parameters.Select(p => Symbols.ShortName(p.Type)))})";
    }

    private static List<RelationModel> Relations(INamedTypeSymbol type)
    {
        var id = Symbols.TypeId(type);
        var relations = new List<RelationModel>();
        var targets = new HashSet<string>();

        void Add(INamedTypeSymbol target, RelationKind kind, string? label = null)
        {
            var targetId = Symbols.TypeId(target);
            relations.Add(new RelationModel(id, targetId, kind, label));
            targets.Add(targetId);
        }

        if (type.BaseType is { } baseType && Symbols.IsSolutionType(baseType))
            Add(baseType.OriginalDefinition, RelationKind.Generalization);
        foreach (var @interface in type.Interfaces.Where(Symbols.IsSolutionType))
            Add(@interface.OriginalDefinition,
                type.TypeKind == Microsoft.CodeAnalysis.TypeKind.Interface ? RelationKind.Generalization : RelationKind.Realization);

        if (type.TypeKind is Microsoft.CodeAnalysis.TypeKind.Enum)
            return relations;

        foreach (var member in SourceOrder(type.GetMembers()).Where(m => !Symbols.IsGenerated(m)))
        {
            var (memberType, name) = member switch
            {
                IFieldSymbol { IsImplicitlyDeclared: false } f => (f.Type, f.Name),
                IPropertySymbol p => (p.Type, p.IsIndexer ? "this[]" : p.Name),
                _ => ((ITypeSymbol?)null, ""),
            };
            if (memberType is null)
                continue;
            foreach (var (target, isMany) in Symbols.ReferencedTypes(memberType).Distinct())
            {
                if (SymbolEqualityComparer.Default.Equals(target, type.OriginalDefinition) && !isMany && member is IFieldSymbol { IsConst: true })
                    continue;
                Add(target, isMany ? RelationKind.Aggregation : RelationKind.Association, name);
            }
        }

        // Parameters of a primary constructor (not of a record) are captured in hidden fields.
        if (!type.IsRecord && PrimaryConstructor(type) is { } primary)
        {
            foreach (var parameter in primary.Parameters)
            foreach (var (target, isMany) in Symbols.ReferencedTypes(parameter.Type).Distinct())
                Add(target, isMany ? RelationKind.Aggregation : RelationKind.Association, parameter.Name);
        }

        // Types only used in signatures: a dependency, unless there is already a stronger relation.
        foreach (var method in SourceOrder(type.GetMembers()).OfType<IMethodSymbol>().Where(m => !m.IsImplicitlyDeclared && !Symbols.IsGenerated(m)))
        {
            var used = method.Parameters.Select(p => p.Type).Append(method.ReturnType);
            foreach (var (target, _) in used.SelectMany(t => Symbols.ReferencedTypes(t)))
            {
                var targetId = Symbols.TypeId(target);
                if (targetId != id && targets.Add(targetId))
                    relations.Add(new RelationModel(id, targetId, RelationKind.Dependency));
            }
        }
        return relations.Distinct().ToList();
    }

    private static IMethodSymbol? PrimaryConstructor(INamedTypeSymbol type) =>
        type.InstanceConstructors.FirstOrDefault(c => c.DeclaringSyntaxReferences.Any(r => r.GetSyntax() is TypeDeclarationSyntax));

    /// <summary>Which namespaces the code of each namespace uses, found from every name in the source.</summary>
    public IReadOnlyList<NamespaceDependency> NamespaceDependencies(IReadOnlyDictionary<string, string> projectByAssembly,
        CancellationToken cancellationToken)
    {
        var result = new HashSet<NamespaceDependency>();
        foreach (var tree in compilation.SyntaxTrees)
        {
            if (Symbols.IsGeneratedFile(tree.FilePath))
                continue;
            cancellationToken.ThrowIfCancellationRequested();
            var model = compilation.GetSemanticModel(tree);
            var root = tree.GetRoot(cancellationToken);
            foreach (var declaration in root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
            {
                if (declaration.Parent is BaseTypeDeclarationSyntax || model.GetDeclaredSymbol(declaration, cancellationToken) is not { } owner)
                    continue;
                var from = Symbols.NamespaceName(owner);
                foreach (var name in declaration.DescendantNodes().OfType<SimpleNameSyntax>())
                {
                    var symbol = model.GetSymbolInfo(name, cancellationToken).Symbol;
                    var used = symbol as INamedTypeSymbol ?? symbol?.ContainingType;
                    if (used is null || !Symbols.IsSolutionType(used))
                        continue;
                    var to = Symbols.NamespaceName(used);
                    var sameProject = SymbolEqualityComparer.Default.Equals(used.ContainingAssembly, compilation.Assembly);
                    if (sameProject && to == from)
                        continue;
                    var project = sameProject ? null : projectByAssembly.GetValueOrDefault(used.ContainingAssembly.Name, used.ContainingAssembly.Name);
                    result.Add(new NamespaceDependency(from, to, project));
                }
            }
        }
        return result.OrderBy(d => d.From, StringComparer.Ordinal).ThenBy(d => d.ToProject, StringComparer.Ordinal)
            .ThenBy(d => d.To, StringComparer.Ordinal).ToList();
    }
}
