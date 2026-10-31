using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tern.App.Commands;
using Tern.Desktop.Views;
using Tern.Workspaces;

namespace Tern.Desktop.Tests;

public sealed class WorkspaceSearchFeedbackTests
{
    [AvaloniaFact]
    public async Task Damaged_search_index_reports_recovery_and_reset_rebuilds_results()
    {
        using var fixture = new DesktopFixture();
        await fixture.Window.CreateWorkspaceAtAsync(fixture.Docs, "Company");
        var root = fixture.Shell.Workspace!.Root;
        await File.WriteAllTextAsync(Path.Combine(root, "people.md"), "# People\nEmployees and teams.");
        await fixture.Shell.Workspace.NotifySavedAsync(Path.Combine(root, "people.md"), "# People\nEmployees and teams.");
        Assert.Single(fixture.Shell.Workspace.Search("Employees").Hits);
        var database = Path.Combine(fixture.Shell.Paths.IndexDirectory, Tern.App.Shell.WorkspaceSession.IndexFileName(root));
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE fts";
            command.ExecuteNonQuery();
        }
        await fixture.Window.RunCommandAsync(CommandIds.FindInWorkspace);
        fixture.Window.Picker!.Input.Text = "Employees";
        Pump(fixture.Window);
        var status = Assert.Single(fixture.Window.Picker.GetVisualDescendants().OfType<TextBlock>(), block => AutomationProperties.GetName(block) == "Search result status");
        Assert.Contains("search index could not be read", status.Text);
        Assert.Contains("Reset Tern", status.Text);
        Assert.DoesNotContain("No matching documents", status.Text);
        fixture.Window.HideOverlay();
        Assert.True(await fixture.Shell.CloseAllWorkspacesAsync());
        Assert.True(fixture.Shell.ResetApplication(new Tern.App.Shell.ResetOptions(RecentHistory: false, Caches: true)));
        Assert.True(await fixture.Shell.OpenWorkspaceAsync(root));
        Assert.Null(fixture.Shell.Workspace!.Search("Employees").Error);
        Assert.Single(fixture.Shell.Workspace.Search("Employees").Hits);
        Assert.Equal("# People\nEmployees and teams.", await File.ReadAllTextAsync(Path.Combine(root, "people.md")));
    }

    [AvaloniaFact]
    public async Task Typed_workspace_or_folder_name_reports_no_document_matches_instead_of_claiming_search_has_not_started()
    {
        using var fixture = new DesktopFixture();
        var root = Path.Combine(fixture.Docs, "Diagrams");
        Directory.CreateDirectory(root);
        WorkspaceDefinition.Create(root, "Diagrams");
        await fixture.WriteAsync("Diagrams/note.md", "# People\nEmployees and teams.");
        Assert.True(await fixture.Shell.OpenWorkspaceAsync(root));
        await fixture.Window.RunCommandAsync(CommandIds.FindInWorkspace);
        Pump(fixture.Window);
        var picker = Assert.Single(fixture.Window.GetVisualDescendants().OfType<QuickPicker>());
        var status = Assert.Single(picker.GetVisualDescendants().OfType<TextBlock>(), block => AutomationProperties.GetName(block) == "Search result status");
        Assert.True(status.IsEffectivelyVisible);
        Assert.Contains("Type a word or phrase", status.Text, StringComparison.Ordinal);
        picker.Input.Text = "Diagrams";
        Pump(fixture.Window);
        Assert.Equal(0, picker.Results.ItemCount);
        Assert.True(status.IsEffectivelyVisible);
        Assert.Contains("No matching documents", status.Text, StringComparison.Ordinal);
        Assert.Contains("Library tree", status.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Type to search", status.Text, StringComparison.Ordinal);
        picker.Input.Text = "Employees";
        Pump(fixture.Window);
        Assert.Equal(1, picker.Results.ItemCount);
        Assert.False(status.IsVisible);
    }

    [AvaloniaFact]
    public void Reusing_picker_resets_custom_empty_feedback_for_the_next_feature()
    {
        var picker = new QuickPicker();
        picker.Open("Search workspace", "missing", _ => [], _ => { }, () => { }, emptyTextProvider: _ => "No workspace documents");
        var status = Assert.Single(picker.GetVisualDescendants().OfType<TextBlock>(), block => AutomationProperties.GetName(block) == "Search result status");
        Assert.Equal("No workspace documents", status.Text);
        picker.Open("Go to file", "missing", _ => [], _ => { }, () => { });
        Assert.Equal("No matches", status.Text);
    }

    private static void Pump(Window window)
    {
        for (var i = 0; i < 4; i++) { window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
    }
}
