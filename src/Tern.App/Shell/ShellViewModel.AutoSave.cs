using Tern.App.Documents;

namespace Tern.App.Shell;

public sealed partial class ShellViewModel
{
    private readonly Dictionary<DocumentTab, CancellationTokenSource> _idleSaves = [];
    private (bool Enabled, int Seconds) _autoSaveConfiguration;

    private void ConfigureAutoSave()
    {
        var configuration = (Settings.AutoSaveEnabled, Settings.AutoSaveIdleSeconds);
        if (_autoSaveConfiguration == configuration) return;
        _autoSaveConfiguration = configuration;
        foreach (var tab in Tabs) ScheduleAutoSave(tab);
    }

    private void CancelAutoSave(DocumentTab tab)
    {
        if (_idleSaves.Remove(tab, out var previous)) previous.Cancel();
    }

    private void ScheduleAutoSave(DocumentTab tab)
    {
        CancelAutoSave(tab);
        if (_disposed || !Settings.AutoSaveEnabled || tab.FilePath is null || !tab.IsDirty || tab.IsReadOnly) return;
        var cancellation = new CancellationTokenSource();
        _idleSaves[tab] = cancellation;
        _ = SaveAfterIdleAsync(tab, tab.Session.Revision, tab.FilePath, cancellation);
    }

    private async Task SaveAfterIdleAsync(DocumentTab tab, long revision, string path, CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(Settings.AutoSaveIdleSeconds, 30, 3600)), cancellation.Token).ConfigureAwait(true);
            if (_disposed || cancellation.IsCancellationRequested || !Settings.AutoSaveEnabled || !Tabs.Contains(tab)
                || !tab.Session.IsCurrent(revision) || tab.FilePath != path || !tab.IsDirty || tab.IsReadOnly) return;
            await SaveTabAsync(tab).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex) { ReportUnexpectedError("save an idle document", ex); }
        finally
        {
            if (_idleSaves.TryGetValue(tab, out var current) && ReferenceEquals(current, cancellation)) _idleSaves.Remove(tab);
            cancellation.Dispose();
        }
    }
}
