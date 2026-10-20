using Dotnetumentation.Core.Model;
using Dotnetumentation.Core.PlantUml;

namespace Dotnetumentation.Core.Generators;

/// <summary>
/// <c>component.puml</c>: the project with the projects and NuGet packages it references;
/// <c>packages.puml</c>: its namespaces and which namespaces they use.
/// </summary>
public sealed class ComponentDiagramGenerator : IDiagramGenerator
{
    public IEnumerable<GeneratedFile> Generate(GeneratorContext context)
    {
        yield return new GeneratedFile(DocumentationPaths.Components, Components(context.Project));
        yield return new GeneratedFile(DocumentationPaths.Namespaces, Namespaces(context.Project));
    }

    public static string Components(ProjectModel project)
    {
        var writer = new PlantUmlWriter().StartUml($"Components of {project.Name}", "component");
        var self = "project_" + PlantUmlWriter.Id(project.Name);
        writer.Line($"component {PlantUmlWriter.Quote(project.Name)} as {self} <<project>>");

        foreach (var reference in project.ProjectReferences)
            writer.Line($"component {PlantUmlWriter.Quote(reference)} as project_{PlantUmlWriter.Id(reference)} <<project>>");
        if (project.PackageReferences.Count > 0)
        {
            writer.Block("package \"NuGet\" {", () =>
            {
                foreach (var package in project.PackageReferences)
                {
                    var name = package.Version is null ? package.Name : $"{package.Name}\\n{package.Version}";
                    writer.Line($"component {PlantUmlWriter.Quote(name)} as nuget_{PlantUmlWriter.Id(package.Name)} <<nuget>>");
                }
            });
        }

        foreach (var reference in project.ProjectReferences)
            writer.Line($"{self} --> project_{PlantUmlWriter.Id(reference)}");
        foreach (var package in project.PackageReferences)
            writer.Line($"{self} ..> nuget_{PlantUmlWriter.Id(package.Name)}");
        return writer.EndUml().ToString();
    }

    public static string Namespaces(ProjectModel project)
    {
        var writer = new PlantUmlWriter().StartUml($"Namespaces of {project.Name}", "package");
        writer.Block($"package {PlantUmlWriter.Quote(project.Name)} as project_{PlantUmlWriter.Id(project.Name)} {{", () =>
        {
            foreach (var ns in project.Namespaces.Union(project.NamespaceDependencies.Where(d => d.ToProject is null).Select(d => d.To))
                         .Order(StringComparer.Ordinal))
                writer.Block($"package {PlantUmlWriter.Quote(DocumentationPaths.NamespaceTitle(ns))} as {Alias(null, ns)} {{", () => { });
        });

        foreach (var group in project.NamespaceDependencies.Where(d => d.ToProject is not null).GroupBy(d => d.ToProject!)
                     .OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            writer.Block($"package {PlantUmlWriter.Quote(group.Key)} as project_{PlantUmlWriter.Id(group.Key)} {{", () =>
            {
                foreach (var ns in group.Select(d => d.To).Distinct().Order(StringComparer.Ordinal))
                    writer.Block($"package {PlantUmlWriter.Quote(DocumentationPaths.NamespaceTitle(ns))} as {Alias(group.Key, ns)} {{", () => { });
            });
        }

        foreach (var dependency in project.NamespaceDependencies)
            writer.Line($"{Alias(null, dependency.From)} ..> {Alias(dependency.ToProject, dependency.To)}");
        return writer.EndUml().ToString();
    }

    private static string Alias(string? project, string ns) =>
        "ns_" + (project is null ? "" : PlantUmlWriter.Id(project) + "__") + PlantUmlWriter.Id(ns.Length == 0 ? "global" : ns);
}
