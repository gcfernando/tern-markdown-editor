using System.Text;
using Tern.App.Documents;
using Tern.Domain.Documents;
using Tern.Filesystem;

namespace Tern.App.Shell;

/// <summary>Raised when the user chooses Details or Copy report on an error banner.</summary>
public sealed class ErrorReportEventArgs(string report, bool copy) : EventArgs
{
    public string Report { get; } = report;
    public bool Copy { get; } = copy;
}

/// <summary>
/// App-wide error boundary support (B-01) and non-throwing post-save side effects (B-01, D-01, B-02, B-06).
/// Nothing here may throw into a save, edit or command call stack.
/// </summary>
public sealed partial class ShellViewModel
{
    public const string OriginalVersionLabel = "Original (before first Tern save)";
    public const string ExternalVersionLabel = "External version replaced by yours";
    public const string BeforeSaveVersionLabel = "Version before this save";

    private int _preSaveDamaged;
    private string? _preSaveWarning;
    private string? _lastListenerFailureKey;
    private long _lastListenerFailureTick;

    public event EventHandler<ErrorReportEventArgs>? ErrorReportRequested;

    /// <summary>Test seam: runs before a workspace is told about a saved file so fault injection can throw there.</summary>
    internal Func<Task>? BeforeWorkspaceSavedNotification { get; set; }

    /// <summary>
    /// Logs an unexpected failure and shows it without interrupting the user: an Error in Activity and a red banner with
    /// Details and Copy report. Safe from any thread and never throws.
    /// </summary>
    public void ReportUnexpectedError(string action, Exception exception)
    {
        try
        {
            WriteDiagnostic(exception);
            var report = BuildErrorReport(action, exception, includeMessage: true);
            var problem = UserProblem.Explain(exception, $"Something went wrong: {action}", includeMessage: false);
            var text = $"{problem.Title}. Your text is safe. {problem.Why} {problem.Next}";
            SetStatus(text, StatusSeverity.Error);
            ShowBanner(text, StatusSeverity.Error,
                new BannerAction("Details", () => ErrorReportRequested?.Invoke(this, new ErrorReportEventArgs(report, false))),
                new BannerAction("Copy report", () => ErrorReportRequested?.Invoke(this, new ErrorReportEventArgs(report, true))));
        }
        catch (Exception reporting)
        {
            System.Diagnostics.Trace.TraceError("Error reporting failed: " + reporting.GetType().Name);
        }
    }

