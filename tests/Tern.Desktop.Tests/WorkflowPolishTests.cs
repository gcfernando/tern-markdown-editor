using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tern.App.Commands;
using Tern.App.Shell;
using Tern.App.Settings;
using Tern.Desktop.ViewModels;
using Tern.Desktop.Views;
using Tern.Markdown;

namespace Tern.Desktop.Tests;

public sealed class WorkflowPolishTests
{
    [AvaloniaFact]
    public async Task Reading_options_use_the_shared_popup_and_only_offer_navigation_when_the_toolbar_hides_it()
    {
        using var f = new DesktopFixture();
        f.Shell.NewDocument("# Heading\n\nReading content");
        await f.Window.RunCommandAsync(CommandIds.ViewReading);
        foreach (var width in new[] { 1440, 720 })
        {
            f.Window.Width = width;
            Pump(f.Window);
            var options = f.Window.GetVisualDescendants().OfType<Button>().Single(button => AutomationProperties.GetName(button) == "Reading options");
            var flyout = Assert.IsType<Flyout>(options.Flyout);
            Assert.Contains("tern-popup", flyout.FlyoutPresenterClasses);
            Assert.Equal(!f.Window.ReadingNavActions.IsVisible, f.Window.ReadingFlyoutNav.IsVisible);
            Assert.Equal(["Go to top", "Page up", "Page down"], f.Window.ReadingFlyoutNav.Children.OfType<Button>().Select(button => button.Content));
            Assert.Equal(["Go to top", "Page up", "Page down"], f.Window.ReadingNavActions.Children.OfType<Button>().Take(3).Select(AutomationProperties.GetName));
        }
    }

