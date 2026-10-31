using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tern.App.Commands;
using Tern.Desktop.Views;
using Tern.Markdown;

namespace Tern.Desktop.Tests;

public sealed class Oct30ReviewTests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Adding_a_picture_to_a_blank_draft_saves_then_continues(bool paste)
    {
        using var f = new DesktopFixture();
        var draft = f.Shell.NewDocument();
        f.Dialogs.SavePath = Path.Combine(f.Docs, "note.md");
        var image = await f.WriteAsync("picture.png", string.Empty);
        using (var bitmap = new SkiaSharp.SKBitmap(2, 2))
        {
            bitmap.Erase(SkiaSharp.SKColors.Teal);
            using var encoded = SkiaSharp.SKImage.FromBitmap(bitmap).Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
            await File.WriteAllBytesAsync(image, encoded.ToArray());
        }
        var file = Assert.IsAssignableFrom<IStorageFile>(await f.Window.StorageProvider.TryGetFileFromPathAsync(new Uri(image)));
        f.Window.ImageFilePicker = _ => Task.FromResult<IReadOnlyList<IStorageFile>>([file]);
        f.Window.ImageAttachmentPicker = _ => throw new InvalidOperationException("An existing image next to the saved note needs no copy picker.");
        if (paste) await f.Window.Clipboard!.SetFilesAsync([file]);
        else await f.Window.Clipboard!.SetTextAsync("");
        if (paste) await f.Window.PasteEditorAsync();
        else await f.Window.RunCommandAsync(CommandIds.InsertImage);
        Assert.Equal(f.Dialogs.SavePath, draft.FilePath);
        Assert.Equal(string.Empty, await File.ReadAllTextAsync(f.Dialogs.SavePath));
        Assert.Contains("(picture.png)", draft.CurrentText);
        Assert.True(draft.IsDirty);
        Assert.True(await f.Shell.SaveTabAsync(draft));
        Assert.Contains("(picture.png)", await File.ReadAllTextAsync(f.Dialogs.SavePath));
    }

    [AvaloniaFact]
    public async Task Cancelling_the_note_save_does_not_open_an_image_picker_or_lose_the_draft()
    {
        using var f = new DesktopFixture();
        var draft = f.Shell.NewDocument();
        f.Window.ImageFilePicker = _ => throw new InvalidOperationException("Do not pick an image after save cancellation.");
        await f.Window.RunCommandAsync(CommandIds.InsertImage);
        Assert.Null(draft.FilePath);
        Assert.Equal(string.Empty, draft.CurrentText);
        Assert.Contains(draft, f.Shell.Tabs);
        Assert.Contains("no image was inserted", f.Shell.StatusMessage);
    }

    [AvaloniaTheory]
    [InlineData("FindBox")]
    [InlineData("ReplaceBox")]
    public async Task Textbox_history_and_formatting_shortcuts_never_edit_the_source(string name)
    {
        using var f = new DesktopFixture();
        var tab = f.Shell.NewDocument("original");
        f.Window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        f.Window.ShowFindBar(true);
        var input = f.Window.FindControl<TextBox>(name)!;
        f.Window.Editor.Document.Insert(0, "changed ");
        f.Window.Editor.Select(0, 7);
        input.Text = "search text";
        input.Focus();
        foreach (var key in new[] { Key.Z, Key.Y, Key.B, Key.I })
        {
            input.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = KeyModifiers.Control });
            Assert.Equal("changed original", tab.CurrentText);
        }
        f.Window.Editor.Focus();
        Dispatcher.UIThread.RunJobs();
        Assert.True(f.Window.CanRunCommand(CommandIds.Undo));
        f.Window.Editor.TextArea.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Z, KeyModifiers = KeyModifiers.Control });
        Assert.Equal("original", tab.CurrentText);
    }

    [AvaloniaFact]
    public async Task Changing_export_format_requires_a_new_destination_and_rejects_the_old_extension()
    {
        using var f = new DesktopFixture();
        var dialog = new ExportWindow("note.md", null, "text", MarkdownProfile.Gfm, null, ExportFormat.Html);
        dialog.Show(f.Window);
        var pathBox = dialog.GetVisualDescendants().OfType<TextBox>()
            .Single(box => Avalonia.Automation.AutomationProperties.GetName(box) == "Output file");
        var oldPath = Path.Combine(f.Docs, "note.html");
        pathBox.Text = oldPath;
        dialog.Format = ExportFormat.Pdf;
        Assert.True(string.IsNullOrEmpty(pathBox.Text));
        Assert.Contains("Format changed", dialog.StatusText);
        Assert.False(await dialog.ExportAsync(oldPath));
        Assert.False(File.Exists(oldPath));
        var pdfPath = Path.Combine(f.Docs, "note.pdf");
        Assert.True(await dialog.ExportAsync(pdfPath));
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString((await File.ReadAllBytesAsync(pdfPath))[..4]));
        dialog.Close();
    }

    [AvaloniaFact]
    public async Task Truncated_export_reports_incomplete_in_the_dialog()
    {
        using var f = new DesktopFixture();
        var dialog = new ExportWindow("large.md", null, new string('x', 4_000_100), MarkdownProfile.Gfm, null, ExportFormat.Html);
        Assert.True(await dialog.ExportAsync(Path.Combine(f.Docs, "large.html")));
        Assert.Contains("incomplete", dialog.StatusText);
    }
}
