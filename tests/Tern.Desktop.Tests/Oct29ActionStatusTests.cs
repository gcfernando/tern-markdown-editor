using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Headless.XUnit;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Tern.App.Commands;

namespace Tern.Desktop.Tests;

public sealed class Oct29ActionStatusTests
{
    [AvaloniaFact]
    public async Task Formatting_and_mode_changes_publish_feedback_but_reselecting_the_same_mode_keeps_a_diagnostic()
    {
        using var fixture = new DesktopFixture();
        fixture.Shell.NewDocument("word");
        await fixture.Window.RunCommandAsync(CommandIds.ViewReading);
        Assert.Equal("Reading view", fixture.Shell.StatusMessage);
        fixture.Shell.StatusMessage = "Could not save: permission denied.";
        await fixture.Window.RunCommandAsync(CommandIds.ViewReading);
        Assert.Equal("Could not save: permission denied.", fixture.Shell.StatusMessage);
        var source = fixture.Window.FindControl<Avalonia.Controls.Primitives.ToggleButton>("SourceButton")!;
        source.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal("Source view", fixture.Shell.StatusMessage);
        fixture.Window.EditorControl.Select(0, 4);
        await fixture.Window.RunCommandAsync(CommandIds.Bold);
        Assert.Contains("Bold", fixture.Shell.StatusMessage);
        Assert.Equal("**word**", fixture.Shell.ActiveTab!.CurrentText);
    }

    [AvaloniaFact]
    public async Task Explicit_attachment_command_explains_that_clipboard_text_is_not_image_data()
    {
        using var fixture = new DesktopFixture();
        await fixture.Shell.OpenFileAsync(await fixture.WriteAsync("picture-note.md", "Keep"));
        await fixture.Window.Clipboard!.SetTextAsync("ordinary text");
        await fixture.Window.RunCommandAsync(CommandIds.PasteImage);
        Assert.Contains("No image was pasted", fixture.Shell.StatusMessage);
        Assert.Contains("Paste text with Ctrl+V", fixture.Shell.StatusMessage);
        Assert.Equal("Keep", fixture.Shell.ActiveTab!.CurrentText);
    }

    [AvaloniaFact]
    public async Task Explicit_attachment_command_reads_a_copied_file_and_reports_when_the_source_disappears()
    {
        using var fixture = new DesktopFixture();
        await fixture.Shell.OpenFileAsync(await fixture.WriteAsync("picture-note.md", "Keep"));
        var imagePath = Path.Combine(fixture.Docs, "copied-picture.png");
        await File.WriteAllBytesAsync(imagePath, Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Wl2WvYAAAAASUVORK5CYII="));
        var file = await fixture.Window.StorageProvider.TryGetFileFromPathAsync(new Uri(imagePath));
        Assert.NotNull(file);
        await fixture.Window.Clipboard!.SetFilesAsync([file]);
        File.Delete(imagePath);
        await fixture.Window.RunCommandAsync(CommandIds.PasteImage);
        Assert.Contains("Could not insert image", fixture.Shell.StatusMessage);
        Assert.Contains("copied-picture.png", fixture.Shell.StatusMessage);
        Assert.DoesNotContain("No image was pasted", fixture.Shell.StatusMessage);
        Assert.Equal("Keep", fixture.Shell.ActiveTab!.CurrentText);
    }
}
