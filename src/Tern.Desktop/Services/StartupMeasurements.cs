using System.Diagnostics;
using System.Text.Json;

namespace Tern.Desktop.Services;

/// <summary>Opt-in startup phase evidence. No filenames, document contents, arguments, or credentials are recorded.</summary>
internal static class StartupMeasurements
{
    private static readonly string? TracePath = Environment.GetEnvironmentVariable("TERN_STARTUP_TRACE");
    private static readonly List<object> Phases = [];

    public static void Mark(string phase)
    {
        if (string.IsNullOrWhiteSpace(TracePath)) return;
        using var process = Process.GetCurrentProcess();
        Phases.Add(new
        {
            phase,
            milliseconds = Math.Round((DateTime.UtcNow - process.StartTime.ToUniversalTime()).TotalMilliseconds, 2),
            workingSetBytes = process.WorkingSet64,
            privateBytes = process.PrivateMemorySize64,
        });
        try
        {
            var path = Path.GetFullPath(TracePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var json = JsonSerializer.Serialize(new
            {
                processId = process.Id,
                startedUtc = process.StartTime.ToUniversalTime(),
                os = Environment.OSVersion.ToString(),
                logicalProcessors = Environment.ProcessorCount,
                runtime = Environment.Version.ToString(),
#if DEBUG
                build = "Debug",
#else
                build = "Release",
#endif
                phases = Phases,
            });
            File.WriteAllText(path + ".tmp", json);
            File.Move(path + ".tmp", path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { Trace.TraceWarning("Startup measurement could not be written: {0}", ex.Message); }
    }
}
