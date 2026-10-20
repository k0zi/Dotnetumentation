![Dotnetumentation](src/Dotnetumentation.App/Assets/header.png)

# Dotnetumentation

Static UML documentation of .NET solutions. Every C# project of a solution is analyzed with Roslyn and documented with
PlantUML (`.puml`) diagrams in a `docs.net` folder next to its project file. No AI is involved: the same code always
gives the same files.

- **CLI** (`src/Dotnetumentation.Cli`): generation only.
- **GUI** (`src/Dotnetumentation.App`): generation and a browser for the diagrams, built with Avalonia and
  [KD.Avalonia.Rice](https://www.nuget.org/packages/KD.Avalonia.Rice) (`UmlDiagramView`, `MarkdownView`).
- **Core** (`src/Dotnetumentation.Core`): loading (MSBuildWorkspace), analysis, PlantUML writing; no UI dependency.

## Output

```
MyProject/docs.net/
  README.md                       overview: types with summaries, links, embedded component/namespace diagrams
  component.puml                  the project, the projects and NuGet packages it references
  packages.puml                   namespaces and which namespaces they use
  class/<Namespace>.puml          types with members; inheritance, implementation, associations, dependencies
  sequence/<Type>/<Method>.puml   calls into the solution, followed to a depth, with alt/opt/loop/try blocks
  activity/<Type>/<Method>.puml   control flow of the method body (if, switch, loops, try/catch, return/throw)
```

- Sequence diagrams are made for methods that call into the solution; activity diagrams for methods with branches,
  loops or at least three statements.
- Sequence diagrams follow the calls to the end of the own code by default (each method's body is expanded once per
  diagram, recursion included). Calls into .NET and other libraries end the chain: they are shown as messages to an
  `<<external>>` participant, but not explored. Lambdas are not followed, as they may run later.
- Files are only rewritten when their content changes; diagrams of removed code are deleted, so the folder always
  matches the code.
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
| `--verbose` | | progress messages and load warnings |

Exit codes: `0` success, `1` nothing could be loaded or generated, `2` done with warnings.

## GUI

```sh
dotnet run --project src/Dotnetumentation.App
```

*Generate* takes the same options; *Browse* lists the `docs.net` folders below the solution and shows the diagrams
(pan, zoom, fit) and the Markdown overviews, whose links open the diagrams. The last solution and options are saved to
`~/.config/dotnetumentation/settings.json`.

## Development

```sh
dotnet build Dotnetumentation.slnx
dotnet test
```

The tests check the analyzers on in-memory compilations, run the whole generation on `TestData/SampleSolution` (a copy
in a temporary folder, restored first) and parse every generated diagram with the KD.Avalonia.Rice parser.
