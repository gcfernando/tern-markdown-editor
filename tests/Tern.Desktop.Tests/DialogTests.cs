using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PdfSharp.Pdf.IO;
using Tern.App.Settings;
using Tern.Desktop.Views;
using Tern.Domain.Merging;
using Tern.Domain.Text;
using Tern.Filesystem;
using Tern.Markdown;

namespace Tern.Desktop.Tests;

public class DialogTests
{
    private const string ExportSample = "# Export Title\n\nSome **bold** text and a [site](https://example.com).\n\n| a | b |\n|---|---|\n| 1 | 2 |\n\n```csharp\nvar x = 1;\n```\n\n<script>alert('x')</script>\n";

    private static async Task Settle()
    {
        await Task.Delay(300);
        Dispatcher.UIThread.RunJobs();
    }

    private static T Find<T>(Visual root, string automationName) where T : Control =>
        root.GetVisualDescendants().OfType<T>().FirstOrDefault(c => AutomationProperties.GetName(c) == automationName)
        ?? throw new InvalidOperationException($"No {typeof(T).Name} named '{automationName}'.");

    private static string TempFile(string name)
    {
        var folder = Path.Combine(Path.GetTempPath(), "tern-desktop-tests", "out", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return Path.Combine(folder, name);
    }

    // ---- Settings -----------------------------------------------------------------------------------------------

    [AvaloniaFact]
    public async Task Settings_window_edits_the_settings_object()
    {
        var settings = new AppSettings();
        var window = new SettingsWindow(settings);
        window.Show();
        await Settle();

        var theme = Find<ComboBox>(window, "Theme");
        theme.SelectedItem = ThemeChoice.Paper;
        Assert.Equal(ThemeChoice.Paper, settings.Theme);

        var wrap = Find<CheckBox>(window, "Wrap long lines");
        var before = settings.WordWrap;
        wrap.IsChecked = !before;
        Assert.Equal(!before, settings.WordWrap);

        Find<NumericUpDown>(window, "Editor font size").Value = 19;
        Assert.Equal(19, settings.EditorFontSize);

        Find<NumericUpDown>(window, "Tab size").Value = 8;
        Assert.Equal(8, settings.TabSize);

        Find<ComboBox>(window, "Default profile (new files)").SelectedItem = MarkdownProfile.CommonMark;
        Assert.Equal(MarkdownProfile.CommonMark, settings.DefaultProfile);

        var external = Find<CheckBox>(window, "Allow opening links in other applications (asks first)");
        Assert.True(settings.AllowExternalLinkOpening); // on by default, and every open still asks first
        external.IsChecked = false;
        Assert.False(settings.AllowExternalLinkOpening);

        Find<Button>(window, "Close settings").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Assert.False(window.IsVisible);
    }

    [AvaloniaFact]
    public async Task Settings_window_ignores_a_blank_font_name_and_out_of_range_numbers()
    {
        var settings = new AppSettings();
        var originalFont = settings.EditorFontFamily;
        var window = new SettingsWindow(settings);
        window.Show();
        await Settle();

        var font = Find<TextBox>(window, "Editor font");
        font.Focus();
        font.Text = "   ";
        Find<Button>(window, "Close settings").Focus();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(originalFont, settings.EditorFontFamily);

        var size = Find<NumericUpDown>(window, "Editor font size");
        size.Value = 5000;
        Assert.InRange(settings.EditorFontSize, 8, 48);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Settings_changed_in_the_dialog_survive_a_restart()
    {
        using var f = new DesktopFixture();
        var window = new SettingsWindow(f.Shell.Settings);
        window.Show();
        await Settle();
        Find<ComboBox>(window, "Theme").SelectedItem = ThemeChoice.Paper;
        Find<NumericUpDown>(window, "Reading width").Value = 900;
        window.Close();
        f.Shell.SaveSettings();

        var reloaded = new SettingsStore(Path.Combine(f.AppData, "settings")).Load();
        Assert.Equal(ThemeChoice.Paper, reloaded.Theme);
        Assert.Equal(900, reloaded.ReadingWidth);
    }

    // ---- About --------------------------------------------------------------------------------------------------

    [AvaloniaFact]
    public async Task About_window_states_offline_use_and_lists_every_third_party_component()
    {
        var window = new AboutWindow();
        window.Show();
        await Settle();

        var details = Find<Expander>(window, "Third-party details");
        Assert.False(details.IsExpanded);
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Developed by Gehan Fernando");
        details.IsExpanded = true;
        await Settle();
        var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? string.Empty).ToList();
        Assert.Contains(texts, t => t.StartsWith("Version ", StringComparison.Ordinal) && t.Length > "Version ".Length);
        Assert.Contains(texts, t => t.Contains("Editing works offline", StringComparison.Ordinal)
            && t.Contains("local cache for offline reopening", StringComparison.Ordinal)
            && t.Contains("Other online image links", StringComparison.Ordinal));
        foreach (var (name, license) in AboutWindow.ThirdParty)
        {
            Assert.Contains(texts, t => t.Contains(name, StringComparison.Ordinal) && t.Contains(license, StringComparison.Ordinal));
        }

        Assert.Contains(AboutWindow.ThirdParty, t => t.Name == "PDFsharp" && t.License == "MIT");
        Find<Button>(window, "Close about").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Assert.False(window.IsVisible);
    }

    // ---- Encoding and line endings ------------------------------------------------------------------------------

    [AvaloniaFact]
    public async Task Encoding_picker_reopens_a_clean_file_with_the_chosen_encoding()
    {
        using var f = new DesktopFixture();
        var path = Path.Combine(f.Docs, "legacy.md");
        await File.WriteAllBytesAsync(path, [(byte)'c', (byte)'a', (byte)'f', 0xE9, (byte)'\n']);
        await f.Shell.OpenFileAsync(path);
        await Settle();

        f.Window.StatusEncoding.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Settle();
        var rows = f.Window.Picker!.Results.Items.OfType<PickerItem>().ToList();
        Assert.Equal(TextFileCodec.AvailableEncodings.Count, rows.Count);

        f.Window.Picker.Results.SelectedIndex = rows.FindIndex(r => r.Payload is EncodingOption { Kind: TextEncodingKind.Latin1, HasBom: false });
        f.Window.Picker.Input.RaiseEvent(new Avalonia.Input.KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Avalonia.Input.Key.Enter });
        await Settle();

        Assert.Contains("café", f.Shell.ActiveTab!.CurrentText, StringComparison.Ordinal);
        Assert.False(f.Window.IsOverlayOpen);
    }

