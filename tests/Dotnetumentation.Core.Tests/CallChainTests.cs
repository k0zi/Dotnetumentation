using Dotnetumentation.Core.Analysis;
using Dotnetumentation.Core.Generators;
using KD.Avalonia.Rice.Controls.Uml;
using Microsoft.CodeAnalysis;

namespace Dotnetumentation.Core.Tests;

public class CallChainTests
{
    /// <summary>Step0 → Step1 → … → Step9 → Console.WriteLine, plus a recursive method.</summary>
    private static readonly string Source = "namespace Chain;\npublic class Steps\n{\n"
        + string.Concat(Enumerable.Range(0, 9).Select(i => $"    public void Step{i}() => Step{i + 1}();\n"))
        + "    public void Step9() { var text = string.Join(\",\", new[] { \"a\" }); System.Console.WriteLine(text); }\n"
        + "    public int Countdown(int n) => n == 0 ? 0 : Countdown(n - 1);\n"
        + "}\n";

    private static string Diagram(string method, int? maxDepth)
    {
        var compilation = TestCompilation.Create(Source);
        Assert.Empty(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
        var symbol = compilation.GetTypeByMetadataName("Chain.Steps")!.GetMembers(method).OfType<IMethodSymbol>().Single();
        var sequence = new CallGraphAnalyzer(t => compilation.GetSemanticModel(t), maxDepth).Analyze(symbol, CancellationToken.None)!;
        return SequenceDiagramGenerator.Diagram(sequence);
    }

    [Fact]
    public void Without_a_limit_the_whole_chain_is_followed_to_the_library_call()
    {
        var text = Diagram("Step0", maxDepth: null);

        for (var i = 1; i <= 9; i++)
            Assert.Contains($"Steps -> Steps : Step{i}()", text);
        Assert.Contains("participant \"Console\" as Console <<external>>", text);
        Assert.Contains("Steps -> Console : WriteLine(text)", text);
        Assert.Contains("Steps -> string : Join(\",\", new[] { \"a\" })", text);
        Assert.Contains("string --> Steps : string", text);
        RiceAssert.Parses(text, UmlDiagramKind.Sequence);
    }

    [Fact]
    public void A_limit_stops_following_the_own_code()
    {
        var text = Diagram("Step0", maxDepth: 3);

        // Three levels of bodies are expanded: the call of Step4 is shown, its body is not.
        Assert.Contains("Steps -> Steps : Step4()", text);
        Assert.DoesNotContain("Step5()", text);
        Assert.DoesNotContain("Console", text);
    }

    [Fact]
    public void Recursion_is_followed_once()
    {
        var text = Diagram("Countdown", maxDepth: null);
        Assert.Single(text.Split('\n'), l => l.Contains("Steps -> Steps : Countdown(n - 1)"));
    }
}
