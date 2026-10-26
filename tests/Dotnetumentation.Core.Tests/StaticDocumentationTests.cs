using System.Text.RegularExpressions;
using Dotnetumentation.Core.Analysis;
using Dotnetumentation.Core.Generators;
using Dotnetumentation.Core.Model;
using Microsoft.CodeAnalysis;

namespace Dotnetumentation.Core.Tests;

public class StaticDocumentationTests
{
    private static GeneratorContext Context()
    {
        var compilation = TestCompilation.Create("""
            namespace Demo;
            /// <summary>Stores documents.</summary>
            public interface IStore { }
            /// <summary>Writes documentation.</summary>
            /// <remarks>Uses <see cref="IStore"/> safely.</remarks>
            public class Writer
            {
                /// <summary>Creates a writer.</summary>
                /// <param name="store">Destination store.</param>
                public Writer(IStore store) { }
                /// <summary>Writes <paramref name="text"/>.</summary>
                /// <param name="text">Text &lt;script&gt;alert(1)&lt;/script&gt;.</param>
                /// <returns>The number of characters.</returns>
                /// <exception cref="System.ArgumentNullException">Text is null.</exception>
                public int Write(string text) => text.Length;
                public int Write(int count) => count;
                private void Secret() { }
            }
            public class Container<T> { public class Nested { } }
            public class Container { }
            """);
        Assert.Empty(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
        var types = new TypeAnalyzer(compilation, Visibility.Public).Analyze(CancellationToken.None);
        var project = new ProjectModel("Demo", "/src/Demo.csproj", "net10.0", "Demo", ["Domain"], [new("Example", "1.0")], types);
        return new(project, [], [], types.ToDictionary(t => t.Id), DiagramKinds.None);
    }

    [Fact]
    public void Reference_pages_extract_contracts_and_escape_comment_html()
    {
        var context = Context();
        var files = DocumentationGenerator.GeneratorsFor(context.Diagrams).SelectMany(g => g.Generate(context)).ToDictionary(f => f.RelativePath, f => f.Content);
        var writer = files[DocumentationPaths.TypeReference("Demo.Writer")];
        Assert.Contains("Writes documentation.", writer);
        Assert.Contains("Destination store.", writer);
        Assert.Contains("The number of characters.", writer);
        Assert.Contains("ArgumentNullException", writer);
        Assert.Contains("&lt;script&gt;", writer);
        Assert.DoesNotContain("<script>", writer);
        Assert.DoesNotContain("Secret", writer);
        Assert.Contains("Write(string)", writer);
        Assert.Contains("Write(int)", writer);
        Assert.Contains("Domain", files[DocumentationPaths.Dependencies]);
        Assert.Contains("Example", files[DocumentationPaths.Dependencies]);
        Assert.Contains("Member summary is missing", files[DocumentationPaths.Coverage]);
        Assert.DoesNotContain(".puml", string.Join("\n", files.Values));
    }

    [Fact]
    public void Every_generated_markdown_link_resolves_and_output_is_deterministic()
    {
        var context = Context();
        var generators = DocumentationGenerator.GeneratorsFor(context.Diagrams);
        var first = generators.SelectMany(g => g.Generate(context)).ToList();
        var second = generators.SelectMany(g => g.Generate(context)).ToList();
        Assert.Equal(first, second);
        Assert.Equal(first.Count, first.Select(f => f.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        var paths = first.Select(f => Path.GetFullPath(Path.Combine("/docs", f.RelativePath))).ToHashSet();
        foreach (var file in first)
        foreach (Match link in Regex.Matches(file.Content, @"\]\(([^)]+)\)"))
        {
            var target = Path.GetFullPath(Path.Combine("/docs", Path.GetDirectoryName(file.RelativePath)!, link.Groups[1].Value));
            Assert.Contains(target, paths);
        }
    }
}
