using Tern.App.Commands;
using Tern.App.Shell;
using Tern.Domain.Text;

namespace Tern.App.Tests;

public sealed class Oct29DocumentWorkflowTests
{
    [Fact]
    public async Task Manual_revert_clears_dirty_and_closes_without_prompt_while_output_remains_enabled()
    {
        using var fixture = new ShellFixture();
        var path = Path.Combine(fixture.Docs, "revert.md");
        await File.WriteAllTextAsync(path, "original \n");
        var tab = (await fixture.Shell.OpenFileAsync(path))!;
        tab.ApplyEdits([TextEdit.Insert(0, "change ")]);
        Assert.True(tab.IsDirty);
        tab.ApplyEdits([TextEdit.Delete(0, 7)]);
        Assert.False(tab.IsDirty);
        Assert.True(DocumentCommandPolicy.CanExecute(CommandIds.Print, fixture.Shell.GetCommandContext()));
        Assert.True(DocumentCommandPolicy.CanExecute(CommandIds.ExportPdf, fixture.Shell.GetCommandContext()));
        Assert.True(await fixture.Shell.CloseTabAsync(tab));
        Assert.Equal(0, fixture.Dialogs.SavePrompts);
    }

    [Theory]
    [InlineData(SaveChoice.Cancel, false)]
    [InlineData(SaveChoice.Discard, true)]
    public async Task Library_close_applies_save_decision_before_removing_tab_and_reference(SaveChoice choice, bool closed)
    {
        using var fixture = new ShellFixture();
        var path = Path.Combine(fixture.Docs, "library.md");
        await File.WriteAllTextAsync(path, "saved");
        var tab = (await fixture.Shell.OpenFileAsync(path))!;
        tab.ApplyEdits([TextEdit.Insert(0, "unsaved ")]);
        fixture.Dialogs.SaveChoice = choice;
        Assert.Equal(closed, await fixture.Shell.CloseLibraryReferenceAsync(path));
        Assert.Equal(!closed, fixture.Shell.Tabs.Contains(tab));
        Assert.Equal(!closed, fixture.Shell.Recent.Files.Any(item => item.Path == path));
        Assert.Equal("saved", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Cancelled_save_keeps_library_and_open_document_intact()
    {
        using var fixture = new ShellFixture();
        var path = Path.Combine(fixture.Docs, "library.md");
        await File.WriteAllTextAsync(path, "saved");
        var tab = (await fixture.Shell.OpenFileAsync(path))!;
        tab.ApplyEdits([TextEdit.Insert(0, "unsaved ")]);
        fixture.Dialogs.SaveChoice = SaveChoice.Save;
        // A held read handle prevents Windows atomic replacement.
        using var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Assert.False(await fixture.Shell.CloseLibraryReferenceAsync(path));
        Assert.Contains(tab, fixture.Shell.Tabs);
        Assert.Contains(fixture.Shell.Recent.Files, item => item.Path == path);
        Assert.True(tab.IsDirty);
    }

    [Fact]
    public async Task Library_close_reports_history_persistence_failure_after_explicit_discard_without_deleting_the_file()
    {
        using var fixture = new ShellFixture();
        var path = Path.Combine(fixture.Docs, "history-locked.md");
        await File.WriteAllTextAsync(path, "saved");
        var tab = (await fixture.Shell.OpenFileAsync(path))!;
        tab.ApplyEdits([TextEdit.Insert(0, "unsaved ")]);
        fixture.Dialogs.SaveChoice = SaveChoice.Discard;
        var historyPath = Path.Combine(fixture.Shell.Paths.StateDirectory, Tern.Workspaces.RecentItemsStore.FileName);
        using var locked = new FileStream(historyPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        Assert.True(await fixture.Shell.CloseLibraryReferenceAsync(path));
        Assert.DoesNotContain(tab, fixture.Shell.Tabs);
        Assert.DoesNotContain(fixture.Shell.Recent.Files, item => item.Path == path);
        Assert.NotNull(fixture.Shell.Recent.LastSaveError);
        Assert.Contains("could not be removed from saved history", fixture.Shell.StatusMessage);
        Assert.Contains(new Tern.Workspaces.RecentItemsStore(fixture.Shell.Paths.StateDirectory).Files, item => item.Path == path);
        Assert.Equal("saved", await File.ReadAllTextAsync(path));
    }
}
