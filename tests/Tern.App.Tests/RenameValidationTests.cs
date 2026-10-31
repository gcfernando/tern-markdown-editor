using Tern.Domain.Text;
using Tern.Filesystem;
using Tern.Workspaces;
using Tern.App.Shell;
using Tern.Recovery;

namespace Tern.App.Tests;

public sealed class RenameValidationTests
{
    [Theory]
    [InlineData("CON.md")]
    [InlineData("COM¹.md")]
    [InlineData("NUL.txt")]
    [InlineData("../other.md")]
    [InlineData("bad?.md")]
    [InlineData("name.")]
    [InlineData("name ")]
    [InlineData(".")]
    [InlineData("bad\0.md")]
    public async Task Invalid_name_keeps_disk_identity_and_dirty_text(string name)
    {
        using var fixture = new ShellFixture();
        var path = await fixture.WriteAsync("original.md", "saved\n");
        var tab = (await fixture.Shell.OpenFileAsync(path))!;
        tab.ApplyEdits([TextEdit.Insert(0, "unsaved ")]);
        var revision = tab.Session.Revision;
        Assert.False(await fixture.Shell.RenameDocumentAsync(tab, name));
        Assert.Equal(path, tab.FilePath);
        Assert.Equal("saved\n", await File.ReadAllTextAsync(path));
        Assert.Equal("unsaved saved\n", tab.CurrentText);
        Assert.Equal(revision, tab.Session.Revision);
        Assert.True(tab.IsDirty);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Existing_file_or_directory_is_never_replaced(bool directory)
    {
        using var fixture = new ShellFixture();
        var path = await fixture.WriteAsync("original.md", "original");
        var destination = Path.Combine(fixture.Docs, "existing.md");
        if (directory) Directory.CreateDirectory(destination);
        else await File.WriteAllTextAsync(destination, "keep collision");
        var tab = (await fixture.Shell.OpenFileAsync(path))!;
        Assert.False(await fixture.Shell.RenameDocumentAsync(tab, "existing.md"));
        Assert.Equal(path, tab.FilePath);
        Assert.Equal("original", await File.ReadAllTextAsync(path));
        if (directory) Assert.True(Directory.Exists(destination));
        else Assert.Equal("keep collision", await File.ReadAllTextAsync(destination));
    }

    [Fact]
    public async Task Case_only_rename_keeps_dirty_content_and_updates_actual_directory_spelling()
    {
        using var fixture = new ShellFixture();
        var path = await fixture.WriteAsync("case.md", "saved\r\n");
        var tab = (await fixture.Shell.OpenFileAsync(path))!;
        tab.ApplyEdits([TextEdit.Insert(0, "unsaved ")]);
        Assert.True(await fixture.Shell.RenameDocumentAsync(tab, "CASE.md"));
        Assert.Contains(Directory.EnumerateFiles(fixture.Docs).Select(Path.GetFileName), name => name == "CASE.md");
        Assert.Equal("unsaved saved\r\n", tab.CurrentText);
        Assert.True(tab.IsDirty);
        Assert.Equal("saved\r\n", await File.ReadAllTextAsync(tab.FilePath!));
    }

    [Fact]
    public async Task Exclusive_file_handle_refuses_physical_rename_without_mutating_open_work()
    {
        using var fixture = new ShellFixture();
        var path = await fixture.WriteAsync("locked.md", "saved");
        var tab = (await fixture.Shell.OpenFileAsync(path))!;
        tab.ApplyEdits([TextEdit.Insert(0, "unsaved ")]);
        await using var handle = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Assert.False(await fixture.Shell.RenameDocumentAsync(tab, "renamed.md"));
        Assert.Equal(path, tab.FilePath);
        Assert.True(File.Exists(path));
        Assert.False(File.Exists(Path.Combine(fixture.Docs, "renamed.md")));
        Assert.Equal("unsaved saved", tab.CurrentText);
        Assert.True(tab.IsDirty);
    }

    [Fact]
    public async Task Nested_root_link_review_preserves_open_dirty_backlink_until_explicit_apply_and_save()
    {
        using var fixture = new ShellFixture();
        var source = await fixture.WriteAsync("source.md", "[target](nested/target.md)\n");
        var target = await fixture.WriteAsync("nested/target.md", "# Target\n");
        Assert.True(await fixture.Shell.OpenWorkspaceAsync(fixture.Docs));
        Assert.True(await fixture.Shell.OpenWorkspaceAsync(Path.GetDirectoryName(target)!));
        var sourceTab = (await fixture.Shell.OpenFileAsync(source))!;
        sourceTab.ApplyEdits([TextEdit.Insert(0, "unsaved ")]);
        var targetTab = (await fixture.Shell.OpenFileAsync(target))!;
        Assert.True(await fixture.Shell.RenameDocumentAsync(targetTab, "renamed.md"));
        var plan = Assert.IsType<RepairPlan>(fixture.Shell.LastRenameRepairPlan);
        Assert.Contains(plan.Files, repair => repair.Path == source && repair.ResultText == "unsaved [target](nested/renamed.md)\n");
        Assert.Equal("unsaved [target](nested/target.md)\n", sourceTab.CurrentText);
        Assert.Equal("[target](nested/target.md)\n", await File.ReadAllTextAsync(source));
        Assert.True((await fixture.Shell.ApplyRenameRepairsAsync(plan)).Succeeded);
        Assert.Equal("unsaved [target](nested/renamed.md)\n", sourceTab.CurrentText);
        Assert.True(sourceTab.IsDirty);
        Assert.Equal("[target](nested/target.md)\n", await File.ReadAllTextAsync(source));
    }

    [Fact]
    public async Task Rename_eligibility_follows_origin_readonly_and_saved_local_identity_with_notifications()
    {
        using var fixture = new ShellFixture();
        var tab = fixture.Shell.NewDocument();
        Assert.True(tab.CanRename);
        var changes = new List<string?>();
        tab.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        tab.IsExternalDraft = true;
        Assert.False(tab.CanRename);
        Assert.Contains(nameof(tab.CanRename), changes);
        Assert.False(await fixture.Shell.RenameDocumentAsync(tab, "online-cannot-rename.md"));
        tab.ApplyEdits([TextEdit.Insert(0, "online content\n")]);
        changes.Clear();
        var path = Path.Combine(fixture.Docs, "downloaded.md");
        Assert.IsType<SaveSucceeded>(await tab.SaveAsAsync(path, false));
        Assert.True(tab.CanRename);
        Assert.Contains(nameof(tab.CanRename), changes);
        tab.Session.SetReadOnly(true);
        Assert.True(tab.CanRename);
        Assert.True(await fixture.Shell.RenameDocumentAsync(tab, "readonly-renamed.md"));
        Assert.True(tab.IsReadOnly);
        Assert.Equal("online content\n", await File.ReadAllTextAsync(tab.FilePath!));
    }

    [Fact]
    public async Task Decode_readonly_file_can_be_renamed_without_reencoding_its_invalid_bytes()
    {
        using var fixture = new ShellFixture();
        var path = Path.Combine(fixture.Docs, "invalid.md");
        byte[] bytes = [0x61, 0xFF, 0x80, 0x62];
        await File.WriteAllBytesAsync(path, bytes);
        var tab = (await fixture.Shell.OpenFileAsync(path))!;
        Assert.True(tab.IsReadOnly);
        Assert.True(tab.CanRename);
        Assert.True(await fixture.Shell.RenameDocumentAsync(tab, "renamed-invalid.md"));
        Assert.True(tab.IsReadOnly);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(tab.FilePath!));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task Renaming_one_exact_source_updates_all_duplicate_tab_identities_without_merging_buffers()
    {
        using var fixture = new ShellFixture();
        var path = await fixture.WriteAsync("duplicate.md", "saved\n");
        var first = (await fixture.Shell.OpenFileAsync(path))!;
        var second = (await fixture.Shell.OpenFileAsync(path, duplicateBehavior: DuplicateOpenBehavior.NewTab))!;
        Assert.NotSame(first, second);
        first.ApplyEdits([TextEdit.Insert(0, "first ")]);
        second.ApplyEdits([TextEdit.Insert(0, "second ")]);
        var firstRevision = first.Session.Revision;
        var secondRevision = second.Session.Revision;
        Assert.True(await fixture.Shell.RenameDocumentAsync(first, "renamed.md"));
        Assert.Equal(first.FilePath, second.FilePath);
        Assert.Equal("first saved\n", first.CurrentText);
        Assert.Equal("second saved\n", second.CurrentText);
        Assert.Equal(firstRevision, first.Session.Revision);
        Assert.Equal(secondRevision, second.Session.Revision);
        Assert.True(first.IsDirty && second.IsDirty);
        Assert.Equal("saved\n", await File.ReadAllTextAsync(first.FilePath!));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task Renaming_recovered_local_draft_updates_visible_recovery_name_without_creating_disk_file()
    {
        using var fixture = new ShellFixture();
        var record = new RecoveryRecord(Guid.NewGuid(), null, "previous.md", 3, DateTime.UtcNow, null,
            TextFileFormat.NewFileDefault, "recovered work\n");
        var tab = await fixture.Shell.RestoreRecoveryAsync(record);
        Assert.True(tab.IsRecovered);
        Assert.True(tab.CanRename);
        Assert.True(await fixture.Shell.RenameDocumentAsync(tab, "new-name.md"));
        Assert.Equal("new-name.md", tab.RecoveryName);
        Assert.Contains("new-name.md", tab.DisplayName, StringComparison.Ordinal);
        Assert.DoesNotContain("previous.md", tab.DisplayName, StringComparison.Ordinal);
        Assert.Null(tab.FilePath);
        Assert.Equal("recovered work\n", tab.CurrentText);
        Assert.Empty(Directory.EnumerateFiles(fixture.Docs));
    }
}
