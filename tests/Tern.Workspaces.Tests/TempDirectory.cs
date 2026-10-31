using System.Text;

namespace Tern.Workspaces.Tests;

/// <summary>A real directory under the system temp folder, removed on dispose.</summary>
public sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetFullPath(System.IO.Path.GetTempPath()), "tern-ws-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Combine(string relative) => System.IO.Path.Combine(Path, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));

    public string Write(string relative, string content, Encoding? encoding = null)
    {
        var full = Combine(relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content, encoding ?? new UTF8Encoding(false));
        return full;
    }

    public string Read(string relative) => File.ReadAllText(Combine(relative));

    private static void RemoveLinks(DirectoryInfo directory)
    {
        foreach (var child in directory.EnumerateDirectories().ToList())
        {
            if (child.LinkTarget is not null || child.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                child.Delete();
            }
            else
            {
                RemoveLinks(child);
            }
        }
    }

    public void Dispose()
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    // Junctions and symlinks must be removed without descending into their targets.
                    RemoveLinks(new DirectoryInfo(Path));
                    Directory.Delete(Path, true);
                }

                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(100);
            }
        }
    }
}
