using System.Text;

namespace Dotnetumentation.Core.PlantUml;

/// <summary>Builds PlantUML text with indentation; always produces <c>\n</c> line endings.</summary>
public sealed class PlantUmlWriter
{
    private readonly StringBuilder _text = new();
    private int _indent;

    /// <summary>
    /// <paramref name="kind"/> is written as a <c>' rice:kind=…</c> comment: PlantUML ignores it, KD.Avalonia.Rice reads the
    /// diagram type from it instead of guessing it from the content.
    /// </summary>
    public PlantUmlWriter StartUml(string? title = null, string? kind = null)
    {
        Line("@startuml");
        if (kind is not null)
            Line($"' rice:kind={kind}");
        if (!string.IsNullOrWhiteSpace(title))
            Line($"title {SingleLine(title)}");
        return this;
    }

    public PlantUmlWriter EndUml() => Line("@enduml");

    public PlantUmlWriter Line(string text = "")
    {
        if (text.Length > 0)
            _text.Append(' ', _indent * 2);
        _text.Append(EscapeComments(text)).Append('\n');
        return this;
    }

    /// <summary>
    /// <c>/'</c> and <c>'/</c> open and close a block comment anywhere in a line, so a zero width space is put between
    /// the two characters when code text contains them.
    /// </summary>
    private static string EscapeComments(string text) =>
        text.Contains("/'") || text.Contains("'/") ? text.Replace("/'", "/\u200B'").Replace("'/", "'\u200B/") : text;

    /// <summary>Writes <paramref name="header"/>, indents what <paramref name="body"/> writes, then writes <paramref name="footer"/>.</summary>
    public PlantUmlWriter Block(string header, Action body, string footer = "}")
    {
        Line(header);
        _indent++;
        body();
        _indent--;
        return Line(footer);
    }

    public PlantUmlWriter Indented(Action body)
    {
        _indent++;
        body();
        _indent--;
        return this;
    }

    public override string ToString() => _text.ToString();

    /// <summary>A text inside double quotes, on one line; quotes inside become apostrophes.</summary>
    public static string Quote(string text) => $"\"{SingleLine(text).Replace('"', '\'')}\"";

    /// <summary>A label text: backslashes are doubled, so <c>\n</c> in code is not read as a line break.</summary>
    public static string Text(string text) => SingleLine(text).Replace("\\", "\\\\");

    /// <summary>A text inside <c>( … )</c> (if conditions, branch labels): no line breaks, no closing parenthesis.</summary>
    public static string Parenthesized(string text) => Text(text).Replace(')', ']').Replace('(', '[');

    /// <summary>Collapses whitespace runs (including line breaks) into one space.</summary>
    public static string SingleLine(string text)
    {
        var sb = new StringBuilder(text.Length);
        var space = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                space = sb.Length > 0;
                continue;
            }
            if (space)
                sb.Append(' ');
            space = false;
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>Shortens <paramref name="text"/> to <paramref name="max"/> characters with an ellipsis.</summary>
    public static string Truncate(string text, int max) => text.Length <= max ? text : text[..(max - 1)].TrimEnd() + "…";

    /// <summary>An identifier PlantUML accepts without quotes: letters, digits and underscores.</summary>
    public static string Id(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
            sb.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');
        if (sb.Length == 0 || char.IsDigit(sb[0]))
            sb.Insert(0, '_');
        return sb.ToString();
    }

    /// <summary>A file name without characters that are invalid on any platform.</summary>
    public static string FileName(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            if (char.IsLetterOrDigit(c) || c is '_' or '-' or '.')
                sb.Append(c);
            else if (c is ',' or ' ' or '(' or '<' or '[' && (sb.Length == 0 || sb[^1] != '_'))
                sb.Append('_');
        }
        var result = sb.ToString().Trim('_', '.');
        if (result.Length > 120)
            result = result[..110] + "_" + StableHash(name);
        return result.Length == 0 ? "_" : result;
    }

    /// <summary>A short hash that is the same in every run (unlike <see cref="string.GetHashCode()"/>).</summary>
    public static string StableHash(string text)
    {
        var hash = 2166136261u;
        foreach (var c in text)
            hash = (hash ^ c) * 16777619u;
        return hash.ToString("x8");
    }
}
