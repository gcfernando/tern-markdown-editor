using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tern.App.Commands;
using Tern.App.Settings;
using Tern.Desktop.Views;
using Tern.Domain.Text;
using Tern.Filesystem;

namespace Tern.Desktop.Tests;

public sealed class SettingsDefaultsWorkflowTests
{
    [AvaloniaFact]
    public async Task Defaults_are_staged_applied_persisted_and_cancel_keeps_last_applied_values()
    {
        using var f = new DesktopFixture();
        var old = f.Shell.NewDocument("Keep\r\noriginal\n");
        var oldFormat = old.Session.Format;
        var window = new SettingsWindow(f.Shell.Settings, initialCategory: "Files", commit: () =>
        { if (!f.Shell.SaveSettings()) throw new IOException("Settings save failed"); f.Window.ApplySettings(); });
        window.Show(f.Window);
        try
        {
            var encoding = Find<ComboBox>(window, "Default encoding (new files)");
            var ending = Find<ComboBox>(window, "Default line endings (new files)");
            var bom = Find<CheckBox>(window, "Include a BOM in new files");
            Assert.DoesNotContain(LineEnding.None, ending.Items.Cast<LineEnding>());
            encoding.SelectedItem = encoding.Items.OfType<EncodingOption>().Single(item => item.Kind == TextEncodingKind.Utf16BigEndian);
            ending.SelectedItem = LineEnding.Lf;
            Assert.True(bom.IsChecked);
            Assert.False(bom.IsEnabled);
            Assert.Equal(TextEncodingKind.Utf8, f.Shell.Settings.DefaultEncoding);
            Click(window, "Apply");
            Assert.Equal(TextEncodingKind.Utf16BigEndian, f.Shell.Settings.DefaultEncoding);
            Assert.Equal(LineEnding.Lf, f.Shell.Settings.DefaultLineEnding);
            var stored = new SettingsStore(f.Shell.Paths.SettingsDirectory).Load();
            Assert.Equal(TextEncodingKind.Utf16BigEndian, stored.DefaultEncoding);
            Assert.True(stored.DefaultEncodingHasBom);
            Assert.Equal(oldFormat, old.Session.Format);
            Assert.Equal("Keep\r\noriginal\n", old.CurrentText);
            var next = f.Shell.NewDocument();
            Assert.Equal(TextEncodingKind.Utf16BigEndian, next.Session.Format.Encoding);
            Assert.Equal(LineEnding.Lf, next.Session.Format.DominantLineEnding);
            Click(window, "Reset this category to defaults");
            encoding = Find<ComboBox>(window, "Default encoding (new files)");
            ending = Find<ComboBox>(window, "Default line endings (new files)");
            bom = Find<CheckBox>(window, "Include a BOM in new files");
            Assert.Equal(TextEncodingKind.Utf8, Assert.IsType<EncodingOption>(encoding.SelectedItem).Kind);
            Assert.Equal(LineEnding.CrLf, ending.SelectedItem);
            Assert.False(bom.IsChecked);
            Assert.True(bom.IsEnabled);
            Assert.Equal(TextEncodingKind.Utf16BigEndian, f.Shell.Settings.DefaultEncoding);
            Assert.Equal(LineEnding.Lf, f.Shell.Settings.DefaultLineEnding);
            Assert.Equal(oldFormat, old.Session.Format);
            encoding.SelectedItem = encoding.Items.OfType<EncodingOption>().Single(item => item.Kind == TextEncodingKind.Windows1252);
            Assert.False(bom.IsChecked);
            Assert.False(bom.IsEnabled);
            Click(window, "Cancel");
            Click(Assert.Single(window.OwnedWindows.OfType<PromptWindow>()), "Discard changes");
            await Task.Delay(20); Pump(f.Window);
            Assert.False(window.IsVisible);
            Assert.Equal(TextEncodingKind.Utf16BigEndian, f.Shell.Settings.DefaultEncoding);
        }
        finally { await Discard(window); }
    }

