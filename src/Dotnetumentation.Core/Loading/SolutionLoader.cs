using System.Runtime.CompilerServices;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;

namespace Dotnetumentation.Core.Loading;

/// <summary>An opened solution: the C# projects to document (one per project file) and the load problems.</summary>
public sealed class LoadedSolution(MSBuildWorkspace workspace, Solution solution, IReadOnlyList<Project> projects,
    IReadOnlyList<string> diagnostics) : IDisposable
{
    public Solution Solution { get; } = solution;
    public IReadOnlyList<Project> Projects { get; } = projects;
    public IReadOnlyList<string> Diagnostics { get; } = diagnostics;

    public void Dispose() => workspace.Dispose();
}

public static class SolutionLoader
{
    private static readonly Lock Gate = new();

    /// <summary>
    /// Finds the MSBuild of the installed .NET SDK. Must run before any MSBuild type is loaded, so the workspace code
    /// is kept in a separate method.
    /// </summary>
    public static void EnsureMsBuildRegistered()
    {
        lock (Gate)
        {
            if (!MSBuildLocator.IsRegistered)
                MSBuildLocator.RegisterDefaults();
        }
    }

    public static Task<LoadedSolution> LoadAsync(string path, IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"File not found: {path}", path);
        EnsureMsBuildRegistered();
        return LoadCoreAsync(Path.GetFullPath(path), progress, cancellationToken);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<LoadedSolution> LoadCoreAsync(string path, IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var diagnostics = new List<string>();
        var workspace = MSBuildWorkspace.Create();
        try
        {
            workspace.RegisterWorkspaceFailedHandler(e =>
            {
                lock (diagnostics)
                    diagnostics.Add($"{e.Diagnostic.Kind}: {e.Diagnostic.Message}");
            });

            var loadProgress = new Progress<ProjectLoadProgress>(p =>
            {
                if (p.Operation == ProjectLoadOperation.Resolve)
                    progress?.Report($"Loaded {Path.GetFileName(p.FilePath)}" + (p.TargetFramework is null ? "" : $" ({p.TargetFramework})"));
            });

            Solution solution;
            if (Path.GetExtension(path).Equals(".csproj", StringComparison.OrdinalIgnoreCase))
                solution = (await workspace.OpenProjectAsync(path, loadProgress, cancellationToken)).Solution;
            else
                solution = await workspace.OpenSolutionAsync(path, loadProgress, cancellationToken);

            // A multi-targeted project is loaded once per target framework; the first one is documented.
            var projects = solution.Projects
                .Where(p => p.Language == LanguageNames.CSharp && p.FilePath is not null)
                .GroupBy(p => p.FilePath!, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .OrderBy(p => p.FilePath, StringComparer.Ordinal)
                .ToList();

            return new LoadedSolution(workspace, solution, projects, diagnostics);
        }
        catch
        {
            workspace.Dispose();
            throw;
        }
    }

    /// <summary>The project name without the <c>(net8.0)</c> suffix of multi-targeted projects.</summary>
    public static string ProjectName(Project project) =>
        project.FilePath is { } file ? Path.GetFileNameWithoutExtension(file) : project.Name;
}
