using Tern.App.Help;
using Tern.Domain.Text;

namespace Tern.App.Tests;

public sealed class TemplateAndDailyNoteTests
{
    private static readonly DateTimeOffset Time = new(2026, 11, 19, 14, 35, 0, TimeSpan.FromHours(5.5));

    [Fact]
    public void Template_insertion_expands_dates_time_and_title_and_keeps_the_existing_document()
    {
        using var f = new ShellFixture();
        var existing = f.Shell.NewDocument("Keep my existing text");
        var template = StarterTemplates.All.Single(item => item.Name == "Meeting Notes");
        var meeting = f.Shell.NewDocumentFromTemplate(template, Time);
        Assert.Equal("Meeting Notes 2026-11-19.md", meeting.DisplayName);
        Assert.Contains("Date: 2026-11-19", meeting.CurrentText);
        Assert.DoesNotContain("YYYY-MM-DD", meeting.CurrentText);
        Assert.Null(meeting.FilePath);
        Assert.Contains("\r\n", meeting.CurrentText);
        Assert.Equal("Keep my existing text", existing.CurrentText);
        var custom = f.Shell.NewDocumentFromTemplate(new StarterTemplate("Custom", "", "{{title}}\n{{date}} {{time}}\n{{untouched}}"), Time);
        Assert.Equal("Custom 2026-11-19\r\n2026-11-19 14:35\r\n{{untouched}}", custom.CurrentText);
    }

    [Fact]
    public async Task Global_and_workspace_user_templates_are_loaded_without_modifying_sources()
    {
        using var f = new ShellFixture();
        Directory.CreateDirectory(f.Shell.UserTemplatesDirectory);
        var user = Path.Combine(f.Shell.UserTemplatesDirectory, "Personal.md");
        const string personal = "# {{title}}\nDate: {{date}} Time: {{time}}";
        await File.WriteAllTextAsync(user, personal);
        var workspace = Path.Combine(f.Docs, ".tern", "templates");
        Directory.CreateDirectory(workspace);
        var shared = Path.Combine(workspace, "Shared.md");
        await File.WriteAllTextAsync(shared, "# Shared YYYY-MM-DD");
        await File.WriteAllTextAsync(Path.Combine(workspace, "not-a-template.txt"), "Keep");
        Assert.True(await f.Shell.OpenWorkspaceAsync(f.Docs));
        var catalog = await f.Shell.LoadStarterTemplatesAsync();
        Assert.Empty(catalog.Warnings);
        var item = Assert.Single(catalog.Items, template => template.SourcePath == user);
        Assert.Contains(catalog.Items, template => template.SourcePath == shared);
        Assert.DoesNotContain(catalog.Items, template => template.Name == "not-a-template");
        var note = f.Shell.NewDocumentFromTemplate(item, Time);
        Assert.Contains("# Personal 2026-11-19", note.CurrentText);
        Assert.Contains("2026-11-19 Time: 14:35", note.CurrentText);
        Assert.Equal(personal, await File.ReadAllTextAsync(user));
    }

    [Fact]
    public async Task Oversized_and_linked_templates_are_skipped_with_explicit_warnings()
    {
        using var f = new ShellFixture();
        var directory = f.Shell.UserTemplatesDirectory;
        Directory.CreateDirectory(directory);
        var oversized = Path.Combine(directory, "oversized.md");
        await File.WriteAllBytesAsync(oversized, new byte[StarterTemplates.MaximumTemplateBytes + 1]);
        var source = await f.WriteAsync("private-source.md", "# Do not follow");
        var linked = Path.Combine(directory, "linked.md");
        File.CreateSymbolicLink(linked, source);
        var catalog = await f.Shell.LoadStarterTemplatesAsync();
        Assert.DoesNotContain(catalog.Items, item => item.SourcePath == oversized || item.SourcePath == linked);
        Assert.Contains(catalog.Warnings, warning => warning.Contains(oversized));
        Assert.Contains(catalog.Warnings, warning => warning.Contains(linked));
        Assert.Equal("# Do not follow", await File.ReadAllTextAsync(source));
        Assert.Contains("Skipped", f.Shell.StatusMessage);
    }

    [Fact]
    public async Task Todays_note_is_created_once_and_reopening_preserves_unsaved_edits()
    {
        using var f = new ShellFixture();
        Assert.True(await f.Shell.OpenWorkspaceAsync(f.Docs));
        var first = await f.Shell.OpenTodaysNoteAsync(Time);
        Assert.NotNull(first);
        var path = Path.Combine(f.Docs, "Journal", "2026-11-19.md");
        Assert.Equal(path, first.FilePath);
        Assert.False(first.IsDirty);
        var original = await File.ReadAllTextAsync(path);
        Assert.Contains("Date: 2026-11-19", original);
        first.ApplyEdits([TextEdit.Insert(first.CurrentText.Length, "\r\nKeep unsaved text")]);
        Assert.Same(first, await f.Shell.OpenTodaysNoteAsync(Time));
        Assert.Single(f.Shell.Tabs);
        Assert.True(first.IsDirty);
        Assert.Contains("Keep unsaved text", first.CurrentText);
        Assert.Equal(original, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Todays_existing_note_is_opened_verbatim_instead_of_being_replaced_by_a_template()
    {
        using var f = new ShellFixture();
        var path = await f.WriteAsync(Path.Combine("Journal", "2026-11-19.md"), "# My existing journal\nKeep exact bytes.");
        var bytes = await File.ReadAllBytesAsync(path);
        Assert.True(await f.Shell.OpenWorkspaceAsync(f.Docs));
        var tab = await f.Shell.OpenTodaysNoteAsync(Time);
        Assert.NotNull(tab);
        Assert.Equal("# My existing journal\nKeep exact bytes.", tab.CurrentText);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        Assert.False(tab.IsDirty);
    }

    [Fact]
    public async Task Existing_daily_notes_are_never_overwritten_and_linked_journal_folders_are_not_followed_outside_the_workspace()
    {
        using var f = new ShellFixture();
        var workspace = Path.Combine(f.Docs, "workspace");
        var outside = Path.Combine(f.Docs, "outside");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(outside);
        var existing = Path.Combine(outside, "2026-11-19.md");
        await File.WriteAllTextAsync(existing, "# Private existing note");
        Directory.CreateSymbolicLink(Path.Combine(workspace, "Journal"), outside);
        Assert.True(await f.Shell.OpenWorkspaceAsync(workspace));
        Assert.Null(await f.Shell.OpenTodaysNoteAsync(Time));
        Assert.Empty(f.Shell.Tabs);
        Assert.Equal("# Private existing note", await File.ReadAllTextAsync(existing));
    }
}
