using Dotnetumentation.Core.Model;
using Dotnetumentation.Core.PlantUml;

namespace Dotnetumentation.Core.Generators;

/// <summary>A file of the documentation, relative to the output folder of the project (with <c>/</c> separators).</summary>
public sealed record GeneratedFile(string RelativePath, string Content);

/// <summary>Everything known about one project when its documentation is written.</summary>
public sealed record GeneratorContext(
    ProjectModel Project,
    IReadOnlyList<ActivityModel> Activities,
    IReadOnlyList<SequenceModel> Sequences,
    IReadOnlyDictionary<string, TypeModel> SolutionTypes,
    DiagramKinds Diagrams)
{
    /// <summary>
    /// Every method with a sequence diagram, in source order: the analyzed ones (<see cref="Sequences"/>) and, in an
    /// incremental update, those whose diagram is kept from the earlier run.
    /// </summary>
    public IReadOnlyList<MethodDiagram> SequenceLinks { get; init; } =
        Sequences.Select(s => new MethodDiagram(s.Namespace, s.TypeName, s.MethodId)).ToList();

    /// <summary>Every method with an activity diagram, like <see cref="SequenceLinks"/>.</summary>
    public IReadOnlyList<MethodDiagram> ActivityLinks { get; init; } =
        Activities.Select(a => new MethodDiagram(a.Namespace, a.TypeName, a.MethodId)).ToList();

    /// <summary>The graph database of the project; null when it is not generated.</summary>
    public Dotnetumentation.Core.Graph.CodeGraph? Graph { get; init; }
}

public interface IDiagramGenerator
{
    IEnumerable<GeneratedFile> Generate(GeneratorContext context);
}

/// <summary>File names shared by the generators and the index.</summary>
public static class DocumentationPaths
{
    public const string Index = "README.md";
    public const string Dependencies = "dependencies.md";
    public const string Coverage = "documentation-coverage.md";
    public const string Graph = "graph.md";

    // Keep paths bounded while distinguishing generic arity, nested types and sanitized names.
    public static string TypeReference(string id)
    {
        var name = PlantUmlWriter.FileName(id);
        if (name.Length > 80) name = name[..80];
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(id)));
        return $"api/{name}-{hash}.md";
    }

    public const string Components = "component.puml";
    public const string Namespaces = "packages.puml";

    public static string ClassDiagram(string ns) => $"class/{PlantUmlWriter.FileName(NamespaceTitle(ns))}.puml";

    public static string Sequence(string typeName, string ns, string methodId) => MethodPath("sequence", typeName, ns, methodId);

    public static string Activity(string typeName, string ns, string methodId) => MethodPath("activity", typeName, ns, methodId);

    public static string NamespaceTitle(string ns) => ns.Length == 0 ? "(global)" : ns;

    private static string MethodPath(string folder, string typeName, string ns, string methodId) =>
        $"{folder}/{PlantUmlWriter.FileName(ns.Length == 0 ? typeName : $"{ns}.{typeName}")}/{PlantUmlWriter.FileName(methodId)}.puml";
}
