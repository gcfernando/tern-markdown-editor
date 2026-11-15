using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tern.App.Commands;
using Tern.App.Settings;
using Tern.Desktop.Themes;
using Tern.Desktop.Views;
using Tern.Domain.Text;
using Tern.Workspaces;

namespace Tern.Desktop.Tests;

public sealed class GuiWorkspaceFeaturesTests
{
    public static IEnumerable<object[]> Viewports => from size in new[] { (900, 560), (1280, 720), (1440, 900) }
        from scale in new[] { 1d, 1.5, 2d } from theme in new[] { ThemeChoice.Midnight, ThemeChoice.Paper }
        select new object[] { size.Item1, size.Item2, scale, theme };

    [AvaloniaTheory]
    [MemberData(nameof(Viewports))]
    public async Task Core_shell_actions_remain_separate_and_reachable(int width, int height, double scale, ThemeChoice theme)
    {
        using var f = new DesktopFixture();
        var tab = f.Shell.NewDocument("# Synthetic note\n\nReadable content.");
        f.Window.Width = width; f.Window.Height = height;
        f.Shell.Settings.UiScale = scale;
        f.Shell.Settings.Theme = theme;
        f.Window.ApplyTheme();
        f.Window.ApplySettings();
        await f.Window.RebuildPreviewAsync();
        for (var i = 0; i < 100 && tab.Analysis is null; i++) { await Task.Delay(10); Dispatcher.UIThread.RunJobs(); }
        Assert.NotNull(tab.Analysis);
        Pump(f);
        var controls = new[] { "SourceButton", "SplitButton", "ReadingButton", "AIEntryButton", "SearchBox", "WorkspaceAddButton", "OpenDocumentsButton", "ActivityButton" }
            .Select(name => f.Window.FindControl<Control>(name)!).ToArray();
        foreach (var c in controls)
        {
            Assert.True(c.IsEffectivelyVisible, c.Name);
            var rect = new Rect(c.TranslatePoint(default, f.Window)!.Value, c.Bounds.Size);
            Assert.True(rect.Left >= 0 && rect.Top >= 0 && rect.Right <= width + 1 && rect.Bottom <= height + 1, c.Name + " is clipped: " + rect);
        }
        var modeControls = controls.Take(4).ToArray();
        for (var i = 0; i < modeControls.Length; i++)
            for (var j = i + 1; j < modeControls.Length; j++)
                Assert.False(new Rect(modeControls[i].TranslatePoint(default, f.Window)!.Value, modeControls[i].Bounds.Size)
                    .Intersects(new Rect(modeControls[j].TranslatePoint(default, f.Window)!.Value, modeControls[j].Bounds.Size)));
        using var image = f.Window.CaptureRenderedFrame();
        Assert.NotNull(image);
        var folder = Path.Combine(Screenshots.Folder, "gui-pack"); Directory.CreateDirectory(folder);
        image.Save(Path.Combine(folder, $"shell-{width}-{height}-{scale}-{theme}.png"));
    }

    [AvaloniaFact]
    public async Task Reset_layout_preserves_text_fonts_shortcuts_and_user_inspector_intent()
    {
        using var f = new DesktopFixture();
        var tab = f.Shell.NewDocument("Keep this text");
        f.Shell.Settings.EditorFontSize = 19;
        f.Shell.Registry.SetBinding(CommandIds.Bold, "Ctrl+Alt+B");
        await f.Window.RunCommandAsync(CommandIds.ToggleRightPanel);
        f.Window.Width = 900; Pump(f); f.Window.Width = 1440; Pump(f);
        Assert.False(f.Window.FindControl<Control>("RightPanel")!.IsVisible);
        await f.Window.RunCommandAsync(CommandIds.ToggleFocus);
        await f.Window.RunCommandAsync(CommandIds.ResetLayout);
        Assert.False(f.Window.IsFocusMode);
        Assert.Equal(19, f.Shell.Settings.EditorFontSize);
        Assert.Equal("Keep this text", tab.CurrentText);
        Assert.True(f.Window.FindControl<Control>("RightPanel")!.IsVisible);
    }

    [AvaloniaFact]
    public async Task Sample_is_a_distinct_unsaved_note_and_checklist_dismissal_is_persisted()
    {
        using var f = new DesktopFixture();
        var original = f.Shell.NewDocument("Original text");
        await f.Window.RunCommandAsync(CommandIds.Welcome);
        var sample = f.Window.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Try a sample note") && b.IsEffectivelyVisible);
        sample.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump(f);
        var practice = f.Shell.ActiveTab!;
        Assert.NotSame(original, practice);
        Assert.Contains(original, f.Shell.Tabs);
        Assert.Null(practice.FilePath);
        Assert.Contains("practice", practice.DisplayName, StringComparison.OrdinalIgnoreCase);
        Assert.True(practice.IsDirty);
        var dismiss = f.Window.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Dismiss checklist"));
        dismiss.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.True(new SettingsStore(f.Shell.Paths.SettingsDirectory).Load().OnboardingDismissed);
        Assert.Equal("Original text", original.CurrentText);
    }

    [AvaloniaFact]
    public async Task Duplicate_tab_names_show_locations_and_overflow_activation_keeps_unsaved_work()
    {
        using var f = new DesktopFixture();
        var a = await f.Shell.OpenFileAsync(await f.WriteAsync("one/readme.md", "One"));
        var b = await f.Shell.OpenFileAsync(await f.WriteAsync("two/readme.md", "Two"));
        a!.ApplyEdits([TextEdit.Insert(0, "Unsaved ")]);
        Pump(f);
        var labels = f.Window.TabStrip.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Name == "TabTitleText").Select(t => t.Text).ToArray();
        Assert.Contains(labels, l => l?.Contains("one", StringComparison.Ordinal) == true);
        Assert.Contains(labels, l => l?.Contains("two", StringComparison.Ordinal) == true);
        f.Window.FindControl<Button>("OpenDocumentsButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var row = f.Window.Picker!.Results.Items.OfType<PickerItem>().Single(i => ReferenceEquals(i.Payload, a));
        Assert.NotNull(row.CloseDocument);
        f.Window.Picker.Results.SelectedItem = row;
        f.Window.Picker.Input.RaiseEvent(new Avalonia.Input.KeyEventArgs { RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent, Key = Avalonia.Input.Key.Enter });
        Assert.Same(a, f.Shell.ActiveTab);
        Assert.True(a.IsDirty);
        Assert.Equal("Two", b!.CurrentText);
    }

    private static void Pump(DesktopFixture f)
    { for (var i = 0; i < 6; i++) { f.Window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); } }
}
