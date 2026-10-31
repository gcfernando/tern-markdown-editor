using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.Headless.XUnit;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Tern.App.Commands;
using Tern.Domain.Text;

namespace Tern.Desktop.Tests;

public sealed class ImageCommandWorkflowTests
{
    [AvaloniaFact]
    public async Task Insert_Local_Image_requests_a_raster_file_then_creates_a_relative_attachment_as_one_undoable_edit()
    {
        using var fixture = new DesktopFixture();
        var tab = (await fixture.Shell.OpenFileAsync(await fixture.WriteAsync("note.md", "before\n")))!;
        await fixture.Window.Clipboard!.SetTextAsync("");
        var sourcePath = await fixture.WriteAsync("external/source.png", string.Empty);
        var bytes = ImageBytes();
        await File.WriteAllBytesAsync(sourcePath, bytes);
        var source = await ExistingFile(fixture, sourcePath);
        var target = await FutureFile(fixture, Path.Combine(fixture.Docs, "attachments", "attached picture.png"));
        var openCalls = 0; var saveCalls = 0;
        fixture.Window.ImageImportChoicePicker = _ => Task.FromResult(Tern.Desktop.Views.ImageImportChoice.Copy);
        fixture.Window.ImageFilePicker = options =>
        {
            openCalls++;
            Assert.False(options.AllowMultiple);
            Assert.Contains(options.FileTypeFilter!, filter => filter.Patterns!.Contains("*.png"));
            return Task.FromResult<IReadOnlyList<IStorageFile>>([source]);
        };
        fixture.Window.ImageAttachmentPicker = options =>
        {
            saveCalls++;
            Assert.Contains("image copy", options.Title);
            Assert.Equal("png", options.DefaultExtension);
            return Task.FromResult<IStorageFile?>(target);
        };
        await fixture.Window.RunCommandAsync(CommandIds.InsertImage);
        Assert.Equal(1, openCalls); Assert.Equal(1, saveCalls);
        Assert.Contains("(attachments/attached%20picture.png)", tab.CurrentText);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(target.TryGetLocalPath()!));
        Assert.True(File.Exists(sourcePath));
        Assert.True(tab.IsDirty);
        await fixture.Window.RunCommandAsync(CommandIds.Undo);
        Assert.Equal("before\n", tab.CurrentText);
        Assert.False(tab.IsDirty);
        Assert.True(File.Exists(target.TryGetLocalPath()));
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Paste_Image_as_Attachment_accepts_both_copied_files_and_bitmap_pixels(bool bitmap)
    {
        using var fixture = new DesktopFixture();
        var tab = (await fixture.Shell.OpenFileAsync(await fixture.WriteAsync("note.md", "before\n")))!;
        var bytes = ImageBytes();
        var clipboard = fixture.Window.Clipboard!;
        await clipboard.SetTextAsync("");
        using var stream = new MemoryStream(bytes);
        using var picture = new Avalonia.Media.Imaging.Bitmap(stream);
        if (bitmap)
        {
            await clipboard.SetBitmapAsync(picture);
        }
        else
        {
            var sourcePath = await fixture.WriteAsync("external/copied.png", string.Empty);
            await File.WriteAllBytesAsync(sourcePath, bytes);
            await clipboard.SetFilesAsync([await ExistingFile(fixture, sourcePath)]);
        }
        var target = await FutureFile(fixture, Path.Combine(fixture.Docs, "attachments", "pasted.png"));
        fixture.Window.ImageImportChoicePicker = _ => bitmap
            ? throw new InvalidOperationException("Clipboard pixels cannot link to a source file.")
            : Task.FromResult(Tern.Desktop.Views.ImageImportChoice.Copy);
        fixture.Window.ImageAttachmentPicker = _ => Task.FromResult<IStorageFile?>(target);
        fixture.Window.ImageFilePicker = _ => throw new InvalidOperationException("Clipboard paste must not ask the user to choose the source again.");
        await fixture.Window.RunCommandAsync(CommandIds.PasteImage);
        Assert.True(tab.CurrentText.Contains("(attachments/pasted.png)", StringComparison.Ordinal), fixture.Shell.StatusMessage);
        Assert.StartsWith("Image copied and linked", fixture.Shell.StatusMessage);
        using var codec = SkiaSharp.SKCodec.Create(target.TryGetLocalPath()!);
        Assert.NotNull(codec); Assert.Equal(2, codec.Info.Width); Assert.Equal(2, codec.Info.Height);
        Assert.True(tab.IsDirty);
    }

