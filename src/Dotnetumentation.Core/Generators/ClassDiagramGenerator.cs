using Dotnetumentation.Core.Model;
using Dotnetumentation.Core.PlantUml;
using TypeKind = Dotnetumentation.Core.Model.TypeKind;

namespace Dotnetumentation.Core.Generators;

/// <summary>One class diagram per namespace: its types with members and their relations to other solution types.</summary>
public sealed class ClassDiagramGenerator : IDiagramGenerator
{
    public IEnumerable<GeneratedFile> Generate(GeneratorContext context)
    {
        foreach (var group in context.Project.Types.GroupBy(t => t.Namespace).OrderBy(g => g.Key, StringComparer.Ordinal))
            yield return new GeneratedFile(DocumentationPaths.ClassDiagram(group.Key), Diagram(group.Key, group.ToList(), context.SolutionTypes));
    }

    public static string Diagram(string ns, IReadOnlyList<TypeModel> types, IReadOnlyDictionary<string, TypeModel> solutionTypes)
    {
        var writer = new PlantUmlWriter().StartUml($"Namespace {DocumentationPaths.NamespaceTitle(ns)}", "class");
        var ids = types.Select(t => t.Id).ToHashSet();
        var relations = types.SelectMany(t => t.Relations).Distinct().ToList();

        writer.Block($"package {PlantUmlWriter.Quote(DocumentationPaths.NamespaceTitle(ns))} {{", () =>
        {
            foreach (var type in types)
                WriteType(writer, type);
        });

        // Related types of other namespaces are shown by name only, in their own package.
        var others = relations.Select(r => r.ToId).Where(id => !ids.Contains(id)).Distinct()
            .Select(id => solutionTypes.GetValueOrDefault(id) ?? Stub(id))
            .GroupBy(t => t.Namespace).OrderBy(g => g.Key, StringComparer.Ordinal);
        foreach (var group in others)
        {
            writer.Block($"package {PlantUmlWriter.Quote(DocumentationPaths.NamespaceTitle(group.Key))} {{", () =>
            {
                foreach (var type in group.OrderBy(t => t.Name, StringComparer.Ordinal))
                    writer.Line(Declaration(type));
            });
        }

        foreach (var relation in relations)
            writer.Line(Relation(relation));
        return writer.EndUml().ToString();
    }

    private static void WriteType(PlantUmlWriter writer, TypeModel type)
    {
        if (type.Members.Count == 0)
        {
            writer.Line(Declaration(type));
            return;
        }
        writer.Block(Declaration(type) + " {", () =>
        {
            MemberKind? previous = null;
            foreach (var member in type.Members)
            {
                // Fields, properties and the rest are separated like in a UML class box.
                if (previous is not null && Section(previous.Value) != Section(member.Kind))
                    writer.Line("--");
                previous = member.Kind;
                writer.Line(Member(member, type.Kind));
            }
        });
    }

    private static int Section(MemberKind kind) => kind switch
    {
        MemberKind.Field or MemberKind.EnumValue => 0,
        MemberKind.Property or MemberKind.Event => 1,
        _ => 2,
    };

    public static string Alias(string typeId) => PlantUmlWriter.Id(typeId);

    private static string Declaration(TypeModel type)
    {
        var keyword = type.Kind switch
        {
            TypeKind.Interface => "interface",
            TypeKind.Enum => "enum",
            TypeKind.Struct or TypeKind.RecordStruct => "struct",
            _ when type.IsAbstract => "abstract class",
            _ => "class",
        };
        var stereotypes = new List<string>();
        if (type.Kind is TypeKind.Record or TypeKind.RecordStruct)
            stereotypes.Add("record");
        if (type.Kind == TypeKind.Delegate)
            stereotypes.Add("delegate");
        if (type.IsStatic)
            stereotypes.Add("static");
        else if (type.IsSealed)
            stereotypes.Add("sealed");

        var text = $"{keyword} {PlantUmlWriter.Quote(type.Name)} as {Alias(type.Id)}";
        foreach (var stereotype in stereotypes)
            text += $" <<{stereotype}>>";
        return text;
    }

    private static string Member(MemberModel member, TypeKind owner)
    {
        if (member.Kind == MemberKind.EnumValue)
            return member.Name;
        var visibility = owner == TypeKind.Interface && member.Visibility == Visibility.Public ? "" : member.Visibility switch
        {
            Visibility.Public => "+",
            Visibility.Protected => "#",
            Visibility.Internal => "~",
            _ => "-",
        };
        var modifier = member.IsStatic ? "{static} " : member.IsAbstract ? "{abstract} " : "";
        return $"{modifier}{visibility}{PlantUmlWriter.SingleLine(member.Signature)}";
    }

    private static string Relation(RelationModel relation)
    {
        var from = Alias(relation.FromId);
        var to = Alias(relation.ToId);
        var label = relation.Label is null ? "" : " : " + PlantUmlWriter.Text(relation.Label);
        return relation.Kind switch
        {
            RelationKind.Generalization => $"{from} --|> {to}",
            RelationKind.Realization => $"{from} ..|> {to}",
            RelationKind.Aggregation => $"{from} o-- \"*\" {to}{label}",
            RelationKind.Association => $"{from} --> {to}{label}",
            _ => $"{from} ..> {to}",
        };
    }

    /// <summary>A type outside the documented projects, known by its id only.</summary>
    private static TypeModel Stub(string id)
    {
        var dot = id.LastIndexOf('.');
        var name = (dot >= 0 ? id[(dot + 1)..] : id).Replace('+', '.');
        var tick = name.IndexOf('`');
        if (tick >= 0)
            name = name[..tick];
        return new TypeModel
        {
            Id = id, Name = name, Namespace = dot >= 0 ? id[..dot] : "", Kind = TypeKind.Class, Visibility = Visibility.Public,
        };
    }
}
