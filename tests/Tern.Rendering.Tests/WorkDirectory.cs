namespace Tern.Rendering.Tests;

/// <summary>A scratch directory under the test output folder, removed on dispose.</summary>
internal sealed class WorkDirectory : IDisposable
{
    public WorkDirectory()
    {
        Path = System.IO.Path.Combine(AppContext.BaseDirectory, "work", Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public string Write(string relative, byte[] content)
    {
        var full = Combine(relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, content);
        return full;
    }

    public string Write(string relative, string content) => Write(relative, System.Text.Encoding.UTF8.GetBytes(content));

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: a lingering junction or locked file must not fail the test run.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

internal static class SampleImages
{
    /// <summary>A real 1x1 PNG.</summary>
    public static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    public static string PngDataUri => "data:image/png;base64," + Convert.ToBase64String(Png);
}
