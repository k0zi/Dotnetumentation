using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Dotnetumentation.Core.Tests;

/// <summary>In-memory compilations of C# snippets against the running .NET, without MSBuild.</summary>
internal static class TestCompilation
{
    private static readonly MetadataReference[] References =
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
        .Split(Path.PathSeparator)
        .Where(p => Path.GetFileName(p).StartsWith("System.", StringComparison.Ordinal) || Path.GetFileName(p) is "mscorlib.dll" or "netstandard.dll")
        .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
        .ToArray();

    public static CSharpCompilation Create(params string[] sources) =>
        CSharpCompilation.Create("Test",
            sources.Select((s, i) => CSharpSyntaxTree.ParseText(s, path: $"/src/File{i}.cs")),
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
}
