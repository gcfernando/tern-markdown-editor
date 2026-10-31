using Tern.App.Commands;
using Tern.App.Documents;
using Tern.App.Shell;
using Tern.Domain.Text;
using Tern.Filesystem;
using Tern.Markdown;
using Tern.Recovery;

namespace Tern.App.Tests;

public sealed class Oct30ReviewTests
{
    [Fact]
    public async Task Saving_a_note_with_an_invalid_local_reference_preserves_its_text()
    {
        using var f = new ShellFixture();
        const string text = "[invalid](bad%00path.png)\n";
        var original = await f.WriteAsync("original/note.md", text);
        var tab = (await f.Shell.OpenFileAsync(original))!;
        f.Dialogs.SavePath = Path.Combine(f.Docs, "copy/note.md");
        Assert.True(await f.Shell.SaveTabAsAsync(tab));
        Assert.Equal(text, await File.ReadAllTextAsync(f.Dialogs.SavePath));
    }

    [Fact]
    public async Task Empty_named_draft_saves_and_uses_its_explicit_name_instead_of_a_heading()
    {
        using var f = new ShellFixture();
        var draft = f.Shell.NewDocument();
        await f.Shell.RenameDocumentAsync(draft, "chosen.md");
        f.Dialogs.SavePath = Path.Combine(f.Docs, "chosen.md");
        Assert.True(DocumentCommandPolicy.CanExecute(CommandIds.Save, f.Shell.GetCommandContext()));
        Assert.True(await f.Shell.SaveTabAsync(draft));
        Assert.Equal(string.Empty, await File.ReadAllTextAsync(f.Dialogs.SavePath));
        Assert.Equal("chosen.md", f.Dialogs.SuggestedName);

        var headed = f.Shell.NewDocument("# Heading title\n");
        await Task.Delay(250);
        await f.Shell.RenameDocumentAsync(headed, "explicit.txt");
        f.Dialogs.SavePath = Path.Combine(f.Docs, "explicit.txt");
        Assert.True(await f.Shell.SaveTabAsAsync(headed));
        Assert.Equal("explicit.txt", f.Dialogs.SuggestedName);
    }

    [Fact]
    public async Task Cancelling_save_of_an_empty_draft_keeps_it_unnamed_and_open()
    {
        using var f = new ShellFixture();
        var draft = f.Shell.NewDocument();
        Assert.False(await f.Shell.SaveTabAsync(draft));
        Assert.Contains(draft, f.Shell.Tabs);
        Assert.Null(draft.FilePath);
    }

    [Fact]
    public async Task Save_as_copies_local_images_reference_links_html_and_nested_note_dependencies()
    {
        using var f = new ShellFixture();
        await f.WriteAsync("original/images/picture.png", "current image bytes");
        await f.WriteAsync("original/nested/readme.md", "![nested](../images/picture.png)");
        const string text = "![picture][pic]\n\n[pic]: images/picture.png\n\n<a href=\"nested/readme.md\">More</a>\n<img src='images/picture.png'>\n";
        var original = await f.WriteAsync("original/note.md", text);
        var tab = (await f.Shell.OpenFileAsync(original))!;
        f.Dialogs.SavePath = Path.Combine(f.Docs, "copy", "note.md");
        Assert.True(await f.Shell.SaveTabAsAsync(tab));
        Assert.Equal(text, await File.ReadAllTextAsync(f.Dialogs.SavePath));
        Assert.Equal("current image bytes", await File.ReadAllTextAsync(Path.Combine(f.Docs, "copy/images/picture.png")));
        Assert.Equal("![nested](../images/picture.png)", await File.ReadAllTextAsync(Path.Combine(f.Docs, "copy/nested/readme.md")));
    }

