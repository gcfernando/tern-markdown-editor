using Tern.App.Shell;
using Tern.Domain.Documents;
using Tern.Domain.Text;
using Tern.Filesystem;
using Tern.Workspaces;

namespace Tern.App.Tests;

public sealed class DuplicateSourceWorkflowTests
{
    [Theory]
    [InlineData(DuplicateOpenChoice.Cancel)]
    [InlineData(DuplicateOpenChoice.NewTab)]
    [InlineData(DuplicateOpenChoice.Replace)]
    public async Task Explicit_local_duplicate_obeys_choice_and_preserves_unrelated_tab(DuplicateOpenChoice choice)
    {
        using var fixture = new ShellFixture();
        var path = await fixture.WriteAsync("source.md", "Original\r\n");
        var original = (await fixture.Shell.OpenFileAsync(path))!;
        var otherPath = await fixture.WriteAsync("other.md", "Other file\r\n");
        var other = (await fixture.Shell.OpenFileAsync(otherPath))!;
        fixture.Dialogs.DuplicateChoice = choice;
        await File.WriteAllTextAsync(path, "Fresh disk text\r\n");

        var opened = await fixture.Shell.OpenFileAsync(path, duplicateBehavior: DuplicateOpenBehavior.Ask);

        Assert.Equal(1, fixture.Dialogs.DuplicatePrompts);
        Assert.Equal(0, fixture.Dialogs.SavePrompts);
        Assert.Contains(other, fixture.Shell.Tabs);
        Assert.Equal("Other file\r\n", other.CurrentText);
        Assert.Equal("Fresh disk text\r\n", await File.ReadAllTextAsync(path));
        if (choice == DuplicateOpenChoice.Cancel)
        {
            Assert.Null(opened);
            Assert.Equal(2, fixture.Shell.Tabs.Count);
            Assert.Same(other, fixture.Shell.ActiveTab);
            Assert.Equal("Original\r\n", original.CurrentText);
        }
        else
        {
            Assert.NotNull(opened);
            Assert.NotSame(original, opened);
            Assert.Equal("Fresh disk text\r\n", opened.CurrentText);
            Assert.False(opened.IsDirty);
            Assert.Same(opened, fixture.Shell.ActiveTab);
            Assert.Equal(choice == DuplicateOpenChoice.NewTab ? 3 : 2, fixture.Shell.Tabs.Count);
            Assert.Equal(choice == DuplicateOpenChoice.NewTab, fixture.Shell.Tabs.Contains(original));
        }
    }

