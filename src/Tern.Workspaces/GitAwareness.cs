using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Tern.Workspaces;

public enum GitFileState { Modified, Untracked }
public sealed record GitSnapshot(string? RepositoryRoot, IReadOnlyDictionary<string, GitFileState> Files);

public static class GitAwareness
{
    public static async Task<GitSnapshot> ReadAsync(string folder, CancellationToken cancellationToken = default)
    {
        var empty = new Dictionary<string, GitFileState>(WorkspacePaths.Comparer);
        if (!HasRepositoryAncestor(folder)) return new(null, empty);
        var rootResult = await RunAsync(folder, ["rev-parse", "--show-toplevel"], cancellationToken).ConfigureAwait(false);
        EnsureSuccess(rootResult);
        var root = Path.GetFullPath(rootResult.Output.TrimEnd('\r', '\n'));
        var status = await RunAsync(folder, ["status", "--porcelain=v1", "-z", "--untracked-files=all", "--ignore-submodules=all", "--", "."], cancellationToken).ConfigureAwait(false);
        EnsureSuccess(status);
        var entries = status.Output.Split('\0');
        for (var i = 0; i < entries.Length; i++)
        {
            var entry = entries[i];
            if (entry.Length < 4) continue;
            var path = Path.GetFullPath(Path.Combine(root, entry[3..]));
            if (!IsInside(root, path)) throw new IOException("Git returned a path outside its repository.");
            empty[path] = entry.StartsWith("??", StringComparison.Ordinal) ? GitFileState.Untracked : GitFileState.Modified;
            if (entry[0] is 'R' or 'C' || entry[1] is 'R' or 'C') i++;
        }
        return new(root, empty);
    }

    public static async Task<string> ReadHeadTextAsync(string path, CancellationToken cancellationToken = default)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var snapshot = await ReadAsync(folder, cancellationToken).ConfigureAwait(false);
        if (snapshot.RepositoryRoot is not { } root) throw new IOException("This file is not in a Git repository.");
        var relative = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
        if (!IsInside(root, Path.GetFullPath(path))) throw new IOException("The file is outside the Git repository.");
        var head = await RunAsync(root, ["rev-parse", "--verify", "--quiet", "HEAD"], cancellationToken).ConfigureAwait(false);
        if (head.ExitCode == 1) return string.Empty;
        EnsureSuccess(head);
        // A missing HEAD path is an added/untracked file, not a failed comparison.
        var exists = await RunAsync(root, ["ls-tree", "--name-only", "-z", "HEAD", "--", relative], cancellationToken).ConfigureAwait(false);
        EnsureSuccess(exists);
        if (exists.Output.Length == 0) return string.Empty;
        var blob = await RunAsync(root, ["show", "--no-ext-diff", "--no-textconv", "HEAD:" + relative], cancellationToken).ConfigureAwait(false);
        EnsureSuccess(blob);
        if (blob.Output.Contains('\0')) throw new IOException("The committed file is binary; a text comparison is unavailable.");
        return blob.Output;
    }

    private static bool IsInside(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return !Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static bool HasRepositoryAncestor(string folder)
    {
        for (var current = new DirectoryInfo(folder); current is not null; current = current.Parent)
            if (Directory.Exists(Path.Combine(current.FullName, ".git")) || File.Exists(Path.Combine(current.FullName, ".git"))) return true;
        return false;
    }

    private sealed record Result(int ExitCode, string Output, string Error);
    private static void EnsureSuccess(Result result)
    {
        if (result.ExitCode != 0) throw new IOException("Git could not read this repository: " + result.Error.Trim());
    }

    private static async Task<Result> RunAsync(string folder, string[] arguments, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        var start = new ProcessStartInfo("git")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in new[] { "--no-pager", "--no-optional-locks", "--literal-pathspecs", "-c", "core.fsmonitor=false", "-c", "core.untrackedCache=false", "-C", folder }.Concat(arguments))
            start.ArgumentList.Add(argument);
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        using var process = new Process { StartInfo = start };
        var started = false;
        try
        {
            started = process.Start();
            var output = ReadBoundedAsync(process.StandardOutput, 8 * 1024 * 1024, timeout.Token);
            var error = ReadBoundedAsync(process.StandardError, 8192, timeout.Token);
            await Task.WhenAll(output, error, process.WaitForExitAsync(timeout.Token)).ConfigureAwait(false);
            return new(process.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false));
        }
        catch (Win32Exception ex) { throw new IOException("Git is unavailable. Install Git to use repository markers and comparisons.", ex); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new IOException("Reading Git status timed out; no repository changes were made."); }
        finally
        {
            if (started) StopIfRunning(() => process.HasExited, () => process.Kill(entireProcessTree: true));
        }
    }

    /// <summary>Git can exit between the check and the kill. That must not replace the real result or error.</summary>
    internal static void StopIfRunning(Func<bool> hasExited, Action kill)
    {
        try { if (!hasExited()) kill(); }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or AggregateException) { }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, int limit, CancellationToken token)
    {
        var text = new StringBuilder();
        var buffer = new char[8192];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) > 0)
        {
            if (text.Length + count > limit) throw new IOException("The Git result is too large to display safely.");
            text.Append(buffer, 0, count);
        }
        return text.ToString();
    }
}
