using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tern.App.Commands;
using Tern.App.Shell;
using Tern.Desktop.Views;
using Tern.Export;
using Tern.Rendering.Preview;
using Tern.Rendering.Resources;

namespace Tern.Desktop.Tests;

public sealed class ImageLinkCopyChoiceTests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Insert_local_image_keeps_the_picker_when_clipboard_contains_text_or_a_non_image_file(bool copiedFile)
    {
        using var f = new DesktopFixture();
        var tab = (await f.Shell.OpenFileAsync(await f.WriteAsync("1.md", "")))!;
        var image = await f.WriteAsync("TPic.png", "");
        await File.WriteAllBytesAsync(image, ImageBytes());
        if (copiedFile)
            await f.Window.Clipboard!.SetFilesAsync([await StorageFileAsync(f, await f.WriteAsync("other.txt", "text"))]);
        else await f.Window.Clipboard!.SetTextAsync("ordinary text");
        var file = await StorageFileAsync(f, image);
        var picked = 0;
        f.Window.ImageFilePicker = _ => { picked++; return Task.FromResult<IReadOnlyList<IStorageFile>>([file]); };
        f.Window.ImageImportChoicePicker = _ => throw new InvalidOperationException("The selected image is adjacent to the note.");
        await f.Window.RunCommandAsync(CommandIds.InsertImage);
        Assert.Equal(1, picked);
        Assert.Equal("![TPic](TPic.png)", tab.CurrentText);
    }

    [AvaloniaTheory]
    [InlineData("Drop")]
    [InlineData("CtrlV")]
    [InlineData("InsertLocalImage")]
    [InlineData("InsertCopiedImage")]
    [InlineData("PasteAttachment")]
    public async Task An_image_beside_the_note_links_without_dialogs_for_every_file_entry_point(string method)
    {
        using var f = new DesktopFixture();
        var note = await f.WriteAsync("1.md", "");
        var image = await f.WriteAsync("TPic.png", "");
        var bytes = ImageBytes();
        await File.WriteAllBytesAsync(image, bytes);
        var tab = (await f.Shell.OpenFileAsync(note))!;
        f.Window.ImageImportChoicePicker = _ => throw new InvalidOperationException("Adjacent images must not ask Link or Copy.");
        f.Window.ImageAttachmentPicker = _ => throw new InvalidOperationException("Adjacent images must not ask for a copy location.");
        if (method == "CtrlV")
        {
            await f.Window.Clipboard!.SetFilesAsync([await StorageFileAsync(f, image)]);
            f.Window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            f.Window.Editor.Focus();
            f.Window.Editor.TextArea.RaiseEvent(new KeyEventArgs
            { RoutedEvent = InputElement.KeyDownEvent, Key = Key.V, KeyModifiers = KeyModifiers.Control });
            for (var attempt = 0; attempt < 100 && tab.CurrentText.Length == 0; attempt++)
            { await Task.Delay(10); Dispatcher.UIThread.RunJobs(); }
        }
        else await ImportFileAsync(f, method, image);
        Assert.Equal("![TPic](TPic.png)", tab.CurrentText);
        Assert.Empty(f.Window.OwnedWindows);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(image));
        Assert.Equal(2, Directory.GetFiles(f.Docs).Length);
        await f.Window.RebuildPreviewAsync();
        Assert.IsType<LocalFile>(ImageNodes(f.Window.PreviewControl.Document!).Single().Resolution);
        await f.Window.RunCommandAsync(CommandIds.Undo);
        Assert.Equal("", tab.CurrentText);
        Assert.True(File.Exists(image));
    }

    [AvaloniaTheory]
    [InlineData("Drop", false)]
    [InlineData("Drop", true)]
    [InlineData("CtrlV", false)]
    [InlineData("CtrlV", true)]
    [InlineData("InsertLocalImage", false)]
    [InlineData("InsertLocalImage", true)]
    [InlineData("InsertCopiedImage", false)]
    [InlineData("InsertCopiedImage", true)]
    [InlineData("PasteAttachment", false)]
    [InlineData("PasteAttachment", true)]
    public async Task Every_file_entry_point_offers_link_for_subfolder_and_external_images(string method, bool outside)
    {
        using var f = new DesktopFixture();
        var note = await f.WriteAsync("note.md", "");
        var image = Path.GetFullPath(await f.WriteAsync(outside ? "../external/MPic.png" : "images/MPic.png", ""));
        var bytes = ImageBytes();
        await File.WriteAllBytesAsync(image, bytes);
        if (outside) await f.Shell.OpenWorkspaceAsync(f.Docs);
        var tab = (await f.Shell.OpenFileAsync(note))!;
        f.Window.ImageImportChoicePicker = null;
        f.Window.ImageAttachmentPicker = _ => throw new InvalidOperationException("Link must not open a copy dialog.");
        var importing = ImportFileAsync(f, method, image);
        await ChooseAsync(f, "Link original");
        await importing;
        Assert.StartsWith("Original image linked", f.Shell.StatusMessage);
        Assert.Equal(outside ? "![MPic](../external/MPic.png)" : "![MPic](images/MPic.png)", tab.CurrentText);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(image));
        Assert.False(File.Exists(Path.Combine(f.Docs, "MPic.png")));
        await f.Window.RebuildPreviewAsync();
        var node = ImageNodes(f.Window.PreviewControl.Document!).Single();
        Assert.Equal(image, Assert.IsType<LocalFile>(node.Resolution).CanonicalPath);
        Assert.True(await f.Shell.SaveTabAsync(tab));
        if (outside) Assert.Empty(f.Shell.GetBrokenLinks(f.Shell.Workspace!));

        using var reopened = new ShellViewModel(f.Shell.Paths, new ScriptedDialogs(), new NullLauncher());
        var restored = (await reopened.OpenFileAsync(note))!;
        var preview = Options(restored.FilePath!, restored.LinkedImageFiles);
        Assert.IsType<LocalFile>(ImageNodes(PreviewBuilder.Build(restored.CurrentText, preview)).Single().Resolution);
        var html = HtmlExporter.Export(restored.CurrentText, new HtmlExportOptions { Preview = preview });
        Assert.Contains("data:image/png;base64,", html.Html);
        Assert.Empty(html.Warnings);

        if (outside)
        {
            var other = Path.GetFullPath(await f.WriteAsync("../external/unselected.png", ""));
            await File.WriteAllBytesAsync(other, bytes);
            var resolver = PreviewBuilder.CreateResolver(preview);
            Assert.IsType<Blocked>(resolver.ResolveImage("../external/unselected.png"));
            Assert.IsType<Blocked>(resolver.ResolveLink("../external/MPic.png"));
        }
    }

    [AvaloniaTheory]
    [InlineData("Drop")]
    [InlineData("CtrlV")]
    [InlineData("InsertLocalImage")]
    [InlineData("InsertCopiedImage")]
    [InlineData("PasteAttachment")]
    public async Task Every_file_entry_point_copies_to_the_chosen_folder_only_after_the_copy_choice(string method)
    {
        using var f = new DesktopFixture();
        var tab = (await f.Shell.OpenFileAsync(await f.WriteAsync("note.md", "")))!;
        var source = await f.WriteAsync("images/MPic.png", "");
        var bytes = ImageBytes();
        await File.WriteAllBytesAsync(source, bytes);
        var copyPath = Path.GetFullPath(await f.WriteAsync("../chosen-copy/copied image.png", ""));
        var copy = await StorageFileAsync(f, copyPath);
        File.Delete(copyPath);
        f.Window.ImageImportChoicePicker = null;
        f.Window.ImageAttachmentPicker = _ => Task.FromResult<IStorageFile?>(copy);
        var importing = ImportFileAsync(f, method, source);
        await ChooseAsync(f, "Copy image…");
        await importing;
        Assert.Equal("![copied image](../chosen-copy/copied%20image.png)", tab.CurrentText);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(source));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(copyPath));
        await f.Window.RebuildPreviewAsync();
        Assert.IsType<LocalFile>(ImageNodes(f.Window.PreviewControl.Document!).Single().Resolution);
        await f.Window.RunCommandAsync(CommandIds.Undo);
        Assert.Equal("", tab.CurrentText);
        Assert.True(File.Exists(copyPath));
    }

    [AvaloniaTheory]
    [InlineData("Drop")]
    [InlineData("CtrlV")]
    [InlineData("InsertLocalImage")]
    [InlineData("InsertCopiedImage")]
    [InlineData("PasteAttachment")]
    public async Task Every_file_entry_point_preserves_the_note_when_the_link_or_copy_choice_is_cancelled(string method)
    {
        using var f = new DesktopFixture();
        var tab = (await f.Shell.OpenFileAsync(await f.WriteAsync("note.md", "")))!;
        var image = await f.WriteAsync("images/MPic.png", "");
        await File.WriteAllBytesAsync(image, ImageBytes());
        f.Window.ImageImportChoicePicker = null;
        f.Window.ImageAttachmentPicker = _ => throw new InvalidOperationException("Cancel must not pick a copy destination.");
        var importing = ImportFileAsync(f, method, image);
        await ChooseAsync(f, "Cancel");
        await importing;
        Assert.Equal("", tab.CurrentText);
        Assert.False(tab.IsDirty);
        Assert.Empty(tab.LinkedImageFiles);
    }

    [AvaloniaFact]
    public async Task Clipboard_pixels_need_only_a_copy_destination_because_they_have_no_source_file()
    {
        using var f = new DesktopFixture();
        var tab = (await f.Shell.OpenFileAsync(await f.WriteAsync("note.md", "")))!;
        var target = await f.WriteAsync("images/copy.png", "");
        var copy = await StorageFileAsync(f, target);
        File.Delete(target);
        using var stream = new MemoryStream(ImageBytes());
        using var picture = new Avalonia.Media.Imaging.Bitmap(stream);
        var clipboard = f.Window.Clipboard!;
        await clipboard.SetTextAsync("");
        await clipboard.SetBitmapAsync(picture);
        f.Window.ImageImportChoicePicker = _ => throw new InvalidOperationException("Clipboard pixels have no original file to link.");
        f.Window.ImageAttachmentPicker = _ => Task.FromResult<IStorageFile?>(copy);
        await f.Window.RunCommandAsync(CommandIds.PasteImage);
        Assert.Equal("![copy](images/copy.png)", tab.CurrentText);
        using var saved = SkiaSharp.SKBitmap.Decode(target);
        Assert.NotNull(saved);
        Assert.Equal(2, saved.Width);
        Assert.Equal(2, saved.Height);
        Assert.Equal(SkiaSharp.SKColors.Blue, saved.GetPixel(0, 0));
    }

    private static async Task ImportFileAsync(DesktopFixture f, string method, string path)
    {
        if (method == "Drop") { await f.Window.OpenDroppedAsync([path]); return; }
        var file = await StorageFileAsync(f, path);
        if (method == "InsertLocalImage")
        {
            await f.Window.Clipboard!.SetTextAsync("");
            f.Window.ImageFilePicker = _ => Task.FromResult<IReadOnlyList<IStorageFile>>([file]);
            await f.Window.RunCommandAsync(CommandIds.InsertImage);
            return;
        }
        await f.Window.Clipboard!.SetFilesAsync([file]);
        if (method == "InsertCopiedImage")
        {
            f.Window.ImageFilePicker = _ => throw new InvalidOperationException("The source file is already known from the clipboard.");
            await f.Window.RunCommandAsync(CommandIds.InsertImage);
        }
        else if (method == "PasteAttachment") await f.Window.RunCommandAsync(CommandIds.PasteImage);
        else await f.Window.PasteEditorAsync();
    }

    private static PreviewOptions Options(string path, IReadOnlyList<string> files) => new()
    { DocumentPath = path, Resolver = new ResourceResolverOptions { AdditionalImageFiles = files } };

    private static IEnumerable<ImageNode> ImageNodes(PreviewDocument document) => document.Blocks.OfType<ParagraphNode>()
        .SelectMany(paragraph => paragraph.Inlines).OfType<ImageNode>();

    private static async Task<IStorageFile> StorageFileAsync(DesktopFixture f, string path) =>
        Assert.IsAssignableFrom<IStorageFile>(await f.Window.StorageProvider.TryGetFileFromPathAsync(new Uri(Path.GetFullPath(path))));

    private static async Task ChooseAsync(DesktopFixture f, string label)
    {
        for (var attempt = 0; attempt < 100 && !f.Window.OwnedWindows.OfType<PromptWindow>().Any(); attempt++)
        { await Task.Delay(10); Dispatcher.UIThread.RunJobs(); }
        var prompt = Assert.Single(f.Window.OwnedWindows.OfType<PromptWindow>());
        prompt.UpdateLayout();
        var button = Assert.Single(prompt.GetVisualDescendants().OfType<Button>(), candidate => Equals(candidate.Content, label));
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    private static byte[] ImageBytes()
    {
        using var bitmap = new SkiaSharp.SKBitmap(2, 2);
        bitmap.Erase(SkiaSharp.SKColors.Blue);
        using var image = SkiaSharp.SKImage.FromBitmap(bitmap);
        using var bytes = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        return bytes.ToArray();
    }
}
