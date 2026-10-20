using Dotnetumentation.Core.Model;
using Dotnetumentation.Core.PlantUml;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Dotnetumentation.Core.Analysis;

/// <summary>Turns a method body into a tree of activity nodes, from the syntax only.</summary>
internal static class ControlFlowAnalyzer
{
    private const int MaxTextLength = 80;
    /// <summary>Consecutive simple statements are merged into one action of at most this many lines.</summary>
    private const int MaxActionLines = 5;

    /// <summary>Null when the method has no body (abstract, extern, partial definition).</summary>
    public static IReadOnlyList<ActivityNode>? Analyze(BaseMethodDeclarationSyntax method, bool returnsValue)
    {
        if (method.Body is { } body)
            return Statements(body.Statements);
        if (method.ExpressionBody is { } arrow)
        {
            var text = Text(arrow.Expression);
            return returnsValue ? [new StopNode($"return {text}")] : [new ActionNode(text)];
        }
        return null;
    }

    /// <summary>The method has branches or loops, or enough statements to be worth a diagram.</summary>
    public static bool IsInteresting(IReadOnlyList<ActivityNode> nodes) =>
        nodes.Any(n => n is IfNode or WhileNode or RepeatNode or TryNode) || CountStatements(nodes) >= 3;

    private static int CountStatements(IReadOnlyList<ActivityNode> nodes) =>
        nodes.Sum(n => n is ActionNode a ? a.Text.Count(c => c == '\n') + 1 : 1);

    private static List<ActivityNode> Statements(IEnumerable<StatementSyntax> statements)
    {
        var nodes = new List<ActivityNode>();
        var pending = new List<string>();

        void Flush()
        {
            for (var i = 0; i < pending.Count; i += MaxActionLines)
                nodes.Add(new ActionNode(string.Join("\n", pending.Skip(i).Take(MaxActionLines))));
            pending.Clear();
        }

        foreach (var statement in statements)
        {
            if (Simple(statement) is { } text)
            {
                pending.Add(text);
                continue;
            }
            Flush();
            nodes.AddRange(Complex(statement));
        }
        Flush();
        return nodes;
    }

    /// <summary>The text of a statement that is a plain step of the flow, null for control flow statements.</summary>
    private static string? Simple(StatementSyntax statement) => statement switch
    {
        ExpressionStatementSyntax e => Text(e.Expression),
        LocalDeclarationStatementSyntax d => (d.UsingKeyword.IsKind(SyntaxKind.UsingKeyword) ? "using " : "") + Text(d.Declaration),
        BreakStatementSyntax => "break",
        ContinueStatementSyntax => "continue",
        GotoStatementSyntax g => Text(g),
        YieldStatementSyntax { RawKind: (int)SyntaxKind.YieldReturnStatement, Expression: { } value } => "yield return " + Text(value),
        EmptyStatementSyntax => null,
        _ => null,
    };

    private static IEnumerable<ActivityNode> Complex(StatementSyntax statement)
    {
        switch (statement)
        {
            case BlockSyntax block:
                return Statements(block.Statements);
            case IfStatementSyntax @if:
                return [If(@if)];
            case SwitchStatementSyntax @switch:
                return [Switch(@switch)];
            case WhileStatementSyntax @while:
                return [new WhileNode(Text(@while.Condition), "true", Body(@while.Statement))];
            case DoStatementSyntax @do:
                return [new RepeatNode(Text(@do.Condition), Body(@do.Statement))];
            case ForStatementSyntax @for:
                return For(@for);
            case ForEachStatementSyntax @foreach:
                return [new WhileNode($"for each {@foreach.Identifier.Text} in {Text(@foreach.Expression)}", null, Body(@foreach.Statement))];
            case ForEachVariableStatementSyntax @foreach:
                return [new WhileNode($"for each {Text(@foreach.Variable)} in {Text(@foreach.Expression)}", null, Body(@foreach.Statement))];
            case TryStatementSyntax @try:
                return [Try(@try)];
            case ReturnStatementSyntax @return:
                return [new StopNode(@return.Expression is null ? null : "return " + Text(@return.Expression))];
            case ThrowStatementSyntax @throw:
                return [new EndNode(@throw.Expression is null ? "throw" : "throw " + Text(@throw.Expression))];
            case YieldStatementSyntax:
                return [new StopNode("yield break")];
            case UsingStatementSyntax @using:
                return Prefixed($"using ({Text((SyntaxNode?)@using.Declaration ?? @using.Expression!)})", @using.Statement);
            case LockStatementSyntax @lock:
                return Prefixed($"lock ({Text(@lock.Expression)})", @lock.Statement);
            case FixedStatementSyntax @fixed:
                return Prefixed($"fixed ({Text(@fixed.Declaration)})", @fixed.Statement);
            case CheckedStatementSyntax @checked:
                return Statements(@checked.Block.Statements);
            case UnsafeStatementSyntax @unsafe:
                return Statements(@unsafe.Block.Statements);
            case LabeledStatementSyntax labeled:
                return Prefixed(labeled.Identifier.Text + ":", labeled.Statement);
            case LocalFunctionStatementSyntax:
            case EmptyStatementSyntax:
                return [];
            default:
                return [new ActionNode(Text(statement))];
        }
    }

