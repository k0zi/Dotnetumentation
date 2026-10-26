using System.Text;
using Dotnetumentation.Core.Graph;

namespace Dotnetumentation.Core.Generators;

/// <summary><c>graph.md</c>: the structure of the graph database next to it, what this project put in it and example queries.</summary>
public sealed class GraphDocumentationGenerator : IDiagramGenerator
{
    private static readonly (string Kind, string Description)[] Edges =
    [
        (EdgeKinds.Contains, "project → namespace and file, namespace → type, type → member and nested type"),
        (EdgeKinds.DeclaredIn, "type or member → the file declaring it (every file of a partial type)"),
        (EdgeKinds.Inherits, "type → its base class, interface → its base interface"),
        (EdgeKinds.Implements, "type → an interface it implements"),
        (EdgeKinds.Association, "type → the type of a field, property or captured constructor parameter; label: the member"),
        (EdgeKinds.Aggregation, "type → the element type of a collection field or property; label: the member"),
        (EdgeKinds.Dependency, "type → a type only used in a method signature"),
        (EdgeKinds.Calls, "method, constructor or property → a method or constructor (`base(…)`, `this(…)`) it calls"),
        (EdgeKinds.Creates, "method, constructor or property → a constructor it calls with `new`"),
        (EdgeKinds.Overrides, "member → the base class member it overrides"),
        (EdgeKinds.ImplementsMember, "member → the interface member it implements"),
        (EdgeKinds.ProjectReference, "project → a project it references"),
        (EdgeKinds.PackageReference, "project → a NuGet package; label: the version"),
        (EdgeKinds.UsesNamespace, "namespace → a namespace whose types its code uses (with component diagrams only)"),
    ];

    private static readonly (string Column, string Description)[] NodeColumns =
    [
        ("id", "unique id, see below"),
        ("kind", "node kind, see below"),
        ("name", "short name: `OrderService`, `Place`, `Outer.Inner<T>`"),
        ("full_name", "qualified name: `Sample.App.OrderService.Place(Order)`"),
        ("project", "the project defining the node; empty for .NET and package code"),
        ("namespace", "namespace of a type or member"),
        ("visibility", "`public`, `protected`, `internal` or `private`"),
        ("signature", "UML style signature of a member; target framework of a project; version of a package"),
        ("declaration", "C# declaration"),
        ("summary", "the XML `<summary>`"),
        ("file", "source file, relative to the folder of the database"),
        ("line", "1-based line of the declaration"),
        ("is_external", "1 for .NET and package code"),
        ("is_stub", "1 when the node is only referenced here and defined in another project or assembly: name only"),
    ];

