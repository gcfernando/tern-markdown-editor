namespace Tern.Desktop.Tests;

internal static class Screenshots
{
    /// <summary>artifacts\screenshots beside Tern.slnx, so images land in the repository tree rather than a temp folder.</summary>
    public static string Folder { get; } = Resolve();

    private static string Resolve()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Tern.slnx")))
        {
            dir = dir.Parent;
        }

        var folder = Path.Combine(dir?.FullName ?? AppContext.BaseDirectory, "artifacts", "screenshots");
        Directory.CreateDirectory(folder);
        return folder;
    }
}
