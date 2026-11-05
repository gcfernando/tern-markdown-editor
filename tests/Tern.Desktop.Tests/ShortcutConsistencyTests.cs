using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tern.App.Commands;

namespace Tern.Desktop.Tests;

public sealed class ShortcutConsistencyTests
{
    [AvaloniaFact]
    public void Writing_panel_and_toggle_follow_a_remapped_shortcut()
    {
        using var f = new DesktopFixture();
        f.Shell.NewDocument("A note");
        Assert.True(f.Shell.Registry.SetBinding(CommandIds.AskAI, "Ctrl+Alt+F10").Succeeded);
        f.Window.ApplySettings(); Pump(f);
        Press(f.Window, Key.Space, KeyModifiers.Alt);
        Assert.False(f.Window.IsOverlayOpen);
        Press(f.Window, Key.F10, KeyModifiers.Control | KeyModifiers.Alt);
        Pump(f);
        Assert.True(f.Window.IsOverlayOpen);
        var prompt = Assert.Single(f.Window.OverlayLayer.Children.OfType<Tern.Desktop.Views.AIPromptPanel>());
        Assert.Contains(prompt.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Ctrl+Alt+F10");
        Press(f.Window, Key.F10, KeyModifiers.Control | KeyModifiers.Alt);
        Assert.False(f.Window.IsOverlayOpen);
    }

    [AvaloniaFact]
    public async Task Enter_alias_dispatches_the_registered_task_shortcut()
    {
        using var f = new DesktopFixture();
        var tab = f.Shell.NewDocument("- [ ] task");
        Pump(f);
        f.Window.Editor.Focus();
        for (var attempt = 0; attempt < 100 && tab.Analysis is null; attempt++)
        {
            await Task.Delay(20);
            Pump(f);
        }
        Assert.NotNull(tab.Analysis);
        Press(f.Window.Editor.TextArea, Key.Enter, KeyModifiers.Control);
        Assert.Equal("- [x] task", tab.CurrentText);
        for (var attempt = 0; attempt < 100 && tab.Analysis?.Tasks.Any(t => t.Checked) != true; attempt++)
        {
            await Task.Delay(20);
            Pump(f);
        }
        Assert.True(tab.Analysis?.Tasks.Any(t => t.Checked));
        f.Shell.Registry.SetBinding(CommandIds.ToggleTaskAtCaret, "Ctrl+Alt+Return");
        Press(f.Window.Editor.TextArea, Key.Return, KeyModifiers.Control | KeyModifiers.Alt);
        Assert.Equal("- [ ] task", tab.CurrentText);
    }

    [AvaloniaFact]
    public void Menu_number_keys_match_the_keys_used_by_the_keyboard_handler()
    {
        using var f = new DesktopFixture();
        foreach (var menu in f.Window.ApplicationMenu.GetLogicalDescendants().OfType<MenuItem>())
        {
            if (menu.Tag is not string id || !f.Shell.Registry.TryGet(id, out _)) continue;
            Assert.True(Gesture.TryParse(f.Shell.Registry.GetGesture(id), out var expected));
            Assert.NotNull(menu.InputGesture);
            Assert.Equal(Enum.Parse<Key>(expected.Key), menu.InputGesture.Key);
        }
        var reference = f.Window.ApplicationMenu.GetLogicalDescendants().OfType<MenuItem>()
            .Single(m => Equals(m.Tag, CommandIds.ToggleReference));
        Assert.Equal(Key.D2, reference.InputGesture!.Key);
        reference.ApplyTemplate();
        Assert.Contains("Ctrl+Alt+2", string.Join("; ", reference.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Name + "=" + t.Text)), StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void Tab_hints_follow_remapping_unbinding_and_newly_created_tabs()
    {
        using var f = new DesktopFixture();
        f.Shell.NewDocument("first");
        f.Shell.Registry.SetBinding(CommandIds.NewFile, "Ctrl+Alt+F10");
        f.Shell.Registry.SetBinding(CommandIds.CloseTab, "Ctrl+Alt+F11");
        f.Shell.Registry.SetBinding(CommandIds.Rename, "Ctrl+Alt+F12");
        f.Window.ApplySettings();
        f.Shell.NewDocument("second");
        Pump(f);
        var buttons = f.Window.GetVisualDescendants().OfType<Button>().ToList();
        Assert.Contains("Ctrl+Alt+F10", ToolTip.GetTip(buttons.Single(b => AutomationProperties.GetName(b) == "New document"))!.ToString());
        var closes = buttons.Where(b => AutomationProperties.GetName(b) == "Close tab").ToList();
        Assert.Equal(2, closes.Count);
        Assert.All(closes, b => Assert.Contains("Ctrl+Alt+F11", ToolTip.GetTip(b)!.ToString()));
        var labels = f.Window.TabStrip.GetVisualDescendants().OfType<TextBlock>()
            .Where(t => ToolTip.GetTip(t)?.ToString()?.StartsWith("Double-click", StringComparison.Ordinal) == true).ToList();
        Assert.Equal(2, labels.Count);
        Assert.All(labels, t => Assert.Contains("Ctrl+Alt+F12", ToolTip.GetTip(t)!.ToString()));
        f.Shell.Registry.ClearBinding(CommandIds.CloseTab);
        f.Window.ApplySettings();
        Pump(f);
        Assert.All(closes, b => Assert.DoesNotContain("Ctrl+", ToolTip.GetTip(b)!.ToString()));
    }

    [AvaloniaFact]
    public void History_uses_only_registered_bindings_and_keeps_the_session_in_sync()
    {
        using var f = new DesktopFixture();
        var tab = f.Shell.NewDocument("original");
        Pump(f);
        f.Window.Editor.Focus();
        f.Window.Editor.Document.Insert(0, "changed ");
        Press(f.Window.Editor.TextArea, Key.Z, KeyModifiers.Control);
        Assert.Equal("original", tab.CurrentText);
        Press(f.Window.Editor.TextArea, Key.Y, KeyModifiers.Control);
        Assert.Equal("changed original", tab.CurrentText);
        f.Shell.Registry.SetBinding(CommandIds.Undo, "Ctrl+Alt+U");
        f.Shell.Registry.SetBinding(CommandIds.Redo, "Ctrl+Alt+R");
        Assert.Equal("changed original", tab.CurrentText);
        Press(f.Window.Editor.TextArea, Key.Z, KeyModifiers.Control);
        Assert.Equal("changed original", tab.CurrentText);
        Press(f.Window.Editor.TextArea, Key.U, KeyModifiers.Control | KeyModifiers.Alt);
        Assert.Equal("original", tab.CurrentText);
        Press(f.Window.Editor.TextArea, Key.Y, KeyModifiers.Control);
        Press(f.Window.Editor.TextArea, Key.Z, KeyModifiers.Control | KeyModifiers.Shift);
        Assert.Equal("original", tab.CurrentText);
        Press(f.Window.Editor.TextArea, Key.R, KeyModifiers.Control | KeyModifiers.Alt);
        Assert.Equal("changed original", tab.CurrentText);
        f.Shell.Registry.ClearBinding(CommandIds.Undo);
        Press(f.Window.Editor.TextArea, Key.Z, KeyModifiers.Control);
        Assert.Equal("changed original", tab.CurrentText);
        f.Shell.Registry.ForceBinding(CommandIds.Bold, "Ctrl+Z");
        f.Window.Editor.Select(0, 7);
        Press(f.Window.Editor.TextArea, Key.Z, KeyModifiers.Control);
        Assert.Contains("**changed**", tab.CurrentText, StringComparison.Ordinal);
        AvaloniaEdit.ApplicationCommands.Undo.Execute(null, f.Window.Editor.TextArea);
        Assert.Equal("changed original", tab.CurrentText);
    }

    [AvaloniaFact]
    public void Custom_F6_and_Escape_bindings_take_precedence_over_pane_fallbacks()
    {
        using var f = new DesktopFixture();
        f.Shell.NewDocument("test");
        f.Shell.Registry.SetBinding(CommandIds.ToggleReference, "Ctrl+F6");
        Press(f.Window, Key.F6, KeyModifiers.Control);
        Assert.True(f.Window.ReferencePane.IsVisible);
        f.Shell.Registry.SetBinding(CommandIds.ToggleReference, "Ctrl+Escape");
        Press(f.Window, Key.F8, KeyModifiers.None);
        Assert.True(f.Window.IsFocusMode);
        Press(f.Window, Key.Escape, KeyModifiers.Control);
        Assert.True(f.Window.IsFocusMode);
        Assert.False(f.Window.ReferencePane.IsVisible);
        Press(f.Window, Key.Escape, KeyModifiers.None);
        Assert.False(f.Window.IsFocusMode);
    }

    [AvaloniaFact]
    public async Task F6_reaches_the_active_inspector_tab_library_and_document_tabs()
    {
        using var f = new DesktopFixture();
        f.Shell.NewDocument("# Heading");
        await f.Window.RunCommandAsync(CommandIds.ViewSplit);
        f.Window.InspectorContent.SelectedIndex = 1;
        Pump(f);
        Assert.True(f.Window.Editor.TextArea.Focus());
        Press(f.Window.Editor.TextArea, Key.F6, KeyModifiers.None);
        Assert.True(f.Window.PreviewControl.IsKeyboardFocusWithin);
        Press(f.Window.PreviewControl, Key.F6, KeyModifiers.None);
        var selectedInspector = Assert.Single(f.Window.InspectorContent.Items.OfType<TabItem>(), item => item.IsSelected);
        Assert.True(selectedInspector.IsKeyboardFocusWithin);
        Press(selectedInspector, Key.F6, KeyModifiers.None);
        Assert.True(f.Window.SearchBox.IsKeyboardFocusWithin);
        Press(f.Window.SearchBox, Key.F6, KeyModifiers.None);
        Assert.True(f.Window.WorkspaceAddButton.IsKeyboardFocusWithin);
        Press(f.Window.WorkspaceAddButton, Key.F6, KeyModifiers.None);
        Assert.True(f.Window.TabStrip.IsKeyboardFocusWithin);
        var tab = Assert.Single(f.Window.TabStrip.GetVisualDescendants().OfType<ListBoxItem>(), item => item.IsSelected);
        Press(tab, Key.F6, KeyModifiers.Shift);
        Assert.True(f.Window.WorkspaceAddButton.IsKeyboardFocusWithin);
    }

    [AvaloniaFact]
    public void Focused_splitter_resizes_the_library_with_arrow_keys()
    {
        using var f = new DesktopFixture();
        f.Shell.NewDocument("Note");
        Pump(f);
        var splitter = Assert.Single(f.Window.GetVisualDescendants().OfType<GridSplitter>(),
            control => AutomationProperties.GetName(control) == "Resize sidebar");
        var width = f.Window.Body.ColumnDefinitions[0].ActualWidth;
        Assert.True(splitter.IsTabStop);
        Assert.True(splitter.Focus(NavigationMethod.Tab));
        f.Window.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null);
        Pump(f);
        Assert.True(f.Window.Body.ColumnDefinitions[0].ActualWidth > width);
    }

    private static void Press(InputElement target, Key key, KeyModifiers modifiers) =>
        target.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers });

    private static void Pump(DesktopFixture f)
    {
        f.Window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }
}
