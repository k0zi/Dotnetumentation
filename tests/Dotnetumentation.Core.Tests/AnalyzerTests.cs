using Dotnetumentation.Core.Analysis;
using Dotnetumentation.Core.Generators;
using Dotnetumentation.Core.Model;
using KD.Avalonia.Rice.Controls.Uml;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Dotnetumentation.Core.Tests;

public class AnalyzerTests
{
    private const string Source = """
        namespace Shop;

        public interface IStore { void Save(Order order); }

        public abstract class Entity { public int Id { get; set; } }

        public class Order : Entity
        {
            public List<Line> Lines { get; } = new();
            public Customer? Owner { get; set; }
            public static Order Empty() => new();
        }

        public record Line(string Product, int Quantity);

        public class Customer : Entity { }

        public class Checkout(IStore store)
        {
            public int Run(Order order)
            {
                var count = 0;
                if (order.Lines.Count == 0)
                    return -1;
                else if (order.Owner is null)
                    throw new InvalidOperationException("no owner");
                for (var i = 0; i < order.Lines.Count; i++)
                    count += Check(order.Lines[i]);
                switch (count)
                {
                    case 0: Log("none"); break;
                    case > 10: Log("many"); break;
                    default: break;
                }
                do { count--; } while (count > 5);
                try { store.Save(order); }
                catch (IOException e) when (e.HResult != 0) { Log(e.Message); }
                finally { Log("done"); }
                return count;
            }

            private int Check(Line line) => line.Quantity > 0 ? 1 : 0;

            private void Log(string text) { }
        }
        """;

    private static (Compilation Compilation, TypeAnalyzer Analyzer) Analyze()
    {
        var compilation = TestCompilation.Create("global using System; global using System.IO; global using System.Collections.Generic;", Source);
        Assert.Empty(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
        return (compilation, new TypeAnalyzer(compilation, Visibility.Internal));
    }

    [Fact]
    public void Types_members_and_relations_are_found()
    {
        var types = Analyze().Analyzer.Analyze(CancellationToken.None).ToDictionary(t => t.Name);

        Assert.Equal(["Checkout", "Customer", "Entity", "IStore", "Line", "Order"], types.Keys.Order());
        Assert.True(types["Entity"].IsAbstract);
        Assert.Equal(Model.TypeKind.Record, types["Line"].Kind);
        Assert.Contains(types["Order"].Members, m => m.Signature == "Empty() : Order" && m.IsStatic);
        // Private members are below the default visibility.
        Assert.DoesNotContain(types["Checkout"].Members, m => m.Name == "Check");

        var order = types["Order"].Relations;
        Assert.Contains(new RelationModel("Shop.Order", "Shop.Entity", RelationKind.Generalization), order);
        Assert.Contains(new RelationModel("Shop.Order", "Shop.Line", RelationKind.Aggregation, "Lines"), order);
        Assert.Contains(new RelationModel("Shop.Order", "Shop.Customer", RelationKind.Association, "Owner"), order);
        Assert.Contains(new RelationModel("Shop.Checkout", "Shop.IStore", RelationKind.Association, "store"), types["Checkout"].Relations);
    }

    [Fact]
    public void Class_diagram_is_readable_by_rice()
    {
        var types = Analyze().Analyzer.Analyze(CancellationToken.None);
        var text = ClassDiagramGenerator.Diagram("Shop", types, types.ToDictionary(t => t.Id));

        var diagram = (UmlGraphDiagram)RiceAssert.Parses(text, UmlDiagramKind.Class);
        Assert.Contains(diagram.AllElements(), e => e.Name == "Order");
        Assert.Contains(diagram.Relations, r => r.Kind == UmlRelationKind.Generalization);
    }

    [Fact]
    public void Activity_diagram_follows_the_control_flow()
    {
        var method = Analyze().Compilation.SyntaxTrees.ElementAt(1).GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>()
            .First(m => m.Identifier.Text == "Run");
        var body = ControlFlowAnalyzer.Analyze(method, returnsValue: true)!;
        Assert.True(ControlFlowAnalyzer.IsInteresting(body));

        var text = ActivityDiagramGenerator.Diagram(new ActivityModel("Shop", "Checkout", "Run(Order)", body));
        Assert.Contains("if (order.Lines.Count == 0) then (yes)", text);
        Assert.Contains("elseif (order.Owner is null) then (yes)", text);
        Assert.Contains("while (i < order.Lines.Count) is (true)", text);
        Assert.Contains("if (count is 0) then (yes)", text);
        Assert.Contains("repeat while (count > 5)", text);
        Assert.Contains("if (IOException when e.HResult != 0 thrown?) then (catch)", text);
        Assert.Contains(":throw new InvalidOperationException(\"no owner\");", text);
        RiceAssert.Parses(text, UmlDiagramKind.Activity);
    }

    [Fact]
    public void Sequence_diagram_follows_calls_into_the_solution()
    {
        var (compilation, _) = Analyze();
        var checkout = compilation.GetTypeByMetadataName("Shop.Checkout")!;
        var run = checkout.GetMembers("Run").OfType<IMethodSymbol>().Single();

        var sequence = new CallGraphAnalyzer(t => compilation.GetSemanticModel(t), maxDepth: 3).Analyze(run, CancellationToken.None)!;
        var text = SequenceDiagramGenerator.Diagram(sequence);

        Assert.Contains("[-> Checkout : Run(order)", text);
        Assert.Contains("Checkout -> IStore : Save(order)", text);
        Assert.Contains("Checkout -> Checkout : Check(order.Lines[i])", text);
        Assert.Contains("loop for i < order.Lines.Count", text);
        Assert.Contains("group try", text);
        Assert.Contains("[<-- Checkout : int", text);
        // Creating the record is not an interaction.
        Assert.DoesNotContain("Line : new", text);
        RiceAssert.Parses(text, UmlDiagramKind.Sequence);
    }

    [Fact]
    public void Methods_without_calls_into_the_solution_have_no_sequence_diagram()
    {
        var (compilation, _) = Analyze();
        var check = compilation.GetTypeByMetadataName("Shop.Checkout")!.GetMembers("Check").OfType<IMethodSymbol>().Single();
        Assert.Null(new CallGraphAnalyzer(t => compilation.GetSemanticModel(t), 3).Analyze(check, CancellationToken.None));
    }
}
