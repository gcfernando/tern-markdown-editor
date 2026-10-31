using System.Diagnostics;

namespace Tern.Security.Tests;

/// <summary>A throw-away folder tree with an "outside" sibling that nothing in the workspace may ever touch.</summary>
public sealed class Sandbox : IDisposable
{
    public Sandbox()
    {
        Root = Path.Combine(Path.GetTempPath(), "tern-security-tests", Guid.NewGuid().ToString("N"));
        Workspace = Path.Combine(Root, "ws");
        Outside = Path.Combine(Root, "outside");
        Directory.CreateDirectory(Workspace);
        Directory.CreateDirectory(Outside);
        File.WriteAllText(Path.Combine(Outside, "secret.md"), "# secret\n");
        File.WriteAllText(Path.Combine(Outside, "secret.png"), "not really a png");
    }

    public string Root { get; }

    public string Workspace { get; }

    public string Outside { get; }

    public string Write(string relative, string text = "# x\n")
    {
        var path = Path.Combine(Workspace, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    /// <summary>Creates a directory junction (no privilege needed on Windows) or a symlink elsewhere. Returns false when neither is possible.</summary>
    public bool TryLinkDirectory(string linkPath, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(linkPath)!);
        if (OperatingSystem.IsWindows())
        {
            using var process = Process.Start(new ProcessStartInfo("cmd.exe")
            {
                ArgumentList = { "/c", "mklink", "/J", linkPath, target },
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })!;
            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit();
            return Directory.Exists(linkPath);
        }

        try
        {
            Directory.CreateSymbolicLink(linkPath, target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public bool TryLinkFile(string linkPath, string target)
    {
        try
        {
            File.CreateSymbolicLink(linkPath, target);
            return File.Exists(linkPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        try
        {
            // Remove links first so deleting the tree can never follow one into the "outside" folder.
            RemoveLinks(new DirectoryInfo(Root));
            Directory.Delete(Root, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void RemoveLinks(DirectoryInfo directory)
    {
        foreach (var child in directory.EnumerateDirectories())
        {
            if (child.LinkTarget is not null)
            {
                child.Delete();
            }
            else
            {
                RemoveLinks(child);
            }
        }
    }
}

public static class Hostile
{
    /// <summary>Runs work that must finish (or fail cleanly) in bounded time; a hang fails the test instead of the build.</summary>
    public static T Run<T>(Func<T> work, int seconds = 30)
    {
        var task = Task.Run(work);
        if (!task.Wait(TimeSpan.FromSeconds(seconds)))
        {
            throw new TimeoutException($"Input was not handled within {seconds} s.");
        }

        return task.Result;
    }

    public static void Run(Action work, int seconds = 30) => Run(() =>
    {
        work();
        return 0;
    }, seconds);
}