    [Fact]
    public async Task Destination_attachment_conflicts_preserve_existing_files_and_roll_back_new_attachments()
    {
        using var f = new ShellFixture();
        await f.WriteAsync("original/a.png", "new a");
        await f.WriteAsync("original/z.png", "new z");
        var original = await f.WriteAsync("original/note.md", "![a](a.png) ![z](z.png)");
        var tab = (await f.Shell.OpenFileAsync(original))!;
        var existing = await f.WriteAsync("copy/z.png", "keep existing");
        f.Dialogs.SavePath = Path.Combine(f.Docs, "copy/note.md");
        Assert.False(await f.Shell.SaveTabAsAsync(tab));
        Assert.Equal(Path.GetFullPath(original), tab.FilePath);
        Assert.Equal("keep existing", await File.ReadAllTextAsync(existing));
        Assert.False(File.Exists(Path.Combine(f.Docs, "copy/a.png")));
        Assert.False(File.Exists(f.Dialogs.SavePath));
    }

    [Fact]
    public async Task Save_as_refuses_to_copy_a_reference_outside_the_document_folder()
    {
        using var f = new ShellFixture();
        await f.WriteAsync("outside.png", "outside bytes");
        var original = await f.WriteAsync("original/note.md", "![outside](../outside.png)");
        var tab = (await f.Shell.OpenFileAsync(original))!;
        f.Dialogs.SavePath = Path.Combine(f.Docs, "copy/note.md");
        Assert.False(await f.Shell.SaveTabAsAsync(tab));
        Assert.Equal(Path.GetFullPath(original), tab.FilePath);
        Assert.Contains(f.Dialogs.Messages, m => m.Contains("relative reference leaves", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Save_as_uses_the_current_downloaded_attachment_and_does_not_resurrect_removed_resources()
    {
        using var f = new ShellFixture();
        var relative = "assets/" + new string('a', 64) + ".png";
        var tab = f.Shell.OpenOnlineDocument("https://example.com/note.md", "note.md", "![image](" + relative + ")");
        tab.ResourceFiles = new Dictionary<string, byte[]> { [relative] = [1, 2, 3] };
        f.Dialogs.SavePath = Path.Combine(f.Docs, "first/note.md");
        Assert.True(await f.Shell.SaveTabAsAsync(tab));
        var asset = Path.Combine(f.Docs, "first", relative);
        await File.WriteAllBytesAsync(asset, [4, 5, 6]);
        f.Dialogs.SavePath = Path.Combine(f.Docs, "second/note.md");
        Assert.True(await f.Shell.SaveTabAsAsync(tab));
        Assert.Equal(new byte[] { 4, 5, 6 }, await File.ReadAllBytesAsync(Path.Combine(f.Docs, "second", relative)));
        Assert.Equal(new byte[] { 4, 5, 6 }, await File.ReadAllBytesAsync(asset));
    }

    [Fact]
    public async Task Recovery_keeps_a_resource_base_and_save_as_preserves_the_images()
    {
        using var f = new ShellFixture();
        await f.WriteAsync("original/picture.png", "picture");
        var original = await f.WriteAsync("original/note.md", "disk version");
        var recovered = await f.Shell.RestoreRecoveryAsync(new RecoveryRecord(Guid.NewGuid(), original, "note.md", 1,
            DateTime.UtcNow, null, TextFileFormat.NewFileDefault, "![image](picture.png)"));
        Assert.Null(recovered.FilePath);
        Assert.Equal(original, recovered.ResourceDocumentPath);
        var checkpoint = Assert.Single(f.Shell.RecoveryStore.Scan().Entries, entry => entry.Record.SessionId == recovered.Session.Id).Record;
        Assert.Equal(original, checkpoint.OriginalPath);
        Assert.Equal("note.md", checkpoint.DisplayName);
        f.Dialogs.SavePath = Path.Combine(f.Docs, "recovered/note.md");
        Assert.True(await f.Shell.SaveTabAsAsync(recovered));
        Assert.Equal("picture", await File.ReadAllTextAsync(Path.Combine(f.Docs, "recovered/picture.png")));
        Assert.Equal("disk version", await File.ReadAllTextAsync(original));
    }

    [Fact]
    public async Task Read_only_copy_requires_consent_and_writes_UTF8_to_a_separate_file()
    {
        using var f = new ShellFixture();
        var source = Path.Combine(f.Docs, "invalid.md");
        byte[] original = [0xff, 0xfe, 0x00];
        await File.WriteAllBytesAsync(source, original);
        var tab = (await f.Shell.OpenFileAsync(source))!;
        Assert.True(tab.IsReadOnly);
        Assert.True(DocumentCommandPolicy.CanExecute(CommandIds.SaveAs, f.Shell.GetCommandContext()));
        f.Dialogs.SavePath = Path.Combine(f.Docs, "copy.md");
        Assert.False(await f.Shell.SaveTabAsAsync(tab));
        Assert.False(File.Exists(f.Dialogs.SavePath));
        f.Dialogs.ReadOnlyCopy = true;
        var preview = tab.CurrentText;
        Assert.True(await f.Shell.SaveTabAsAsync(tab));
        Assert.False(tab.IsReadOnly);
        Assert.Equal(preview, await File.ReadAllTextAsync(f.Dialogs.SavePath));
        Assert.Equal(TextEncodingKind.Utf8, tab.Session.Format.Encoding);
        Assert.Equal(original, await File.ReadAllBytesAsync(source));
    }

    [Fact]
    public async Task Read_only_save_as_cannot_overwrite_its_source_even_when_called_directly()
    {
        using var f = new ShellFixture();
        var source = Path.Combine(f.Docs, "invalid.md");
        byte[] bytes = [0xff, 0xfe, 0x00];
        await File.WriteAllBytesAsync(source, bytes);
        using var tab = await DocumentTab.OpenAsync(source, new DocumentFileService(), MarkdownProfile.Gfm);
        Assert.IsType<SaveFailed>(await tab.SaveAsAsync(source, overwriteExisting: true));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(source));
    }

    [Fact]
    public async Task Restoring_the_disk_baseline_clears_a_previous_conflict_without_losing_edits()
    {
        using var f = new ShellFixture();
        var source = await f.WriteAsync("note.md", "base");
        var tab = (await f.Shell.OpenFileAsync(source))!;
        tab.ApplyEdits([TextEdit.Insert(0, "mine ")]);
        await File.WriteAllTextAsync(source, "competing edit");
        Assert.Equal(DiskCheckResult.Conflict, await tab.CheckDiskAsync(false));
        await File.WriteAllTextAsync(source, "base");
        Assert.Equal(DiskCheckResult.Unchanged, await tab.CheckDiskAsync(false));
        Assert.Null(tab.Conflict);
        Assert.Equal("mine base", tab.CurrentText);
        Assert.True(await f.Shell.SaveTabAsync(tab));
    }

    [Fact]
    public async Task Duplicate_tabs_restore_in_order_with_their_own_positions_and_active_instance()
    {
        using var f = new ShellFixture();
        var path = await f.WriteAsync("note.md", "some sufficiently long text");
        var first = (await f.Shell.OpenFileAsync(path))!;
        var second = (await f.Shell.OpenFileAsync(path, duplicateBehavior: DuplicateOpenBehavior.NewTab))!;
        f.Shell.RememberViewState(first, 2, 5);
        f.Shell.RememberViewState(second, 12, 15);
        Assert.True(f.Shell.SaveSession());
        using var restored = new ShellViewModel(f.Shell.Paths, new ScriptedDialogs(), new RecordingLauncher());
        await restored.RestoreSessionAsync();
        Assert.Equal(2, restored.Tabs.Count);
        Assert.Same(restored.Tabs[1], restored.ActiveTab);
        Assert.Equal(2, restored.GetViewState(restored.Tabs[0])!.CaretOffset);
        Assert.Equal(12, restored.GetViewState(restored.Tabs[1])!.CaretOffset);
    }
}
