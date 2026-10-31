using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input.Platform;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Tern.App.Commands;
using Tern.Desktop.Services;

namespace Tern.Desktop.Tests;

public sealed class BeginnerEditingTests
{
    [AvaloniaFact]
    public async Task Output_actions_start_disabled_with_explanation_and_opened_file_enables_them()
    {
        using var f = new DesktopFixture();
        f.Shell.NewStartupDocument();
        Dispatcher.UIThread.RunJobs();
        foreach (var id in new[] { CommandIds.ExportHtml, CommandIds.ExportPdf, CommandIds.Print })
        {
            Assert.False(f.Window.CanRunCommand(id));
            var item = f.Window.GetLogicalDescendants().OfType<MenuItem>().Single(m => Equals(m.Tag, id));
            Assert.False(item.IsEnabled);
            Assert.True(ToolTip.GetShowOnDisabled(item));
            Assert.Contains("write something", ToolTip.GetTip(item)?.ToString());
        }
        await f.Shell.OpenFileAsync(await f.WriteAsync("empty.md", ""));
        Assert.Single(f.Shell.Tabs);
        Assert.True(f.Window.CanRunCommand(CommandIds.Print));
    }

    [AvaloniaFact]
    public async Task Source_text_paste_is_one_undoable_edit_and_url_preserves_selected_label()
    {
        using var f = new DesktopFixture();
        var tab = f.Shell.NewDocument("Keep label");
        f.Window.EditorControl.Select(5, 5);
        await f.Window.Clipboard!.SetTextAsync("https://example.com/page");
        await f.Window.PasteEditorAsync();
        Assert.Equal("Keep [label](https://example.com/page)", tab.CurrentText);
        await f.Window.RunCommandAsync(CommandIds.Undo);
        Assert.Equal("Keep label", tab.CurrentText);
        f.Window.EditorControl.Select(5, 5);
        await f.Window.Clipboard!.SetTextAsync("new words");
        await f.Window.PasteEditorAsync();
        Assert.Equal("Keep new words", tab.CurrentText);
    }

    [AvaloniaTheory]
    [InlineData(CommandIds.Bold)]
    [InlineData(CommandIds.Italic)]
    [InlineData(CommandIds.Strikethrough)]
    [InlineData(CommandIds.InlineCode)]
    [InlineData(CommandIds.ToggleQuote)]
    [InlineData(CommandIds.ToggleBullet)]
    [InlineData(CommandIds.ToggleNumbered)]
    [InlineData(CommandIds.ToggleTask)]
    [InlineData(CommandIds.Heading1)]
    [InlineData(CommandIds.Heading2)]
    [InlineData(CommandIds.Heading3)]
    [InlineData(CommandIds.InsertCodeBlock)]
    [InlineData(CommandIds.InsertRule)]
    [InlineData(CommandIds.InsertTable)]
    [InlineData(CommandIds.InsertLink)]
    public async Task Formatting_selected_and_empty_text_is_undoable_and_preview_builds(string id)
    {
        using var f = new DesktopFixture();
        foreach (var source in new[] { "", "example" })
        {
            var tab = f.Shell.NewDocument(source);
            f.Window.EditorControl.Select(0, source.Length);
            await f.Window.RunCommandAsync(id);
            Assert.NotEqual(source, tab.CurrentText);
            Assert.InRange(f.Window.EditorControl.SelectionStart, 0, tab.CurrentText.Length);
            await f.Window.RebuildPreviewAsync();
            Assert.NotNull(f.Window.PreviewControl.Document);
            await f.Window.RunCommandAsync(CommandIds.Undo);
            Assert.Equal(source, tab.CurrentText);
            await f.Window.RunCommandAsync(CommandIds.Redo);
            Assert.NotEqual(source, tab.CurrentText);
        }
    }

