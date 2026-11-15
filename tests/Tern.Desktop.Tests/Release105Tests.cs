using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tern.App.Commands;
using Tern.App.Help;
using Tern.App.Settings;
using Tern.Desktop.Services;
using Tern.Desktop.Views;
using Tern.Export;
using Tern.Markdown;

namespace Tern.Desktop.Tests;

public sealed class Release105Tests
{
    [AvaloniaFact]
    public async Task Range_changes_replace_stale_status_and_preview_only_the_selected_original_pages()
    {
        using var f = new DesktopFixture();
        var markdown = string.Join("\n\n", Enumerable.Range(1, 120).Select(n => $"## Section {n}\n\nOriginal content for section {n}. Keep this paragraph and its page number unchanged when selecting printable pages.\n\n- [x] Reviewed\n- [ ] Follow up"));
        var window = new PrintPreviewWindow("range-review.md", markdown, ExportDefaults.PreviewOptions);
        window.OpenPdf = _ => null; window.Show(f.Window); await window.Preparation; Pump(window);
        try
        {
            Assert.True(window.PageCount >= 11);
            var radios = window.GetVisualDescendants().OfType<RadioButton>().ToArray();
            var number = Assert.Single(window.GetVisualDescendants().OfType<NumericUpDown>());
            var status = window.GetVisualDescendants().OfType<TextBlock>().Single(t => AutomationProperties.GetName(t) == "Print preview status");
            await window.ShowPageAsync(9);
            radios.Single(b => Equals(b.Content, "Current page")).IsChecked = true;
            Assert.True(await window.OpenPrintablePdfAsync(Path.Combine(f.Docs, "old-current.pdf")));
            Assert.Contains("Opened 1 selected page", status.Text);
            radios.Single(b => Equals(b.Content, "Pages")).IsChecked = true;
            window.PageRanges.Text = "1-3,5";
            Assert.Null(window.OutputPath); Assert.DoesNotContain("Opened", status.Text);
            await window.Navigation; Pump(window);
            Assert.Equal(1, window.DisplayedPage); Assert.Equal(4, number.Maximum);
            var next = window.GetVisualDescendants().OfType<Button>().Single(b => AutomationProperties.GetName(b) == "Next preview page");
            foreach (var page in new[] { 2, 3, 5 })
            {
                next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await window.Navigation; Pump(window);
                Assert.Equal(page, window.DisplayedPage);
            }
            Assert.False(next.IsEnabled);
            Capture(window, "print-selected-range");
            foreach (var (range, count) in new[] { ("1-3,5", 4), ("11,2-4,7,7,1-2", 6), ("1–3, 5", 4), ("2", 1) })
            {
                window.PageRanges.Text = range; await window.Navigation; Pump(window);
                Assert.True(PrintPageSelection.TryParse(range, window.PageCount, out var expected, out _));
                Assert.Contains(window.DisplayedPage, expected);
                var output = Path.Combine(f.Docs, "range-" + count + ".pdf");
                Assert.True(await window.OpenPrintablePdfAsync(output));
                using var pdf = await WindowsPdfPreview.LoadAsync(await File.ReadAllBytesAsync(output));
                Assert.Equal(count, pdf.PageCount);
            }
            window.PageRanges.Text = "0, 99999";
            Assert.Null(window.OutputPath); Assert.False(await window.OpenPrintablePdfAsync(Path.Combine(f.Docs, "invalid-range.pdf")));
            Assert.DoesNotContain("Opened", status.Text);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Changing_ranges_during_PDF_preparation_never_opens_an_obsolete_selection()
    {
        using var f = new DesktopFixture();
        var markdown = string.Join("\n\n", Enumerable.Range(1, 120).Select(n => "## Section " + n + "\n\nA paragraph for the print race regression."));
        var window = new PrintPreviewWindow("range-race.md", markdown, ExportDefaults.PreviewOptions);
        var launches = 0; window.OpenPdf = _ => { launches++; return null; };
        window.Show(f.Window); await window.Preparation; Pump(window);
        try
        {
            window.GetVisualDescendants().OfType<RadioButton>().Single(b => Equals(b.Content, "Pages")).IsChecked = true;
            window.PageRanges.Text = "1-3"; await window.Navigation;
            var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            window.ExtractPages = async (data, pages) => { await resume.Task; return PrintPageSelection.Extract(data, pages); };
            var target = Path.Combine(f.Docs, "obsolete.pdf"); var pending = window.OpenPrintablePdfAsync(target);
            window.PageRanges.Text = "1,3"; resume.SetResult();
            Assert.False(await pending); Assert.Equal(0, launches); Assert.False(File.Exists(target)); Assert.Null(window.OutputPath);
            window.ExtractPages = (data, pages) => Task.FromResult(PrintPageSelection.Extract(data, pages));
            Assert.True(await window.OpenPrintablePdfAsync(target)); Assert.Equal(1, launches);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Duplicate_image_prompt_names_the_file_and_requires_an_explicit_replace()
    {
        using var f = new DesktopFixture();
        var tab = (await f.Shell.OpenFileAsync(await f.WriteAsync("note.md", "Keep")))!;
        var bytes = GuiRemainingFeatureTests.ImageBytes();
        var sourcePath = await f.WriteAsync("external/source.png", ""); await File.WriteAllBytesAsync(sourcePath, bytes);
        var target = await f.WriteAsync("assets/diagram image.png", "original image bytes");
        var source = Assert.IsAssignableFrom<IStorageFile>(await f.Window.StorageProvider.TryGetFileFromPathAsync(new Uri(sourcePath)));
        var destination = Assert.IsAssignableFrom<IStorageFile>(await f.Window.StorageProvider.TryGetFileFromPathAsync(new Uri(target)));
        f.Window.ImageFilePicker = _ => Task.FromResult<IReadOnlyList<IStorageFile>>([source]);
        f.Window.ImageImportChoicePicker = _ => Task.FromResult(ImageImportChoice.Copy);
        f.Window.ImageAttachmentPicker = _ => Task.FromResult<IStorageFile?>(destination);
        await f.Window.Clipboard!.SetTextAsync("");
        async Task<(Task Pending, PromptWindow Prompt)> Start()
        {
            var pending = f.Window.RunCommandAsync(CommandIds.InsertImage);
            for (var attempt = 0; attempt < 100 && !f.Window.OwnedWindows.OfType<PromptWindow>().Any(); attempt++)
            { await Task.Delay(10); Pump(f.Window); }
            return (pending, Assert.Single(f.Window.OwnedWindows.OfType<PromptWindow>()));
        }
        var flow = await Start();
        Assert.Equal("Replace image file?", flow.Prompt.Title);
        var message = Assert.Single(flow.Prompt.GetVisualDescendants().OfType<SelectableTextBlock>()).Text!;
        Assert.Contains(Path.GetFileName(target), message);
        var shownPath = Assert.Single(message.Split('\n'), line => line.StartsWith("Destination: ", StringComparison.Ordinal))["Destination: ".Length..].Trim();
        Assert.True(Tern.Workspaces.WorkspacePaths.PathsEqual(target, shownPath), message);
        var cancel = flow.Prompt.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Cancel"));
        Assert.True(cancel.IsDefault); Capture(flow.Prompt, "replace-image");
        cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await flow.Pending;
        Assert.Equal("original image bytes", await File.ReadAllTextAsync(target)); Assert.Equal("Keep", tab.CurrentText);
        flow = await Start();
        flow.Prompt.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Replace file")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await flow.Pending;
        Assert.Equal(bytes, await File.ReadAllBytesAsync(target)); Assert.Contains("assets/diagram%20image.png", tab.CurrentText);
    }

    [AvaloniaTheory]
    [InlineData(8d)]
    [InlineData(16d)]
    [InlineData(40d)]
    public async Task Task_markers_keep_full_borders_and_focus_space_at_supported_font_sizes(double size)
    {
        using var f = new DesktopFixture();
        f.Shell.Settings.PreviewFontSize = size; f.Window.ApplySettings();
        var tab = f.Shell.NewDocument("- [ ] Unchecked task\r\n- [x] Checked task\r\n  - Nested item\r\n");
        await f.Window.RunCommandAsync(CommandIds.ViewReading); await f.Window.RebuildPreviewAsync(); Pump(f.Window);
        var boxes = f.Window.PreviewControl.GetVisualDescendants().OfType<CheckBox>().ToArray();
        Assert.Equal(2, boxes.Length);
        Assert.Contains(boxes, box => box.IsChecked == true); Assert.Contains(boxes, box => box.IsChecked == false);
        Assert.All(boxes, box => { Assert.True(box.Bounds.Width >= 20); Assert.True(box.Bounds.Height >= 20); });
        Capture(f.Window, "checkbox-font-" + size);
        Assert.Equal("- [ ] Unchecked task\r\n- [x] Checked task\r\n  - Nested item\r\n", tab.CurrentText);
    }

    [AvaloniaFact]
    public async Task Metadata_uses_current_text_preserves_other_fields_and_applies_before_saving()
    {
        using var f = new DesktopFixture();
        var path = await f.WriteAsync("metadata.md", "---\r\ntitle: Launch\r\nowner: Team\r\ntags: [old]\r\n---\r\n\r\n# Plan\r\n");
        var tab = (await f.Shell.OpenFileAsync(path))!;
        async Task<Window> Open()
        {
            f.Window.EditTagsButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            for (var attempt = 0; attempt < 100 && !f.Window.OwnedWindows.Any(w => w.Title == "Edit note metadata"); attempt++)
            { await Task.Delay(10); Pump(f.Window); }
            return Assert.Single(f.Window.OwnedWindows, w => w.Title == "Edit note metadata");
        }
        var dialog = await Open(); Assert.False(dialog.CanResize);
        var input = Assert.Single(dialog.GetVisualDescendants().OfType<TextBox>());
        input.Text = "work, planning"; Pump(dialog); Capture(dialog, "metadata-tags");
        dialog.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Apply tags")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Task.Delay(10); Pump(f.Window);
        Assert.Contains("owner: Team", tab.CurrentText); Assert.Contains("title: Launch", tab.CurrentText);
        Assert.Contains("work", tab.CurrentText); Assert.Contains("planning", tab.CurrentText);
        Assert.Contains("tags: [old]", await File.ReadAllTextAsync(path));
        Assert.True(await f.Shell.SaveTabAsync(tab));
        Assert.Equal(tab.CurrentText, await File.ReadAllTextAsync(path));
        dialog = await Open();
        dialog.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Clear tags")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        dialog.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Apply tags")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Task.Delay(10); Pump(f.Window);
        Assert.Contains("tags: []", tab.CurrentText); Assert.Contains("owner: Team", tab.CurrentText);
        Assert.Contains("planning", await File.ReadAllTextAsync(path));
        // Open immediately after replacing front matter, before a background
        // inspector analysis is guaranteed to have caught up.
        tab.ApplyEdits([new Tern.Domain.Text.TextEdit(0, tab.CurrentText.Length, "# Replacement\r\n")]);
        dialog = await Open();
        input = Assert.Single(dialog.GetVisualDescendants().OfType<TextBox>());
        Assert.True(string.IsNullOrEmpty(input.Text)); input.Text = "fresh"; Pump(dialog);
        dialog.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Apply tags")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Task.Delay(10); Pump(f.Window);
        Assert.Contains("# Replacement", tab.CurrentText); Assert.Contains("fresh", tab.CurrentText);
        Assert.DoesNotContain("owner: Team", tab.CurrentText);
    }

    [AvaloniaFact]
    public void Installed_105_tutorial_displays_a_local_screenshot_without_a_connection()
    {
        using var f = new DesktopFixture();
        var guide = new HelpWindow(f.Shell.Registry, "tutorial.1.0.5.0"); guide.Show(f.Window); Pump(guide);
        try
        {
            var screenshot = guide.GetVisualDescendants().OfType<Expander>().Single(e => Equals(e.Header, "Screenshot · Tern 1.0.5"));
            Assert.True(screenshot.IsVisible); screenshot.IsExpanded = true; Pump(guide);
            Assert.NotNull(Assert.IsType<Image>(screenshot.Content).Source);
            Capture(guide, "installed-illustrated-guide");
        }
        finally { guide.Close(); }
    }

    [AvaloniaFact]
    public async Task One_complete_document_keeps_its_content_and_selected_PDF_pages_match_original_pixels()
    {
        using var f = new DesktopFixture();
        using var pixels = new SkiaSharp.SKBitmap(320, 100);
        using (var canvas = new SkiaSharp.SKCanvas(pixels))
        using (var boxes = new SkiaSharp.SKPaint { Color = SkiaSharp.SKColors.Teal, IsAntialias = true })
        using (var arrow = new SkiaSharp.SKPaint { Color = SkiaSharp.SKColors.DarkSlateGray, StrokeWidth = 3, IsAntialias = true })
        using (var text = new SkiaSharp.SKPaint { Color = SkiaSharp.SKColors.White, IsAntialias = true })
        using (var font = new SkiaSharp.SKFont(SkiaSharp.SKTypeface.Default, 18))
        {
            canvas.Clear(SkiaSharp.SKColors.White);
            canvas.DrawRoundRect(new SkiaSharp.SKRect(8, 20, 138, 80), 8, 8, boxes);
            canvas.DrawRoundRect(new SkiaSharp.SKRect(182, 20, 312, 80), 8, 8, boxes);
            canvas.DrawLine(146, 50, 174, 50, arrow); canvas.DrawLine(168, 44, 174, 50, arrow); canvas.DrawLine(168, 56, 174, 50, arrow);
            canvas.DrawText("Source", 39, 57, font, text); canvas.DrawText("Reading", 210, 57, font, text);
        }
        using var image = SkiaSharp.SKImage.FromBitmap(pixels);
        using var encoded = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        await File.WriteAllBytesAsync(Path.Combine(f.Docs, "diagram.png"), encoded.ToArray());
        var markdown = "# Release review\r\n\r\n**Ready** and *pending* items with [a link](https://example.com).\r\n\r\n"
            + "- [x] Checked task\r\n- [ ] Unchecked task\r\n  - Nested item\r\n  - [x] Nested checked task\r\n\r\n"
            + "| Item | Status |\r\n|---|---|\r\n| Design | Ready |\r\n| Review | Pending |\r\n\r\n"
            + "![Architecture diagram](diagram.png)\r\n\r\n"
            + string.Join("\r\n\r\n", Enumerable.Range(1, 45).Select(n => $"## Review step {n}\r\n\r\nKeep the same content, order and meaning in every view. **Verify** this section before release.\r\n\r\n- [ ] Follow up {n}\r\n- [x] Reviewed {n}"));
        var path = await f.WriteAsync("complete-review.md", markdown);
        await f.Shell.OpenFileAsync(path);
        await f.Window.RunCommandAsync(CommandIds.ViewSource); Capture(f.Window, "complete-source");
        await f.Window.RunCommandAsync(CommandIds.ViewReading); await f.Window.RebuildPreviewAsync(); Pump(f.Window);
        Capture(f.Window, "complete-reading");
        Assert.Equal(markdown, f.Shell.ActiveTab!.CurrentText);
        var options = new Tern.Rendering.Preview.PreviewOptions { DocumentPath = path };
        var html = HtmlExporter.Export(markdown, new() { Preview = options });
        Assert.Contains("<table", html.Html); Assert.Contains("data:image/png;base64,", html.Html);
        Assert.Contains("Checked task", html.Html); Assert.Contains("Unchecked task", html.Html);
        var window = new PrintPreviewWindow("complete-review.md", markdown, options);
        window.OpenPdf = _ => null; window.Show(f.Window); await window.Preparation; Pump(window);
        try
        {
            Assert.True(window.PageCount >= 5);
            Capture(window, "complete-print-preview");
            var original = Path.Combine(f.Docs, "all.pdf"); Assert.True(await window.OpenPrintablePdfAsync(original));
            using var all = await WindowsPdfPreview.LoadAsync(await File.ReadAllBytesAsync(original));
            var pageImage = Assert.Single(window.GetVisualDescendants().OfType<Image>());
            Assert.InRange(Math.Abs(pageImage.Width - all.PageSize(1).Width), 0, .1);
            await window.ShowPageAsync(2);
            window.GetVisualDescendants().OfType<RadioButton>().Single(b => Equals(b.Content, "Current page")).IsChecked = true;
            var current = Path.Combine(f.Docs, "current.pdf"); Assert.True(await window.OpenPrintablePdfAsync(current));
            using var single = await WindowsPdfPreview.LoadAsync(await File.ReadAllBytesAsync(current));
            Assert.Equal(1, single.PageCount);
            Assert.Equal(await all.RenderAsync(2, 800), await single.RenderAsync(1, 800));
            window.GetVisualDescendants().OfType<RadioButton>().Single(b => Equals(b.Content, "Pages")).IsChecked = true;
            window.PageRanges.Text = "1-3, 5"; Pump(window);
            var subset = Path.Combine(f.Docs, "subset.pdf"); Assert.True(await window.OpenPrintablePdfAsync(subset));
            using var selected = await WindowsPdfPreview.LoadAsync(await File.ReadAllBytesAsync(subset));
            Assert.Equal(4, selected.PageCount);
            for (var i = 0; i < 4; i++) Assert.Equal(await all.RenderAsync(new[] { 1, 2, 3, 5 }[i], 800), await selected.RenderAsync(i + 1, 800));
            window.PageRanges.Text = "0, 99999"; Pump(window);
            Assert.False(await window.OpenPrintablePdfAsync(Path.Combine(f.Docs, "invalid.pdf")));
            Assert.False(File.Exists(Path.Combine(f.Docs, "invalid.pdf")));
            var folder = Path.Combine(Screenshots.Folder, "v1.0.5");
            await File.WriteAllTextAsync(Path.Combine(folder, "complete-document.md"), markdown);
            await File.WriteAllTextAsync(Path.Combine(folder, "complete-document.html"), html.Html);
            File.Copy(Path.Combine(f.Docs, "diagram.png"), Path.Combine(folder, "diagram.png"), true);
            File.Copy(original, Path.Combine(folder, "complete-document.pdf"), true);
            await File.WriteAllBytesAsync(Path.Combine(folder, "complete-printed-page-1.png"), await all.RenderAsync(1, 1200));
            Assert.Equal(markdown, await File.ReadAllTextAsync(path));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Existing_attachment_requires_confirmation_and_rechecks_after_the_choice()
    {
        using var f = new DesktopFixture();
        var path = await f.WriteAsync("attachment review.md", "# Review");
        var tab = (await f.Shell.OpenFileAsync(path))!;
        using var pixels = new SkiaSharp.SKBitmap(2, 2); pixels.Erase(SkiaSharp.SKColors.Red);
        using var image = SkiaSharp.SKImage.FromBitmap(pixels);
        using var encoded = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        var bytes = encoded.ToArray(); var target = Path.Combine(f.Docs, "existing image.png");
        await File.WriteAllTextAsync(target, "original bytes");
        await Assert.ThrowsAsync<IOException>(() => ImageAttachmentImporter.InsertAsync(tab, bytes, f.Docs, "existing image.png", 0, tab.Session.Revision,
            confirmReplace: file => { Assert.Equal(target, file); return Task.FromResult(false); }));
        Assert.Equal("original bytes", await File.ReadAllTextAsync(target)); Assert.Equal("# Review", tab.CurrentText);
        await Assert.ThrowsAsync<IOException>(() => ImageAttachmentImporter.InsertAsync(tab, bytes, f.Docs, "existing image.png", 0, tab.Session.Revision,
            confirmReplace: async _ => { await File.WriteAllTextAsync(target, "changed by another writer"); return true; }));
        Assert.Equal("changed by another writer", await File.ReadAllTextAsync(target));
        await ImageAttachmentImporter.InsertAsync(tab, bytes, f.Docs, "existing image.png", 0, tab.Session.Revision, confirmReplace: _ => Task.FromResult(true));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(target)); Assert.Contains("existing%20image.png", tab.CurrentText);
    }

    [AvaloniaFact]
    public async Task Dismissal_is_persistent_on_every_surface_and_replay_does_not_reset_it()
    {
        using var f = new DesktopFixture();
        f.Shell.NewDocument(); f.Window.ShowFirstUse(); Pump(f.Window);
        var dismiss = f.Window.EmptyText.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Hide Quick Start tips"));
        dismiss.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(f.Window);
        Assert.True(new SettingsStore(f.Shell.Paths.SettingsDirectory).Load().OnboardingDismissed);
        await f.Window.RunCommandAsync(CommandIds.Welcome); Pump(f.Window);
        Assert.True(new SettingsStore(f.Shell.Paths.SettingsDirectory).Load().OnboardingDismissed);
        var overlay = f.Window.OverlayLayer;
        Capture(f.Window, "help-replay");
        overlay.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Hide Quick Start tips")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.False(overlay.IsVisible);
        Assert.False(f.Window.FirstNoteChecklist.IsVisible);
        f.Window.ShowFirstUse();
        Assert.False(f.Window.EmptyText.IsVisible);
        Assert.True(await f.Shell.CloseTabAsync(f.Shell.ActiveTab!)); Pump(f.Window);
        Assert.True(f.Window.EmptyText.IsVisible);
        foreach (var label in new[] { "New note", "Open files…", "Open folder…", "Try a sample note", "Starter templates", "Open guide", "Tern Quick Start…" })
            Assert.Contains(f.Window.EmptyText.GetVisualDescendants().OfType<Button>(), button => Equals(button.Content, label) && button.IsEffectivelyVisible);
        Assert.DoesNotContain(f.Window.EmptyText.GetVisualDescendants().OfType<Button>(), button => Equals(button.Content, "Hide Quick Start tips") && button.IsEffectivelyVisible);
        Capture(f.Window, "empty-workspace-dismissed");
        f.Window.EmptyText.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Tern Quick Start…")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.True(f.Window.OverlayLayer.IsVisible);
        Assert.True(new SettingsStore(f.Shell.Paths.SettingsDirectory).Load().OnboardingDismissed);
    }

    [AvaloniaFact]
    public void Template_catalog_is_shared_complete_and_has_an_explicit_action()
    {
        using var f = new DesktopFixture();
        var window = new StarterTemplatesWindow(); window.Show(f.Window); Pump(window);
        try
        {
            Assert.Equal(10, window.Catalog.ItemCount);
            Assert.Contains(StarterTemplates.All, t => t.Name == "Architecture Decision Record");
            Assert.All(StarterTemplates.All, t => Assert.StartsWith("# ", t.Markdown));
            Assert.True(window.UseTemplate.IsEnabled);
            Capture(window, "starter-templates");
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Every_starter_template_and_sample_uses_CRLF_from_the_shared_entry_point()
    {
        using var f = new DesktopFixture(); f.Shell.NewDocument(); f.Window.ShowFirstUse(); Pump(f.Window);
        for (var i = 0; i < StarterTemplates.All.Count; i++)
        {
            if (i > 0) await f.Window.RunCommandAsync(CommandIds.Welcome);
            var surface = i == 0 ? (Control)f.Window.EmptyText : f.Window.OverlayLayer;
            surface.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Starter templates")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(f.Window);
            var catalog = Assert.Single(f.Window.OwnedWindows.OfType<StarterTemplatesWindow>());
            catalog.Catalog.SelectedIndex = i; Pump(catalog);
            catalog.UseTemplate.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(f.Window);
            Assert.Equal(StarterTemplates.All[i].Markdown.ReplaceLineEndings("\r\n"), f.Shell.ActiveTab!.CurrentText);
            Assert.Equal(Tern.Domain.Text.LineEnding.CrLf, f.Shell.ActiveTab.Session.Format.DominantLineEnding);
        }
        await f.Window.RunCommandAsync(CommandIds.Welcome);
        f.Window.OverlayLayer.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Try a sample note")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump(f.Window);
        Assert.Equal(Tern.Domain.Text.LineEnding.CrLf, f.Shell.ActiveTab!.Session.Format.DominantLineEnding);
        Assert.DoesNotContain("\n", f.Shell.ActiveTab.CurrentText.Replace("\r\n", ""));
    }

    [AvaloniaFact]
    public async Task An_unsaved_image_requires_confirmation_before_any_picker_and_save_failure_stops()
    {
        using var f = new DesktopFixture(); f.Shell.NewDocument("# Draft");
        var picks = 0;
        f.Window.ImageFilePicker = _ => { picks++; return Task.FromResult<IReadOnlyList<IStorageFile>>([]); };
        var pending = f.Window.RunCommandAsync(CommandIds.InsertImage); Pump(f.Window);
        var prompt = Assert.Single(f.Window.OwnedWindows.OfType<PromptWindow>());
        Assert.Equal(0, picks);
        Capture(prompt, "save-before-image");
        prompt.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Cancel")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await pending; Assert.Equal(0, picks);
        pending = f.Window.RunCommandAsync(CommandIds.InsertImage); Pump(f.Window);
        prompt = Assert.Single(f.Window.OwnedWindows.OfType<PromptWindow>());
        prompt.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Save note")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await pending; Assert.Equal(0, picks); Assert.Null(f.Shell.ActiveTab!.FilePath);
    }

    [AvaloniaFact]
    public void Link_form_exposes_types_and_blocks_invalid_destinations()
    {
        using var f = new DesktopFixture(); var window = new InsertLinkWindow("", null, []); window.Show(f.Window); Pump(window);
        try
        {
            window.DestinationInput.Text = "javascript:alert(1)"; Assert.False(window.InsertButton.IsEnabled);
            window.DestinationInput.Text = "https://example.com"; Pump(window); Assert.True(window.InsertButton.IsEnabled);
            Capture(window, "insert-link-web");
            window.GetVisualDescendants().OfType<RadioButton>().Single(b => Equals(b.Content, "Local file")).IsChecked = true;
            Assert.False(window.InsertButton.IsEnabled);
            Capture(window, "insert-link-save-required");
            window.GetVisualDescendants().OfType<RadioButton>().Single(b => Equals(b.Content, "Heading in this note")).IsChecked = true;
            Assert.False(window.InsertButton.IsEnabled);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void No_connection_notice_precedes_submission_and_toolbar_does_not_offer_AI()
    {
        using var f = new DesktopFixture();
        Assert.DoesNotContain(f.Window.AuthoringToolbar.GetVisualDescendants().OfType<Button>(), b => Equals(b.Content, "AI"));
        using var tutor = new HelpTutorView(f.Window.AIWorkspace, f.Shell.Registry, () => Task.CompletedTask, null, null, _ => { }, null);
        var window = new Window { Content = tutor, Width = 940, Height = 820 }; window.Show(f.Window); Pump(window);
        try
        {
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), b => b.Text?.Contains("No AI connection is configured") == true);
            Assert.Equal("Find guide steps", tutor.SendButton.Content);
            Assert.False(tutor.SendButton.IsEnabled);
            Capture(window, "ask-and-learn-offline");
        }
        finally { window.Close(); }
    }

    [Fact]
    public void Explorer_commands_quote_both_paths_and_do_not_take_the_default()
    {
        var entries = WindowsFileAssociations.Registration(@"C:\Tools with spaces\Tern.exe");
        Assert.Equal("\"C:\\Tools with spaces\\Tern.exe\" \"%1\"", entries[@"Software\Classes\Tern.Markdown\shell\open\command|"]);
        Assert.DoesNotContain(entries.Keys, k => k.Contains("UserChoice") || k == @"Software\Classes\.md|");
    }

    private static void Pump(Window w) { Dispatcher.UIThread.RunJobs(); w.UpdateLayout(); Dispatcher.UIThread.RunJobs(); w.UpdateLayout(); }
    private static void Capture(Window w, string name)
    {
        Pump(w); var folder = Path.Combine(Screenshots.Folder, "v1.0.5"); Directory.CreateDirectory(folder);
        using var image = w.CaptureRenderedFrame(); Assert.NotNull(image);
        using var output = File.Create(Path.Combine(folder, name + ".png")); image.Save(output);
    }
}
