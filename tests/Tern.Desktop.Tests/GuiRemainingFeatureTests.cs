using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.LogicalTree;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tern.App.Commands;
using Tern.Desktop.Services;
using Tern.Desktop.Views;

namespace Tern.Desktop.Tests;

public sealed class GuiRemainingFeatureTests
{
    internal static byte[] ImageBytes()
    {
        using var bitmap = new SkiaSharp.SKBitmap(64, 40); bitmap.Erase(SkiaSharp.SKColors.SteelBlue);
        using var image = SkiaSharp.SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100); return data.ToArray();
    }

    [AvaloniaFact]
    public async Task Image_form_prevents_duplicate_submissions_and_retains_values_after_collision()
    {
        using var f = new DesktopFixture(); var pending = new TaskCompletionSource<string?>(); var calls = 0;
        var dialog = new InsertImageWindow(ImageBytes(), "diagram.png", Path.Combine(f.Docs, "note.md"), null,
            _ => { calls++; return pending.Task; });
        dialog.Show(f.Window); Pump(dialog);
        dialog.AltInput.Text = "A useful image description";
        dialog.NameInput.Text = "chosen.png";
        Click(dialog.InsertButton); Click(dialog.InsertButton);
        Assert.Equal(1, calls); Assert.False(dialog.InsertButton.IsEnabled);
        pending.SetResult("That filename already exists. Choose another name.");
        await Until(() => dialog.InsertButton.IsEnabled);
        Assert.Equal("chosen.png", dialog.NameInput.Text);
        Assert.Equal("A useful image description", dialog.AltInput.Text);
        Assert.Contains(dialog.GetVisualDescendants().OfType<TextBlock>(), t => t.Text?.Contains("already exists", StringComparison.Ordinal) == true);
        Assert.Empty(Directory.EnumerateFiles(f.Docs, "*", SearchOption.AllDirectories));
        dialog.Close();
    }

    [AvaloniaFact]
    public async Task Locate_image_replaces_only_the_destination_and_is_undoable()
    {
        using var f = new DesktopFixture();
        const string source = "# Note\n\n![Useful description](missing.png)\n";
        var tab = (await f.Shell.OpenFileAsync(await f.WriteAsync("note.md", source)))!;
        var image = Path.Combine(f.Docs, "found image.png"); await File.WriteAllBytesAsync(image, ImageBytes());
        var selected = Assert.IsAssignableFrom<IStorageFile>(await f.Window.StorageProvider.TryGetFileFromPathAsync(new Uri(image)));
        f.Window.ImageFilePicker = _ => Task.FromResult<IReadOnlyList<IStorageFile>>([selected]);
        await f.Window.RebuildPreviewAsync(); Pump(f.Window);
        Click(f.Window.PreviewControl.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Locate image…")));
        await Until(() => tab.CurrentText.Contains("found%20image.png", StringComparison.Ordinal));
        Assert.Equal(source.Replace("missing.png", "found%20image.png", StringComparison.Ordinal), tab.CurrentText);
        Assert.Equal(source, await File.ReadAllTextAsync(tab.FilePath!));
        await f.Window.RunCommandAsync(CommandIds.Undo); Assert.Equal(source, tab.CurrentText);
    }

    [AvaloniaFact]
    public async Task Image_location_result_cannot_edit_a_note_changed_while_the_picker_is_open()
    {
        using var f = new DesktopFixture();
        var tab = (await f.Shell.OpenFileAsync(await f.WriteAsync("note.md", "![Alt](missing.png)\n")))!;
        var pending = new TaskCompletionSource<IReadOnlyList<IStorageFile>>();
        f.Window.ImageFilePicker = _ => pending.Task;
        await f.Window.RebuildPreviewAsync(); Pump(f.Window);
        Click(f.Window.PreviewControl.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Locate image…")));
        tab.ApplyEdits([Tern.Domain.Text.TextEdit.Insert(0, "New text\n")]);
        pending.SetResult([]); Pump(f.Window);
        Assert.Equal("New text\n![Alt](missing.png)\n", tab.CurrentText);
    }

    [AvaloniaFact]
    public async Task Retrying_recovery_protects_the_current_revision_without_saving_the_file()
    {
        using var f = new DesktopFixture(); var tab = f.Shell.NewDocument("An unsaved draft");
        tab.RecoveryError = "Synthetic previous failure";
        Assert.True(await f.Shell.RetryRecoveryAsync(tab));
        Assert.Equal(tab.Session.Revision, tab.CheckpointRevision); Assert.Null(tab.RecoveryError);
        Assert.Null(tab.FilePath); Assert.True(tab.IsDirty);
    }

    [AvaloniaFact]
    public async Task Walkthrough_pauses_for_another_tab_and_cannot_resume_a_closed_practice_note()
    {
        using var f = new DesktopFixture(); var original = f.Shell.NewDocument("Keep my original note");
        await f.Window.RunCommandAsync(CommandIds.Help);
        var help = Assert.Single(f.Window.OwnedWindows.OfType<HelpWindow>()); Pump(help);
        Click(help.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Walk me through")));
        Assert.Empty(f.Window.OwnedWindows);
        var practice = f.Shell.ActiveTab!; Assert.NotSame(original, practice); Assert.Null(practice.FilePath);
        var popup = f.Window.GetLogicalDescendants().OfType<Popup>().Single(p => p.IsOpen);
        var card = Assert.IsAssignableFrom<Control>(popup.Child);
        Button Action(string text) => card.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, text));
        Assert.False(Action("Next").IsEnabled);
        f.Shell.ActiveTab = original; Pump(f.Window);
        Assert.True(Action("Resume practice note").IsEffectivelyVisible);
        Click(Action("Resume practice note")); Assert.Same(practice, f.Shell.ActiveTab);
        f.Dialogs.SaveChoice = Tern.App.Shell.SaveChoice.Discard;
        Assert.True(await f.Shell.CloseTabAsync(practice));
        Click(Action("Resume practice note"));
        Assert.DoesNotContain(practice, f.Shell.Tabs);
        Assert.Contains(card.GetVisualDescendants().OfType<TextBlock>(), t => t.Text?.Contains("practice note was closed", StringComparison.Ordinal) == true);
        Assert.Equal("Keep my original note", original.CurrentText);
        Click(Action("Exit walkthrough")); Assert.False(popup.IsOpen);
    }

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Pump(Window window) { window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 150 && !condition(); i++) { await Task.Delay(20, TestContext.Current.CancellationToken); Dispatcher.UIThread.RunJobs(); }
        Assert.True(condition());
    }
}
