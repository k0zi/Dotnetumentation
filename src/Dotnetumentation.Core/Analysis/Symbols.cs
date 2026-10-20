using System.Xml.Linq;
using Dotnetumentation.Core.Model;
using Microsoft.CodeAnalysis;
using TypeKind = Dotnetumentation.Core.Model.TypeKind;

namespace Dotnetumentation.Core.Analysis;

/// <summary>Conversions from Roslyn symbols to the texts and enums of the model.</summary>
internal static class Symbols
{
    /// <summary><c>Outer.Inner&lt;T&gt;</c>: containing types, no namespace.</summary>
    public static readonly SymbolDisplayFormat TypeNameFormat = new(
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypes,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes
                              | SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers);

    /// <summary>Short type names in member signatures: <c>List&lt;Order&gt;</c>, <c>int?</c>, <c>string[]</c>.</summary>
    public static readonly SymbolDisplayFormat ShortTypeFormat = new(
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameOnly,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes
                              | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier
                              | SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers);

    public static string ShortName(ITypeSymbol type) => type.ToDisplayString(ShortTypeFormat);

    /// <summary>Unique id of a type: namespace + containing types (<c>+</c>) + metadata name (<c>`1</c>).</summary>
    public static string TypeId(INamedTypeSymbol type)
    {
        type = type.OriginalDefinition;
        var name = type.MetadataName;
        for (var outer = type.ContainingType; outer is not null; outer = outer.ContainingType)
            name = outer.MetadataName + "+" + name;
        var ns = NamespaceName(type);
        return ns.Length == 0 ? name : ns + "." + name;
    }

    public static string NamespaceName(ISymbol symbol) =>
        symbol.ContainingNamespace is { IsGlobalNamespace: false } ns ? ns.ToDisplayString() : "";

    /// <summary>The type is declared in C# source of the solution (not in a referenced assembly).</summary>
    public static bool IsSolutionType(ITypeSymbol? type) =>
        type is INamedTypeSymbol { IsImplicitlyDeclared: false } named
        && named.OriginalDefinition.Locations.Any(l => l.IsInSource)
        && !IsGenerated(named.OriginalDefinition);

    public static bool IsGenerated(ISymbol symbol) =>
        symbol.Locations.Where(l => l.IsInSource).All(l => IsGeneratedFile(l.SourceTree?.FilePath));

    public static bool IsGeneratedFile(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return true;
        var file = Path.GetFileName(path);
        if (file.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)
            || file.EndsWith(".g.i.cs", StringComparison.OrdinalIgnoreCase)
            || file.EndsWith(".designer.cs", StringComparison.OrdinalIgnoreCase)
            || file.EndsWith(".AssemblyInfo.cs", StringComparison.OrdinalIgnoreCase)
            || file.EndsWith(".AssemblyAttributes.cs", StringComparison.OrdinalIgnoreCase)
            || file.EndsWith(".GlobalUsings.g.cs", StringComparison.OrdinalIgnoreCase))
            return true;
        var separator = Path.DirectorySeparatorChar;
        return path.Contains($"{separator}obj{separator}", StringComparison.OrdinalIgnoreCase);
    }

    public static Visibility VisibilityOf(ISymbol symbol) => symbol.DeclaredAccessibility switch
    {
        Accessibility.Public => Visibility.Public,
        Accessibility.Protected or Accessibility.ProtectedOrInternal => Visibility.Protected,
        Accessibility.Internal => Visibility.Internal,
        // Interface members without a modifier are public.
        Accessibility.NotApplicable when symbol.ContainingType?.TypeKind == Microsoft.CodeAnalysis.TypeKind.Interface => Visibility.Public,
        _ => Visibility.Private,
    };

    /// <summary>The least visible of the symbol and its containing types.</summary>
    public static Visibility EffectiveVisibility(ISymbol symbol)
    {
        var result = VisibilityOf(symbol);
        for (var outer = symbol.ContainingType; outer is not null; outer = outer.ContainingType)
        {
            var visibility = VisibilityOf(outer);
            if (visibility < result)
                result = visibility;
        }
        return result;
    }

    public static TypeKind KindOf(INamedTypeSymbol type) => type.TypeKind switch
    {
        Microsoft.CodeAnalysis.TypeKind.Interface => TypeKind.Interface,
        Microsoft.CodeAnalysis.TypeKind.Enum => TypeKind.Enum,
        Microsoft.CodeAnalysis.TypeKind.Delegate => TypeKind.Delegate,
        Microsoft.CodeAnalysis.TypeKind.Struct => type.IsRecord ? TypeKind.RecordStruct : TypeKind.Struct,
        _ => type.IsRecord ? TypeKind.Record : TypeKind.Class,
    };

    /// <summary>The first sentence block of the XML documentation <c>&lt;summary&gt;</c>, as plain text.</summary>
    public static string? Summary(ISymbol symbol)
    {
        var xml = symbol.GetDocumentationCommentXml();
        if (string.IsNullOrWhiteSpace(xml))
            return null;
        try
        {
            var summary = XElement.Parse(xml).Element("summary");
            if (summary is null)
                return null;
            var text = string.Concat(summary.Nodes().Select(NodeText));
            text = PlantUml.PlantUmlWriter.SingleLine(text);
            return text.Length == 0 ? null : text;
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    private static string NodeText(XNode node) => node switch
    {
        XText text => text.Value,
        XElement { Name.LocalName: "see" or "seealso" } e =>
            e.Attribute("langword")?.Value ?? ShortCref(e.Attribute("cref")?.Value) ?? e.Value,
        XElement { Name.LocalName: "paramref" or "typeparamref" } e => e.Attribute("name")?.Value ?? "",
        XElement e => string.Concat(e.Nodes().Select(NodeText)),
        _ => "",
    };

    private static string? ShortCref(string? cref)
    {
        if (cref is null)
            return null;
        var name = cref.Length > 2 && cref[1] == ':' ? cref[2..] : cref;
        var paren = name.IndexOf('(');
        if (paren >= 0)
            name = name[..paren];
        var dot = name.LastIndexOf('.');
        return dot >= 0 ? name[(dot + 1)..] : name;
    }

    /// <summary>
    /// The solution types a field or property type refers to, and whether it holds many of them
    /// (arrays, collections, dictionaries).
    /// </summary>
    public static IEnumerable<(INamedTypeSymbol Type, bool IsMany)> ReferencedTypes(ITypeSymbol type, bool isMany = false)
    {
        switch (type)
        {
            case IArrayTypeSymbol array:
                foreach (var t in ReferencedTypes(array.ElementType, true))
                    yield return t;
                yield break;
            case INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable:
                foreach (var t in ReferencedTypes(nullable.TypeArguments[0], isMany))
                    yield return t;
                yield break;
            case INamedTypeSymbol named:
                if (IsSolutionType(named))
                    yield return (named.OriginalDefinition, isMany);
                if (!named.IsGenericType)
                    yield break;
                var many = isMany || IsCollection(named);
                foreach (var argument in named.TypeArguments)
                foreach (var t in ReferencedTypes(argument, many))
                    yield return t;
                yield break;
        }
    }

    private static bool IsCollection(INamedTypeSymbol type) =>
        type.SpecialType != SpecialType.System_String
        && (type.OriginalDefinition.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T
            || type.AllInterfaces.Any(i => i.OriginalDefinition.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T));
}
