using System.Xml.Linq;
using Dotnetumentation.Core.Model;

namespace Dotnetumentation.Core.Analysis;

/// <summary>
/// Reads the NuGet references of a project file. Versions missing from the project come from the nearest
/// <c>Directory.Packages.props</c> (central package management); <c>$(Property)</c> references in versions are
/// resolved from the properties of the project file and the <c>Directory.Build.props</c> files above it.
/// </summary>
internal static class PackageReferenceReader
{
    public static IReadOnlyList<PackageReferenceModel> Read(string projectFile)
    {
        var project = TryLoad(projectFile);
        if (project is null)
            return [];

        Dictionary<string, string>? centralVersions = null;
        Dictionary<string, string>? properties = null;
        var result = new List<PackageReferenceModel>();
        foreach (var reference in project.Descendants().Where(e => e.Name.LocalName == "PackageReference"))
        {
            var name = reference.Attribute("Include")?.Value ?? reference.Attribute("Update")?.Value;
            if (string.IsNullOrWhiteSpace(name))
                continue;
            var version = reference.Attribute("Version")?.Value
                          ?? reference.Elements().FirstOrDefault(e => e.Name.LocalName == "Version")?.Value
                          ?? reference.Attribute("VersionOverride")?.Value;
            if (version is null)
            {
                centralVersions ??= CentralVersions(Path.GetDirectoryName(projectFile));
                version = centralVersions.GetValueOrDefault(name);
            }
            if (version?.Contains("$(") == true)
            {
                properties ??= Properties(projectFile, project);
                version = Expand(version, properties);
            }
            result.Add(new PackageReferenceModel(name.Trim(), version?.Trim()));
        }
        return result.DistinctBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static Dictionary<string, string> CentralVersions(string? directory)
    {
        var versions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var dir = directory; dir is not null; dir = Path.GetDirectoryName(dir))
        {
            var file = Path.Combine(dir, "Directory.Packages.props");
            if (!File.Exists(file))
                continue;
            foreach (var item in TryLoad(file)?.Descendants().Where(e => e.Name.LocalName == "PackageVersion") ?? [])
            {
                if (item.Attribute("Include")?.Value is { } name && item.Attribute("Version")?.Value is { } version)
                    versions.TryAdd(name, version);
            }
            break;
        }
        return versions;
    }

    /// <summary>Properties as MSBuild sees them, roughly: the project file wins over the props files, the nearest props file over the outer ones.</summary>
    private static Dictionary<string, string> Properties(string projectFile, XElement project)
    {
        var files = new List<XElement>();
        for (var dir = Path.GetDirectoryName(projectFile); dir is not null; dir = Path.GetDirectoryName(dir))
        {
            foreach (var name in (string[])["Directory.Packages.props", "Directory.Build.props"])
            {
                var file = Path.Combine(dir, name);
                if (File.Exists(file) && TryLoad(file) is { } props)
                    files.Add(props);
            }
        }
        files.Reverse();
        files.Add(project);

        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        foreach (var group in file.Elements().Where(e => e.Name.LocalName == "PropertyGroup"))
        foreach (var property in group.Elements())
            properties[property.Name.LocalName] = Expand(property.Value, properties);
        return properties;
    }

    private static string Expand(string text, IReadOnlyDictionary<string, string> properties) =>
        System.Text.RegularExpressions.Regex.Replace(text, @"\$\((?<name>[A-Za-z_][\w.-]*)\)",
            m => properties.TryGetValue(m.Groups["name"].Value, out var value) ? value : m.Value);

    private static XElement? TryLoad(string file)
    {
        try
        {
            return XElement.Load(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return null;
        }
    }
}
