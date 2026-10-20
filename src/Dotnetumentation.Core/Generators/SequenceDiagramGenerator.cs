using Dotnetumentation.Core.Model;
using Dotnetumentation.Core.PlantUml;

namespace Dotnetumentation.Core.Generators;

/// <summary>One sequence diagram per method: the calls it makes into the solution, with branches and loops.</summary>
public sealed class SequenceDiagramGenerator : IDiagramGenerator
{
    public IEnumerable<GeneratedFile> Generate(GeneratorContext context) =>
        context.Sequences.Select(s => new GeneratedFile(DocumentationPaths.Sequence(s.TypeName, s.Namespace, s.MethodId), Diagram(s)));

    public static string Diagram(SequenceModel sequence)
    {
        var writer = new PlantUmlWriter().StartUml($"{sequence.TypeName}.{sequence.MethodId}", "sequence");
        writer.Line("hide footbox");
        foreach (var participant in sequence.Participants)
        {
            var keyword = participant.Kind == ParticipantKind.Interface ? "boundary" : "participant";
            var stereotype = participant.Kind == ParticipantKind.External ? " <<external>>" : "";
            writer.Line($"{keyword} {PlantUmlWriter.Quote(participant.Name)} as {participant.Id}{stereotype}");
        }
        Write(writer, sequence.Steps);
        return writer.EndUml().ToString();
    }

    private static void Write(PlantUmlWriter writer, IReadOnlyList<SequenceStep> steps)
    {
        foreach (var step in steps)
        {
            switch (step)
            {
                case SequenceMessage message:
                    writer.Line(Message(message));
                    break;
                case SequenceFragment fragment:
                    var keyword = fragment.Kind.ToString().ToLowerInvariant();
                    for (var i = 0; i < fragment.Sections.Count; i++)
                    {
                        var section = fragment.Sections[i];
                        var label = section.Label is null ? "" : " " + PlantUmlWriter.Text(section.Label);
                        writer.Line((i == 0 ? keyword : "else") + label);
                        writer.Indented(() => Write(writer, section.Steps));
                    }
                    writer.Line("end");
                    break;
            }
        }
    }

    private static string Message(SequenceMessage message)
    {
        // The diagram border stands for the unknown caller of the documented method.
        var from = message.From ?? "[";
        var to = message.To.Length == 0 ? "[" : message.To;
        var arrow = message.IsReturn ? "-->" : "->";
        var text = PlantUmlWriter.Text(message.Text);
        if (to == "[")
            return $"[<{arrow[..^1]} {from} : {text}";
        if (from == "[")
            return $"[{arrow} {to} : {text}";
        return $"{from} {arrow} {to} : {text}";
    }
}
