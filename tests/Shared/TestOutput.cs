namespace Tern.Tests.Shared;

/// <summary>
/// Where tests save screenshots and exports. By default this is a temporary folder removed when the test process ends,
/// so test runs add nothing to the repository. Set TERN_TEST_OUTPUT to keep the files, as the documentation capture does.
/// </summary>
internal static class TestOutput
{
    private static readonly TimeSpan AbandonedAfter = TimeSpan.FromDays(1);
    private static readonly string Root = Resolve();

    public static string Folder(params string[] parts)
    {
        var folder = Path.Combine([Root, .. parts]);
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static string Resolve()
    {
        if (Environment.GetEnvironmentVariable("TERN_TEST_OUTPUT") is { Length: > 0 } kept) return Path.GetFullPath(kept);
        var parent = Path.Combine(Path.GetTempPath(), "tern-test-output");
        RemoveAbandoned(parent);
        var root = Path.Combine(parent, Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        AppDomain.CurrentDomain.ProcessExit += (_, _) => TryDelete(root);
        return root;
    }

    // A killed test process cannot clean up after itself.
    private static void RemoveAbandoned(string parent)
    {
        if (!Directory.Exists(parent)) return;
        foreach (var folder in Directory.EnumerateDirectories(parent))
            if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(folder) > AbandonedAfter) TryDelete(folder);
    }

    private static void TryDelete(string folder)
    {
        try { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
