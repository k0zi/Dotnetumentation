using System.Xml;
using System.Xml.Linq;
using Dotnetumentation.Core.Model;
using Microsoft.CodeAnalysis;

namespace Dotnetumentation.Core.Analysis;

/// <summary>Reads explicitly written XML comments without inferring undocumented behavior.</summary>
internal static class XmlDocumentation
{
    public static DocumentationComment Read(ISymbol symbol)
    {
        var xml = symbol.GetDocumentationCommentXml();
        if (string.IsNullOrWhiteSpace(xml))
            return new();
        try
        {
            var root = XElement.Parse(xml);
            return new DocumentationComment
            {
                Summary = Text(root.Element("summary")),
                Remarks = Text(root.Element("remarks")),
                Returns = Text(root.Element("returns")),
                Value = Text(root.Element("value")),
                Parameters = root.Elements("param").Select(e => new NamedDocumentation(e.Attribute("name")?.Value ?? "", Text(e) ?? "")).ToList(),
                TypeParameters = root.Elements("typeparam").Select(e => new NamedDocumentation(e.Attribute("name")?.Value ?? "", Text(e) ?? "")).ToList(),
                Exceptions = root.Elements("exception").Select(e => new NamedDocumentation(e.Attribute("cref")?.Value ?? "", Text(e) ?? "")).ToList(),
            };
        }
        catch (XmlException)
        {
            return new();
        }
    }

    private static string? Text(XElement? element)
    {
        if (element is null)
            return null;
        var text = PlantUml.PlantUmlWriter.SingleLine(string.Concat(element.Nodes().Select(NodeText)));
        return text.Length == 0 ? null : text;
    }

    private static string NodeText(XNode node) => node switch
    {
        XText text => text.Value,
        XElement { Name.LocalName: "see" or "seealso" } e => e.Attribute("langword")?.Value ?? e.Attribute("cref")?.Value ?? e.Value,
        XElement { Name.LocalName: "paramref" or "typeparamref" } e => e.Attribute("name")?.Value ?? "",
        XElement { Name.LocalName: "para" } e => " " + string.Concat(e.Nodes().Select(NodeText)) + " ",
        XElement e => string.Concat(e.Nodes().Select(NodeText)),
        _ => "",
    };
}