    private static IEnumerable<ActivityNode> Prefixed(string text, StatementSyntax body) =>
        [new ActionNode(text), .. Body(body)];

    private static List<ActivityNode> Body(StatementSyntax statement) =>
        statement is BlockSyntax block ? Statements(block.Statements) : Statements([statement]);

    private static IfNode If(IfStatementSyntax statement)
    {
        var branches = new List<ActivityBranch>();
        for (var current = statement; ;)
        {
            branches.Add(new ActivityBranch(Text(current.Condition), "yes", Body(current.Statement)));
            if (current.Else?.Statement is IfStatementSyntax elseIf)
            {
                current = elseIf;
                continue;
            }
            if (current.Else is { } @else)
                branches.Add(new ActivityBranch(null, "no", Body(@else.Statement)));
            break;
        }
        return new IfNode(branches);
    }

    private static IfNode Switch(SwitchStatementSyntax statement)
    {
        var subject = Text(statement.Expression);
        var branches = new List<ActivityBranch>();
        ActivityBranch? defaultBranch = null;
        foreach (var section in statement.Sections)
        {
            // A trailing break only ends the section.
            var statements = section.Statements.ToList();
            if (statements.Count > 0 && statements[^1] is BreakStatementSyntax)
                statements.RemoveAt(statements.Count - 1);
            var body = Statements(statements);

            var labels = section.Labels.Where(l => l is not DefaultSwitchLabelSyntax).Select(l => l switch
            {
                CaseSwitchLabelSyntax c => Text(c.Value),
                CasePatternSwitchLabelSyntax p => Text(p.Pattern) + (p.WhenClause is null ? "" : " when " + Text(p.WhenClause.Condition)),
                _ => Text(l),
            }).ToList();

            if (section.Labels.Any(l => l is DefaultSwitchLabelSyntax))
                defaultBranch = new ActivityBranch(null, "default", body);
            else
                branches.Add(new ActivityBranch($"{subject} is {string.Join(" or ", labels)}", "yes", body));
        }
        if (branches.Count == 0)
            return new IfNode(defaultBranch is null ? [] : [defaultBranch with { Condition = "true" }]);
        if (defaultBranch is not null)
            branches.Add(defaultBranch);
        return new IfNode(branches);
    }

    private static IEnumerable<ActivityNode> For(ForStatementSyntax statement)
    {
        var nodes = new List<ActivityNode>();
        var initializers = new List<string>();
        if (statement.Declaration is { } declaration)
            initializers.Add(Text(declaration));
        initializers.AddRange(statement.Initializers.Select(Text));
        if (initializers.Count > 0)
            nodes.Add(new ActionNode(string.Join("\n", initializers)));

        var body = Body(statement.Statement);
        if (statement.Incrementors.Count > 0)
            body.Add(new ActionNode(string.Join("\n", statement.Incrementors.Select(Text))));
        nodes.Add(new WhileNode(statement.Condition is null ? "true" : Text(statement.Condition), "true", body));
        return nodes;
    }

    private static TryNode Try(TryStatementSyntax statement)
    {
        var catches = statement.Catches.Select(c =>
        {
            var exception = c.Declaration is null ? "exception" : Text(c.Declaration.Type);
            if (c.Filter is not null)
                exception += $" when {Text(c.Filter.FilterExpression)}";
            return new ActivityBranch(exception, "catch", Statements(c.Block.Statements));
        }).ToList();
        return new TryNode(Statements(statement.Block.Statements), catches,
            statement.Finally is null ? null : Statements(statement.Finally.Block.Statements));
    }

    /// <summary>The source text of a node on one line, without comments, shortened.</summary>
    public static string Text(SyntaxNode node)
    {
        var comments = node.DescendantTrivia().Where(t => t.IsKind(SyntaxKind.SingleLineCommentTrivia)
                                                          || t.IsKind(SyntaxKind.MultiLineCommentTrivia)
                                                          || t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia));
        var clean = node.ReplaceTrivia(comments, (_, _) => SyntaxFactory.Space).WithoutTrivia();
        return PlantUmlWriter.Truncate(PlantUmlWriter.SingleLine(clean.ToString()), MaxTextLength);
    }
}