    [AvaloniaFact]
    public async Task Encoding_picker_refuses_a_lossy_conversion_and_says_so()
    {
        using var f = new DesktopFixture();
        var path = await f.WriteAsync("emoji.md", "smile 😀\n");
        await f.Shell.OpenFileAsync(path);
        f.Window.EditorControl.Document.Insert(0, "x"); // dirty tabs convert instead of reopening
        await Settle();

        f.Window.StatusEncoding.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Settle();
        var rows = f.Window.Picker!.Results.Items.OfType<PickerItem>().ToList();
        f.Window.Picker.Results.SelectedIndex = rows.FindIndex(r => r.Payload is EncodingOption { Kind: TextEncodingKind.Latin1, HasBom: false });
        f.Window.Picker.Input.RaiseEvent(new Avalonia.Input.KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Avalonia.Input.Key.Enter });
        await Settle();

        Assert.Contains("cannot be saved", f.Shell.StatusMessage, StringComparison.Ordinal);
        Assert.Equal(TextEncodingKind.Utf8, f.Shell.ActiveTab!.Session.Format.Encoding);
    }

    [AvaloniaTheory]
    [InlineData(LineEnding.Lf, "\n")]
    [InlineData(LineEnding.CrLf, "\r\n")]
    [InlineData(LineEnding.Cr, "\r")]
    public async Task Line_ending_picker_converts_immediately_and_saves_the_selected_ending(LineEnding ending, string terminator)
    {
        using var f = new DesktopFixture();
        var path = await f.WriteAsync("a.md", "one\r\ntwo\n");
        await f.Shell.OpenFileAsync(path);
        await Settle();

        f.Window.StatusLineEnding.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Settle();
        var rows = f.Window.Picker!.Results.Items.OfType<PickerItem>().ToList();
        Assert.Equal(new[] { LineEnding.CrLf, LineEnding.Lf, LineEnding.Cr }, rows.Select(r => Assert.IsType<LineEnding>(r.Payload)));
        f.Window.Picker.Results.SelectedIndex = rows.FindIndex(r => (LineEnding)r.Payload! == ending);
        f.Window.Picker.Input.RaiseEvent(new Avalonia.Input.KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Avalonia.Input.Key.Enter });
        await Settle();

        var expected = $"one{terminator}two{terminator}";
        Assert.Equal(expected, f.Shell.ActiveTab!.CurrentText);
        Assert.Equal(ending, f.Shell.ActiveTab.Session.Format.DominantLineEnding);
        Assert.False(f.Shell.ActiveTab.Session.Format.HasMixedLineEndings);
        Assert.IsType<SaveSucceeded>(await f.Shell.ActiveTab.SaveAsync());
        Assert.Equal(expected, await File.ReadAllTextAsync(path));
    }

    // ---- Export (end to end, real files) ------------------------------------------------------------------------

    [AvaloniaFact]
    public async Task Export_to_html_writes_a_self_contained_escaped_page()
    {
        var output = TempFile("out.html");
        var window = new ExportWindow("doc.md", null, ExportSample, MarkdownProfile.Gfm, null, ExportFormat.Html);
        window.Show();
        await Settle();

        Assert.True(await window.ExportAsync(output));
        Assert.Contains("Exported to", window.StatusText, StringComparison.Ordinal);

        var html = await File.ReadAllTextAsync(output);
        Assert.StartsWith("<!DOCTYPE html", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Export Title", html, StringComparison.Ordinal);
        Assert.Contains("<table", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script>alert", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("src=\"http", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<link ", html, StringComparison.OrdinalIgnoreCase);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Export_to_pdf_writes_a_document_a_pdf_reader_can_open()
    {
        var output = TempFile("out.pdf");
        var window = new ExportWindow("doc.md", null, ExportSample, MarkdownProfile.Gfm, null, ExportFormat.Pdf);
        window.Show();

        Assert.True(await window.ExportAsync(output));
        var bytes = await File.ReadAllBytesAsync(output);
        Assert.True(bytes.Length > 1000);
        Assert.Equal("%PDF-", System.Text.Encoding.ASCII.GetString(bytes, 0, 5));

        using var stream = new MemoryStream(bytes);
        using var pdf = PdfReader.Open(stream, PdfDocumentOpenMode.Import);
        Assert.True(pdf.PageCount >= 1);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Export_to_text_writes_readable_plain_text()
    {
        var output = TempFile("out.txt");
        var window = new ExportWindow("doc.md", null, ExportSample, MarkdownProfile.Gfm, null, ExportFormat.Text);
        window.Show();

        Assert.True(await window.ExportAsync(output));
        var text = await File.ReadAllTextAsync(output);
        Assert.Contains("Export Title", text, StringComparison.Ordinal);
        Assert.Contains("bold", text, StringComparison.Ordinal);
        Assert.DoesNotContain("**", text, StringComparison.Ordinal);
        Assert.DoesNotContain("# Export", text, StringComparison.Ordinal);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Export_reports_a_missing_path_and_an_unwritable_target_without_throwing()
    {
        var window = new ExportWindow("doc.md", null, ExportSample, MarkdownProfile.Gfm, null, ExportFormat.Html);
        window.Show();

        Assert.False(await window.ExportAsync(" "));
        Assert.Contains("Choose an output file", window.StatusText, StringComparison.Ordinal);

        var folder = TempFile("blocked.html");
        Directory.CreateDirectory(folder);
        Assert.False(await window.ExportAsync(folder));
        Assert.Contains("Export failed", window.StatusText, StringComparison.Ordinal);

        var missingFolder = Path.Combine(Path.GetTempPath(), "tern-desktop-tests", "no-such-" + Guid.NewGuid().ToString("N"), "x.html");
        Assert.True(await window.ExportAsync(missingFolder));
        Assert.True(File.Exists(missingFolder));
        window.Close();
    }

    [AvaloniaFact]
    public async Task Export_overwrites_an_existing_file_atomically_and_leaves_no_temp_files()
    {
        var output = TempFile("again.html");
        await File.WriteAllTextAsync(output, "old");
        var window = new ExportWindow("doc.md", null, "# New\n", MarkdownProfile.Gfm, null, ExportFormat.Html);
        window.Show();

        Assert.True(await window.ExportAsync(output), window.StatusText);
        Assert.Contains("New", await File.ReadAllTextAsync(output), StringComparison.Ordinal);
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(output)!));
        window.Close();
    }

    // ---- Merge dialog -------------------------------------------------------------------------------------------

    private static Task<string?> ShowResult(MergeWindow window)
    {
        var owner = new Window { Width = 300, Height = 200 };
        owner.Show();
        return window.ShowDialog<string?>(owner);
    }

    private static MergeResult Conflicting() => ThreeWayMerger.Merge("one\ntwo\nthree\n", "one\nMINE\nthree\n", "one\nDISK\nthree\n");

    [AvaloniaFact]
    public async Task Merge_window_defaults_to_mine_and_accepts_the_merged_text()
    {
        var merge = Conflicting();
        Assert.Equal(1, merge.ConflictCount);
        var window = new MergeWindow("a.md", merge);
        var result = ShowResult(window);
        await Settle();

        Assert.Contains("MINE", Find<TextBox>(window, "Merged result").Text, StringComparison.Ordinal);
        Find<Button>(window, "Accept merged text").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal("one\nMINE\nthree\n", await result);
    }

    [AvaloniaFact]
    public async Task Merge_window_take_disk_both_and_original_change_the_result()
    {
        var window = new MergeWindow("a.md", Conflicting());
        window.Show();
        await Settle();
        var merged = Find<TextBox>(window, "Merged result");

        Find<Button>(window, "Take disk").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal("one\nDISK\nthree\n", merged.Text);
        Find<Button>(window, "Both").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal("one\nMINE\nDISK\nthree\n", merged.Text);
        Find<Button>(window, "Original").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal("one\ntwo\nthree\n", merged.Text);

        Assert.Equal("two\n", Find<TextBox>(window, "Common ancestor").Text);
        Assert.Equal("MINE\n", Find<TextBox>(window, "My version").Text);
        Assert.Equal("DISK\n", Find<TextBox>(window, "Version on disk").Text);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Merge_window_cancel_returns_null_and_clean_merges_hide_the_choices()
    {
        var window = new MergeWindow("a.md", Conflicting());
        var result = ShowResult(window);
        await Settle();
        Find<Button>(window, "Cancel merge").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Null(await result);

        var clean = ThreeWayMerger.Merge("a\nb\nc\n", "A\nb\nc\n", "a\nb\nC\n");
        Assert.False(clean.HasConflicts);
        var cleanWindow = new MergeWindow("a.md", clean);
        cleanWindow.Show();
        await Settle();
        Assert.Equal("A\nb\nC\n", Find<TextBox>(cleanWindow, "Merged result").Text);
        Assert.False(Find<ListBox>(cleanWindow, "Conflicts").IsVisible);
        cleanWindow.Close();
    }

    // ---- Command palette rendering ------------------------------------------------------------------------------

    [AvaloniaFact]
    public async Task Palette_input_is_painted_with_the_panel_colour_not_black()
    {
        using var f = new DesktopFixture();
        f.Window.ApplyTheme();
        f.Window.ShowPalette();
        await Settle();

        var input = f.Window.Picker!.Input;
        var origin = input.TranslatePoint(new Point(input.Bounds.Width - 20, input.Bounds.Height / 2), f.Window)!.Value;
        Dispatcher.UIThread.RunJobs();
        var frame = f.Window.CaptureRenderedFrame()!;
        var pixel = ReadPixel(frame, (int)origin.X, (int)origin.Y);

        var panel = (SolidColorBrush)Application.Current!.FindResource("Tern.Panel")!;
        Assert.InRange(pixel.R, panel.Color.R - 3, panel.Color.R + 3);
        Assert.InRange(pixel.G, panel.Color.G - 3, panel.Color.G + 3);
        Assert.InRange(pixel.B, panel.Color.B - 3, panel.Color.B + 3);
    }

    private static Color ReadPixel(Avalonia.Media.Imaging.WriteableBitmap bitmap, int x, int y)
    {
        using var buffer = bitmap.Lock();
        var scaleX = buffer.Size.Width / Math.Max(1, bitmap.PixelSize.Width);
        Assert.Equal(1, scaleX);
        var row = buffer.RowBytes * y + x * 4;
        var bytes = new byte[4];
        System.Runtime.InteropServices.Marshal.Copy(buffer.Address + row, bytes, 0, 4);
        return buffer.Format == Avalonia.Platform.PixelFormat.Bgra8888
            ? Color.FromArgb(bytes[3], bytes[2], bytes[1], bytes[0])
            : Color.FromArgb(bytes[3], bytes[0], bytes[1], bytes[2]);
    }
}
