using Dotnetumentation.Core.Model;
using Dotnetumentation.Core.PlantUml;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Dotnetumentation.Core.Analysis;

/// <summary>
/// Follows the calls of a method through the methods of the solution and records them as the messages of a sequence
/// diagram. <paramref name="maxDepth"/> limits how deep calls are followed; null follows them to the end of the own
/// code. Each method's body is expanded once per diagram (later calls of it, recursion included, are shown without
/// it), so the diagram grows with the code, not with the number of paths through it. Calls into referenced assemblies (.NET, NuGet packages) are shown, but not
/// followed; lambdas are not followed, as they may run later.
/// </summary>
internal sealed class CallGraphAnalyzer(Func<SyntaxTree, SemanticModel?> semanticModels, int? maxDepth,
    int maxMessages = GeneratorOptions.DefaultMaxSequenceMessages)
{
    /// <summary>Diagrams are cut after this many messages; 0 means no limit.</summary>
    private readonly int _maxMessages = maxMessages <= 0 ? int.MaxValue : maxMessages;
    private const int MaxArgumentLength = 40;

    private readonly Dictionary<string, SequenceParticipant> _participants = [];
    private readonly HashSet<string> _participantIds = [];
    private readonly HashSet<IMethodSymbol> _expanded = new(SymbolEqualityComparer.Default);
    private int _messages;
    private int _ownCalls;
    private CancellationToken _cancellationToken;

    /// <summary>Null when the method calls nothing in the solution (only library calls are no interaction worth a diagram).</summary>
    public SequenceModel? Analyze(IMethodSymbol method, CancellationToken cancellationToken)
    {
        _participants.Clear();
        _participantIds.Clear();
        _expanded.Clear();
        _messages = 0;
        _ownCalls = 0;
        _cancellationToken = cancellationToken;

        var self = Participant(method.ContainingType);
        _expanded.Add(method.OriginalDefinition);
        var body = Walk(method, self, 0);
        if (_ownCalls == 0)
            return null;

        var steps = new List<SequenceStep> { new SequenceMessage(null, self.Id, CallText(method)) };
        steps.AddRange(body);
        if (_messages >= _maxMessages)
            steps.Add(new SequenceMessage(self.Id, self.Id, $"… (cut after {_maxMessages} messages)"));
        if (!method.ReturnsVoid && method.MethodKind != MethodKind.Constructor)
            steps.Add(new SequenceMessage(self.Id, "", Symbols.ShortName(method.ReturnType), IsReturn: true));

        return new SequenceModel(Symbols.NamespaceName(method.ContainingType), method.ContainingType.ToDisplayString(Symbols.TypeNameFormat),
            TypeAnalyzer.MethodId(method), _participants.Values.ToList(), steps);
    }

    private static bool ContainsMessage(SequenceStep step) => step switch
    {
        SequenceMessage => true,
        SequenceFragment f => f.Sections.Any(s => s.Steps.Any(ContainsMessage)),
        _ => false,
    };

    private static string CallText(IMethodSymbol method) =>
        $"{method.Name}({string.Join(", ", method.Parameters.Select(p => p.Name))})";

    private List<SequenceStep> Walk(IMethodSymbol method, SequenceParticipant self, int depth)
    {
        var (body, model) = Body(method);
        return body is null || model is null ? [] : Steps(body, self, model, depth);
    }

    /// <summary>The body (block or expression) of a source method and the semantic model of its file.</summary>
    private (SyntaxNode? Body, SemanticModel? Model) Body(IMethodSymbol method)
    {
        foreach (var reference in method.OriginalDefinition.DeclaringSyntaxReferences)
        {
            var syntax = reference.GetSyntax(_cancellationToken);
            SyntaxNode? body = syntax switch
            {
                BaseMethodDeclarationSyntax m => (SyntaxNode?)m.Body ?? m.ExpressionBody?.Expression,
                LocalFunctionStatementSyntax f => (SyntaxNode?)f.Body ?? f.ExpressionBody?.Expression,
                _ => null,
            };
            if (body is not null)
                return (body, semanticModels(syntax.SyntaxTree));
        }
        return (null, null);
    }

    private List<SequenceStep> Steps(SyntaxNode node, SequenceParticipant self, SemanticModel model, int depth)
    {
        var steps = new List<SequenceStep>();
        Collect(node, self, model, depth, steps);
        return steps;
    }

    private void Collect(SyntaxNode node, SequenceParticipant self, SemanticModel model, int depth, List<SequenceStep> steps)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        if (_messages >= _maxMessages)
            return;

        switch (node)
        {
            case AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax:
                return;

            case IfStatementSyntax @if:
            {
                var sections = new List<SequenceSection>();
                for (var current = @if; ;)
                {
                    // The first condition runs before the fragment, an else-if condition only in its own branch.
                    var body = new List<SequenceStep>();
                    Collect(current.Condition, self, model, depth, sections.Count == 0 ? steps : body);
                    Collect(current.Statement, self, model, depth, body);
                    sections.Add(new SequenceSection(Label(current.Condition), body));
                    if (current.Else?.Statement is IfStatementSyntax elseIf)
                    {
                        current = elseIf;
                        continue;
                    }
                    if (current.Else is { } @else)
                        sections.Add(new SequenceSection("else", Steps(@else.Statement, self, model, depth)));
                    break;
                }
                AddFragment(steps, sections.Count == 1 ? FragmentKind.Opt : FragmentKind.Alt, sections);
                return;
            }

            case SwitchStatementSyntax @switch:
            {
                Collect(@switch.Expression, self, model, depth, steps);
                var subject = Label(@switch.Expression);
                var sections = @switch.Sections.Select(section =>
                {
                    var label = string.Join(", ", section.Labels.Select(l => l switch
                    {
                        DefaultSwitchLabelSyntax => "default",
                        CaseSwitchLabelSyntax c => Label(c.Value),
                        CasePatternSwitchLabelSyntax p => Label(p.Pattern),
                        _ => Label(l),
                    }));
                    var sectionSteps = new List<SequenceStep>();
                    foreach (var statement in section.Statements)
                        Collect(statement, self, model, depth, sectionSteps);
                    return new SequenceSection($"{subject} is {label}", sectionSteps);
                }).ToList();
                AddFragment(steps, FragmentKind.Alt, sections);
                return;
            }

            case WhileStatementSyntax @while:
                AddFragment(steps, FragmentKind.Loop, [new SequenceSection("while " + Label(@while.Condition),
                    [.. Steps(@while.Condition, self, model, depth), .. Steps(@while.Statement, self, model, depth)])]);
                return;
            case DoStatementSyntax @do:
                AddFragment(steps, FragmentKind.Loop, [new SequenceSection("do while " + Label(@do.Condition),
                    [.. Steps(@do.Statement, self, model, depth), .. Steps(@do.Condition, self, model, depth)])]);
                return;
            case ForStatementSyntax @for:
            {
                if (@for.Declaration is not null)
                    Collect(@for.Declaration, self, model, depth, steps);
                var body = Steps(@for.Statement, self, model, depth);
                foreach (var incrementor in @for.Incrementors)
                    Collect(incrementor, self, model, depth, body);
                AddFragment(steps, FragmentKind.Loop,
                    [new SequenceSection("for " + (@for.Condition is null ? "ever" : Label(@for.Condition)), body)]);
                return;
            }
            case CommonForEachStatementSyntax @foreach:
            {
                Collect(@foreach.Expression, self, model, depth, steps);
                var variable = @foreach is ForEachStatementSyntax f ? f.Identifier.Text : Label(((ForEachVariableStatementSyntax)@foreach).Variable);
                AddFragment(steps, FragmentKind.Loop,
                    [new SequenceSection($"for each {variable} in {Label(@foreach.Expression)}", Steps(@foreach.Statement, self, model, depth))]);
                return;
            }

            case TryStatementSyntax @try:
            {
                var sections = new List<SequenceSection> { new("try", Steps(@try.Block, self, model, depth)) };
                sections.AddRange(@try.Catches.Select(c => new SequenceSection(
                    c.Declaration is null ? "catch" : "catch " + Label(c.Declaration.Type), Steps(c.Block, self, model, depth))));
                if (@try.Finally is not null)
                    sections.Add(new SequenceSection("finally", Steps(@try.Finally.Block, self, model, depth)));
                AddFragment(steps, FragmentKind.Group, sections);
                return;
            }

            case InvocationExpressionSyntax invocation:
                // Receiver and arguments are evaluated before the call.
                Collect(invocation.Expression, self, model, depth, steps);
                Collect(invocation.ArgumentList, self, model, depth, steps);
                Call(invocation, invocation.ArgumentList, self, model, depth, steps);
                return;

            case BaseObjectCreationExpressionSyntax creation:
                foreach (var child in creation.ChildNodes())
                    Collect(child, self, model, depth, steps);
                Call(creation, creation.ArgumentList, self, model, depth, steps);
                return;

            default:
                foreach (var child in node.ChildNodes())
                    Collect(child, self, model, depth, steps);
                return;
        }
    }

    private static void AddFragment(List<SequenceStep> steps, FragmentKind kind, List<SequenceSection> sections)
    {
        if (!sections.Any(s => s.Steps.Any(ContainsMessage)))
            return;
        // Empty branches of an alt are dropped; the remaining ones keep their own conditions.
        if (kind == FragmentKind.Alt)
            sections = sections.Where(s => s.Steps.Count > 0).ToList();
        steps.Add(new SequenceFragment(kind, sections));
    }

    private void Call(ExpressionSyntax call, ArgumentListSyntax? arguments, SequenceParticipant self, SemanticModel model,
        int depth, List<SequenceStep> steps)
    {
        var info = model.GetSymbolInfo(call, _cancellationToken);
        if ((info.Symbol ?? info.CandidateSymbols.FirstOrDefault()) is not IMethodSymbol method)
            return;
        method = method.ReducedFrom ?? method;
        if (method.MethodKind is MethodKind.DelegateInvoke or MethodKind.LocalFunction || method.ContainingType is null)
            return;

        var isExternal = !Symbols.IsSolutionType(method.ContainingType);
        var isCreate = method.MethodKind == MethodKind.Constructor;
        // Creating records, structs and library objects is data handling, not an interaction worth a message.
        if (isCreate && (isExternal || method.ContainingType.IsRecord || method.ContainingType.IsValueType))
            return;

        var target = Participant(method.ContainingType, isExternal);
        var args = arguments is null ? "" : string.Join(", ", arguments.Arguments.Select(a => PlantUmlWriter.Truncate(Label(a), MaxArgumentLength)));
        var isAsync = call.Ancestors().TakeWhile(a => a is not StatementSyntax and not AnonymousFunctionExpressionSyntax)
            .Any(a => a is AwaitExpressionSyntax);
        var text = isCreate ? $"new({args})" : $"{method.Name}({args})";
        steps.Add(new SequenceMessage(self.Id, target.Id, isAsync ? "await " + text : text, IsAsync: isAsync, IsCreate: isCreate));
        _messages++;
        if (!isExternal)
            _ownCalls++;

        var definition = method.OriginalDefinition;
        // Library code is the end of the chain: the call is shown, its body is not explored.
        if (!isExternal && !isCreate && (maxDepth is null || depth < maxDepth) && _expanded.Add(definition))
            steps.AddRange(Walk(method, target, depth + 1));
        if (!method.ReturnsVoid && !isCreate && target.Id != self.Id)
            steps.Add(new SequenceMessage(target.Id, self.Id, Symbols.ShortName(method.ReturnType), IsReturn: true));
    }

    private SequenceParticipant Participant(INamedTypeSymbol type, bool isExternal = false)
    {
        type = type.OriginalDefinition;
        var key = Symbols.TypeId(type);
        if (_participants.TryGetValue(key, out var participant))
            return participant;

        var name = type.ToDisplayString(Symbols.TypeNameFormat);
        var id = PlantUmlWriter.Id(name);
        // Types with the same name in different namespaces get numbered ids.
        for (var i = 2; !_participantIds.Add(id); i++)
            id = PlantUmlWriter.Id(name) + "_" + i;
        var kind = isExternal ? ParticipantKind.External
            : type.TypeKind == Microsoft.CodeAnalysis.TypeKind.Interface || type.IsAbstract ? ParticipantKind.Interface
            : ParticipantKind.Participant;
        participant = new SequenceParticipant(id, name, kind);
        _participants[key] = participant;
        return participant;
    }

    private static string Label(SyntaxNode node) => ControlFlowAnalyzer.Text(node);
}