    /// <summary>Builds a support report. It never contains document text; the message is optional because it may name a path.</summary>
    internal static string BuildErrorReport(string action, Exception exception, bool includeMessage)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Tern error report");
        builder.AppendLine("Time (UTC): " + DateTime.UtcNow.ToString("u"));
        builder.AppendLine("Action: " + action);
        for (var current = exception; current is not null; current = current.InnerException)
        {
            builder.AppendLine("Exception: " + current.GetType().FullName);
            if (includeMessage) builder.AppendLine("Message: " + current.Message);
            if (current.StackTrace is { } stack)
                builder.AppendLine(includeMessage ? stack : new System.Diagnostics.StackTrace(current, false).ToString());
        }
        return builder.ToString();
    }

    private void WriteDiagnostic(Exception? exception, StatusSeverity? severity = null)
    {
        if (Environment.GetEnvironmentVariable("TERN_DIAGNOSTIC_LOG") != "1") return;
        try
        {
            Directory.CreateDirectory(Paths.StateDirectory);
            var file = Path.Combine(Paths.StateDirectory, "diagnostics.log");
            lock (_diagnosticLock)
            {
                if (File.Exists(file) && new FileInfo(file).Length > 512 * 1024) File.Move(file, file + ".old", overwrite: true);
                var report = exception is not null
                    ? BuildErrorReport("unexpected failure", exception, includeMessage: false)
                    : $"Time (UTC): {DateTime.UtcNow:u}{Environment.NewLine}Status: {severity}";
                File.AppendAllText(file, report + Environment.NewLine);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            System.Diagnostics.Trace.TraceWarning("Diagnostic log unavailable: " + ex.GetType().Name);
        }
    }

    private readonly object _diagnosticLock = new();

    private void OnSessionListenerFailed(object? sender, Exception exception)
    {
        // A persistently failing listener would otherwise report on every keystroke.
        var key = exception.GetType().FullName + "|" + (exception.StackTrace?.Split('\n').FirstOrDefault() ?? string.Empty);
        var now = Environment.TickCount64;
        if (key == _lastListenerFailureKey && now - _lastListenerFailureTick < 10_000) return;
        _lastListenerFailureKey = key;
        _lastListenerFailureTick = now;
        ReportUnexpectedError("update the document state", exception);
    }

    /// <summary>Last chance before the process ends: write checkpoints for dirty documents without risking a deadlock.</summary>
    public bool TryFlushRecoveryForCrash(TimeSpan timeout)
    {
        try { return Task.Run(() => _recovery.FlushAllAsync()).Wait(timeout); }
        catch (Exception) { return false; }
    }

    // ---- History around saving (B-06, B-02) -----------------------------------------------------------------------

    /// <summary>
    /// Before a write replaces what is on disk, keeps that content in history: the original at first save, or an external
    /// version about to be replaced. Reads the file itself rather than merge bookkeeping (D-05). Never throws.
    /// </summary>
    private async Task CaptureReplacedVersionAsync(DocumentTab tab, string path, DiskBaseline? expected, bool externalReplaced)
    {
        if (!externalReplaced && expected is null) return;
        try
        {
            var disk = await _files.LoadAsync(path).ConfigureAwait(true);
            // Lossy text must never be stored as a restorable version.
            if (disk.ShouldOpenReadOnly) return;
            // If disk no longer matches what the tab loaded, the save will report a conflict; Keep mine captures it then.
            if (!externalReplaced && !disk.Baseline.SameContentAs(expected!)) return;
            var result = await HistoryStore.CaptureAsync(path, disk.Text,
                externalReplaced ? ExternalVersionLabel : BeforeSaveVersionLabel,
                externalReplaced ? null : OriginalVersionLabel).ConfigureAwait(true);
            _preSaveDamaged += result.DamagedSetAside;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // Nothing on disk to preserve.
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _preSaveWarning = "the previous version could not be kept in history: " + PlainProblem(ex);
        }
    }

    /// <summary>Every step after a successful write is isolated: a failure downgrades to a warning and the save stays successful.</summary>
    private async Task<bool> CompleteSuccessfulSaveAsync(DocumentTab tab, ActivityScope? activity = null,
        bool finishActivity = true, ICollection<string>? completionWarnings = null)
    {
        var damaged = _preSaveDamaged;
        var warnings = new List<string>();
        if (tab.LastResourceSaveWarning is { } resourceWarning) warnings.Add(resourceWarning);
        if (_preSaveWarning is { } preWarning) warnings.Add(preWarning);
        _preSaveDamaged = 0;
        _preSaveWarning = null;
        var path = tab.FilePath!;
        var savedText = tab.LastSavedText;

        activity?.Report("Write verified; updating recent files");
        try { Recent.AddFile(path); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { warnings.Add("the recent list was not updated: " + PlainProblem(ex)); }

        if (savedText is not null)
        {
            activity?.Report("Keeping the saved version in History");
            try { damaged += (await HistoryStore.CaptureAsync(path, savedText).ConfigureAwait(true)).DamagedSetAside; }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { warnings.Add("local history unavailable: " + PlainProblem(ex)); }
        }

        foreach (var workspace in OpenWorkspaces.Where(w => w.IsInside(path)).ToList())
        {
            activity?.Report("Updating the workspace search index");
            try
            {
                if (BeforeWorkspaceSavedNotification is { } hook) await hook().ConfigureAwait(true);
                if (savedText is not null) await workspace.NotifySavedAsync(path, savedText,
                    reportPhase: phase => activity?.Report(phase)).ConfigureAwait(true);
                if (OpenWorkspaces.Contains(workspace))
                {
                    var dirty = Tabs.Where(t => t.FilePath is { } open && SamePath(open, path) && t.IsDirty)
                        .OrderByDescending(t => ReferenceEquals(t, ActiveTab)).FirstOrDefault();
                    if (dirty is not null) workspace.SetUnsavedText(path, dirty.CurrentText);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { warnings.Add("the search index was not updated: " + PlainProblem(ex)); }
        }

        if (damaged > 0) warnings.Insert(0, $"{damaged} damaged history version{(damaged == 1 ? string.Empty : "s")} set aside");
        if (warnings.Count == 0) SetStatus("Saved " + tab.DisplayName, StatusSeverity.Success);
        else if (warnings.Count == 1 && damaged > 0) SetStatus("Saved · " + warnings[0], StatusSeverity.Warning);
        else SetStatus("File saved; " + string.Join("; ", warnings), StatusSeverity.Warning);
        if (completionWarnings is not null)
            foreach (var warning in warnings) completionWarnings.Add(warning);
        if (finishActivity)
            activity?.Complete(warnings.Count > 0 ? string.Join("; ", warnings) : null, warnings.Count > 0,
                warnings.Count > 0 ? "Saved with warning" : "Saved");
        return true;
    }
}
