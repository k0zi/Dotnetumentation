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
    DiagramKinds Diagrams);

public interface IDiagramGenerator
{
    IEnumerable<GeneratedFile> Generate(GeneratorContext context);
}

/// <summary>File names shared by the generators and the index.</summary>
public static class DocumentationPaths
{
    public const string Index = "README.md";
    public const string Components = "component.puml";
    public const string Namespaces = "packages.puml";

    public static string ClassDiagram(string ns) => $"class/{PlantUmlWriter.FileName(NamespaceTitle(ns))}.puml";

    public static string Sequence(string typeName, string ns, string methodId) => MethodPath("sequence", typeName, ns, methodId);

    public static string Activity(string typeName, string ns, string methodId) => MethodPath("activity", typeName, ns, methodId);

    public static string NamespaceTitle(string ns) => ns.Length == 0 ? "(global)" : ns;

    private static string MethodPath(string folder, string typeName, string ns, string methodId) =>
        $"{folder}/{PlantUmlWriter.FileName(ns.Length == 0 ? typeName : $"{ns}.{typeName}")}/{PlantUmlWriter.FileName(methodId)}.puml";
}
