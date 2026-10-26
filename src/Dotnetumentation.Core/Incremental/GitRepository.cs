using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Dotnetumentation.Core.Incremental;

/// <summary>
/// The git repository of the documented solution, read with the <c>git</c> command line (no library dependency).
/// File paths are relative to <see cref="Root"/>, with <c>/</c> separators, as git prints them.
/// </summary>
internal sealed class GitRepository
{
    private GitRepository(string root) => Root = root;

    public string Root { get; }

    /// <summary>Null when the folder is not in a git repository or git is not installed.</summary>
    public static GitRepository? TryOpen(string directory) =>
        Run(directory, "rev-parse", "--show-toplevel") is { } root && root.Trim() is { Length: > 0 } path
            ? new GitRepository(Path.GetFullPath(path))
            : null;

    /// <summary>The checked out commit; null in a repository without commits.</summary>
    public string? Head() => ResolveCommit("HEAD");

    /// <summary>The commit id of a ref (branch, tag, commit, <c>HEAD~2</c>); null when it does not exist.</summary>
    public string? ResolveCommit(string reference) =>
        Run(Root, "rev-parse", "--verify", "--quiet", reference + "^{commit}")?.Trim() is { Length: > 0 } commit ? commit : null;

    /// <summary>Files that differ between the commit and the working tree (staged or not), and the untracked files.</summary>
    public IReadOnlySet<string>? ChangedSince(string commit)
    {
        var changed = Run(Root, "diff", "--name-only", "-z", "--no-renames", commit, "--");
        var untracked = Untracked();
        if (changed is null || untracked is null)
            return null;
        return Split(changed).Concat(untracked).ToHashSet(GenerationState.PathComparer);
    }

    /// <summary>Uncommitted and untracked files: the documentation generated now reflects them, not the commit.</summary>
    public IReadOnlySet<string> Dirty() =>
        (Head() is { } head ? ChangedSince(head) : null)
        ?? (Run(Root, "ls-files", "-z") is { } all ? Split(all).Concat(Untracked() ?? []).ToHashSet(GenerationState.PathComparer) : []);

    private IEnumerable<string>? Untracked() =>
        Run(Root, "ls-files", "--others", "--exclude-standard", "-z") is { } output ? Split(output) : null;

    private static IEnumerable<string> Split(string output) => output.Split('\0', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>The standard output of the git command; null when it failed or git could not be started.</summary>
    private static string? Run(string directory, params string[] args)
    {
        var start = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("-C");
        start.ArgumentList.Add(directory);
        foreach (var arg in args)
            start.ArgumentList.Add(arg);
        try
        {
            using var git = Process.Start(start);
            if (git is null)
                return null;
            var error = git.StandardError.ReadToEndAsync();
            var output = git.StandardOutput.ReadToEnd();
            git.WaitForExit();
            _ = error.Result;
            return git.ExitCode == 0 ? output : null;
        }
        catch (Win32Exception)
        {
            // git is not installed.
            return null;
        }
    }
}
