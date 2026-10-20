using System.Text;

namespace Dotnetumentation.Core;

/// <summary>
/// Writes the generated files of one project. Files whose content did not change are not touched, and diagrams left
/// over from an earlier run (of code that was removed) are deleted, so the folder always matches the code.
/// </summary>
internal static class OutputWriter
{
    private static readonly string[] OwnedExtensions = [".puml", ".md"];
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    public static ProjectResult Write(string projectName, string outputDirectory, IReadOnlyList<Generators.GeneratedFile> files, bool clean)
    {
        if (clean && Directory.Exists(outputDirectory))
            Directory.Delete(outputDirectory, recursive: true);

        int written = 0, unchanged = 0, deleted = 0;
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            var path = Path.GetFullPath(Path.Combine(outputDirectory, file.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
            if (!paths.Add(path))
                continue;
            if (File.Exists(path) && File.ReadAllText(path, Utf8) == file.Content)
            {
                unchanged++;
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, file.Content, Utf8);
            written++;
        }

        if (Directory.Exists(outputDirectory))
        {
            foreach (var stale in Directory.EnumerateFiles(outputDirectory, "*", SearchOption.AllDirectories)
                         .Where(f => OwnedExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                         .Where(f => !paths.Contains(Path.GetFullPath(f))).ToList())
            {
                File.Delete(stale);
                deleted++;
            }
            DeleteEmptyDirectories(outputDirectory);
        }

        return new ProjectResult(projectName, outputDirectory, written, unchanged, deleted,
            paths.Order(StringComparer.Ordinal).ToList());
    }

    private static void DeleteEmptyDirectories(string directory)
    {
        foreach (var child in Directory.EnumerateDirectories(directory))
        {
            DeleteEmptyDirectories(child);
            if (!Directory.EnumerateFileSystemEntries(child).Any())
                Directory.Delete(child);
        }
    }
}
