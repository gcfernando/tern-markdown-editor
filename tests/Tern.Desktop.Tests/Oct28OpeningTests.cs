using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tern.App.Shell;
using Tern.Desktop.Services;
using Tern.Desktop.ViewModels;
using Tern.Desktop.Views;

namespace Tern.Desktop.Tests;

public sealed class Oct28OpeningTests
{
    [AvaloniaTheory]
    [InlineData(Tern.Domain.Text.LineEnding.Lf, "\n")]
    [InlineData(Tern.Domain.Text.LineEnding.CrLf, "\r\n")]
    [InlineData(Tern.Domain.Text.LineEnding.Cr, "\r")]
    public void First_typed_newline_uses_the_selected_mode(Tern.Domain.Text.LineEnding ending, string expected)
    {
        using var f = new DesktopFixture();
        var tab = f.Shell.NewDocument(); tab.ConvertLineEndings(ending); Pump(f);
        f.Window.Editor.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        Pump(f); Assert.Equal(expected, tab.CurrentText);
    }
    [AvaloniaFact]
    public async Task Online_open_is_clean_and_close_prompts_only_after_edits()
    {
        using var f = new DesktopFixture();
        var doc = new MarkdownUrlDocument(new Uri("https://example.com/notes.md"), "notes.md", "# Online\n");
        await f.Window.OpenUrlDocumentAsync(doc); Pump(f);
        var first = f.Shell.ActiveTab!;
        Assert.False(first.IsDirty); Assert.Equal("LF", first.LineEndingText);
        Assert.True(await f.Shell.CloseTabAsync(first)); Assert.Equal(0, f.Dialogs.SavePrompts);
        await f.Window.OpenUrlDocumentAsync(doc);
        var edited = f.Shell.ActiveTab!;
        edited.ApplyEdits([Tern.Domain.Text.TextEdit.Insert(0, "Edit ")]);
        Assert.False(await f.Shell.CloseTabAsync(edited));
        Assert.Equal(1, f.Dialogs.SavePrompts); Assert.Contains(edited, f.Shell.Tabs);
    }

    [AvaloniaFact]
    public async Task Library_click_reuses_canonical_online_tab_and_preserves_edits_without_a_request_dialog()
    {
        using var f = new DesktopFixture();
        await f.Window.OpenUrlDocumentAsync(new MarkdownUrlDocument(new Uri("https://github.com/owner/repo/blob/main/notes.md"), "notes.md", "First\n"));
        var first = f.Shell.ActiveTab!;
        first.ApplyEdits([Tern.Domain.Text.TextEdit.Insert(0, "Edited ")]);
        await f.Window.OpenUrlDocumentAsync(new MarkdownUrlDocument(new Uri("https://raw.githubusercontent.com/owner/repo/main/notes.md#heading"), "notes.md", "Replacement\n"));
        Assert.Single(f.Shell.Tabs); Assert.Same(first, f.Shell.ActiveTab);
        var local = await f.Shell.OpenFileAsync(await f.WriteAsync("other.md", "Local"));
        await f.Window.ShowNavAsync("Recent"); Pump(f);
        var list = f.Window.FindControl<ListBox>("SideList")!;
        list.SelectedItem = Assert.Single(list.Items.OfType<SideRow>(), r => r.IsOnline);
        Pump(f);
        Assert.Same(first, f.Shell.ActiveTab); Assert.Equal("Edited First\n", first.CurrentText);
        Assert.Equal(2, f.Shell.Tabs.Count);
        Assert.DoesNotContain(f.Window.OwnedWindows, w => w is OpenUrlWindow);
        Assert.NotNull(local);
    }

    [AvaloniaFact]
    public async Task Explicit_online_duplicate_asks_and_obeys_new_tab_and_replace()
    {
        using var f = new DesktopFixture();
        var doc = new MarkdownUrlDocument(new Uri("https://example.com/notes.md"), "notes.md", "First\n");
        await f.Window.OpenUrlDocumentAsync(doc);
        f.Dialogs.DuplicateChoice = DuplicateOpenChoice.NewTab;
        await f.Window.OpenUrlDocumentAsync(doc with { Text = "Second\n" }, DuplicateOpenBehavior.Ask);
        Assert.Equal(2, f.Shell.Tabs.Count); Assert.Equal(1, f.Dialogs.DuplicatePrompts);
        f.Dialogs.DuplicateChoice = DuplicateOpenChoice.Replace;
        await f.Window.OpenUrlDocumentAsync(doc with { Text = "Third\n" }, DuplicateOpenBehavior.Ask);
        Assert.Equal(2, f.Shell.Tabs.Count); Assert.Equal("Third\n", f.Shell.ActiveTab!.CurrentText);
        Assert.False(f.Shell.ActiveTab.IsDirty);
    }

    [AvaloniaFact]
    public async Task Right_click_library_opens_only_menu_and_download_icon_is_online_only()
    {
        using var f = new DesktopFixture();
        await f.Window.OpenUrlDocumentAsync(new MarkdownUrlDocument(new Uri("https://example.com/notes.md"), "notes.md", "Remote"));
        var local = await f.Shell.OpenFileAsync(await f.WriteAsync("local.md", "Local"));
        await f.Window.ShowNavAsync("Recent"); Pump(f);
        var list = f.Window.FindControl<ListBox>("SideList")!;
        var online = Assert.Single(list.Items.OfType<SideRow>(), r => r.IsOnline);
        var container = Assert.IsType<ListBoxItem>(list.ContainerFromItem(online));
        var row = Assert.Single(container.GetVisualDescendants().OfType<Grid>(), g => g.ContextMenu is not null);
        var download = Assert.Single(f.Window.GetVisualDescendants().OfType<Button>(), b => b.IsEffectivelyVisible && AutomationProperties.GetName(b) == "Download online file");
        Assert.Same(online, download.DataContext);
        var point = row.TranslatePoint(new Point(12, 12), f.Window)!.Value;
        f.Window.MouseDown(point, MouseButton.Right); f.Window.MouseUp(point, MouseButton.Right); Pump(f);
        Assert.Same(local, f.Shell.ActiveTab); Assert.Equal(2, f.Shell.Tabs.Count);
        Assert.True(row.ContextMenu!.IsOpen);
        Assert.Contains(row.ContextMenu.Items.OfType<MenuItem>(), m => Equals(m.Header, "Open"));
        Assert.DoesNotContain(f.Window.OwnedWindows, w => w is OpenUrlWindow);
        row.ContextMenu.Close();
    }

    private static void Pump(DesktopFixture f)
    { Dispatcher.UIThread.RunJobs(); f.Window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); f.Window.UpdateLayout(); }
}
