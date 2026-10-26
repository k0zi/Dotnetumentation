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

    /// <param name="kept">Files (relative, <c>/</c> separators) of an earlier run that stay as they are and must not be deleted.</param>
    public static ProjectResult Write(string projectName, string outputDirectory, IReadOnlyList<Generators.GeneratedFile> files, bool clean,
        IReadOnlyCollection<string>? kept = null, GenerationMode mode = GenerationMode.Full)
    {
        if (clean && Directory.Exists(outputDirectory))
            Directory.Delete(outputDirectory, recursive: true);

        int written = 0, unchanged = 0, deleted = 0;
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            var path = FullPath(outputDirectory, file.RelativePath);
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
        foreach (var file in kept ?? [])
        {
            var path = FullPath(outputDirectory, file);
            if (File.Exists(path) && paths.Add(path))
                unchanged++;
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
            paths.Order(StringComparer.Ordinal).ToList(), mode);
    }

    /// <summary>The result of a project whose documentation is up to date: its files are left as they are.</summary>
    public static ProjectResult Skip(string projectName, string outputDirectory)
    {
        var files = Directory.Exists(outputDirectory)
            ? Directory.EnumerateFiles(outputDirectory, "*", SearchOption.AllDirectories)
                .Where(f => OwnedExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                .Append(Path.Combine(outputDirectory, Graph.GraphDatabase.FileName)).Where(File.Exists)
                .Select(Path.GetFullPath).Order(StringComparer.Ordinal).ToList()
            : [];
        return new ProjectResult(projectName, outputDirectory, 0, files.Count, 0, files, GenerationMode.Skipped);
    }

    public static string FullPath(string outputDirectory, string relativePath) =>
        Path.GetFullPath(Path.Combine(outputDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar)));

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