    [AvaloniaTheory]
    [InlineData(LineEnding.Lf, "\n")]
    [InlineData(LineEnding.CrLf, "\r\n")]
    [InlineData(LineEnding.Cr, "\r")]
    public async Task Enter_and_workspace_creation_use_current_defaults_in_an_already_open_workspace(LineEnding ending, string newline)
    {
        using var f = new DesktopFixture();
        Assert.True(await f.Shell.OpenWorkspaceAsync(f.Docs));
        f.Shell.Settings.DefaultEncoding = TextEncodingKind.Utf16LittleEndian;
        f.Shell.Settings.DefaultLineEnding = ending;
        var draft = f.Shell.NewDocument("First");
        await f.Window.RunCommandAsync(CommandIds.ViewSource);
        Pump(f.Window);
        f.Window.EditorControl.CaretOffset = 5;
        Assert.True(f.Window.EditorControl.TextArea.Focus());
        f.Window.KeyPress(Key.Enter, RawInputModifiers.None, default, null);
        f.Window.KeyRelease(Key.Enter, RawInputModifiers.None, default, null);
        Pump(f.Window);
        Assert.Equal("First" + newline, draft.CurrentText);
        await f.Window.CreateWorkspaceEntryAsync(false, "chosen-default.md");
        var tab = f.Shell.ActiveTab!;
        Assert.Equal(Path.Combine(f.Docs, "chosen-default.md"), tab.FilePath);
        Assert.Equal(TextEncodingKind.Utf16LittleEndian, tab.Session.Format.Encoding);
        Assert.True(tab.Session.Format.HasBom);
        Assert.Equal(ending, tab.Session.Format.DominantLineEnding);
        Assert.False(tab.IsDirty);
        Assert.Equal(new byte[] { 0xff, 0xfe }, await File.ReadAllBytesAsync(tab.FilePath!));
    }

    [AvaloniaFact]
    public async Task Combined_creation_form_uses_default_bytes_and_format_for_outside_file()
    {
        using var f = new DesktopFixture();
        f.Shell.Settings.DefaultEncoding = TextEncodingKind.Utf8;
        f.Shell.Settings.DefaultEncodingHasBom = true;
        f.Shell.Settings.DefaultLineEnding = LineEnding.Cr;
        var add = Assert.IsType<MenuFlyout>(f.Window.FindControl<Button>("WorkspaceAddButton")!.Flyout);
        Assert.Single(add.Items.OfType<MenuItem>(), item => Equals(item.Tag, CommandIds.NewWorkspaceFile))
            .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await Until(() => f.Window.OwnedWindows.OfType<CreateItemWindow>().Any(), f.Window);
        var form = Assert.Single(f.Window.OwnedWindows.OfType<CreateItemWindow>());
        Find<TextBox>(form, "Parent folder").Text = f.Docs;
        Find<TextBox>(form, "File name").Text = "outside-default.md";
        Pump(form);
        Click(form, "Create");
        var path = Path.Combine(f.Docs, "outside-default.md");
        await Until(() => f.Shell.ActiveTab?.FilePath == path && !f.Window.OwnedWindows.Any(), f.Window);
        Assert.Equal(new byte[] { 0xef, 0xbb, 0xbf }, await File.ReadAllBytesAsync(path));
        Assert.Equal(LineEnding.Cr, f.Shell.ActiveTab!.Session.Format.DominantLineEnding);
        Assert.False(f.Shell.ActiveTab.IsDirty);
    }

    [AvaloniaTheory]
    [InlineData(LineEnding.Lf, "\n")]
    [InlineData(LineEnding.CrLf, "\r\n")]
    [InlineData(LineEnding.Cr, "\r")]
    public async Task Selected_first_line_and_subsequent_multiline_Enter_use_document_newlines_and_undo(LineEnding ending, string newline)
    {
        using var f = new DesktopFixture();
        f.Shell.Settings.DefaultLineEnding = ending;
        var tab = f.Shell.NewDocument("Keep chosen");
        await f.Window.RunCommandAsync(CommandIds.ViewSource);
        Pump(f.Window);
        Assert.True(f.Window.EditorControl.TextArea.Focus());
        f.Window.EditorControl.Select(5, 6);
        PressEnter();
        Assert.Equal("Keep " + newline, tab.CurrentText);
        f.Window.EditorControl.Document.UndoStack.Undo();
        Assert.Equal("Keep chosen", tab.CurrentText);
        f.Window.EditorControl.Select(tab.CurrentText.Length, 0);
        PressEnter();
        f.Window.KeyTextInput("Second"); Pump(f.Window);
        PressEnter();
        Assert.Equal("Keep chosen" + newline + "Second" + newline, tab.CurrentText);
        var destination = Path.Combine(f.Docs, "enter-newlines.md");
        Assert.IsType<SaveSucceeded>(await tab.SaveAsAsync(destination, false));
        Assert.Equal(TextFileCodec.Encode(tab.CurrentText, tab.Session.Format), await File.ReadAllBytesAsync(destination));

        void PressEnter()
        {
            f.Window.KeyPress(Key.Enter, RawInputModifiers.None, default, null);
            f.Window.KeyRelease(Key.Enter, RawInputModifiers.None, default, null);
            Pump(f.Window);
        }
    }