    [AvaloniaFact]
    public async Task Mixed_unsupported_drop_retains_its_problem_even_after_a_supported_file()
    {
        using var f = new DesktopFixture();
        var unsupported = await f.WriteAsync("notes.docx", "unsupported");
        var supported = await f.WriteAsync("notes.md", "# Notes");
        await f.Window.OpenDroppedAsync([unsupported, supported]);
        Assert.Equal(supported, f.Shell.ActiveTab!.FilePath);
        Assert.Contains("notes.docx", f.Shell.StatusMessage);
        Assert.Contains("unsupported", f.Shell.StatusMessage);
    }

    [AvaloniaFact]
    public async Task Text_files_supported_by_the_open_picker_also_open_when_dropped()
    {
        using var f = new DesktopFixture();
        var path = await f.WriteAsync("plain-note.txt", "A plain text note");
        await f.Window.OpenDroppedAsync([path]);
        Assert.Equal(path, f.Shell.ActiveTab!.FilePath);
        Assert.Equal("A plain text note", f.Shell.ActiveTab.CurrentText);
    }

    [AvaloniaFact]
    public async Task Dropping_image_outside_source_does_not_insert_into_active_document()
    {
        using var f = new DesktopFixture();
        var tab = f.Shell.NewDocument("Keep");
        var path = await f.WriteAsync("photo.png", "image placeholder");
        await f.Window.OpenDroppedAsync([path], allowImageInsertion: false);
        Assert.Equal("Keep", tab.CurrentText);
        Assert.Contains("Source editor", f.Shell.StatusMessage);
    }

    [Fact]
    public void Duplicate_image_names_get_predictable_non_overwriting_suggestions()
    {
        var folder = Path.Combine(Path.GetTempPath(), "tern-name-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllText(Path.Combine(folder, "pasted-image.png"), "keep");
            File.WriteAllText(Path.Combine(folder, "pasted-image-2.png"), "keep too");
            Assert.Equal("pasted-image-3.png", ImageAttachmentImporter.SuggestUniqueName(folder, "pasted-image.png"));
            Assert.Equal("keep", File.ReadAllText(Path.Combine(folder, "pasted-image.png")));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [AvaloniaFact]
    public async Task Bitmap_clipboard_preserves_the_draft_when_save_is_cancelled_and_attachments_render_and_export()
    {
        using var f = new DesktopFixture();
        var draft = f.Shell.NewDocument("Keep");
        using var pixels = new SkiaSharp.SKBitmap(2, 2);
        pixels.Erase(SkiaSharp.SKColors.Blue);
        using var image = SkiaSharp.SKImage.FromBitmap(pixels);
        using var encoded = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        var bytes = encoded.ToArray();
        using var stream = new MemoryStream(bytes);
        using var bitmap = new Avalonia.Media.Imaging.Bitmap(stream);
        await f.Window.Clipboard!.SetBitmapAsync(bitmap);
        await f.Window.PasteEditorAsync();
        Assert.Contains("no image was inserted", f.Shell.StatusMessage);
        Assert.Equal("Keep", draft.CurrentText);

        var path = await f.WriteAsync("images.md", "");
        var tab = (await f.Shell.OpenFileAsync(path))!;
        await ImageAttachmentImporter.InsertAsync(tab, bytes, f.Docs, "diagram image.png", 0, tab.Session.Revision);
        await f.Window.RebuildPreviewAsync();
        var paragraph = Assert.IsType<Tern.Rendering.Preview.ParagraphNode>(Assert.Single(f.Window.PreviewControl.Document!.Blocks));
        var node = Assert.IsType<Tern.Rendering.Preview.ImageNode>(Assert.Single(paragraph.Inlines));
        Assert.IsType<Tern.Rendering.Resources.LocalFile>(node.Resolution);
        var options = new Tern.Rendering.Preview.PreviewOptions { DocumentPath = path };
        var html = Tern.Export.HtmlExporter.Export(tab.CurrentText, new() { Preview = options });
        Assert.Contains("data:image/png;base64,", html.Html);
        Assert.Empty(html.Warnings);
        var pdf = Tern.Export.PdfExporter.Export(tab.CurrentText, new() { Preview = options });
        Assert.Empty(pdf.Warnings);
        Assert.True(pdf.PageCount > 0);
    }
}
