using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PdfSharp.Pdf.IO;
using Tern.App.Settings;
using Tern.Desktop.Views;
using Tern.Export;
using Tern.Markdown;

namespace Tern.Desktop.Tests;

public sealed class ExportDefaultPreferencesTests
{
    [AvaloniaFact]
    public async Task Settings_export_defaults_persist_and_drive_actual_PDF_and_HTML_outputs()
    {
        using var f = new DesktopFixture();
        var settings = new SettingsWindow(f.Shell.Settings, commit: () =>
        {
            if (!f.Shell.SaveSettings()) throw new IOException("Could not save synthetic preferences.");
        });
        settings.Show(f.Window);
        try
        {
            Find<TextBox>(settings, "Find a setting").Text = "Export defaults";
            Pump(settings);
            Assert.Empty(settings.WorkingSettings.ExportPreferences.Formats);
            Find<ComboBox>(settings, "Default PDF paper size").SelectedItem = PdfPageSize.Letter;
            Find<NumericUpDown>(settings, "Default PDF margin in millimetres").Value = 27;
            Find<CheckBox>(settings, "Default PDF page numbers").IsChecked = false;
            Find<CheckBox>(settings, "Default PDF front matter properties").IsChecked = true;
            Find<ComboBox>(settings, "Export defaults format").SelectedItem = ExportFormat.Html;
            Pump(settings);
            Find<ComboBox>(settings, "Default HTML appearance").SelectedItem = ExportTheme.Dark;
            Find<TextBox>(settings, "Default HTML document language").Text = "sv";
            Find<CheckBox>(settings, "Default HTML front matter properties").IsChecked = true;
            Assert.Empty(f.Shell.Settings.ExportPreferences.Formats);
            Click(Find<Button>(settings, "Apply"));
            Assert.True(settings.IsVisible);
        }
        finally { await DiscardAndClose(settings); }

        var stored = new SettingsStore(f.Shell.Paths.SettingsDirectory).Load().ExportPreferences;
        Assert.Equal("Letter", stored.Formats["Pdf"].Paper);
        Assert.Equal(27, stored.Formats["Pdf"].Margin);
        Assert.False(stored.Formats["Pdf"].PageNumbers);
        Assert.Equal("Dark", stored.Formats["Html"].Theme);
        Assert.Equal("sv", stored.Formats["Html"].Language);
        var source = await f.WriteAsync("source.md", "---\nowner: Settings export check\n---\n# Report\n\nExact source text.\n");
        var original = await File.ReadAllBytesAsync(source);
        foreach (var kind in new[] { ExportFormat.Pdf, ExportFormat.Html })
        {
            var export = new ExportWindow("source.md", source, await File.ReadAllTextAsync(source),
                MarkdownProfile.Extended, f.Docs, kind, preferences: stored);
            export.Show(f.Window);
            try
            {
                Pump(export);
                var effective = export.CaptureOptions();
                Assert.True(effective.IncludeFrontMatter);
                if (kind == ExportFormat.Pdf)
                {
                    Assert.Equal("Letter", effective.Paper);
                    Assert.Equal(27, effective.Margin);
                    Assert.False(effective.PageNumbers);
                }
                else
                {
                    Assert.Equal("Dark", effective.Theme);
                    Assert.Equal("sv", effective.Language);
                }
                var output = Path.Combine(f.Docs, kind == ExportFormat.Pdf ? "copy.pdf" : "copy.html");
                Assert.True(await export.ExportAsync(output));
                if (kind == ExportFormat.Pdf)
                {
                    using var pdf = PdfReader.Open(output, PdfDocumentOpenMode.ReadOnly);
                    Assert.True(pdf.PageCount > 0);
                    Assert.InRange(pdf.Pages[0].Width.Point, 611.9, 612.1);
                    Assert.InRange(pdf.Pages[0].Height.Point, 791.9, 792.1);
                }
                else
                {
                    var html = await File.ReadAllTextAsync(output);
                    Assert.Contains("<html lang=\"sv\" data-theme=\"dark\">", html);
                    Assert.Contains("Settings export check", html);
                }
                Assert.Equal(original, await File.ReadAllBytesAsync(source));
            }
            finally { export.Close(); }
        }
    }