    [Fact]
    public async Task Passive_local_activation_finds_canonical_source_without_reload_or_prompt()
    {
        using var fixture = new ShellFixture();
        var path = await fixture.WriteAsync("source.md", "Original\r\n");
        var original = (await fixture.Shell.OpenFileAsync(path))!;
        original.ApplyEdits([TextEdit.Insert(0, "Unsaved ")]);
        var revision = original.Session.Revision;
        fixture.Shell.NewDocument();
        File.Delete(path);
        var alternate = Path.Combine(Path.GetDirectoryName(path)!, ".", Path.GetFileName(path).ToUpperInvariant());

        Assert.Same(original, fixture.Shell.FindOpenSource(alternate));
        Assert.Same(original, await fixture.Shell.OpenFileAsync(alternate));

        Assert.Same(original, fixture.Shell.ActiveTab);
        Assert.Equal(2, fixture.Shell.Tabs.Count);
        Assert.Equal("Unsaved Original\r\n", original.CurrentText);
        Assert.Equal(revision, original.Session.Revision);
        Assert.True(original.IsDirty);
        Assert.Equal(0, fixture.Dialogs.DuplicatePrompts);
        Assert.Equal(0, fixture.Dialogs.SavePrompts);
        Assert.Empty(fixture.Dialogs.Messages);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task Failed_replacement_read_retains_the_existing_buffer_and_active_tab()
    {
        using var fixture = new ShellFixture();
        var path = await fixture.WriteAsync("source.md", "Original\r\n");
        var original = (await fixture.Shell.OpenFileAsync(path))!;
        fixture.Dialogs.DuplicateChoice = DuplicateOpenChoice.Replace;
        File.Delete(path);

        var replacement = await fixture.Shell.OpenFileAsync(path, duplicateBehavior: DuplicateOpenBehavior.Ask);

        Assert.Null(replacement);
        Assert.Same(original, Assert.Single(fixture.Shell.Tabs));
        Assert.Same(original, fixture.Shell.ActiveTab);
        Assert.Equal("Original\r\n", original.CurrentText);
        Assert.Single(fixture.Dialogs.Messages);
    }

    [Theory]
    [InlineData(SaveChoice.Cancel)]
    [InlineData(SaveChoice.Save)]
    [InlineData(SaveChoice.Discard)]
    public async Task Replacing_a_modified_local_source_requires_the_unsaved_work_choice(SaveChoice saveChoice)
    {
        using var fixture = new ShellFixture();
        var path = await fixture.WriteAsync("source.md", "Original\r\n");
        var original = (await fixture.Shell.OpenFileAsync(path))!;
        original.ApplyEdits([TextEdit.Insert(0, "Unsaved ")]);
        fixture.Dialogs.DuplicateChoice = DuplicateOpenChoice.Replace;
        fixture.Dialogs.SaveChoice = saveChoice;

        var replacement = await fixture.Shell.OpenFileAsync(path, duplicateBehavior: DuplicateOpenBehavior.Ask);

        Assert.Equal(1, fixture.Dialogs.DuplicatePrompts);
        Assert.Equal(1, fixture.Dialogs.SavePrompts);
        if (saveChoice == SaveChoice.Cancel)
        {
            Assert.Null(replacement);
            Assert.Same(original, Assert.Single(fixture.Shell.Tabs));
            Assert.Equal("Unsaved Original\r\n", original.CurrentText);
            Assert.True(original.IsDirty);
            Assert.Equal("Original\r\n", await File.ReadAllTextAsync(path));
        }
        else
        {
            Assert.NotNull(replacement);
            Assert.NotSame(original, replacement);
            Assert.Same(replacement, Assert.Single(fixture.Shell.Tabs));
            var expected = saveChoice == SaveChoice.Save ? "Unsaved Original\r\n" : "Original\r\n";
            Assert.Equal(expected, replacement.CurrentText);
            Assert.Equal(expected, await File.ReadAllTextAsync(path));
            Assert.False(replacement.IsDirty);
        }
    }

    [Fact]
    public async Task Failed_save_during_replacement_keeps_unsaved_text_and_the_competing_disk_version()
    {
        using var fixture = new ShellFixture();
        var path = await fixture.WriteAsync("source.md", "Original\r\n");
        var original = (await fixture.Shell.OpenFileAsync(path))!;
        original.ApplyEdits([TextEdit.Insert(0, "Unsaved ")]);
        await File.WriteAllTextAsync(path, "Competing version\r\n");
        fixture.Dialogs.DuplicateChoice = DuplicateOpenChoice.Replace;
        fixture.Dialogs.SaveChoice = SaveChoice.Save;
        fixture.Dialogs.ConflictChoice = ConflictChoice.Cancel;

        Assert.Null(await fixture.Shell.OpenFileAsync(path, duplicateBehavior: DuplicateOpenBehavior.Ask));

        Assert.Same(original, Assert.Single(fixture.Shell.Tabs));
        Assert.True(original.IsDirty);
        Assert.Equal("Unsaved Original\r\n", original.CurrentText);
        Assert.Equal("Competing version\r\n", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Online_source_has_a_clean_baseline_and_closes_without_a_save_prompt_until_edited()
    {
        using var fixture = new ShellFixture();
        var online = fixture.Shell.OpenOnlineDocument("https://example.com/notes.md", "notes.md", "Remote text\n");

        Assert.False(online.IsDirty);
        Assert.Null(online.FilePath);
        Assert.True(online.IsExternalDraft);
        Assert.Equal("Remote text\n", online.CurrentText);
        Assert.Same(online, fixture.Shell.FindOpenSource("HTTPS://EXAMPLE.COM:443/notes.md#heading", online: true));
        Assert.True(await fixture.Shell.CloseTabAsync(online));
        Assert.Empty(fixture.Shell.Tabs);
        Assert.Equal(0, fixture.Dialogs.SavePrompts);

        online = fixture.Shell.OpenOnlineDocument("https://example.com/notes.md", "notes.md", "Remote text\n");
        online.ApplyEdits([TextEdit.Insert(0, "Edited ")]);
        Assert.True(online.IsDirty);
        Assert.False(await fixture.Shell.CloseTabAsync(online));
        Assert.Equal(1, fixture.Dialogs.SavePrompts);
        Assert.Same(online, Assert.Single(fixture.Shell.Tabs));
        Assert.Equal("Edited Remote text\n", online.CurrentText);
    }

    [Fact]
    public async Task Undo_to_the_original_online_text_restores_the_clean_remote_baseline()
    {
        using var fixture = new ShellFixture();
        var online = fixture.Shell.OpenOnlineDocument("https://example.com/notes.md", "notes.md", "Original\n");
        var undo = new UndoBuffer(online.CurrentText);
        online.Session.AttachBuffer(undo);
        online.ApplyEdits([TextEdit.Insert(0, "Modified ")]);
        Assert.True(online.IsDirty);

        undo.RestoreAsUndo("Original\n");

        Assert.False(online.IsDirty);
        Assert.True(await fixture.Shell.CloseTabAsync(online));
        Assert.Equal(0, fixture.Dialogs.SavePrompts);
    }

    [Fact]
    public async Task Distinct_local_directories_and_case_sensitive_URL_paths_do_not_share_identity()
    {
        using var fixture = new ShellFixture();
        var first = (await fixture.Shell.OpenFileAsync(await fixture.WriteAsync("one/same.md", "One")))!;
        var second = (await fixture.Shell.OpenFileAsync(await fixture.WriteAsync("two/same.md", "Two")))!;
        var upper = fixture.Shell.OpenOnlineDocument("https://example.com/Guide.md?q=A", "same.md", "Upper");
        var lower = fixture.Shell.OpenOnlineDocument("https://example.com/guide.md?q=A", "same.md", "Lower");
        Assert.NotSame(first, second);
        Assert.NotSame(upper, lower);
        Assert.Same(upper, fixture.Shell.FindOpenSource("https://example.com/Guide.md?q=A", online: true));
        Assert.Same(lower, fixture.Shell.FindOpenSource("https://example.com/guide.md?q=A", online: true));
        Assert.Null(fixture.Shell.FindOpenSource("https://example.com/Guide.md?q=a", online: true));
        Assert.Equal(4, fixture.Shell.Tabs.Count);
        Assert.Equal(0, fixture.Dialogs.DuplicatePrompts);
    }

    [Theory]
    [InlineData(TabCloseScope.All)]
    [InlineData(TabCloseScope.Right)]
    [InlineData(TabCloseScope.Others)]
    public async Task Group_tab_menu_cancellation_preserves_all_target_tabs_and_their_unsaved_text(TabCloseScope scope)
    {
        using var fixture = new ShellFixture();
        var anchor = (await fixture.Shell.OpenFileAsync(await fixture.WriteAsync("anchor.md", "Anchor\r\n")))!;
        var firstPath = await fixture.WriteAsync("first.md", "First\r\n");
        var secondPath = await fixture.WriteAsync("second.md", "Second\r\n");
        var first = (await fixture.Shell.OpenFileAsync(firstPath))!;
        var second = (await fixture.Shell.OpenFileAsync(secondPath))!;
        first.ApplyEdits([TextEdit.Insert(0, "Edited ")]);
        second.ApplyEdits([TextEdit.Insert(0, "Edited ")]);
        var originalTabs = fixture.Shell.Tabs.ToArray();
        fixture.Dialogs.SaveResponses.Enqueue(SaveChoice.Discard);
        fixture.Dialogs.SaveResponses.Enqueue(SaveChoice.Cancel);

        Assert.False(await fixture.Shell.CloseTabsRelativeAsync(anchor, scope));

        Assert.Equal(originalTabs, fixture.Shell.Tabs);
        Assert.Equal(2, fixture.Dialogs.SavePrompts);
        Assert.Equal("Edited First\r\n", first.CurrentText);
        Assert.Equal("Edited Second\r\n", second.CurrentText);
        Assert.True(first.IsDirty);
        Assert.True(second.IsDirty);
        Assert.Equal("First\r\n", await File.ReadAllTextAsync(firstPath));
        Assert.Equal("Second\r\n", await File.ReadAllTextAsync(secondPath));
    }

    [Theory]
    [InlineData(TabCloseScope.This, false, true, false)]
    [InlineData(TabCloseScope.All, true, true, true)]
    [InlineData(TabCloseScope.Right, false, false, true)]
    [InlineData(TabCloseScope.Others, true, false, true)]
    public async Task Tab_menu_closes_only_the_requested_relative_scope(TabCloseScope scope, bool closesLeft, bool closesAnchor, bool closesRight)
    {
        using var fixture = new ShellFixture();
        var left = (await fixture.Shell.OpenFileAsync(await fixture.WriteAsync("left.md", "Left\r\n")))!;
        var anchor = (await fixture.Shell.OpenFileAsync(await fixture.WriteAsync("anchor.md", "Anchor\r\n")))!;
        var right = (await fixture.Shell.OpenFileAsync(await fixture.WriteAsync("right.md", "Right\r\n")))!;

        Assert.True(await fixture.Shell.CloseTabsRelativeAsync(anchor, scope));

        Assert.Equal(!closesLeft, fixture.Shell.Tabs.Contains(left));
        Assert.Equal(!closesAnchor, fixture.Shell.Tabs.Contains(anchor));
        Assert.Equal(!closesRight, fixture.Shell.Tabs.Contains(right));
        Assert.Equal(0, fixture.Dialogs.SavePrompts);
    }

    [Theory]
    [InlineData(SaveChoice.Cancel)]
    [InlineData(SaveChoice.Discard)]
    [InlineData(SaveChoice.Save)]
    public async Task Replacing_modified_online_source_preserves_edits_unless_discard_or_save_was_accepted(SaveChoice choice)
    {
        using var fixture = new ShellFixture();
        const string source = "https://example.com/online.md";
        var online = fixture.Shell.OpenOnlineDocument(source, "online.md", "Original\r\n");
        online.ApplyEdits([TextEdit.Insert(0, "Edited ")]);
        fixture.Dialogs.SaveChoice = choice;
        var savedPath = Path.Combine(fixture.Docs, "saved-online.md");
        fixture.Dialogs.SavePath = savedPath;

        var replacement = await fixture.Shell.ReplaceOnlineDocumentAsync(online, source, "online.md", "Fresh server text\r\n");

        Assert.Equal(1, fixture.Dialogs.SavePrompts);
        if (choice == SaveChoice.Cancel)
        {
            Assert.Null(replacement);
            Assert.Same(online, Assert.Single(fixture.Shell.Tabs));
            Assert.Equal("Edited Original\r\n", online.CurrentText);
            Assert.True(online.IsDirty);
        }
        else
        {
            Assert.NotNull(replacement);
            Assert.Same(replacement, Assert.Single(fixture.Shell.Tabs));
            Assert.Equal("Fresh server text\r\n", replacement.CurrentText);
            Assert.False(replacement.IsDirty);
            Assert.Null(replacement.FilePath);
            Assert.Equal(source, replacement.SourceUrl);
        }
        Assert.Equal(choice == SaveChoice.Save, File.Exists(savedPath));
        if (choice == SaveChoice.Save) Assert.Equal("Edited Original\r\n", await File.ReadAllTextAsync(savedPath));
    }

    [Fact]
    public async Task Canceled_local_save_of_modified_online_source_prevents_replacement()
    {
        using var fixture = new ShellFixture();
        const string source = "https://example.com/online.md";
        var online = fixture.Shell.OpenOnlineDocument(source, "online.md", "Original\r\n");
        online.ApplyEdits([TextEdit.Insert(0, "Edited ")]);
        fixture.Dialogs.SaveChoice = SaveChoice.Save;
        fixture.Dialogs.SavePath = null;

        Assert.Null(await fixture.Shell.ReplaceOnlineDocumentAsync(online, source, "online.md", "Fresh server text\r\n"));

        Assert.Same(online, Assert.Single(fixture.Shell.Tabs));
        Assert.True(online.IsDirty);
        Assert.Equal("Edited Original\r\n", online.CurrentText);
        Assert.Empty(Directory.EnumerateFiles(fixture.Docs));
    }

    [Fact]
    public async Task Download_save_picker_cancellation_creates_no_tab_or_file()
    {
        using var fixture = new ShellFixture();
        fixture.Dialogs.SavePath = null;

        Assert.Null(await fixture.Shell.DownloadOnlineDocumentAsync("https://example.com/download.md", "download.md", "Remote\n"));

        Assert.Empty(fixture.Shell.Tabs);
        Assert.Empty(Directory.EnumerateFiles(fixture.Docs));
        Assert.Empty(fixture.Shell.Recent.Files);
        Assert.Empty(fixture.Dialogs.Messages);
    }

    [Fact]
    public async Task Download_opens_a_clean_local_file_with_real_encoding_and_persisted_local_history()
    {
        using var fixture = new ShellFixture();
        const string source = "https://example.com/download.md";
        const string text = "Café 日本語\r\nSecond line\r\n";
        var format = LineEndingAnalyzer.Describe(TextEncodingKind.Utf16LittleEndian, true, text);
        var path = Path.Combine(fixture.Docs, "download.md");
        fixture.Dialogs.SavePath = path;
        fixture.Shell.Recent.AddUrl(source);

        var downloaded = await fixture.Shell.DownloadOnlineDocumentAsync(source, "download.md", text, format);

        Assert.NotNull(downloaded);
        Assert.Same(downloaded, Assert.Single(fixture.Shell.Tabs));
        Assert.Equal(path, downloaded.FilePath);
        Assert.False(downloaded.IsExternalDraft);
        Assert.False(downloaded.IsDirty);
        Assert.Equal(text, downloaded.CurrentText);
        Assert.Equal(format.Encoding, downloaded.Session.Format.Encoding);
        Assert.True(downloaded.Session.Format.HasBom);
        Assert.Equal(TextFileCodec.Encode(text, format), await File.ReadAllBytesAsync(path));
        var history = new RecentItemsStore(fixture.Shell.Paths.StateDirectory);
        Assert.Equal(path, Assert.Single(history.Files).Path);
        Assert.Empty(history.Urls);
        Assert.True(await fixture.Shell.CloseTabAsync(downloaded));
        Assert.Equal(0, fixture.Dialogs.SavePrompts);
        var reopened = await fixture.Shell.OpenFileAsync(Assert.Single(history.Files).Path);
        Assert.NotNull(reopened);
        Assert.Equal(text, reopened.CurrentText);
        Assert.Equal(format.Encoding, reopened.Session.Format.Encoding);
        Assert.True(reopened.Session.Format.HasBom);
        Assert.False(reopened.IsExternalDraft);
    }

    [Fact]
    public async Task Download_refuses_unapproved_overwrite_and_keeps_existing_bytes()
    {
        using var fixture = new ShellFixture();
        var path = await fixture.WriteAsync("existing.md", "Keep existing\r\n");
        fixture.Dialogs.SavePath = path;
        fixture.Dialogs.Overwrite = false;

        Assert.Null(await fixture.Shell.DownloadOnlineDocumentAsync("https://example.com/download.md", "download.md", "Remote\n"));

        Assert.Equal("Keep existing\r\n", await File.ReadAllTextAsync(path));
        Assert.Empty(fixture.Shell.Tabs);
        Assert.Empty(fixture.Shell.Recent.Files);
    }

    [Fact]
    public async Task Failed_download_destination_keeps_existing_online_edits_for_replacement()
    {
        using var fixture = new ShellFixture();
        const string source = "https://example.com/download.md";
        var online = fixture.Shell.OpenOnlineDocument(source, "download.md", "Original\r\n");
        online.ApplyEdits([TextEdit.Insert(0, "Edited ")]);
        fixture.Dialogs.DuplicateChoice = DuplicateOpenChoice.Replace;
        fixture.Dialogs.SaveChoice = SaveChoice.Discard;
        fixture.Dialogs.SavePath = fixture.Docs; // An existing directory cannot become the destination file.

        Assert.Null(await fixture.Shell.DownloadOnlineDocumentAsync(source, "download.md", "Remote\r\n"));

        Assert.Same(online, Assert.Single(fixture.Shell.Tabs));
        Assert.True(online.IsDirty);
        Assert.Equal("Edited Original\r\n", online.CurrentText);
        Assert.True(Directory.Exists(fixture.Docs));
        Assert.Empty(Directory.EnumerateFiles(fixture.Docs));
        Assert.NotEmpty(fixture.Dialogs.Messages);
    }

    [Fact]
    public async Task Download_rechecks_open_destination_after_the_overwrite_prompt()
    {
        using var fixture = new ShellFixture();
        var path = await fixture.WriteAsync("existing.md", "Keep existing\r\n");
        fixture.Dialogs.SavePath = path;
        Tern.App.Documents.DocumentTab? openedDuringPrompt = null;
        fixture.Dialogs.OverwriteResponse = async () =>
        {
            openedDuringPrompt = (await fixture.Shell.OpenFileAsync(path))!;
            openedDuringPrompt.ApplyEdits([TextEdit.Insert(0, "Unsaved ")]);
            return true;
        };

        Assert.Null(await fixture.Shell.DownloadOnlineDocumentAsync("https://example.com/download.md", "download.md", "Remote\r\n"));

        Assert.NotNull(openedDuringPrompt);
        Assert.Same(openedDuringPrompt, Assert.Single(fixture.Shell.Tabs));
        Assert.True(openedDuringPrompt.IsDirty);
        Assert.Equal("Unsaved Keep existing\r\n", openedDuringPrompt.CurrentText);
        Assert.Equal("Keep existing\r\n", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Downloaded_file_remains_open_and_editable_when_a_long_encoded_source_cannot_fit_URL_history()
    {
        using var fixture = new ShellFixture();
        var source = new Uri("https://example.com/" + new string('文', 1000) + ".md").AbsoluteUri;
        Assert.True(source.Length > 8192);
        var path = Path.Combine(fixture.Docs, "download.md");
        fixture.Dialogs.SavePath = path;

        var downloaded = await fixture.Shell.DownloadOnlineDocumentAsync(source, "download.md", "Remote\r\n");

        Assert.NotNull(downloaded);
        Assert.Same(downloaded, Assert.Single(fixture.Shell.Tabs));
        Assert.Equal(path, downloaded.FilePath);
        Assert.Equal("Remote\r\n", await File.ReadAllTextAsync(path));
        downloaded.ApplyEdits([TextEdit.Insert(0, "Still editable ")]);
        Assert.True(downloaded.IsDirty);
        Assert.Equal("Still editable Remote\r\n", downloaded.CurrentText);
    }

    private sealed class UndoBuffer(string text) : IDocumentBuffer
    {
        private string _text = text;
        public event EventHandler<BufferChangedEventArgs>? Changed;
        public string GetText() => _text;
        public void Apply(IReadOnlyList<TextEdit> edits)
        {
            _text = TextEdit.ApplyAll(_text, edits);
            Changed?.Invoke(this, new BufferChangedEventArgs(false));
        }
        public void RestoreAsUndo(string text)
        {
            _text = text;
            Changed?.Invoke(this, new BufferChangedEventArgs(true));
        }
    }
}