    [AvaloniaTheory]
    [InlineData(ThemeChoice.Paper)]
    [InlineData(ThemeChoice.Midnight)]
    public async Task Capture_actual_new_file_default_preferences(ThemeChoice theme)
    {
        using var f = new DesktopFixture();
        f.Shell.Settings.Theme = theme;
        f.Window.ApplyTheme();
        var window = new SettingsWindow(f.Shell.Settings, initialCategory: "Files") { Width = 1100, Height = 850 };
        window.Show(f.Window);
        try
        {
            Find<ComboBox>(window, "Default encoding (new files)").SelectedItem =
                Find<ComboBox>(window, "Default encoding (new files)").Items.OfType<EncodingOption>()
                    .Single(item => item.Kind == TextEncodingKind.Utf16LittleEndian);
            Assert.True(Find<CheckBox>(window, "Include a BOM in new files").IsChecked);
            Assert.False(Find<CheckBox>(window, "Include a BOM in new files").IsEnabled);
            var directory = Path.Combine(Screenshots.Folder, "reported-settings-defaults");
            Directory.CreateDirectory(directory);
            Capture<ComboBox>("Default encoding (new files)", "default-encoding");
            Capture<CheckBox>("Include a BOM in new files", "bom-required");
            Capture<ComboBox>("Default line endings (new files)", "default-line-endings");
            Find<ComboBox>(window, "Default encoding (new files)").SelectedItem =
                Find<ComboBox>(window, "Default encoding (new files)").Items.OfType<EncodingOption>()
                    .Single(item => item.Kind == TextEncodingKind.Windows1252);
            Assert.False(Find<CheckBox>(window, "Include a BOM in new files").IsChecked);
            Capture<CheckBox>("Include a BOM in new files", "bom-unavailable");

            void Capture<T>(string name, string tag) where T : Control
            {
                Find<TextBox>(window, "Find a setting").Text = name;
                Pump(window);
                var editor = Find<T>(window, name);
                editor.BringIntoView(); Pump(window);
                var position = editor.TranslatePoint(default, window);
                Assert.NotNull(position);
                Assert.InRange(position.Value.Y, 0, window.ClientSize.Height - editor.Bounds.Height);
                Assert.True(editor.IsEffectivelyVisible);
                using var bitmap = window.CaptureRenderedFrame();
                Assert.NotNull(bitmap);
                using var output = File.Create(Path.Combine(directory, theme + "-" + tag + ".png"));
                bitmap.Save(output, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            }
        }
        finally { await Discard(window); }
    }

    private static T Find<T>(Visual root, string name) where T : Control => root.GetVisualDescendants().OfType<T>()
        .Single(control => AutomationProperties.GetName(control) == name || control is Button button && Equals(button.Content, name));
    private static void Click(Window window, string label) => Find<Button>(window, label).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Pump(Window window) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
    private static async Task Until(Func<bool> ready, Window window)
    {
        for (var attempt = 0; attempt < 200 && !ready(); attempt++)
        { await Task.Delay(10, TestContext.Current.CancellationToken); Pump(window); }
        Assert.True(ready());
    }
    private static async Task Discard(Window window)
    {
        if (!window.IsVisible) return;
        window.Close();
        foreach (var prompt in window.OwnedWindows.OfType<PromptWindow>().ToArray()) Click(prompt, "Discard changes");
        await Task.Delay(20); Dispatcher.UIThread.RunJobs();
    }
}
