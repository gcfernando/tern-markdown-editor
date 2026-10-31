using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tern.App.Commands;
using Tern.Desktop.Services;
using Tern.Desktop.Views;
using Tern.Domain.Text;
using Tern.Domain.Documents;
using Tern.Markdown;
using Tern.Desktop.Themes;

namespace Tern.Desktop.Tests;

public sealed class ReleaseSafetyTests
{
    [AvaloniaFact]
    public async Task Recovered_status_identifies_the_original_without_claiming_a_backing_save()
    {
        using var f = new DesktopFixture();
        var path = await f.WriteAsync("recovered-origin.md", "disk version");
        var record = new Tern.Recovery.RecoveryRecord(Guid.NewGuid(), path, "recovered-origin.md", 1,
            DateTime.UtcNow, null, TextFileFormat.NewFileDefault, "recovered version");
        var tab = await f.Shell.RestoreRecoveryAsync(record);
        await Settle();
        Assert.Null(tab.FilePath);
        Assert.Contains(path, f.Window.FindControl<TextBlock>("StatusPath")!.Text!);
        Assert.Contains("not saved", f.Window.FindControl<TextBlock>("StatusPath")!.Text!);
        Assert.Equal("disk version", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public void Clipboard_encoding_stream_enforces_its_limit_before_allocating_output()
    {
        using var output = new BoundedImageStream(16);
        output.Write(new byte[16]);
        Assert.Throws<IOException>(() => output.WriteByte(0));
        Assert.Throws<IOException>(() => output.SetLength(17));
        Assert.Equal(16, output.Length);
    }

    private static async Task Settle()
    {
        await Task.Delay(350);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task Dismissing_a_prompt_returns_cancel_instead_of_the_first_choice()
    {
        using var f = new DesktopFixture();
        var result = PromptWindow.AskAsync(f.Window, "Conflict", "Synthetic conflict", ["Keep mine", "Use disk", "Merge", "Save copy", "Cancel"], 2, 4);
        var prompt = Assert.Single(f.Window.OwnedWindows.OfType<PromptWindow>());
        prompt.Close();
        Assert.Equal(4, await result);
    }

    [AvaloniaFact]
    public async Task Single_replace_uses_the_current_replacement_text()
    {
        using var f = new DesktopFixture();
        f.Shell.NewDocument("cat cat");
        f.Window.ShowFindBar(true);
        await f.Window.SearchAsync("cat");
        f.Window.ReplaceBox.Text = "dog";
        await Settle();
        f.Window.ReplaceCurrent();
        Assert.Equal("dog cat", f.Shell.ActiveTab!.CurrentText);
    }

    [AvaloniaFact]
    public async Task Switching_documents_invalidates_cached_replace_offsets()
    {
        using var f = new DesktopFixture();
        f.Shell.NewDocument("cat");
        f.Window.ShowFindBar(true);
        f.Window.ReplaceBox.Text = "dog";
        await f.Window.SearchAsync("cat");
        var other = f.Shell.NewDocument("unrelated text");
        await Settle();
        f.Window.ReplaceCurrent();
        Assert.Equal("unrelated text", other.CurrentText);
    }

    [AvaloniaFact]
    public async Task Explicit_reload_checks_disk_without_a_preexisting_conflict()
    {
        using var f = new DesktopFixture();
        var path = await f.WriteAsync("reload.md", "old");
        var tab = (await f.Shell.OpenFileAsync(path))!;
        await File.WriteAllTextAsync(path, "new");
        await f.Window.RunCommandAsync(CommandIds.ReloadFromDisk);
        Assert.Equal("new", tab.CurrentText);
    }

    [AvaloniaFact]
    public async Task Export_refuses_the_source_document_path()
    {
        using var f = new DesktopFixture();
        var path = await f.WriteAsync("source.md", "# Keep source\n");
        foreach (var format in Enum.GetValues<ExportFormat>())
        {
            var dialog = new ExportWindow("source.md", path, "# Keep source\n", MarkdownProfile.Gfm, null, format);
            Assert.False(await dialog.ExportAsync(path));
            Assert.Equal("# Keep source\n", await File.ReadAllTextAsync(path));
        }
    }

    [AvaloniaFact]
    public async Task Export_protects_another_open_source_and_its_hard_link_alias()
    {
        using var f = new DesktopFixture();
        var current = await f.WriteAsync("current.md", "current");
        var other = await f.WriteAsync("other.md", "other source");
        var alias = Path.Combine(f.Docs, "other-alias.md");
        if (OperatingSystem.IsWindows()) Assert.True(CreateHardLink(alias, other, 0));
        foreach (var format in Enum.GetValues<ExportFormat>())
        {
            var dialog = new ExportWindow("current.md", current, "current", MarkdownProfile.Gfm, null, format, [current, other]);
            Assert.False(await dialog.ExportAsync(other));
            if (OperatingSystem.IsWindows()) Assert.False(await dialog.ExportAsync(alias));
            Assert.Equal("other source", await File.ReadAllTextAsync(other));
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string newPath, string existingPath, nint reserved);

    private static byte[] ImageBytes()
    {
        using var bitmap = new SkiaSharp.SKBitmap(1, 1);
        bitmap.Erase(SkiaSharp.SKColors.Blue);
        using var image = SkiaSharp.SKImage.FromBitmap(bitmap);
        using var bytes = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        return bytes.ToArray();
    }

    [AvaloniaFact]
    public async Task Image_import_is_a_relative_undoable_edit_and_never_overwrites_an_asset()
    {
        using var f = new DesktopFixture();
        var path = await f.WriteAsync("source.md", "# Source\n");
        var tab = (await f.Shell.OpenFileAsync(path))!;
        var target = await ImageAttachmentImporter.InsertAsync(tab, ImageBytes(), f.Docs, "my image.png", tab.CurrentText.Length, tab.Session.Revision);
        Assert.Contains("![my image](my%20image.png)", tab.CurrentText, StringComparison.Ordinal);
        Assert.Equal("# Source\n", await File.ReadAllTextAsync(path));
        Assert.True(File.Exists(target));
        await Assert.ThrowsAsync<IOException>(() => ImageAttachmentImporter.InsertAsync(tab, ImageBytes(), f.Docs, "my image.png", 0, tab.Session.Revision));
        f.Window.EditorControl.Document.UndoStack.Undo();
        Assert.Equal("# Source\n", tab.CurrentText);
        Assert.True(File.Exists(target));
    }

    [AvaloniaFact]
    public async Task Image_import_refuses_stale_revision_and_paths_outside_the_document_folder()
    {
        using var f = new DesktopFixture();
        var tab = (await f.Shell.OpenFileAsync(await f.WriteAsync("nested/source.md", "source")))!;
        var revision = tab.Session.Revision;
        tab.ApplyEdits([TextEdit.Insert(0, "changed")]);
        await Assert.ThrowsAsync<IOException>(() => ImageAttachmentImporter.InsertAsync(tab, ImageBytes(), Path.GetDirectoryName(tab.FilePath!)!, "stale.png", 0, revision));
        await Assert.ThrowsAsync<IOException>(() => ImageAttachmentImporter.InsertAsync(tab, ImageBytes(), f.Docs, "escape.png", 0, tab.Session.Revision));
        Assert.False(File.Exists(Path.Combine(f.Docs, "escape.png")));
    }

    [AvaloniaFact]
    public async Task Failed_source_transaction_retains_the_attachment_and_reports_its_path()
    {
        using var f = new DesktopFixture();
        var tab = (await f.Shell.OpenFileAsync(await f.WriteAsync("source.md", "unchanged")))!;
        tab.Session.AttachBuffer(new RejectingBuffer("unchanged"));
        var bytes = ImageBytes();
        var error = await Assert.ThrowsAsync<IOException>(() => ImageAttachmentImporter.InsertAsync(tab, bytes, f.Docs, "rollback.png", 0, tab.Session.Revision));
        var attachment = Path.Combine(f.Docs, "rollback.png");
        Assert.Contains(attachment, error.Message, StringComparison.Ordinal);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(attachment));
        Assert.Empty(Directory.GetFiles(f.Docs, ".tern-image-*.tmp"));
        Assert.Equal("unchanged", tab.CurrentText);
    }

    [AvaloniaFact]
    public async Task Failed_source_transaction_never_deletes_a_concurrent_attachment_replacement()
    {
        using var f = new DesktopFixture();
        var path = await f.WriteAsync("source.md", "unchanged");
        var tab = (await f.Shell.OpenFileAsync(path))!;
        var attachment = Path.Combine(f.Docs, "concurrent.png");
        byte[] replacement = [11, 22, 33, 44];
        tab.Session.AttachBuffer(new RejectingBuffer("unchanged", () => File.WriteAllBytes(attachment, replacement)));
        var error = await Assert.ThrowsAsync<IOException>(() => ImageAttachmentImporter.InsertAsync(tab, ImageBytes(), f.Docs, "concurrent.png", 0, tab.Session.Revision));
        Assert.Contains(attachment, error.Message, StringComparison.Ordinal);
        Assert.Equal(replacement, await File.ReadAllBytesAsync(attachment));
        Assert.Equal("unchanged", tab.CurrentText);
        Assert.Equal("unchanged", await File.ReadAllTextAsync(path));
        Assert.Empty(Directory.GetFiles(f.Docs, ".tern-image-*.tmp"));
    }

    [AvaloniaFact]
    public async Task Image_import_rejects_corrupt_or_oversized_data_without_changes()
    {
        using var f = new DesktopFixture();
        var tab = (await f.Shell.OpenFileAsync(await f.WriteAsync("source.md", "unchanged")))!;
        await Assert.ThrowsAsync<IOException>(() => ImageAttachmentImporter.InsertAsync(tab, [1, 2, 3], f.Docs, "invalid.png", 0, tab.Session.Revision));
        await Assert.ThrowsAsync<IOException>(() => ImageAttachmentImporter.InsertAsync(tab, new byte[ImageAttachmentImporter.MaxBytes + 1], f.Docs, "large.png", 0, tab.Session.Revision));
        Assert.Equal("unchanged", tab.CurrentText);
        Assert.False(File.Exists(Path.Combine(f.Docs, "invalid.png")));
        Assert.False(File.Exists(Path.Combine(f.Docs, "large.png")));
    }

    [AvaloniaFact]
    public async Task Image_names_with_brackets_spaces_and_unicode_produce_one_valid_image_link()
    {
        using var f = new DesktopFixture();
        var tab = (await f.Shell.OpenFileAsync(await f.WriteAsync("source.md", string.Empty)))!;
        await ImageAttachmentImporter.InsertAsync(tab, ImageBytes(), f.Docs, "[diagram] café.png", 0, tab.Session.Revision);
        var analysis = Tern.Markdown.MarkdownAnalyzer.Analyze(tab.CurrentText, MarkdownProfile.Gfm);
        var image = Assert.Single(analysis.Links, link => link.Kind == Tern.Markdown.LinkKind.Image);
        Assert.Contains("%5Bdiagram%5D%20caf%C3%A9.png", image.Destination, StringComparison.OrdinalIgnoreCase);
    }

    [AvaloniaFact]
    public async Task Current_document_profile_changes_without_editing_source_or_default()
    {
        using var f = new DesktopFixture();
        var path = await f.WriteAsync("profile.md", "# Heading\n\n~~strike~~\n");
        var tab = (await f.Shell.OpenFileAsync(path))!;
        var revision = tab.Session.Revision;
        var defaultProfile = f.Shell.Settings.DefaultProfile;
        f.Window.StatusProfile.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        var picker = f.Window.Picker!;
        picker.Results.SelectedItem = picker.Results.Items.OfType<PickerItem>().Single(item => Equals(item.Payload, MarkdownProfile.CommonMark));
        picker.Input.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        await Settle();
        Assert.Equal(MarkdownProfile.CommonMark, tab.Profile);
        Assert.Equal(defaultProfile, f.Shell.Settings.DefaultProfile);
        Assert.Equal(revision, tab.Session.Revision);
        Assert.False(tab.IsDirty);
        Assert.Equal("# Heading\n\n~~strike~~\n", await File.ReadAllTextAsync(path));
    }

    [AvaloniaFact]
    public async Task Reading_mode_and_palette_dismissal_focus_the_visible_preview()
    {
        using var f = new DesktopFixture();
        f.Shell.NewDocument("# Read me\n");
        await f.Window.RunCommandAsync(CommandIds.ViewReading);
        await Settle();
        Assert.True(f.Window.PreviewControl.IsKeyboardFocusWithin);
        f.Window.ShowPalette();
        f.Window.HideOverlay();
        Assert.True(f.Window.PreviewControl.IsKeyboardFocusWithin);
    }

    [AvaloniaFact]
    public async Task Reading_scroll_anchor_survives_a_theme_rebuild()
    {
        using var f = new DesktopFixture();
        f.Shell.NewDocument(string.Join("\n\n", Enumerable.Range(1, 200).Select(i => $"Paragraph {i} with some content.")));
        await f.Window.RunCommandAsync(CommandIds.ViewReading);
        await f.Window.RebuildPreviewAsync();
        await Settle();
        var scroller = Assert.Single(f.Window.PreviewControl.GetVisualDescendants().OfType<ScrollViewer>());
        scroller.Offset = new Vector(0, 2000);
        await Settle();
        var line = f.Window.PreviewControl.TopSourceLine;
        Assert.True(line > 1);
        await f.Window.RunCommandAsync(CommandIds.ToggleTheme);
        await f.Window.RebuildPreviewAsync();
        await Settle();
        Assert.InRange(f.Window.PreviewControl.TopSourceLine, line - 2, line + 2);
    }

    private sealed class RejectingBuffer(string text, Action? beforeReject = null) : IDocumentBuffer
    {
        public event EventHandler<BufferChangedEventArgs>? Changed { add { } remove { } }
        public string GetText() => text;
        public void Apply(IReadOnlyList<TextEdit> edits)
        {
            beforeReject?.Invoke();
            throw new InvalidOperationException("Synthetic transaction failure");
        }
    }

    [AvaloniaFact]
    public void Theme_text_and_semantic_state_pairs_have_sufficient_contrast()
    {
        foreach (var resources in new[] { ThemeManager.Midnight(), ThemeManager.Paper() })
        {
            foreach (var pair in new[] { (ThemeManager.SyntaxMarker, ThemeManager.Surface), (ThemeManager.Accent, ThemeManager.AccentSoft), (ThemeManager.OnDanger, ThemeManager.Danger) })
            {
                var a = ((Avalonia.Media.SolidColorBrush)resources[pair.Item1]!).Color;
                var b = ((Avalonia.Media.SolidColorBrush)resources[pair.Item2]!).Color;
                var lighter = Math.Max(Luminance(a), Luminance(b));
                var darker = Math.Min(Luminance(a), Luminance(b));
                Assert.True((lighter + .05) / (darker + .05) >= 4.5, $"{pair.Item1}/{pair.Item2} fails text contrast");
            }
        }
    }

    private static double Luminance(Avalonia.Media.Color color)
    {
        static double Channel(byte value)
        {
            var s = value / 255.0;
            return s <= .04045 ? s / 12.92 : Math.Pow((s + .055) / 1.055, 2.4);
        }
        return .2126 * Channel(color.R) + .7152 * Channel(color.G) + .0722 * Channel(color.B);
    }

    [AvaloniaFact]
    public void Empty_untitled_document_does_not_claim_it_was_saved()
    {
        using var f = new DesktopFixture();
        f.Shell.NewDocument();
        Assert.Equal("Not saved to file", f.Window.StatusSaved.Text);
    }

    [AvaloniaFact]
    public async Task Manual_merge_text_survives_cancelling_a_conflict_choice_rebuild()
    {
        using var f = new DesktopFixture();
        var merge = Tern.Domain.Merging.ThreeWayMerger.Merge("base\n", "mine\n", "disk\n");
        var dialog = new MergeWindow("source.md", merge);
        dialog.Show();
        var result = dialog.GetVisualDescendants().OfType<TextBox>().Single(t => AutomationProperties.GetName(t) == "Merged result");
        result.Text = "manual result\n";
        var button = dialog.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Take disk"));
        button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        await Settle();
        Assert.Single(dialog.OwnedWindows.OfType<PromptWindow>()).Close(1);
        await Settle();
        Assert.Equal("manual result\n", result.Text);
        dialog.Close();
    }
}
