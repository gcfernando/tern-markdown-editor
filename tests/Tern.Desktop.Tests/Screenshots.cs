namespace Tern.Desktop.Tests;

internal static class Screenshots
{
    /// <summary>Temporary by default; see <see cref="Tern.Tests.Shared.TestOutput"/>.</summary>
    public static string Folder { get; } = Tern.Tests.Shared.TestOutput.Folder("screenshots");

    public static string RepositoryRoot
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Tern.slnx"))) directory = directory.Parent;
            return directory?.FullName ?? throw new DirectoryNotFoundException("Tern.slnx was not found above the test output.");
        }
    }
}