    public IEnumerable<GeneratedFile> Generate(GeneratorContext context)
    {
        if (context.Graph is not { } graph)
            yield break;

        var md = new StringBuilder("[Project overview](README.md)\n\n# Graph database\n\n");
        md.Append($"[`{GraphDatabase.FileName}`]({GraphDatabase.FileName}) in this folder is an SQLite database of the code of the project: its namespaces, files, ");
        md.Append("types and members as nodes, their relations and direct calls as edges, and the documentation pages and diagrams of each node. ");
        md.Append($"`<Solution>{GraphDatabase.SolutionFileSuffix}` next to the solution file holds the graphs of every project in one database.\n\n");
        md.Append("Open it with any SQLite tool (`sqlite3`, DB Browser for SQLite, a library of your language), search it in the *Symbols* tab of the ");
        md.Append("Dotnetumentation app, or with `dotnetumentation query <folder> <text>`.\n\n");
        md.Append("Nodes follow the visibility filter of the documentation; calls are read from the members that are documented. ");
        md.Append("Calls are resolved by the compiler, so a call through an interface leads to the interface member: follow `implements_member` to the implementations.\n\n");

        md.Append("## Contents\n\n| Node kind | Defined here | Referenced only (stubs) |\n|---|---:|---:|\n");
        foreach (var group in graph.Nodes.GroupBy(n => n.Kind).OrderBy(g => g.Key, StringComparer.Ordinal))
            md.Append($"| {group.Key} | {group.Count(n => !n.IsStub)} | {group.Count(n => n.IsStub)} |\n");
        md.Append("\n| Edge kind | Count |\n|---|---:|\n");
        foreach (var group in graph.Edges.Distinct().GroupBy(e => e.Kind).OrderBy(g => g.Key, StringComparer.Ordinal))
            md.Append($"| {group.Key} | {group.Count()} |\n");

        md.Append("\n## Tables\n\n```sql\n").Append(GraphDatabase.Schema.TrimEnd()).Append("\n```\n\n");
        md.Append("- `meta`: `schema_version`, `tool_version`, `scope` (`project` or `solution`), `project`, `assembly`, `target_framework`.\n");
        md.Append("- `nodes`: one row per node.\n");
        md.Append("- `edges`: directed relations `source → target`; `label` is a member name, a package version or empty.\n");
        md.Append("- `documents`: the pages of a node; `kind` is `api` (reference page), `class` (class diagram), `sequence` or `activity`; ");
        md.Append("`path` is relative to the folder of the database.\n");
        md.Append("- `search`: full text index (FTS5) of `name`, `words` (the parts of a camel case name: `Order Service`), `full_name` and `summary`.\n\n");
        md.Append("| `nodes` column | |\n|---|---|\n");
        foreach (var (column, description) in NodeColumns)
            md.Append($"| `{column}` | {description} |\n");

        md.Append("\n## Ids\n\n");
        md.Append("Types and members have their C# documentation comment id, the same as in XML documentation files: ");
        md.Append("`T:Sample.Domain.IRepository`1`, `M:Sample.App.OrderService.Place(Sample.Domain.Order)`, `P:…`, `F:…`, `E:…`, ");
        md.Append("constructors `M:Type.#ctor(…)`. Other nodes: `N:<namespace>` (`N:(global)`), `project:<name>`, `package:<name>`, ");
        md.Append("`file:<path relative to the solution folder>`. Ids are the same in every graph of the solution.\n\n");

        md.Append("## Node kinds\n\n");
        md.Append(string.Join(", ", new[] { NodeKinds.Project, NodeKinds.Package, NodeKinds.Namespace, NodeKinds.File }
            .Concat(NodeKinds.Types).Concat(NodeKinds.Members).Select(k => $"`{k}`")));
        md.Append(".\n\n## Edge kinds\n\n| Kind | |\n|---|---|\n");
        foreach (var (kind, description) in Edges)
            md.Append($"| `{kind}` | {description} |\n");

        md.Append("\n## Example queries\n\n");
        Example(md, "Find a symbol by a word of its name or summary", """
            SELECT n.kind, n.full_name, n.file, n.line
            FROM search JOIN nodes n ON n.id = search.id
            WHERE search MATCH 'order*'
            ORDER BY rank;
            """);
        Example(md, "Who calls a method", """
            SELECT caller.full_name, caller.file, caller.line
            FROM edges e JOIN nodes caller ON caller.id = e.source
            WHERE e.kind = 'calls' AND e.target = 'M:Sample.Domain.IRepository`1.Add(`0)';
            """);
        Example(md, "What implements an interface, and its members", """
            SELECT n.full_name FROM edges e JOIN nodes n ON n.id = e.source
            WHERE e.kind = 'implements' AND e.target = 'T:Sample.Domain.IRepository`1';
            """);
        Example(md, "Everything a method reaches through calls, with the depth", """
            WITH RECURSIVE reached(id, depth) AS (
                SELECT 'M:Sample.App.OrderService.Place(Sample.Domain.Order)', 0
                UNION
                SELECT e.target, r.depth + 1 FROM reached r
                JOIN edges e ON e.source = r.id AND e.kind IN ('calls', 'creates')
                WHERE r.depth < 10
            )
            SELECT n.full_name, min(r.depth) AS depth, n.is_external
            FROM reached r JOIN nodes n ON n.id = r.id
            GROUP BY n.id ORDER BY depth, n.full_name;
            """);
        Example(md, "Types using a type (fields, properties, signatures)", """
            SELECT n.full_name, e.kind, e.label FROM edges e JOIN nodes n ON n.id = e.source
            WHERE e.target = 'T:Sample.Domain.Order' AND e.kind IN ('association', 'aggregation', 'dependency');
            """);
        Example(md, "The reference page and diagrams of a member", """
            SELECT kind, path FROM documents WHERE node = 'M:Sample.App.OrderService.Place(Sample.Domain.Order)';
            """);

        yield return new GeneratedFile(DocumentationPaths.Graph, md.ToString());
    }

    private static void Example(StringBuilder md, string title, string sql) =>
        md.Append($"**{title}**\n\n```sql\n{sql.TrimEnd()}\n```\n\n");
}
