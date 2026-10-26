using System.Net;
using System.Text;
using Dotnetumentation.Core.Model;

namespace Dotnetumentation.Core.Generators;

/// <summary>Deterministic reference pages that complement the UML views using the same analyzed model.</summary>
public sealed class StaticDocumentationGenerator : IDiagramGenerator
{
    public IEnumerable<GeneratedFile> Generate(GeneratorContext context)
    {
        foreach (var type in context.Project.Types.OrderBy(t => t.Id, StringComparer.Ordinal))
            yield return new GeneratedFile(DocumentationPaths.TypeReference(type.Id), TypePage(type, context));
        yield return new GeneratedFile(DocumentationPaths.Dependencies, Dependencies(context));
        yield return new GeneratedFile(DocumentationPaths.Coverage, Coverage(context.Project));
    }

    private static string TypePage(TypeModel type, GeneratorContext context)
    {
        var md = new StringBuilder("[Project overview](../README.md)\n\n");
        md.Append($"# {Text(type.FullName)}\n\n");
        md.Append($"{type.Visibility.ToString().ToLowerInvariant()} {type.Kind} in namespace {Code(DocumentationPaths.NamespaceTitle(type.Namespace))}.\n\n");
        md.Append($"Declaration: {Code(type.Declaration ?? type.Name)}\n\n");
        AppendComment(md, type.Documentation, type.Summary);
        if (type.SourceFile is { } source)
        {
            // Resolve from the project rather than assuming which output folder name the caller selected.
            var relative = Path.GetRelativePath(context.Project.Directory, source).Replace('\\', '/');
            md.Append($"Source (relative to project): {Code(relative)}\n\n");
        }
        if (context.Diagrams.HasFlag(DiagramKinds.Class))
            md.Append($"[Class diagram](../{DocumentationPaths.ClassDiagram(type.Namespace)})\n\n");

        md.Append("## Type relationships\n\n");
        AppendRelations(md, type, context, "../");
        md.Append("## Members\n\n");
        if (type.Members.Count == 0)
            md.Append("No members match the selected visibility.\n\n");
        foreach (var member in type.Members)
        {
            md.Append($"### {Text(member.Id ?? member.Name)}\n\n");
            md.Append($"{member.Visibility.ToString().ToLowerInvariant()} {member.Kind.ToString().ToLowerInvariant()}");
            if (member.IsStatic) md.Append("; static");
            if (member.IsAbstract) md.Append("; abstract");
            md.Append($".\n\nDeclaration: {Code(member.Declaration ?? member.Signature)}\n\n");
            AppendComment(md, member.Documentation);
            foreach (var sequence in context.SequenceLinks.Where(s => s.Namespace == type.Namespace && s.TypeName == type.Name && s.MethodId == member.Id))
                md.Append($"[Sequence diagram](../{DocumentationPaths.Sequence(sequence.TypeName, sequence.Namespace, sequence.MethodId)})\n\n");
            foreach (var activity in context.ActivityLinks.Where(a => a.Namespace == type.Namespace && a.TypeName == type.Name && a.MethodId == member.Id))
                md.Append($"[Activity diagram](../{DocumentationPaths.Activity(activity.TypeName, activity.Namespace, activity.MethodId)})\n\n");
        }
        return md.ToString();
    }

    private static void AppendComment(StringBuilder md, DocumentationComment comment, string? fallbackSummary = null)
    {
        md.Append(Text(comment.Summary ?? fallbackSummary ?? "No XML summary provided.")).Append("\n\n");
        if (comment.Remarks is { } remarks)
            md.Append($"Remarks: {Text(remarks)}\n\n");
        AppendNamed(md, "Type parameters", comment.TypeParameters);
        AppendNamed(md, "Parameters", comment.Parameters);
        if (comment.Returns is { } returns)
            md.Append($"Returns: {Text(returns)}\n\n");
        if (comment.Value is { } value)
            md.Append($"Value: {Text(value)}\n\n");
        AppendNamed(md, "Documented exceptions", comment.Exceptions);
    }

    private static void AppendNamed(StringBuilder md, string title, IReadOnlyList<NamedDocumentation> entries)
    {
        if (entries.Count == 0) return;
        md.Append($"**{title}**\n\n");
        foreach (var entry in entries)
            md.Append($"- {Code(entry.Name)}: {Text(entry.Text)}\n");
        md.Append('\n');
    }

