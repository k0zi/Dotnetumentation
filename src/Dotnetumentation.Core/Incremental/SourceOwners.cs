using Microsoft.CodeAnalysis;

namespace Dotnetumentation.Core.Incremental;

/// <summary>Converts source file paths to the form git and the state file use: relative to the root, <c>/</c> separators.</summary>
internal sealed class SourcePaths(string root)
{
    public string Relative(string path) => Path.GetRelativePath(root, Path.GetFullPath(path)).Replace('\\', '/');
}

/// <summary>Which projects (by project file path) a changed file belongs to.</summary>
internal sealed class SourceOwners
{
    private readonly Dictionary<string, HashSet<string>> _files = new(GenerationState.PathComparer);
    private readonly List<(string Directory, string Project)> _directories = [];

    public SourceOwners(Solution solution, SourcePaths source)
    {
        foreach (var project in solution.Projects.Where(p => p.Language == LanguageNames.CSharp && p.FilePath is not null))
        {
            // A file linked into several projects belongs to each of them.
            foreach (var path in project.Documents.Select(d => d.FilePath).OfType<string>().Append(project.FilePath!))
            {
                var relative = source.Relative(path);
                if (!_files.TryGetValue(relative, out var owners))
                    _files[relative] = owners = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                owners.Add(project.FilePath!);
            }
            _directories.Add((source.Relative(Path.GetDirectoryName(project.FilePath!)!) + "/", project.FilePath!));
        }
    }

    /// <summary>The projects compiling the file; a file no project knows (a deleted one) belongs to the project of its folder.</summary>
    public IEnumerable<string> Of(string relativePath)
    {
        if (_files.TryGetValue(relativePath, out var owners))
            return owners;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return _directories.Where(d => d.Directory == "./" || relativePath.StartsWith(d.Directory, comparison))
            .GroupBy(d => d.Directory.Length).OrderByDescending(g => g.Key).Take(1)
            .SelectMany(g => g.Select(d => d.Project));
    }
}