    [AvaloniaFact]
    public async Task Format_reset_and_folder_forgetting_are_staged_isolated_and_plain_text_has_no_ineffective_controls()
    {
        using var f = new DesktopFixture();
        var live = f.Shell.Settings;
        live.ExportPreferences.Formats["Pdf"] = new ExportPreset(Paper: "Letter", Margin: 26);
        live.ExportPreferences.Formats["Html"] = new ExportPreset(Theme: "Dark", Language: "sv");
        live.ExportPreferences.Formats["Text"] = new ExportPreset();
        live.ExportPreferences.WorkspaceFolders[f.Docs] = f.Docs;
        var file = await f.WriteAsync("keep.md", "Keep this file unchanged.\n");
        var settings = new SettingsWindow(live);
        settings.Show(f.Window);
        try
        {
            Find<TextBox>(settings, "Find a setting").Text = "Export defaults";
            Pump(settings);
            Click(Find<Button>(settings, "Reset selected export format defaults"));
            Assert.False(settings.WorkingSettings.ExportPreferences.Formats.ContainsKey("Pdf"));
            Assert.Equal("Letter", live.ExportPreferences.Formats["Pdf"].Paper);
            Assert.Equal("sv", settings.WorkingSettings.ExportPreferences.Formats["Html"].Language);
            Click(Find<Button>(settings, "Forget remembered export folders"));
            Assert.Empty(settings.WorkingSettings.ExportPreferences.WorkspaceFolders);
            Assert.Single(live.ExportPreferences.WorkspaceFolders);
            Assert.Equal("Keep this file unchanged.\n", await File.ReadAllTextAsync(file));
            Click(Find<Button>(settings, "Apply"));
            Assert.Empty(live.ExportPreferences.WorkspaceFolders);
            Assert.False(live.ExportPreferences.Formats.ContainsKey("Pdf"));
            Assert.Equal("sv", live.ExportPreferences.Formats["Html"].Language);
            Find<ComboBox>(settings, "Export defaults format").SelectedItem = ExportFormat.Text;
            Pump(settings);
            var editor = Find<Control>(settings, "Export defaults");
            Assert.Single(editor.GetVisualDescendants().OfType<ComboBox>());
            Assert.Empty(editor.GetVisualDescendants().OfType<CheckBox>());
            Assert.Empty(editor.GetVisualDescendants().OfType<NumericUpDown>());
            Find<ComboBox>(settings, "Export defaults format").SelectedItem = ExportFormat.Html;
            Pump(settings);
            Find<TextBox>(settings, "Default HTML document language").Text = "fr";
            Assert.Equal("fr", settings.WorkingSettings.ExportPreferences.Formats["Html"].Language);
            Assert.Equal("sv", live.ExportPreferences.Formats["Html"].Language);
            await DiscardAndClose(settings);
            Assert.Equal("sv", live.ExportPreferences.Formats["Html"].Language);
            Assert.Equal("Keep this file unchanged.\n", await File.ReadAllTextAsync(file));
        }
        finally { await DiscardAndClose(settings); }
    }

    [AvaloniaTheory]
    [InlineData(ThemeChoice.Paper)]
    [InlineData(ThemeChoice.Midnight)]
    public async Task Capture_actual_editable_export_defaults_in_each_theme(ThemeChoice theme)
    {
        using var f = new DesktopFixture();
        f.Shell.Settings.Theme = theme;
        f.Window.ApplyTheme();
        var settings = new SettingsWindow(f.Shell.Settings);
        settings.Show(f.Window);
        try
        {
            Find<TextBox>(settings, "Find a setting").Text = "Export defaults";
            Pump(settings);
            await Capture(Find<ComboBox>(settings, "Default PDF paper size"), "pdf-options");
            await Capture(Find<CheckBox>(settings, "Default PDF front matter properties"), "pdf-properties");
            Find<ComboBox>(settings, "Export defaults format").SelectedItem = ExportFormat.Html;
            Pump(settings);
            await Capture(Find<ComboBox>(settings, "Default HTML appearance"), "html-options");
            await Capture(Find<CheckBox>(settings, "Default HTML front matter properties"), "html-properties");
        }
        finally { await DiscardAndClose(settings); }

        async Task Capture(Control target, string name)
        {
            target.BringIntoView();
            await Task.Delay(80, TestContext.Current.CancellationToken);
            Pump(settings);
            Assert.True(target.IsEffectivelyVisible);
            var position = target.TranslatePoint(default, settings);
            Assert.NotNull(position);
            Assert.InRange(position.Value.Y, 0, settings.Bounds.Height - 1);
            Assert.True(target.Bounds.Height > 0);
            var folder = Path.Combine(Screenshots.Folder, "reported-settings-preferences-expanded", "export");
            Directory.CreateDirectory(folder);
            using var bitmap = settings.CaptureRenderedFrame();
            Assert.NotNull(bitmap);
            using var output = File.Create(Path.Combine(folder, theme + "-" + name + ".png"));
            bitmap.Save(output, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        }
    }

    private static T Find<T>(Visual root, string name) where T : Control => root.GetVisualDescendants().OfType<T>()
        .Single(control => AutomationProperties.GetName(control) == name || control is Button button && Equals(button.Content, name));
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Pump(Window window) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); }
    private static async Task DiscardAndClose(Window window)
    {
        if (!window.IsVisible) return;
        window.Close();
        foreach (var prompt in window.OwnedWindows.OfType<PromptWindow>().ToArray())
            Click(Find<Button>(prompt, "Discard changes"));
        await Task.Delay(20);
        Dispatcher.UIThread.RunJobs();
    }
}