    [AvaloniaFact]
    public void Main_guidance_dismissal_is_saved_without_hiding_dialog_guidance()
    {
        using var f = new DesktopFixture();
        Pump(f.Window);
        Assert.True(f.Window.ContextGuidance.IsVisible);
        var dismiss = Assert.Single(f.Window.ContextGuidance.GetVisualDescendants().OfType<Button>(),
            button => Equals(button.Content, "Dismiss guidance"));
        dismiss.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.False(f.Window.ContextGuidance.IsVisible);
        Assert.False(new SettingsStore(f.Shell.Paths.SettingsDirectory).Load().ShowContextGuidance);
        var window = new SettingsWindow(f.Shell.Settings);
        window.Show(f.Window);
        try
        {
            Pump(window);
            Assert.Contains(window.GetVisualDescendants().OfType<GuidanceStrip>(), strip => strip.IsEffectivelyVisible);
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<Button>(), button => Equals(button.Content, "Dismiss guidance") && button.IsEffectivelyVisible);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Empty_workspace_offers_next_steps_and_created_note_receives_editor_focus()
    {
        using var f = new DesktopFixture();
        Assert.True(await f.Window.CreateWorkspaceAtAsync(f.Docs, "Empty"));
        Pump(f.Window);
        var card = Assert.Single(f.Window.FileTree.GetVisualDescendants().OfType<Border>(),
            border => border.Name == "WorkspaceSectionCard");
        foreach (var label in new[] { "New note here", "Import existing files", "Learn workspaces" })
            Assert.Contains(card.GetVisualDescendants().OfType<Button>(), button => Equals(button.Content, label) && button.IsEffectivelyVisible);
        Assert.Empty(f.Window.OwnedWindows);
        card.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "New note here"))
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => f.Window.OwnedWindows.OfType<CreateItemWindow>().Any(), f.Window);
        var create = Assert.Single(f.Window.OwnedWindows.OfType<CreateItemWindow>());
        var input = create.GetVisualDescendants().OfType<TextBox>().Single(control => AutomationProperties.GetName(control) == "File name");
        input.Text = "first.md";
        create.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Create"))
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => f.Window.OwnedWindows.Count == 0 && f.Window.EditorControl.IsKeyboardFocusWithin, f.Window);
        Assert.True(File.Exists(Path.Combine(f.Shell.Workspace!.Root, "first.md")));
    }

    [AvaloniaFact]
    public async Task Inspector_backlinks_open_the_source_line_without_replacing_the_library_tree_and_tags_start_search()
    {
        using var f = new DesktopFixture();
        var target = await f.WriteAsync("target.md", "---\ntags: [planning]\n---\n# Target\n");
        var source = await f.WriteAsync("source.md", "# Source\n\n[Target](target.md)\n");
        Assert.True(await f.Shell.OpenWorkspaceAsync(f.Docs));
        await f.Shell.OpenFileAsync(target);
        await Until(() => f.Shell.Workspace!.Graph.GetBacklinks(target).Count > 0 && f.Shell.ActiveTab!.Analysis is not null, f.Window);
        f.Window.RefreshLibraryContents();
        await f.Window.ShowNavAsync("Files");
        f.Window.InspectorContent.SelectedIndex = 1;
        Pump(f.Window);
        var backlink = Assert.Single(f.Window.InspectorBacklinks.Items.OfType<SideRow>());
        Assert.Equal(source, backlink.Path);
        Assert.Contains("line 3", backlink.Detail);
        Assert.True(f.Window.FileTree.IsVisible);
        f.Window.InspectorBacklinks.SelectedItem = backlink;
        await Until(() => f.Shell.ActiveTab?.FilePath == source, f.Window);
        Assert.Equal(3, f.Window.EditorControl.Document.GetLineByOffset(f.Window.EditorControl.CaretOffset).LineNumber);
        Assert.True(f.Window.FileTree.IsVisible);
        await f.Shell.OpenFileAsync(target);
        f.Window.InspectorContent.SelectedIndex = 2;
        Pump(f.Window);
        var tag = Assert.Single(f.Window.TagList.GetVisualDescendants().OfType<Button>());
        tag.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var picker = Assert.Single(f.Window.OverlayLayer.Children.OfType<QuickPicker>());
        Assert.Equal("tag:\"planning\"", picker.Input.Text);
        await picker.QueryWork;
        Assert.NotEmpty(picker.Results.Items);
        Assert.Equal(5, picker.GetVisualDescendants().OfType<Button>().Count(button => AutomationProperties.GetName(button)?.StartsWith("Insert search filter ") == true));
        var title = picker.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "title:"));
        picker.Input.CaretIndex = picker.Input.SelectionStart = picker.Input.SelectionEnd = picker.Input.Text!.Length;
        title.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.EndsWith(" title:", picker.Input.Text);
        Assert.True(picker.Input.IsKeyboardFocusWithin);
        picker.Open("Other picker", "", _ => [], _ => { }, () => { });
        Assert.DoesNotContain(picker.GetVisualDescendants().OfType<Button>(), button => button.IsEffectivelyVisible
            && AutomationProperties.GetName(button)?.StartsWith("Insert search filter ") == true);
    }

    [AvaloniaFact]
    public async Task Failed_preview_has_an_inline_retry_and_retains_recovery_in_activity()
    {
        using var f = new DesktopFixture();
        var tab = f.Shell.NewDocument("# Keep this Markdown");
        await f.Window.RunCommandAsync(CommandIds.ViewSplit);
        f.Window.BeforePreviewBuild = () => throw new IOException("Synthetic preview failure");
        await f.Window.RebuildPreviewAsync();
        Pump(f.Window);
        Assert.True(f.Window.PreviewProblem.IsVisible);
        Assert.Null(f.Window.PreviewControl.Document);
        Assert.Equal(StatusSeverity.Error, f.Shell.Banner!.Severity);
        var activity = Assert.Single(f.Shell.Activities.Items, item => item.Phase.Contains("Synthetic preview failure"));
        Assert.Equal("Rebuild preview", Assert.Single(activity.Actions).Label);
        f.Window.BeforePreviewBuild = null;
        activity.Actions[0].Invoke();
        await Until(() => f.Window.PreviewControl.Document is not null, f.Window);
        Assert.False(f.Window.PreviewProblem.IsVisible);
        Assert.Null(f.Shell.Banner);
        Assert.Equal("# Keep this Markdown", tab.CurrentText);
        Assert.Contains(f.Shell.Activities.Items, item => item.Id == activity.Id && item.State == ActivityState.Failed);
    }

    [AvaloniaFact]
    public async Task First_export_opens_the_picker_and_exports_without_another_export_click()
    {
        using var f = new DesktopFixture();
        var target = Path.Combine(f.Docs, "copy.html");
        var picks = 0;
        var window = new ExportWindow("source.md", null, "# A copy", MarkdownProfile.Extended, null, ExportFormat.Html)
        {
            PickOutputFile = format => { picks++; Assert.Equal(ExportFormat.Html, format); return Task.FromResult<string?>(target); }
        };
        window.Show(f.Window);
        try
        {
            Assert.Equal("Export a copy", window.Title);
            Assert.True(await window.RequestExportAsync());
            Assert.Equal(1, picks);
            Assert.Contains("A copy", await File.ReadAllTextAsync(target));
            window.Format = ExportFormat.Pdf;
            var path = Assert.Single(window.GetVisualDescendants().OfType<TextBox>(),
                control => AutomationProperties.GetName(control) == "Output file");
            Assert.Equal(Path.Combine(f.Docs, "copy.pdf"), path.Text);
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<Button>(), button => button.IsVisible && Equals(button.Content, "Open file"));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Repeated_export_requests_share_one_picker_and_cancel_writes_nothing()
    {
        using var f = new DesktopFixture();
        var choice = new TaskCompletionSource<string?>();
        var picks = 0;
        var window = new ExportWindow("draft.md", null, "# Draft", MarkdownProfile.Extended, null, ExportFormat.Html)
        {
            PickOutputFile = _ => { picks++; return choice.Task; }
        };
        window.Show(f.Window);
        try
        {
            var pending = window.RequestExportAsync();
            Assert.False(await window.RequestExportAsync());
            Assert.Equal(1, picks);
            choice.SetResult(null);
            Assert.False(await pending);
            Assert.Contains("canceled", window.StatusText);
            Assert.Empty(Directory.GetFiles(f.Docs));
        }
        finally { choice.TrySetResult(null); window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Replace_all_shortcut_is_scoped_to_find_and_preserves_one_undo(bool focusReplacement)
    {
        using var f = new DesktopFixture();
        var tab = f.Shell.NewDocument("cat cat");
        await f.Window.RunCommandAsync(CommandIds.ViewSource);
        f.Window.ShowFindBar(true);
        f.Window.ReplaceBox.Text = "dog";
        await f.Window.SearchAsync("cat");
        Pump(f.Window);
        f.Window.FindBox.Focus();
        f.Window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
        Assert.True(f.Window.ReplaceBox.IsKeyboardFocusWithin);
        (focusReplacement ? f.Window.ReplaceBox : f.Window.FindBox).Focus();
        f.Window.KeyPress(Key.Enter, RawInputModifiers.Control | RawInputModifiers.Alt, PhysicalKey.Enter, null);
        await Until(() => tab.CurrentText == "dog dog", f.Window);
        f.Window.EditorControl.Undo();
        Assert.Equal("cat cat", tab.CurrentText);
        f.Window.EditorControl.Focus();
        f.Window.KeyPress(Key.Enter, RawInputModifiers.Control | RawInputModifiers.Alt, PhysicalKey.Enter, null);
        Assert.DoesNotContain("dog", tab.CurrentText);
    }

    [AvaloniaFact]
    public async Task Limited_results_explain_why_replace_all_is_disabled()
    {
        using var f = new DesktopFixture();
        f.Shell.NewDocument(new string('a', 100_001));
        f.Window.ShowFindBar(true);
        await f.Window.SearchAsync("a");
        Assert.False(f.Window.ReplaceAllButton.IsEnabled);
        Assert.True(ToolTip.GetShowOnDisabled(f.Window.ReplaceAllButton));
        Assert.Contains("limited", ToolTip.GetTip(f.Window.ReplaceAllButton)?.ToString());
    }

    [AvaloniaFact]
    public async Task Help_entry_points_reuse_one_window_and_navigate_to_the_requested_feature()
    {
        using var f = new DesktopFixture();
        await f.Window.RunCommandAsync(CommandIds.Help);
        var guide = Assert.Single(f.Window.OwnedWindows.OfType<HelpWindow>());
        var settings = new SettingsWindow(f.Shell.Settings);
        var shortcuts = new ShortcutsWindow(f.Shell.Registry);
        settings.Show(f.Window);
        shortcuts.Show(f.Window);
        try
        {
            var fromSettings = HelpWindow.ShowFor(settings, () => throw new InvalidOperationException("Must reuse the open guide"), CommandIds.ExportAs);
            Assert.Same(guide, fromSettings);
            Pump(guide);
            Assert.Equal(f.Shell.Registry.Commands.Single(command => command.Id == CommandIds.ExportAs).Title,
                guide.GetVisualDescendants().OfType<TextBlock>().Single(control => AutomationProperties.GetName(control) == "Lesson title").Text);
            Assert.Same(guide, HelpWindow.ShowFor(shortcuts, () => throw new InvalidOperationException("Must reuse the open guide"), CommandIds.Find));
            await f.Window.RunCommandAsync(CommandIds.Help);
            Assert.Same(guide, Assert.Single(f.Window.OwnedWindows.OfType<HelpWindow>()));
            Assert.DoesNotContain(settings.OwnedWindows, child => child is HelpWindow);
            Assert.DoesNotContain(shortcuts.OwnedWindows, child => child is HelpWindow);
        }
        finally { guide.Close(); shortcuts.Close(); settings.Close(); }
    }

    [AvaloniaFact]
    public async Task Middle_click_uses_the_normal_unsaved_confirmation_and_keeps_canceled_edits()
    {
        using var f = new DesktopFixture();
        var target = f.Shell.NewDocument("Keep these unsaved edits");
        var active = f.Shell.NewDocument("Current document");
        Pump(f.Window);
        var container = Assert.Single(f.Window.TabStrip.GetVisualDescendants().OfType<ListBoxItem>(),
            item => ReferenceEquals(item.DataContext, target));
        var point = container.TranslatePoint(new Point(20, 15), f.Window)!.Value;
        f.Window.MouseDown(point, MouseButton.Middle);
        f.Window.MouseUp(point, MouseButton.Middle);
        await Until(() => f.Dialogs.SavePrompts == 1, f.Window);
        Assert.Contains(target, f.Shell.Tabs);
        Assert.Same(target, f.Shell.ActiveTab);
        Assert.Equal("Keep these unsaved edits", target.CurrentText);
        Assert.True(target.IsDirty);
        f.Shell.ActiveTab = active;
        f.Dialogs.SaveChoice = SaveChoice.Discard;
        f.Window.MouseDown(point, MouseButton.Middle);
        f.Window.MouseUp(point, MouseButton.Middle);
        await Until(() => !f.Shell.Tabs.Contains(target), f.Window);
        Assert.Same(active, f.Shell.ActiveTab);
    }

    private static void Pump(Window window)
    { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }

    private static async Task Until(Func<bool> done, Window window)
    {
        for (var attempt = 0; attempt < 100 && !done(); attempt++)
        { Pump(window); await Task.Delay(10, TestContext.Current.CancellationToken); }
        Assert.True(done(), $"Active: {window.IsActive}; owned: {string.Join(", ", window.OwnedWindows.Select(child => child.Title))}; focus: {window.FocusManager?.GetFocusedElement()}; " +
            (window is MainWindow main ? $"editor visible/enabled: {main.EditorControl.IsEffectivelyVisible}/{main.EditorControl.IsEffectivelyEnabled}; text: {main.EditorControl.Text}" : ""));
    }
}
