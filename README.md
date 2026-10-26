![Dotnetumentation](src/Dotnetumentation.App/Assets/header.png)

# Dotnetumentation

Static UML and reference documentation of .NET solutions. Every C# project of a solution is analyzed with Roslyn and documented with
PlantUML (`.puml`) diagrams and Markdown reference pages in a `docs.net` folder next to its project file. No AI is involved: the same code always
gives the same files.

- **CLI** (`src/Dotnetumentation.Cli`): generation only.
- **GUI** (`src/Dotnetumentation.App`): generation and a browser for the diagrams, built with Avalonia and
  [KD.Avalonia.Rice](https://www.nuget.org/packages/KD.Avalonia.Rice) (`UmlDiagramView`, `MarkdownView`).
- **Core** (`src/Dotnetumentation.Core`): loading (MSBuildWorkspace), analysis, PlantUML writing; no UI dependency.

## Output

```
MySolution.graph.db               graph database of the whole solution (all projects merged)
MyProject/docs.net/
  README.md                       overview: types with summaries, links, embedded component/namespace diagrams
  graph.db                        graph database (SQLite) of the project's classes, functions, relations and calls
  graph.md                        structure of the graph database, its contents and example queries
  api/<TypeId>.md                 type/member reference with declarations and XML documentation
  dependencies.md                 project/package references and source type relationships
  documentation-coverage.md       types and members missing XML summaries
  component.puml                  the project, the projects and NuGet packages it references
  packages.puml                   namespaces and which namespaces they use
  class/<Namespace>.puml          types with members; inheritance, implementation, associations, dependencies
  sequence/<Type>/<Method>.puml   calls into the solution, followed to a depth, with alt/opt/loop/try blocks
  activity/<Type>/<Method>.puml   control flow of the method body (if, switch, loops, try/catch, return/throw)
  .dotnetumentation-state.json    git commit, options and diagram sources of the last run, for incremental updates
```

- Reference pages are always generated, regardless of the selected diagram kinds, and respect the visibility filter.
  They include explicitly written XML summaries, remarks, parameters, type parameters, returns, values and exceptions.
  API filenames include a readable type name and a stable identity hash to keep generic and nested types distinct.
- Dependency descriptions report source relationships, not inferred DI registrations or runtime behavior. Missing-summary
  counts cover the selected types and members; inherited comments are not resolved and parameter coverage is not scored.
- Sequence diagrams are made for methods that call into the solution; activity diagrams for methods with branches,
  loops or at least three statements.
- Sequence diagrams follow the calls to the end of the own code by default (each method's body is expanded once per
  diagram, recursion included). Calls into .NET and other libraries end the chain: they are shown as messages to an
  `<<external>>` participant, but not explored. Lambdas are not followed, as they may run later.
- Files are only rewritten when their content changes; diagrams of removed code are deleted, so the folder always
  matches the code.
- With `--incremental` only what changed in git since the last generation is updated (see below).
- Every diagram starts with a `' rice:kind=…` comment: PlantUML ignores it, KD.Avalonia.Rice reads the type from it.

## CLI

```sh
dotnet run --project src/Dotnetumentation.Cli -- generate path/to/MySolution.slnx
```

| Option | Default | |
|--------|---------|---|
| `--diagrams`, `-d` | all | `class`, `component`, `sequence`, `activity` (comma separated or repeated) |
| `--visibility`, `-v` | `internal` | least visibility of documented types and members: `public`, `protected`, `internal`, `private` |
| `--sequence-depth` | `all` | how deep sequence diagrams follow calls into the own code: a number or `all` |
| `--max-messages` | `500` | cut a sequence diagram after this many messages; `0` means no limit |
| `--include`, `--exclude` | | project name patterns with `*` and `?` |
| `--output-dir-name` | `docs.net` | folder name next to each project file |
| `--clean` | | delete the output folders before writing |
| `--incremental`, `-i` | | only update what changed in git since the last generation |
| `--no-graph` | | do not write the graph databases |
| `--since` | | incremental update compared with this commit, branch or tag instead of the last generation |
| `--verbose` | | progress messages and load warnings |

Exit codes: `0` success, `1` nothing could be loaded or generated, `2` done with warnings.

### Searching the graph database

```sh
dotnet run --project src/Dotnetumentation.Cli -- query path/to/MySolution.slnx "order service"
dotnet run --project src/Dotnetumentation.Cli -- query path/to/MySolution.slnx Place --kind method --relations
```

`query <path> <text>` takes a `.db` file, a solution or project file, a `docs.net` folder or any folder above them, and uses
the solution graph when there is one. Every word matches the start of a word of the name, of its camel case parts
(`serv` finds `OrderService`), of the full name or of the summary. A single match, or every match with `--relations`,
is shown with its relations (calls, called by, implements, implemented by, references, …) and its documentation
pages. `--kind` (`-k`) takes `type`, `member` or a node kind (`class`, `interface`, `method`, `property`, …),
`--limit` (`-n`) the number of matches (20).

### Incremental updates

Every run saves the checked out commit, the options and, for each sequence and activity diagram, the source files it
was made from to `.dotnetumentation-state.json` in the output folder. `--incremental` then compares the working tree
(committed, staged, unstaged and untracked changes) with that commit:

- A project is skipped when no `.cs` or project file of it, or of a project it references, changed.
- In an updated project the types, class diagrams and reference pages are made again; sequence and activity diagrams
  are kept when none of their source files changed.
- Everything is generated again when the options or the tool version differ, a solution level build file
  (`.sln`, `.slnx`, `.props`, `.targets`, `global.json`, `nuget.config`) or the project file changed, or there is no
  state file. Outside a git repository a warning is shown and everything is generated.
- Uncommitted changes at the time of the generation are recorded too, so reverting them later updates the documentation.
- Known limit: a sequence diagram is not updated when only a new overload in another file would change which method
  one of its calls resolves to. A run without `--incremental` always generates everything.

`--clean` and `--incremental` cannot be combined. The state file can be committed with the documentation or ignored.

## Graph database

Every project gets `docs.net/graph.db`, an SQLite database of its code, and the solution gets `<Solution>.graph.db`
next to the solution file with every project merged (a type referenced by another project has its full row there).
`graph.md` next to each database describes the structure, counts what the project put in it and lists example
queries. Open it with any SQLite tool, the *Symbols* tab of the GUI or `query`.

| Table | Content |
|-------|---------|
| `nodes` | projects, packages, namespaces, files, types (`class`, `record`, `struct`, `record struct`, `interface`, `enum`, `delegate`) and members (`method`, `constructor`, `property`, `field`, `event`, `enum value`): name, full name, project, visibility, signature, declaration, XML summary, source file and line, `is_external` (.NET or package code), `is_stub` (only referenced, defined elsewhere) |
| `edges` | `source → target` with a `kind` and an optional `label`: `contains`, `declared_in`, `inherits`, `implements`, `association`, `aggregation`, `dependency`, `calls`, `creates`, `overrides`, `implements_member`, `project_reference`, `package_reference`, `uses_namespace` |
| `documents` | the reference page (`api`), class, sequence and activity diagrams of a node |
| `search` | FTS5 full text index of names, camel case name parts, full names and summaries |
| `meta` | schema and tool version, scope (`project` or `solution`), project, assembly, target framework |

- Ids are C# documentation comment ids (`T:Ns.Type`1`, `M:Ns.Type.Place(Ns.Order)`, `P:`, `F:`, `E:`), plus `N:<namespace>`,
  `project:<name>`, `package:<name>` and `file:<path relative to the solution folder>`: the same in every database.
- Paths are relative to the folder of the database file, so the documentation can be moved or committed as a whole.
- Nodes follow the visibility filter; calls are read from the documented members (lambdas included) and resolved by the
  compiler: a call through an interface leads to the interface member, `implements_member` to its implementations.
  Called .NET and package methods are stub nodes, so the callers of a library method can be found too.
- The same code always gives the same bytes; the file is only replaced when its content changes. With `--incremental`
  a skipped project keeps its database, and the solution graph is merged again from all of them.

## GUI

```sh
dotnet run --project src/Dotnetumentation.App
```

*Generate* takes the same options (*Only update what changed since the last generation* is `--incremental`, the
graph database checkbox is the opposite of `--no-graph`); *Browse* lists the `docs.net` folders below the solution and shows the diagrams
(pan, zoom, fit) and the Markdown reference pages, whose links open related pages and diagrams. The last solution and options are saved to
`~/.config/dotnetumentation/settings.json`.

In *Browse*, open the solution folder, a project folder, or a generated `docs.net` folder directly. The tree starts with
*Overview*, lists the dependency and missing-summary reports, and shows API pages by their headings rather than hashed
filenames. Select a page to read it, or follow its links to other reference pages and UML diagrams inside the app.
The *Symbols* tab searches the graph database: a selected class or function shows its declaration, summary, source,
documentation pages and relations, each a link to the related symbol.

## Development

```sh
dotnet build Dotnetumentation.slnx
dotnet test
```

The tests check the analyzers on in-memory compilations, run the whole generation on `TestData/SampleSolution` (a copy
in a temporary folder, restored first) and parse every generated diagram with the KD.Avalonia.Rice parser.
