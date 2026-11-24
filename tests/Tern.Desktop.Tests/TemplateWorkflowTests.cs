using Avalonia.Controls;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Tern.App.Commands;
using Tern.App.Help;
using Tern.Desktop.Views;

namespace Tern.Desktop.Tests;

public sealed class TemplateWorkflowTests
{
    [AvaloniaFact]
    public void Template_and_daily_note_commands_are_discoverable_with_a_document_open()
    {
        using var f = new DesktopFixture();
        f.Shell.NewDocument("Keep the current note");
        Assert.True(f.Shell.Registry.TryGet("file.newFromTemplate", out _));
        Assert.True(f.Shell.Registry.TryGet("file.openTodaysNote", out _));
        var commands = f.Window.GetLogicalDescendants().OfType<MenuItem>().Select(item => item.Tag).OfType<string>();
        Assert.Contains("file.newFromTemplate", commands);
        Assert.Contains("file.openTodaysNote", commands);
        var add = Assert.IsType<MenuFlyout>(f.Window.FindControl<Button>("WorkspaceAddButton")!.Flyout);
        Assert.Contains(add.Items.OfType<MenuItem>(), item => Equals(item.Tag, "file.newFromTemplate"));
    }

    [AvaloniaFact]
    public async Task The_template_command_opens_the_real_picker_and_inserts_a_dated_unsaved_note()
    {
        using var f = new DesktopFixture();
        var existing = f.Shell.NewDocument("Keep my open note");
        Directory.CreateDirectory(f.Shell.UserTemplatesDirectory);
        await File.WriteAllTextAsync(Path.Combine(f.Shell.UserTemplatesDirectory, "Personal.md"), "# {{title}}\n{{date}} {{time}}");
        var todayBefore = DateTime.Now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var pending = f.Window.RunCommandAsync(CommandIds.NewFromTemplate);
        StarterTemplatesWindow? picker = null;
        for (var index = 0; index < 200 && picker is null; index++)
        {
            Dispatcher.UIThread.RunJobs();
            picker = f.Window.OwnedWindows.OfType<StarterTemplatesWindow>().FirstOrDefault();
            if (picker is null) await Task.Delay(10);
        }
        Assert.NotNull(picker);
        try
        {
            var templates = picker.Catalog.Items.OfType<StarterTemplate>().ToArray();
            Assert.Contains(templates, item => item.Name == "Personal");
            picker.Catalog.SelectedItem = templates.Single(item => item.Name == "Meeting Notes");
            picker.UseTemplate.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await pending;
            var note = f.Shell.ActiveTab!;
            var todayAfter = DateTime.Now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            Assert.True(note.CurrentText.Contains("Date: " + todayBefore) || note.CurrentText.Contains("Date: " + todayAfter));
            Assert.StartsWith("Meeting Notes ", note.DisplayName);
            Assert.Null(note.FilePath);
            Assert.Equal("Keep my open note", existing.CurrentText);
        }
        finally { if (picker.IsVisible) picker.Close(); }
    }
}
