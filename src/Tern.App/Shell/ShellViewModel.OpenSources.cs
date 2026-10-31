using Tern.App.Documents;
using Tern.Domain.Documents;
using Tern.Domain.Text;
using Tern.Filesystem;

namespace Tern.App.Shell;

public sealed partial class ShellViewModel
{
    private readonly SemaphoreSlim _explicitOpenGate = new(1, 1);
    public Task<DuplicateOpenChoice> AskDuplicateOpenAsync(DocumentTab tab) => _dialogs.AskDuplicateOpenAsync(tab.DisplayName);

    public DocumentTab? FindOpenSource(string source, bool online = false)
    {
        if (online)
        {
            var key = NormalizeOnlineSource(source);
            return Tabs.FirstOrDefault(t => t.SourceUrl is { } url && Uri.TryCreate(url, UriKind.Absolute, out var parsed)
                && parsed.Scheme is "http" or "https" && parsed.UserInfo.Length == 0 && NormalizeOnlineSource(url) == key);
        }
        var full = Path.GetFullPath(source);
        return Tabs.FirstOrDefault(t => t.FilePath is { } path && SamePath(path, full))
            ?? Tabs.FirstOrDefault(t => t.FilePath is { } path && FileIdentity.AreSameFile(path, full));
    }

    private static string NormalizeOnlineSource(string source)
    {
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0)
            throw new ArgumentException("Use an HTTP or HTTPS address without a username or password.");
        return new UriBuilder(uri) { Host = uri.IdnHost, Fragment = string.Empty }.Uri.AbsoluteUri;
    }

    public DocumentTab OpenOnlineDocument(string url, string name, string text, TextFileFormat? format = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var canonical = NormalizeOnlineSource(url);
        var actualFormat = format ?? LineEndingAnalyzer.Describe(TextEncodingKind.Utf8, false, text);
        var session = new DocumentSession(null, text, actualFormat, null);
        session.SetDraftName(name);
        var tab = new DocumentTab(session, _files, Settings.DefaultProfile) { IsExternalDraft = true, SourceUrl = canonical };
        Attach(tab);
        return tab;
    }

    public async Task<DocumentTab?> DownloadOnlineDocumentAsync(string url, string name, string text, TextFileFormat? format = null, IReadOnlyDictionary<string, byte[]>? resources = null)
    {
        if (_disposed) return null;
        DocumentTab? replacing = FindOpenSource(url, online: true);
        var choice = replacing is null ? DuplicateOpenChoice.NewTab : await _dialogs.AskDuplicateOpenAsync(replacing.DisplayName).ConfigureAwait(true);
        if (choice == DuplicateOpenChoice.Cancel) return null;
        if (choice == DuplicateOpenChoice.Replace && !await ConfirmCloseAsync([replacing!]).ConfigureAwait(true)) return null;
        var approvedRevision = replacing?.Session.Revision;
        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        var path = await _dialogs.PickSavePathAsync(name, Directory.Exists(downloads) ? downloads : null).ConfigureAwait(true);
        if (path is null || _disposed) return null;
        DocumentTab? candidate = null;
        try
        {
            var destinationTab = FindOpenSource(path);
            if (destinationTab is not null && !ReferenceEquals(destinationTab, replacing))
            { _dialogs.ShowMessage("File already open", "Choose another download filename. Your open file and edits were kept."); return null; }
            var expected = await _files.ReadBaselineAsync(path).ConfigureAwait(true);
            if (expected is not null && !await _dialogs.ConfirmOverwriteAsync(path).ConfigureAwait(true)) return null;
            candidate = new DocumentTab(new DocumentSession(null, text,
                format ?? LineEndingAnalyzer.Describe(TextEncodingKind.Utf8, false, text), null), _files, Settings.DefaultProfile)
            { SourceUrl = NormalizeOnlineSource(url), ResourceFiles = resources ?? new Dictionary<string, byte[]>() };
            var outcome = await SerializeIdentityAsync<SaveOutcome>(() =>
            {
                if (_disposed || Tabs.Any(t => !ReferenceEquals(t, replacing) && t.FilePath is { } open && FileIdentity.AreSameFile(open, path)))
                    return Task.FromResult<SaveOutcome>(new SaveFailed("The destination is now open. Choose another filename; your tabs were kept."));
                if (choice == DuplicateOpenChoice.Replace && replacing is not null
                    && (!Tabs.Contains(replacing) || !replacing.Session.IsCurrent(approvedRevision!.Value)))
                    return Task.FromResult<SaveOutcome>(new SaveFailed("The document changed while downloading. Your edits were kept."));
                return candidate.SaveAsCheckedAsync(path, expected, SaveOptions);
            }, DocumentOperation.Saving).ConfigureAwait(true);
            if (outcome is not SaveSucceeded)
            { _dialogs.ShowMessage("Download not saved", outcome is SaveFailed failed ? failed.Reason : "The destination changed. Nothing was overwritten; choose another filename."); candidate.Dispose(); return null; }
            if (_disposed) { candidate.Dispose(); return null; }
            if (choice == DuplicateOpenChoice.Replace && replacing is not null && Tabs.Contains(replacing)
                && replacing.Session.IsCurrent(approvedRevision!.Value)) RemoveTab(replacing, retireRecovery: true);
            Attach(candidate);
            Recent.AddFile(candidate.FilePath!);
            try { Recent.Remove(Tern.Workspaces.RecentKind.Url, url); }
            catch (ArgumentException) { /* A long source URL need not be stored to keep the saved local file. */ }
            StatusMessage = "Downloaded and opened " + candidate.FilePath;
            return candidate;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.ComponentModel.Win32Exception)
        { if (candidate is not null && !Tabs.Contains(candidate)) candidate.Dispose(); _dialogs.ShowMessage("Cannot download file", ex.Message); return null; }
    }

    public async Task<DocumentTab?> ReplaceOnlineDocumentAsync(DocumentTab existing, string url, string name, string text, TextFileFormat? format = null)
    {
        if (!Tabs.Contains(existing) || !await ConfirmCloseAsync([existing]).ConfigureAwait(true)) return null;
        if (!Tabs.Contains(existing)) return null;
        var index = Tabs.IndexOf(existing);
        var replacement = OpenOnlineDocument(url, name, text, format);
        RemoveTab(existing, retireRecovery: true);
        Tabs.Move(Tabs.IndexOf(replacement), Math.Min(index, Tabs.Count - 1));
        return replacement;
    }

    public async Task<bool> CloseTabsRelativeAsync(DocumentTab tab, TabCloseScope scope)
    {
        var index = Tabs.IndexOf(tab);
        if (index < 0) return false;
        var targets = scope switch
        {
            TabCloseScope.All => Tabs.ToList(),
            TabCloseScope.Right => Tabs.Skip(index + 1).ToList(),
            TabCloseScope.Others => Tabs.Where(t => !ReferenceEquals(t, tab)).ToList(),
            _ => [tab],
        };
        if (!await ConfirmCloseAsync(targets).ConfigureAwait(true)) return false;
        foreach (var target in targets) RemoveTab(target, retireRecovery: true);
        return true;
    }

    private async Task<DocumentTab?> OpenExplicitFileAsync(string path, TextEncodingKind? encoding, bool activate, bool recordRecent, DuplicateOpenBehavior behavior)
    {
        await _explicitOpenGate.WaitAsync().ConfigureAwait(true);
        try
        {
            var existing = FindOpenSource(path);
            if (existing is null) return await SerializeIdentityAsync(() => OpenFileCoreAsync(path, encoding, activate, recordRecent), DocumentOperation.Loading).ConfigureAwait(true);
            var choice = behavior == DuplicateOpenBehavior.NewTab ? DuplicateOpenChoice.NewTab
                : await _dialogs.AskDuplicateOpenAsync(existing.DisplayName).ConfigureAwait(true);
            if (choice == DuplicateOpenChoice.Cancel || !Tabs.Contains(existing)) return null;
            if (choice == DuplicateOpenChoice.Replace && !await ConfirmCloseAsync([existing]).ConfigureAwait(true)) return null;
            var approvedRevision = existing.Session.Revision;
            return await SerializeIdentityAsync<DocumentTab?>(async () =>
            {
                if (_disposed) return null;
                var opened = await DocumentTab.OpenAsync(path, _files, Settings.DefaultProfile, encoding).ConfigureAwait(true);
                opened.LinkedImageFiles = _linkedImages.Get(path);
                if (_disposed) { opened.Dispose(); return null; }
                if (choice == DuplicateOpenChoice.Replace)
                {
                    if (!Tabs.Contains(existing) || !existing.Session.IsCurrent(approvedRevision))
                    { opened.Dispose(); StatusMessage = "The open document changed. Your tab and edits were kept."; return null; }
                    var index = Tabs.IndexOf(existing);
                    RemoveTab(existing, retireRecovery: true);
                    Attach(opened, activate);
                    Tabs.Move(Tabs.IndexOf(opened), Math.Min(index, Tabs.Count - 1));
                }
                else Attach(opened, activate);
                if (recordRecent) Recent.AddFile(opened.FilePath!);
                return opened;
            }, DocumentOperation.Loading).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or System.ComponentModel.Win32Exception)
        { _dialogs.ShowMessage("Cannot open file", ex.Message); return null; }
        finally { _explicitOpenGate.Release(); }
    }
}
