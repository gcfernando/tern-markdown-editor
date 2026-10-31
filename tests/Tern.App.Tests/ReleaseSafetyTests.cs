using Tern.App.Documents;
using Tern.App.Panels;
using Tern.Domain.Documents;
using Tern.Domain.Text;
using Tern.Filesystem;
using Tern.Markdown;
using Tern.Recovery;

namespace Tern.App.Tests;

public sealed class ReleaseSafetyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Save_as_target_that_appears_during_the_operation_never_routes_to_original_conflict_actions(bool untitled)
    {
        using var f = new ShellFixture();
        var original = await f.WriteAsync("original.md", "original disk");
        var tab = untitled ? f.Shell.NewDocument("mine") : (await f.Shell.OpenFileAsync(original))!;
        if (!untitled) tab.ApplyEdits([TextEdit.Insert(0, "mine ")]);
        var target = Path.Combine(f.Docs, "new-target.md");
        f.Dialogs.SavePath = target;
        f.Dialogs.ConflictChoice = Tern.App.Shell.ConflictChoice.KeepMine;
        f.Shell.BeforeSaveAsWrite = () => { File.WriteAllText(target, "competing writer"); return Task.CompletedTask; };
        Assert.False(await f.Shell.SaveTabAsAsync(tab));
        Assert.Equal("competing writer", await File.ReadAllTextAsync(target));
        Assert.Equal("original disk", await File.ReadAllTextAsync(original));
        Assert.Null(tab.Conflict);
        Assert.Equal(untitled ? null : original, tab.FilePath);
        Assert.True(tab.IsDirty);
    }

    [Fact]
    public async Task Opening_a_save_as_destination_waits_for_its_canonical_session()
    {
        using var f = new ShellFixture();
        var tab = f.Shell.NewDocument("mine");
        var target = Path.Combine(f.Docs, "destination.md");
        f.Dialogs.SavePath = target;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Shell.BeforeSaveAsWrite = async () => { entered.SetResult(); await release.Task; };
        var saving = f.Shell.SaveTabAsAsync(tab);
        await entered.Task;
        var opening = f.Shell.OpenFileAsync(target);
        Assert.False(opening.IsCompleted);
        release.SetResult();
        Assert.True(await saving);
        Assert.Same(tab, await opening);
        Assert.Single(f.Shell.Tabs, t => t.FilePath == target);
    }

    [Fact]
    public async Task Conflict_save_copy_cannot_overwrite_another_open_document()
    {
        using var f = new ShellFixture();
        var original = await f.WriteAsync("original.md", "original");
        var other = await f.WriteAsync("other.md", "other source");
        var tab = (await f.Shell.OpenFileAsync(original))!;
        await f.Shell.OpenFileAsync(other);
        tab.ApplyEdits([TextEdit.Insert(0, "mine ")]);
        await File.WriteAllTextAsync(original, "disk change");
        f.Dialogs.ConflictChoice = Tern.App.Shell.ConflictChoice.SaveCopy;
        f.Dialogs.SavePath = other;
        f.Dialogs.Overwrite = true;
        Assert.False(await f.Shell.SaveTabAsync(tab));
        Assert.Equal("other source", await File.ReadAllTextAsync(other));
        Assert.Equal("disk change", await File.ReadAllTextAsync(original));
        Assert.IsType<SaveFailed>(await tab.SaveCopyAsync(original, true));
        Assert.Equal("disk change", await File.ReadAllTextAsync(original));
    }

    [Fact]
    public void Explicit_data_root_isolates_all_application_state()
    {
        using var root = new TempDir();
        var previous = Environment.GetEnvironmentVariable("TERN_DATA_ROOT");
        try
        {
            Environment.SetEnvironmentVariable("TERN_DATA_ROOT", root.Path);
            Assert.Equal(Tern.App.Shell.AppPaths.Under(root.Path), Tern.App.Shell.AppPaths.Default());
        }
        finally { Environment.SetEnvironmentVariable("TERN_DATA_ROOT", previous); }
    }

    [Fact]
    public async Task Merge_rejects_disk_changes_after_the_reviewed_snapshot()
    {
        using var f = new ShellFixture();
        var path = await f.WriteAsync("merge.md", "a\nb\nc\n");
        var tab = (await f.Shell.OpenFileAsync(path))!;
        tab.ApplyEdits([TextEdit.Insert(0, "mine\n")]);
        await File.WriteAllTextAsync(path, "a\nb\nc\ndisk one\n");
        var merge = await tab.BuildMergeAsync();
        const string newest = "a\nb\nc\ndisk one\ndisk two\n";
        await File.WriteAllTextAsync(path, newest);
        Assert.False(await tab.ApplyMergedTextAsync(merge.MergedText));
        Assert.Equal(newest, await File.ReadAllTextAsync(path));
        Assert.NotNull(tab.Conflict);
        Assert.IsType<SaveConflict>(await tab.SaveAsync());
    }

    [Fact]
    public async Task Merge_rejects_buffer_changes_after_review()
    {
        using var f = new ShellFixture();
        var tab = (await f.Shell.OpenFileAsync(await f.WriteAsync("merge.md", "base\n")))!;
        var merge = await tab.BuildMergeAsync();
        tab.ApplyEdits([TextEdit.Insert(0, "new typing\n")]);
        Assert.False(await tab.ApplyMergedTextAsync(merge.MergedText));
        Assert.StartsWith("new typing", tab.CurrentText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Recovery_is_a_separate_unsaved_copy_even_when_the_original_is_already_open()
    {
        using var f = new ShellFixture();
        var path = await f.WriteAsync("document.md", "newer disk");
        var disk = await f.Shell.OpenFileAsync(path);
        var record = new RecoveryRecord(Guid.NewGuid(), path, "document.md", 2, DateTime.UtcNow, "OLD-DISK-HASH",
            LineEndingAnalyzer.Describe(TextEncodingKind.Utf8, false, "recovered"), "recovered");
        var restored = await f.Shell.RestoreRecoveryAsync(record);
        Assert.NotSame(disk, restored);
        Assert.True(restored.IsUntitled);
        Assert.True(restored.IsRecovered);
        Assert.True(restored.IsDirty);
        Assert.Equal(path, restored.RecoveredFromPath);
        restored.ApplyEdits([TextEdit.Insert(0, "edited ")]);
        Assert.Equal("Recovered — document.md", restored.DisplayName);
        Assert.Single(f.Shell.Tabs, t => t.FilePath == path);
        Assert.Equal("newer disk", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Repeated_links_on_one_line_do_not_break_the_inspector()
    {
        using var f = new ShellFixture();
        var path = await f.WriteAsync("source.md", "[a](target.md) [b](target.md)\n");
        await f.WriteAsync("target.md", "# Target\n");
        await f.Shell.OpenWorkspaceAsync(f.Docs);
        var tab = (await f.Shell.OpenFileAsync(path))!;
        for (var i = 0; i < 100 && tab.Analysis is null; i++) await Task.Delay(10);
        var panel = DocumentPanel.Build(tab, f.Shell.Workspace);
        Assert.Equal(2, panel.Links.Count);
        Assert.Equal(0, panel.LinksBroken);
    }

    [Fact]
    public void Seeded_untitled_content_is_unsaved()
    {
        using var tab = DocumentTab.CreateUntitled(new DocumentFileService(), MarkdownProfile.Gfm, "draft");
        Assert.True(tab.IsDirty);
        Assert.True(tab.IsUntitled);
        Assert.Equal(LineEnding.CrLf, tab.Session.Format.DominantLineEnding);
    }

    [Fact]
    public async Task Format_changed_during_save_remains_unsaved_even_after_an_undo_notification()
    {
        using var dir = new TempDir();
        var path = dir.File("format.md");
        await File.WriteAllTextAsync(path, "saved text");
        var files = new DocumentFileService();
        using var tab = await DocumentTab.OpenAsync(path, files, MarkdownProfile.Gfm);
        var buffer = new UndoProbeBuffer(tab.CurrentText);
        tab.Session.AttachBuffer(buffer);
        tab.ApplyEdits([TextEdit.Insert(0, "edit ")]);
        files.BeforeReplace = _ => tab.Session.ChangeFormat(tab.Session.Format with { Encoding = TextEncodingKind.Utf16LittleEndian, HasBom = true });
        Assert.IsType<SaveSucceeded>(await tab.SaveAsync());
        Assert.Equal(TextEncodingKind.Utf16LittleEndian, tab.Session.Format.Encoding);
        Assert.True(tab.IsDirty);
        buffer.SignalUndo();
        Assert.True(tab.IsDirty);
        Assert.DoesNotContain((byte)0, await File.ReadAllBytesAsync(path));
    }

    [Theory]
    [InlineData(TextEncodingKind.Utf16LittleEndian, true, TextEncodingKind.Utf8, true)]
    [InlineData(TextEncodingKind.Utf8, true, TextEncodingKind.Utf16LittleEndian, true)]
    public async Task Matching_external_text_preserves_an_unsaved_format_conversion(
        TextEncodingKind requestedEncoding, bool requestedBom, TextEncodingKind externalEncoding, bool externalBom)
    {
        using var dir = new TempDir();
        var path = dir.File("external-format.md");
        const string text = "same characters\n";
        await File.WriteAllBytesAsync(path, TextFileCodec.Encode(text, LineEndingAnalyzer.Describe(TextEncodingKind.Utf8, false, text)));
        var files = new DocumentFileService();
        using var tab = await DocumentTab.OpenAsync(path, files, MarkdownProfile.Gfm);
        Assert.True(tab.TryConvertEncoding(requestedEncoding, requestedBom));
        var requested = tab.Session.Format;
        var external = TextFileCodec.Encode(text, LineEndingAnalyzer.Describe(externalEncoding, externalBom, text));
        await File.WriteAllBytesAsync(path, external);

        Assert.Equal(DiskCheckResult.BaselineRefreshed, await tab.CheckDiskAsync(true));
        Assert.True(tab.IsDirty);
        Assert.Equal(requested, tab.Session.Format);
        Assert.Equal(external, await File.ReadAllBytesAsync(path));
        Assert.Null(tab.Conflict);
        Assert.IsType<SaveSucceeded>(await tab.SaveAsync());
        const string savedText = text;
        Assert.Equal(TextFileCodec.Encode(savedText, requested), await File.ReadAllBytesAsync(path));
        Assert.Equal(requestedEncoding, tab.Session.Format.Encoding);
        Assert.Equal(requestedBom, tab.Session.Format.HasBom);
        Assert.Equal("LF", tab.LineEndingText);
        Assert.False(tab.IsDirty);
    }

    [Fact]
    public async Task Matching_external_text_and_requested_format_can_become_clean()
    {
        using var dir = new TempDir();
        var path = dir.File("converged-format.md");
        await File.WriteAllTextAsync(path, "same characters\n");
        using var tab = await DocumentTab.OpenAsync(path, new DocumentFileService(), MarkdownProfile.Gfm);
        Assert.True(tab.TryConvertEncoding(TextEncodingKind.Utf16LittleEndian, true));
        await File.WriteAllBytesAsync(path, TextFileCodec.Encode(tab.CurrentText, tab.Session.Format));

        Assert.Equal(DiskCheckResult.BaselineRefreshed, await tab.CheckDiskAsync(true));
        Assert.False(tab.IsDirty);
        Assert.Null(tab.Conflict);
    }

    [Theory]
    [InlineData("original text")]
    [InlineData("\uFFFD")]
    public async Task Auto_reload_of_invalid_bytes_is_read_only_even_when_lossy_text_matches(string original)
    {
        using var dir = new TempDir();
        var path = dir.File("invalid-external.md");
        await File.WriteAllTextAsync(path, original);
        using var tab = await DocumentTab.OpenAsync(path, new DocumentFileService(), MarkdownProfile.Gfm);
        byte[] invalid = [0xFF];
        await File.WriteAllBytesAsync(path, invalid);

        Assert.Equal(DiskCheckResult.ReloadedSilently, await tab.CheckDiskAsync(true));
        Assert.True(tab.IsReadOnly);
        Assert.False(tab.IsDirty);
        Assert.NotNull(tab.ReadOnlyReason);
        Assert.IsType<SaveFailed>(await tab.SaveAsync());
        Assert.Equal(invalid, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task Auto_reload_of_repaired_bytes_restores_writable_state()
    {
        using var dir = new TempDir();
        var path = dir.File("repaired-external.md");
        await File.WriteAllBytesAsync(path, [0xFF]);
        using var tab = await DocumentTab.OpenAsync(path, new DocumentFileService(), MarkdownProfile.Gfm);
        Assert.True(tab.IsReadOnly);
        await File.WriteAllTextAsync(path, "repaired\n");

        Assert.Equal(DiskCheckResult.ReloadedSilently, await tab.CheckDiskAsync(true));
        Assert.False(tab.IsReadOnly);
        Assert.Null(tab.ReadOnlyReason);
        Assert.Equal("repaired\n", tab.CurrentText);
    }

    [Theory]
    [InlineData("encoding", false)]
    [InlineData("encoding", true)]
    [InlineData("disk", false)]
    [InlineData("disk", true)]
    [InlineData("check", false)]
    [InlineData("check", true)]
    public async Task Loaded_content_cannot_replace_a_newer_edit_or_save_as_identity(string operation, bool saveAs)
    {
        using var dir = new TempDir();
        var path = dir.File("read-race.md");
        await File.WriteAllTextAsync(path, "original");
        var files = new DocumentFileService();
        using var tab = await DocumentTab.OpenAsync(path, files, MarkdownProfile.Gfm);
        await File.WriteAllTextAsync(path, "external");
        var read = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        files.AfterReadForLoadAsync = async _ => { read.TrySetResult(); await resume.Task; };
        var pending = operation switch
        {
            "encoding" => tab.ReopenWithEncodingAsync(TextEncodingKind.Latin1),
            "disk" => tab.TakeDiskVersionAsync(),
            _ => DiskCheckAppliedAsync(tab),
        };
        try
        {
            await read.Task.WaitAsync(TimeSpan.FromSeconds(3));
            if (saveAs)
                Assert.IsType<SaveSucceeded>(await tab.SaveAsAsync(dir.File("new-identity.md"), false));
            else
                tab.ApplyEdits([TextEdit.Insert(0, "mine ")]);
            var expectedPath = tab.FilePath;
            var expectedText = tab.CurrentText;
            var expectedBaseline = tab.Session.Baseline;
            var expectedFormat = tab.Session.Format;
            resume.TrySetResult();

            Assert.False(await pending.WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.Equal(expectedPath, tab.FilePath);
            Assert.Equal(expectedText, tab.CurrentText);
            Assert.Equal(expectedBaseline, tab.Session.Baseline);
            Assert.Equal(expectedFormat, tab.Session.Format);
            Assert.Equal(!saveAs, tab.IsDirty);
            Assert.Equal("external", await File.ReadAllTextAsync(path));
        }
        finally { resume.TrySetResult(); await pending; }
    }

    [Fact]
    public async Task Automatic_reload_cannot_replace_an_edit_saved_during_the_disk_read()
    {
        using var dir = new TempDir();
        var path = dir.File("saved-during-read.md");
        await File.WriteAllTextAsync(path, "original");
        var files = new DocumentFileService();
        using var tab = await DocumentTab.OpenAsync(path, files, MarkdownProfile.Gfm);
        await File.WriteAllTextAsync(path, "external");
        var read = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        files.AfterReadForLoadAsync = async _ => { read.TrySetResult(); await resume.Task; };
        var pending = tab.CheckDiskAsync(true);
        try
        {
            await read.Task.WaitAsync(TimeSpan.FromSeconds(3));
            tab.ApplyEdits([TextEdit.Insert(0, "mine ")]);
            Assert.IsType<SaveSucceeded>(await tab.KeepMineAndOverwriteAsync());
            Assert.False(tab.IsDirty);
            var savedBaseline = tab.Session.Baseline;
            resume.TrySetResult();

            Assert.Equal(DiskCheckResult.Unchanged, await pending.WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.Equal("mine original", tab.CurrentText);
            Assert.Equal("mine original", await File.ReadAllTextAsync(path));
            Assert.Equal(savedBaseline, tab.Session.Baseline);
            Assert.False(tab.IsDirty);
        }
        finally { resume.TrySetResult(); await pending; }
    }

    private static async Task<bool> DiskCheckAppliedAsync(DocumentTab tab) =>
        await tab.CheckDiskAsync(true) != DiskCheckResult.Unchanged;

    private sealed class UndoProbeBuffer(string text) : IDocumentBuffer
    {
        private string _text = text;
        public event EventHandler<BufferChangedEventArgs>? Changed;
        public string GetText() => _text;
        public void Apply(IReadOnlyList<TextEdit> edits)
        {
            _text = TextEdit.ApplyAll(_text, edits);
            Changed?.Invoke(this, new BufferChangedEventArgs(false));
        }
        public void SignalUndo() => Changed?.Invoke(this, new BufferChangedEventArgs(true));
    }

    [Fact]
    public async Task Queued_analysis_for_a_superseded_profile_cannot_publish()
    {
        var context = new QueuedContext();
        using var tab = CreateQueuedTab(context);
        var published = new List<MarkdownProfile>();
        tab.AnalysisChanged += (_, _) => published.Add(tab.Analysis!.Profile);
        await WaitForPost(context);
        tab.SetProfile(MarkdownProfile.CommonMark);
        for (var i = 0; i < 100 && context.Count < 2; i++) await Task.Delay(10);
        context.Drain();
        Assert.Equal(MarkdownProfile.CommonMark, tab.Analysis!.Profile);
        Assert.DoesNotContain(MarkdownProfile.Extended, published);
    }

    [Fact]
    public async Task Disposed_tabs_ignore_already_queued_analysis()
    {
        var context = new QueuedContext();
        var tab = CreateQueuedTab(context);
        await WaitForPost(context);
        tab.Dispose();
        context.Drain();
        Assert.Null(tab.Analysis);
        Assert.Throws<ObjectDisposedException>(() => tab.ApplyEdits([TextEdit.Insert(0, "late")]));
    }

    private static DocumentTab CreateQueuedTab(QueuedContext context)
    {
        var previous = SynchronizationContext.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            return new DocumentTab(new DocumentSession(null, "# Heading\n", LineEndingAnalyzer.Describe(TextEncodingKind.Utf8, false, "# Heading\n"), null),
                new DocumentFileService(), MarkdownProfile.Extended);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    private static async Task WaitForPost(QueuedContext context)
    {
        for (var i = 0; i < 100 && context.Count == 0; i++) await Task.Delay(10);
        Assert.True(context.Count > 0);
    }

    private sealed class QueuedContext : SynchronizationContext
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _pending = new();
        public int Count => _pending.Count;
        public override void Post(SendOrPostCallback d, object? state) => _pending.Enqueue((d, state));
        public void Drain()
        {
            while (_pending.TryDequeue(out var item)) item.Callback(item.State);
        }
    }
}