    [AvaloniaTheory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Picker_cancellation_and_native_failure_preserve_the_note_and_report_the_result(int stage)
    {
        using var fixture = new DesktopFixture();
        await fixture.Window.Clipboard!.SetTextAsync("");
        var tab = (await fixture.Shell.OpenFileAsync(await fixture.WriteAsync("note.md", "Keep\n")))!;
        fixture.Window.ImageImportChoicePicker = _ => Task.FromResult(Tern.Desktop.Views.ImageImportChoice.Copy);
        var path = await fixture.WriteAsync("external/source.png", string.Empty);
        await File.WriteAllBytesAsync(path, ImageBytes());
        var source = await ExistingFile(fixture, path);
        fixture.Window.ImageFilePicker = _ => stage == 2
            ? Task.FromException<IReadOnlyList<IStorageFile>>(new NativePickerFailureException())
            : Task.FromResult<IReadOnlyList<IStorageFile>>(stage == 0 ? [] : [source]);
        fixture.Window.ImageAttachmentPicker = _ => stage == 3
            ? Task.FromException<IStorageFile?>(new NativePickerFailureException())
            : Task.FromResult<IStorageFile?>(null);
        await fixture.Window.RunCommandAsync(CommandIds.InsertImage);
        Assert.Equal("Keep\n", tab.CurrentText);
        Assert.False(tab.IsDirty);
        Assert.Contains(stage < 2 ? "cancelled" : "Native picker failure", fixture.Shell.StatusMessage, StringComparison.OrdinalIgnoreCase);
        if (stage >= 2)
        {
            var diagnostic = fixture.Shell.StatusMessage;
            await Task.Delay(1800, TestContext.Current.CancellationToken);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.Equal(diagnostic, fixture.Shell.StatusMessage);
        }
    }

    [AvaloniaFact]
    public async Task Document_changed_during_image_selection_is_never_modified_by_the_returned_picker_result()
    {
        using var fixture = new DesktopFixture();
        await fixture.Window.Clipboard!.SetTextAsync("");
        var tab = (await fixture.Shell.OpenFileAsync(await fixture.WriteAsync("note.md", "Keep\n")))!;
        var path = Path.Combine(fixture.Docs, "source.png");
        await File.WriteAllBytesAsync(path, ImageBytes());
        var source = await ExistingFile(fixture, path);
        fixture.Window.ImageFilePicker = _ =>
        {
            tab.ApplyEdits([TextEdit.Insert(0, "New edit\n")]);
            return Task.FromResult<IReadOnlyList<IStorageFile>>([source]);
        };
        fixture.Window.ImageAttachmentPicker = _ => throw new InvalidOperationException("A stale source selection must not open an attachment picker.");
        await fixture.Window.RunCommandAsync(CommandIds.InsertImage);
        Assert.Equal("New edit\nKeep\n", tab.CurrentText);
        Assert.Contains("document changed while choosing", fixture.Shell.StatusMessage);
    }

    [AvaloniaTheory]
    [InlineData("Format")]
    [InlineData("OrdinaryPaste")]
    public async Task Image_beside_the_note_is_linked_without_copy_or_save_picker(string entry)
    {
        using var fixture = new DesktopFixture();
        var tab = (await fixture.Shell.OpenFileAsync(await fixture.WriteAsync("note.md", "before\n")))!;
        var path = await fixture.WriteAsync("existing picture.png", string.Empty);
        var bytes = ImageBytes();
        await File.WriteAllBytesAsync(path, bytes);
        var source = await ExistingFile(fixture, path);
        fixture.Window.ImageFilePicker = _ => Task.FromResult<IReadOnlyList<IStorageFile>>([source]);
        fixture.Window.ImageAttachmentPicker = _ => throw new InvalidOperationException("An image beside the note must not request a copy destination.");
        if (entry == "OrdinaryPaste")
        {
            await fixture.Window.Clipboard!.SetFilesAsync([source]);
            await fixture.Window.PasteEditorAsync();
        }
        else
        {
            await fixture.Window.Clipboard!.SetTextAsync("");
            var parent = Assert.Single(fixture.Window.FindControl<Menu>("ApplicationMenu")!.Items.OfType<MenuItem>(),
                item => item.Header!.ToString()!.Replace("_", "", StringComparison.Ordinal) == entry);
            var command = Assert.Single(parent.Items.OfType<MenuItem>(), item => Equals(item.Tag, CommandIds.InsertImage));
            var formatCommands = parent.Items.OfType<MenuItem>().ToArray();
            Assert.Equal(CommandIds.PasteImage, formatCommands[Array.IndexOf(formatCommands, command) + 1].Tag);
            var edit = Assert.Single(fixture.Window.FindControl<Menu>("ApplicationMenu")!.Items.OfType<MenuItem>(),
                item => Equals(item.Header, "_Edit"));
            Assert.DoesNotContain(edit.Items.OfType<MenuItem>(), item => item.Tag is CommandIds.InsertImage or CommandIds.PasteImage);
            command.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            for (var attempt = 0; attempt < 100 && !tab.IsDirty; attempt++)
            { Dispatcher.UIThread.RunJobs(); await Task.Delay(10, TestContext.Current.CancellationToken); }
        }
        Assert.Contains("(existing%20picture.png)", tab.CurrentText);
        Assert.StartsWith("Original image linked", fixture.Shell.StatusMessage);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        Assert.Equal(2, Directory.GetFiles(fixture.Docs).Length);
        await fixture.Window.RunCommandAsync(CommandIds.Undo);
        Assert.Equal("before\n", tab.CurrentText);
        Assert.False(tab.IsDirty);
        Assert.True(File.Exists(path));
    }

    [AvaloniaFact]
    public async Task Pasting_multiple_files_links_adjacent_files_and_asks_link_or_copy_for_other_locations()
    {
        using var fixture = new DesktopFixture();
        var tab = (await fixture.Shell.OpenFileAsync(await fixture.WriteAsync("note.md", "before\n")))!;
        var local = await fixture.WriteAsync("local.png", string.Empty);
        var external = await fixture.WriteAsync("elsewhere/external.png", string.Empty);
        await File.WriteAllBytesAsync(local, ImageBytes());
        await File.WriteAllBytesAsync(external, ImageBytes());
        await fixture.Window.Clipboard!.SetFilesAsync([await ExistingFile(fixture, local), await ExistingFile(fixture, external)]);
        var target = await FutureFile(fixture, Path.Combine(fixture.Docs, "copied.png"));
        fixture.Window.ImageImportChoicePicker = path =>
        {
            Assert.Equal(Path.GetFullPath(external), Path.GetFullPath(path));
            return Task.FromResult(Tern.Desktop.Views.ImageImportChoice.Copy);
        };
        var saveCalls = 0;
        fixture.Window.ImageAttachmentPicker = _ => { saveCalls++; return Task.FromResult<IStorageFile?>(target); };
        await fixture.Window.RunCommandAsync(CommandIds.PasteImage);
        Assert.Equal(1, saveCalls);
        Assert.Contains("(local.png)", tab.CurrentText);
        Assert.Contains("(copied.png)", tab.CurrentText);
        Assert.Equal(ImageBytes(), await File.ReadAllBytesAsync(target.TryGetLocalPath()!));
    }

    [AvaloniaFact]
    public async Task Invalid_image_beside_the_note_does_not_insert_a_link_or_request_a_save()
    {
        using var fixture = new DesktopFixture();
        var tab = (await fixture.Shell.OpenFileAsync(await fixture.WriteAsync("note.md", "Keep\n")))!;
        var path = await fixture.WriteAsync("broken.png", "This is not an image");
        await fixture.Window.Clipboard!.SetFilesAsync([await ExistingFile(fixture, path)]);
        fixture.Window.ImageAttachmentPicker = _ => throw new InvalidOperationException("An invalid local image must not request a save.");
        await fixture.Window.RunCommandAsync(CommandIds.PasteImage);
        Assert.Equal("Keep\n", tab.CurrentText);
        Assert.False(tab.IsDirty);
        Assert.Contains("image is invalid", fixture.Shell.StatusMessage);
    }

    private static async Task<IStorageFile> ExistingFile(DesktopFixture fixture, string path) =>
        Assert.IsAssignableFrom<IStorageFile>(await fixture.Window.StorageProvider.TryGetFileFromPathAsync(new Uri(path)));

    private static async Task<IStorageFile> FutureFile(DesktopFixture fixture, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, []);
        var file = await ExistingFile(fixture, path);
        File.Delete(path);
        return file;
    }

    private static byte[] ImageBytes()
    {
        using var bitmap = new SkiaSharp.SKBitmap(2, 2);
        bitmap.Erase(SkiaSharp.SKColors.Teal);
        using var image = SkiaSharp.SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }

    private sealed class NativePickerFailureException : System.Runtime.InteropServices.ExternalException
    {
        public NativePickerFailureException() : base("Native picker failure") { }
    }
}
