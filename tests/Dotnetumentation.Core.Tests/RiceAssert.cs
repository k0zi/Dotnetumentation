using KD.Avalonia.Rice.Controls.Uml;

namespace Dotnetumentation.Core.Tests;

/// <summary>Checks generated PlantUML with the parser of KD.Avalonia.Rice, which the GUI uses to show it.</summary>
internal static class RiceAssert
{
    public static UmlDiagram Parses(string plantUml, UmlDiagramKind kind)
    {
        var document = UmlDocument.Parse(plantUml);
        var problems = document.Diagnostics.Where(d => d.Severity != UmlDiagnosticSeverity.Info).ToList();
        Assert.True(problems.Count == 0, string.Join("\n", problems) + "\n\n" + plantUml);
        var diagram = Assert.Single(document.Diagrams);
        Assert.Equal(kind, diagram.Kind);
        return diagram;
    }
}
