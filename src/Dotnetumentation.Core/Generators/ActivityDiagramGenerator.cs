using Dotnetumentation.Core.Model;
using Dotnetumentation.Core.PlantUml;

namespace Dotnetumentation.Core.Generators;

/// <summary>One activity diagram per method: the control flow of its body.</summary>
public sealed class ActivityDiagramGenerator : IDiagramGenerator
{
    public IEnumerable<GeneratedFile> Generate(GeneratorContext context) =>
        context.Activities.Select(a => new GeneratedFile(DocumentationPaths.Activity(a.TypeName, a.Namespace, a.MethodId), Diagram(a)));

    public static string Diagram(ActivityModel activity)
    {
        var writer = new PlantUmlWriter().StartUml($"{activity.TypeName}.{activity.MethodId}", "activity");
        writer.Line("start");
        Write(writer, activity.Body);
        if (activity.Body.LastOrDefault() is not (StopNode or EndNode))
            writer.Line("stop");
        return writer.EndUml().ToString();
    }

    private static void Write(PlantUmlWriter writer, IReadOnlyList<ActivityNode> nodes)
    {
        foreach (var node in nodes)
            Write(writer, node);
    }

    private static void Write(PlantUmlWriter writer, ActivityNode node)
    {
        switch (node)
        {
            case ActionNode action:
                Action(writer, action.Text);
                break;

            case IfNode { Branches.Count: > 0 } @if:
                for (var i = 0; i < @if.Branches.Count; i++)
                {
                    var branch = @if.Branches[i];
                    var label = branch.Label is null ? "" : $" ({PlantUmlWriter.Parenthesized(branch.Label)})";
                    if (i == 0)
                        writer.Line($"if ({PlantUmlWriter.Text(branch.Condition ?? "true")}) then{label}");
                    else if (branch.Condition is not null)
                        writer.Line($"elseif ({PlantUmlWriter.Text(branch.Condition)}) then{label}");
                    else
                        writer.Line($"else{label}");
                    writer.Indented(() => Write(writer, branch.Body));
                }
                writer.Line("endif");
                break;

            case WhileNode loop:
                writer.Line($"while ({PlantUmlWriter.Text(loop.Condition)})" + (loop.Label is null ? "" : $" is ({PlantUmlWriter.Parenthesized(loop.Label)})"));
                writer.Indented(() => Write(writer, loop.Body));
                writer.Line("endwhile");
                break;

            case RepeatNode repeat:
                writer.Line("repeat");
                writer.Indented(() => Write(writer, repeat.Body));
                writer.Line($"repeat while ({PlantUmlWriter.Text(repeat.Condition)})");
                break;

            case TryNode @try:
                writer.Block("partition \"try\" {", () => Write(writer, @try.Body));
                for (var i = 0; i < @try.Catches.Count; i++)
                {
                    var @catch = @try.Catches[i];
                    writer.Line($"{(i == 0 ? "if" : "elseif")} ({PlantUmlWriter.Text(@catch.Condition ?? "exception")} thrown?) then (catch)");
                    writer.Indented(() => Write(writer, @catch.Body));
                }
                if (@try.Catches.Count > 0)
                    writer.Line("endif");
                if (@try.Finally is { Count: > 0 } @finally)
                    writer.Block("partition \"finally\" {", () => Write(writer, @finally));
                break;

            case StopNode stop:
                if (stop.Text is not null)
                    Action(writer, stop.Text);
                writer.Line("stop");
                break;

            case EndNode end:
                if (end.Text is not null)
                    Action(writer, end.Text);
                writer.Line("end");
                break;
        }
    }

    /// <summary>A multi-line action is written on one line with <c>\n</c>, so no line can end it early.</summary>
    private static void Action(PlantUmlWriter writer, string text)
    {
        var lines = text.Split('\n').Select(PlantUmlWriter.Text).Where(l => l.Length > 0).ToList();
        if (lines.Count > 0)
            writer.Line($":{string.Join("\\n", lines)};");
    }
}