    private static string Dependencies(GeneratorContext context)
    {
        var project = context.Project;
        var md = new StringBuilder("[Project overview](README.md)\n\n# Dependencies\n\n");
        md.Append("Relationships below are extracted from declarations and signatures. They do not establish runtime calls or DI registrations.\n\n");
        md.Append("## Project references\n\n");
        foreach (var reference in project.ProjectReferences.Order(StringComparer.Ordinal))
            md.Append($"- {Code(reference)}\n");
        if (project.ProjectReferences.Count == 0) md.Append("No project references.\n");
        md.Append("\n## Package references\n\n");
        foreach (var package in project.PackageReferences.OrderBy(p => p.Name, StringComparer.Ordinal))
            md.Append($"- {Code(package.Name)}: {Code(package.Version ?? "version not specified in project")}\n");
        if (project.PackageReferences.Count == 0) md.Append("No package references found.\n");
        md.Append("\n## Source type relationships\n\n");
        foreach (var type in project.Types.OrderBy(t => t.Id, StringComparer.Ordinal))
        {
            md.Append($"### {Text(type.FullName)}\n\n");
            AppendRelations(md, type, context, "");
        }
        return md.ToString();
    }

    private static void AppendRelations(StringBuilder md, TypeModel type, GeneratorContext context, string prefix)
    {
        if (type.Relations.Count == 0)
            md.Append("No source type relationships found.\n\n");
        foreach (var relation in type.Relations.OrderBy(r => r.Kind).ThenBy(r => r.ToId, StringComparer.Ordinal).ThenBy(r => r.Label, StringComparer.Ordinal))
        {
            var verb = relation.Kind switch
            {
                RelationKind.Generalization => "Inherits from",
                RelationKind.Realization => "Implements",
                RelationKind.Association => "References through a field, property or captured constructor parameter",
                RelationKind.Aggregation => "Contains a collection of",
                _ => "Uses in a signature",
            };
            var local = context.Project.Types.FirstOrDefault(t => t.Id == relation.ToId);
            var name = context.SolutionTypes.GetValueOrDefault(relation.ToId)?.FullName ?? relation.ToId;
            var target = local is null ? Code(name) : $"[{Text(name)}]({prefix}{DocumentationPaths.TypeReference(local.Id)})";
            md.Append($"- {verb} {target}");
            if (relation.Label is { } label) md.Append($" ({Code(label)})");
            md.Append(".\n");
        }
        if (type.Relations.Count > 0) md.Append('\n');
    }

    private static string Coverage(ProjectModel project)
    {
        var types = project.Types.OrderBy(t => t.Id, StringComparer.Ordinal).ToList();
        var total = types.Count + types.Sum(t => t.Members.Count);
        var missing = types.Sum(t => (HasSummary(t) ? 0 : 1) + t.Members.Count(m => string.IsNullOrWhiteSpace(m.Documentation.Summary)));
        var md = new StringBuilder("[Project overview](README.md)\n\n# Documentation summaries\n\n");
        md.Append($"{total - missing} of {total} selected types and members have an explicit XML summary; {missing} are missing one.\n\n");
        md.Append("This report respects the visibility filter. It checks explicit summaries only; inherited comments are not resolved. Parameter documentation completeness and behavioral correctness are not assessed.\n\n");
        if (missing == 0) md.Append("No missing summaries.\n");
        foreach (var type in types)
        {
            if (HasSummary(type) && type.Members.All(m => !string.IsNullOrWhiteSpace(m.Documentation.Summary))) continue;
            md.Append($"## [{Text(type.FullName)}]({DocumentationPaths.TypeReference(type.Id)})\n\n");
            if (!HasSummary(type)) md.Append("- Type summary is missing.\n");
            foreach (var member in type.Members.Where(m => string.IsNullOrWhiteSpace(m.Documentation.Summary)))
                md.Append($"- Member summary is missing: {Code(member.Declaration ?? member.Signature)}\n");
            md.Append('\n');
        }
        return md.ToString();
    }

    private static bool HasSummary(TypeModel type) => !string.IsNullOrWhiteSpace(type.Documentation.Summary ?? type.Summary);

    // XML comments are content, never raw HTML or Markdown instructions.
    private static string Text(string value) => WebUtility.HtmlEncode(value)
        .Replace("\\", "\\\\").Replace("`", "\\`").Replace("*", "\\*").Replace("_", "\\_")
        .Replace("[", "\\[").Replace("]", "\\]").Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");

    private static string Code(string value)
    {
        var fence = "`";
        while (value.Contains(fence, StringComparison.Ordinal)) fence += "`";
        return $"{fence} {value.Replace('\r', ' ').Replace('\n', ' ')} {fence}";
    }
}
