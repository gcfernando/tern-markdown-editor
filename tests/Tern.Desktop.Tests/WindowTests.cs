using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tern.App.Commands;
using Tern.App.Settings;
using Tern.App.Shell;
using Tern.Desktop.Themes;
using Tern.Desktop.Views;

namespace Tern.Desktop.Tests;

public class WindowTests
{
    private const string Sample = "---\ntitle: Sample\ntags: [alpha, beta]\n---\n\n# Heading One\n\nSome **bold** text and a [link](https://example.com).\n\n## Second\n\n- [ ] open task\n- [x] done task\n\n```csharp\nvar x = 1;\n```\n";

    private static async Task Settle()
    {
        await Task.Delay(400);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Window_opens_with_shell_and_empty_state()
    {
        using var f = new DesktopFixture();
        Assert.Same(f.Shell, f.Window.Shell);
        Assert.Empty(f.Shell.Tabs);
        Assert.True(f.Window.IsVisible);
    }

    [AvaloniaFact]
    public async Task Document_preview_uses_the_browser_reading_font_independently_of_window_chrome()
    {
        using var f = new DesktopFixture();
        f.Window.FontFamily = new Avalonia.Media.FontFamily("Inter");
        f.Window.ApplySettings();
        await f.Shell.OpenFileAsync(await f.WriteAsync("reading-font.md", "# Heading\n\nReadable body text.\n"));
        await Settle();
        Assert.StartsWith("Segoe UI", f.Window.PreviewControl.FontFamily.Name, StringComparison.Ordinal);
        var paragraph = Assert.Single(f.Window.PreviewControl.GetVisualDescendants().OfType<Avalonia.Controls.SelectableTextBlock>(),
            text => text.Inlines?.OfType<Avalonia.Controls.Documents.Run>().Any(run => run.Text == "Readable body text.") == true);
        Assert.StartsWith("Segoe UI", paragraph.FontFamily.Name, StringComparison.Ordinal);
        Assert.Equal(16, f.Window.PreviewControl.FontSize);
    }

    [AvaloniaFact]
    public async Task Opening_a_file_shows_text_and_preview()
    {
        using var f = new DesktopFixture();
        var path = await f.WriteAsync("a.md", Sample);
        await f.Shell.OpenFileAsync(path);
        await Settle();

        Assert.Equal(Sample.Replace("\r\n", "\n"), f.Window.EditorControl.Document.Text.Replace("\r\n", "\n"));
        Assert.NotNull(f.Window.PreviewControl.Document);
        Assert.Equal("a.md", f.Window.TabStrip.Items.OfType<object>().Count() == 1 ? f.Shell.ActiveTab!.DisplayName : null);
    }

    [AvaloniaFact]
    public async Task Typing_marks_dirty_and_refreshes_preview()
    {
        using var f = new DesktopFixture();
        await f.Shell.OpenFileAsync(await f.WriteAsync("a.md", "# One\n"));
        await Settle();
        var before = f.Window.PreviewControl.Document;
        Assert.False(f.Shell.ActiveTab!.IsDirty);

        f.Window.EditorControl.Document.Insert(f.Window.EditorControl.Document.TextLength, "\n## Added\n");
        await Settle();

        Assert.True(f.Shell.ActiveTab.IsDirty);
        Assert.NotSame(before, f.Window.PreviewControl.Document);
        Assert.Contains("Added", f.Shell.ActiveTab.CurrentText);
    }

    [AvaloniaFact]
    public async Task Undo_and_redo_keep_the_session_in_sync()
    {
        using var f = new DesktopFixture();
        await f.Shell.OpenFileAsync(await f.WriteAsync("a.md", "# One\n"));
        await Settle();
        var editor = f.Window.EditorControl;
        editor.Document.Insert(0, "X");
        Assert.StartsWith("X# One", f.Shell.ActiveTab!.CurrentText, StringComparison.Ordinal);

        editor.Document.UndoStack.Undo();
        Assert.StartsWith("# One", f.Shell.ActiveTab.CurrentText, StringComparison.Ordinal);
        editor.Document.UndoStack.Redo();
        Assert.StartsWith("X# One", f.Shell.ActiveTab.CurrentText, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task Mode_buttons_switch_visible_panes()
    {
        using var f = new DesktopFixture();
        await f.Shell.OpenFileAsync(await f.WriteAsync("a.md", "# One\n"));
        await f.Window.RunCommandAsync(CommandIds.ViewSource);
        Assert.True(f.Window.EditorHost.IsVisible);
        Assert.False(f.Window.PreviewHost.IsVisible);

        await f.Window.RunCommandAsync(CommandIds.ViewReading);
        Assert.False(f.Window.EditorHost.IsVisible);
        Assert.True(f.Window.PreviewHost.IsVisible);

        await f.Window.RunCommandAsync(CommandIds.ViewSplit);
        Assert.True(f.Window.EditorHost.IsVisible);
        Assert.True(f.Window.PreviewHost.IsVisible);
        Assert.Equal(ViewMode.Split, f.Shell.ViewMode);
    }

    [AvaloniaFact]
    public async Task Closing_a_dirty_tab_prompts_and_cancel_keeps_it()
    {
        using var f = new DesktopFixture();
        await f.Shell.OpenFileAsync(await f.WriteAsync("a.md", "# One\n"));
        f.Window.EditorControl.Document.Insert(0, "dirty ");

        f.Dialogs.SaveChoice = SaveChoice.Cancel;
        await f.Window.RunCommandAsync(CommandIds.CloseTab);
        Assert.Equal(1, f.Dialogs.SavePrompts);
        Assert.Single(f.Shell.Tabs);

        f.Dialogs.SaveChoice = SaveChoice.Discard;
        await f.Window.RunCommandAsync(CommandIds.CloseTab);
        Assert.Empty(f.Shell.Tabs);
    }

    [AvaloniaFact]
    public async Task Command_palette_filters_and_runs_commands()
    {
        using var f = new DesktopFixture();
        f.Shell.NewDocument("text");
        f.Window.ShowPalette();
        Assert.True(f.Window.IsOverlayOpen);
        var picker = f.Window.Picker!;
        picker.Input.Text = "reading";
        await Settle();
        Assert.NotEmpty(picker.Results.Items.OfType<object>());
        Assert.True(picker.Results.Items.OfType<object>().Count() < 20);

        picker.Results.SelectedIndex = 0;
        picker.Input.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        await Settle();
        Assert.False(f.Window.IsOverlayOpen);
        Assert.Equal(ViewMode.Reading, f.Shell.ViewMode);
    }

    [AvaloniaFact]
    public async Task Quick_open_lists_workspace_files_and_headings()
    {
        using var f = new DesktopFixture();
        await f.WriteAsync("alpha.md", "# Alpha Title\n\n## Deep Section\n");
        await f.WriteAsync("notes\\beta.md", "# Beta\n");
        await f.Shell.OpenWorkspaceAsync(f.Docs);
        await f.Shell.OpenFileAsync(Path.Combine(f.Docs, "alpha.md"));
        await Settle();

        f.Window.ShowQuickOpen("beta");
        await Settle();
        Assert.Single(f.Window.Picker!.Results.Items.OfType<object>());
        f.Window.HideOverlay();

        f.Window.ShowQuickOpen("#deep");
        await Settle();
        Assert.Single(f.Window.Picker!.Results.Items.OfType<object>());
    }

    [AvaloniaFact]
    public async Task Find_and_replace_work_with_options()
    {
        using var f = new DesktopFixture();
        await f.Shell.OpenFileAsync(await f.WriteAsync("a.md", "Cat cat concat\n"));
        f.Window.ShowFindBar(true);
        f.Window.FindBox.Text = "cat";
        await Settle();
        Assert.Contains("3", f.Window.FindStatusText, StringComparison.Ordinal);

        f.Window.CaseToggle.IsChecked = true;
        f.Window.WordToggle.IsChecked = true;
        await f.Window.SearchAsync("cat");
        Assert.Contains("1", f.Window.FindStatusText, StringComparison.Ordinal);

        f.Window.ReplaceBox.Text = "dog";
        await f.Window.ReplaceAllAsync();
        Assert.Equal("Cat dog concat\n", f.Window.EditorControl.Document.Text);
    }

    [AvaloniaFact]
    public async Task Sidebar_tree_lists_workspace_and_opens_files()
    {
        using var f = new DesktopFixture();
        await f.WriteAsync("one.md", "# One\n");
        await f.WriteAsync("sub\\two.md", "# Two\n");
        await f.Shell.OpenWorkspaceAsync(f.Docs);
        await Settle();

        Assert.NotEmpty(f.Window.FileTree.Items.OfType<object>());
        await f.Window.ShowNavAsync("Files");
        Assert.Equal("Files", f.Window.ActiveNav);
    }

    [AvaloniaFact]
    public async Task Outline_lists_headings_and_click_navigates()
    {
        using var f = new DesktopFixture();
        await f.Shell.OpenFileAsync(await f.WriteAsync("a.md", Sample));
        await Settle();
        Assert.Equal(2, f.Window.OutlineList.Items.OfType<object>().Count());

        f.Window.OutlineList.SelectedIndex = 1;
        await Settle();
        var line = f.Window.EditorControl.Document.GetLineByOffset(f.Window.EditorControl.CaretOffset).LineNumber;
        Assert.Equal(10, line);
    }

    [AvaloniaFact]
    public void Theme_switching_changes_variant_and_resources()
    {
        using var f = new DesktopFixture();
        f.Shell.Settings.Theme = ThemeChoice.Paper;
        f.Window.ApplyTheme();
        Assert.Equal(ThemeManager.VariantFor(ThemeChoice.Paper), Application.Current!.RequestedThemeVariant);
        var paper = Lookup(ThemeManager.Bg);

        f.Shell.Settings.Theme = ThemeChoice.Midnight;
        f.Window.ApplyTheme();
        var midnight = Lookup(ThemeManager.Bg);
        Assert.NotEqual(paper, midnight);
        Assert.NotNull(Lookup("Tern.Preview.Background"));
    }

    [AvaloniaFact]
    public async Task Shortcut_rebinding_applies_and_reports_conflicts()
    {
        using var f = new DesktopFixture();
        var window = new ShortcutsWindow(f.Shell.Registry);
        window.Show();

        Assert.True(await window.TryAssignAsync(CommandIds.ViewReading, "Ctrl+Alt+F9"));
        Assert.Equal("Ctrl+Alt+F9", f.Shell.Registry.GetGesture(CommandIds.ViewReading));

        var cancelled = window.TryAssignAsync(CommandIds.ViewSource, "Ctrl+Alt+F9");
        await AnswerPrompt(window, 1);
        Assert.False(await cancelled);
        Assert.Equal("Ctrl+Alt+F9", f.Shell.Registry.GetGesture(CommandIds.ViewReading));

        var moved = window.TryAssignAsync(CommandIds.ViewSource, "Ctrl+Alt+F9");
        await AnswerPrompt(window, 0);
        Assert.True(await moved);
        Assert.Equal("Ctrl+Alt+F9", f.Shell.Registry.GetGesture(CommandIds.ViewSource));
        window.Close();
    }

    private static async Task AnswerPrompt(Avalonia.Controls.Window owner, int choice)
    {
        for (var i = 0; i < 50; i++)
        {
            Dispatcher.UIThread.RunJobs();
            if (owner.OwnedWindows.OfType<PromptWindow>().FirstOrDefault() is { } prompt)
            {
                prompt.Close(choice);
                return;
            }

            await Task.Delay(50);
        }

        Assert.Fail("The conflict prompt never appeared.");
    }

    [AvaloniaFact]
    public async Task Dropping_a_file_and_a_folder_opens_them()
    {
        using var f = new DesktopFixture();
        var file = await f.WriteAsync("drop.md", "# Dropped\n");
        await f.WriteAsync("inside\\x.md", "# X\n");

        await f.Window.OpenDroppedAsync([file, Path.Combine(f.Docs, "inside")]);
        Assert.NotNull(f.Shell.Workspace);
        Assert.Contains(f.Shell.Tabs, t => t.DisplayName == "drop.md");
    }

    [AvaloniaFact]
    public async Task Formatting_command_wraps_selection_in_one_undo_step()
    {
        using var f = new DesktopFixture();
        await f.Shell.OpenFileAsync(await f.WriteAsync("a.md", "word\n"));
        f.Window.EditorControl.Select(0, 4);
        await f.Window.RunCommandAsync(CommandIds.Bold);
        Assert.StartsWith("**word**", f.Window.EditorControl.Document.Text, StringComparison.Ordinal);

        f.Window.EditorControl.Document.UndoStack.Undo();
        Assert.StartsWith("word", f.Window.EditorControl.Document.Text, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task Main_window_screenshots_are_rendered()
    {
        using var f = new DesktopFixture();
        await f.WriteAsync("README.md", "# Readme\n");
        await f.WriteAsync("notes\\ideas.md", "# Ideas\n\n- [ ] one\n");
        await f.Shell.OpenWorkspaceAsync(f.Docs);
        await f.Shell.OpenFileAsync(await f.WriteAsync("sample.md", Sample));
        f.Shell.Settings.Theme = ThemeChoice.Midnight;
        f.Window.ApplyTheme();
        await Settle();
        Save(f.Window, "tern-main-midnight.png");

        f.Window.ShowPalette();
        await Settle();
        Save(f.Window, "tern-palette.png");
        f.Window.HideOverlay();

        f.Shell.Settings.Theme = ThemeChoice.Paper;
        f.Window.ApplyTheme();
        await Settle();
        Save(f.Window, "tern-main-paper.png");
    }

    [AvaloniaFact]
    public async Task Closing_from_the_library_removes_the_tab_but_keeps_the_file()
    {
        using var f = new DesktopFixture();
        var path = await f.WriteAsync("one.md", "# One\n");
        await f.Shell.OpenWorkspaceAsync(f.Docs);
        await f.Shell.OpenFileAsync(path);
        await f.Window.ShowNavAsync("Files");
        await Settle();
        var button = Assert.Single(f.Window.FileTree.GetVisualDescendants().OfType<Avalonia.Controls.Button>(), b =>
            b.IsVisible && Avalonia.Automation.AutomationProperties.GetName(b) == "Close document");
        button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        await Settle();
        Assert.Empty(f.Shell.Tabs);
        Assert.True(f.Window.EmptyText.IsVisible);
        Assert.Equal("# One\n", await File.ReadAllTextAsync(path));
    }

    [AvaloniaFact]
    public async Task Folder_close_cancels_safely_then_closes_documents_and_collapses()
    {
        using var f = new DesktopFixture();
        var path = await f.WriteAsync("notes/one.md", "# One\n");
        await f.Shell.OpenWorkspaceAsync(f.Docs);
        var tab = (await f.Shell.OpenFileAsync(path))!;
        await f.Window.ShowNavAsync("Files");
        var row = Assert.Single(Assert.Single(f.Window.FileTree.Items.OfType<Tern.Desktop.ViewModels.TreeRow>()).Children, r => r.Name == "notes");
        row.IsExpanded = true;
        tab.ApplyEdits([new Tern.Domain.Text.TextEdit(0, 0, "dirty ")]);
        Assert.False(await f.Window.CloseTreeFolderAsync(row));
        Assert.True(row.IsExpanded);
        Assert.Contains(tab, f.Shell.Tabs);
        f.Dialogs.SaveChoice = SaveChoice.Discard;
        Assert.True(await f.Window.CloseTreeFolderAsync(row));
        Assert.False(row.IsExpanded);
        Assert.Empty(f.Shell.Tabs);
        Assert.True(Directory.Exists(row.FullPath));
    }

    [AvaloniaFact]
    public async Task Workspace_card_closes_the_workspace_and_returns_to_the_welcome_canvas()
    {
        using var f = new DesktopFixture();
        var path = await f.WriteAsync("one.md", "# One\n");
        await f.Shell.OpenWorkspaceAsync(f.Docs);
        await f.Shell.OpenFileAsync(path);
        // The tree's row buttons are created by the next layout pass.
        f.Window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Assert.False(f.Window.WorkspaceCard.IsVisible);
        var button = Assert.Single(f.Window.FileTree.GetVisualDescendants().OfType<Avalonia.Controls.Button>(),
            control => Avalonia.Automation.AutomationProperties.GetName(control) == "Close this Library section"
                && control.IsEffectivelyVisible);
        button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        await Settle();
        Assert.Null(f.Shell.Workspace);
        Assert.False(f.Window.WorkspaceCard.IsVisible);
        Assert.True(f.Window.EmptyText.IsVisible);
    }

    [AvaloniaFact]
    public async Task Window_close_prompts_before_discarding_dirty_documents()
    {
        using var f = new DesktopFixture();
        var tab = f.Shell.NewDocument();
        tab.ApplyEdits([new Tern.Domain.Text.TextEdit(0, 0, "unsaved")]);
        f.Window.Close();
        await Settle();
        Assert.True(f.Window.IsVisible);
        Assert.Contains(tab, f.Shell.Tabs);
        f.Dialogs.SaveChoice = SaveChoice.Discard;
        f.Window.Close();
        await Settle();
        Assert.False(f.Window.IsVisible);
    }

    [AvaloniaFact]
    public async Task Welcome_and_compact_layouts_render()
    {
        using var f = new DesktopFixture();
        f.Shell.Settings.Theme = ThemeChoice.Midnight;
        f.Window.ApplyTheme();
        await Settle();
        Save(f.Window, "tern-studio-welcome.png");
        f.Window.Width = 1000;
        f.Window.Height = 720;
        await Settle();
        Assert.False(f.Window.RightPanel.IsVisible);
        Save(f.Window, "tern-studio-compact.png");
    }

    [Fact]
    public void Windows_entry_point_requires_sta_for_native_file_drops()
    {
        var entry = typeof(MainWindow).Assembly.EntryPoint;
        Assert.NotNull(entry);
        Assert.True(Attribute.IsDefined(entry, typeof(STAThreadAttribute)));
    }

    [AvaloniaFact]
    public async Task Creating_files_and_folders_updates_the_workspace_and_never_overwrites()
    {
        using var f = new DesktopFixture();
        await f.Shell.OpenWorkspaceAsync(f.Docs);
        await f.Window.CreateWorkspaceEntryAsync(true, "My notes");
        Assert.True(Directory.Exists(Path.Combine(f.Docs, "My notes")));
        await f.Window.CreateWorkspaceEntryAsync(false, "new-note");
        var path = Path.Combine(f.Docs, "new-note.md");
        Assert.True(File.Exists(path));
        Assert.Equal(path, f.Shell.ActiveTab!.FilePath);
        await File.WriteAllTextAsync(path, "keep me");
        await f.Window.CreateWorkspaceEntryAsync(false, "new-note.md");
        Assert.Equal("keep me", await File.ReadAllTextAsync(path));
        await f.Window.CreateWorkspaceEntryAsync(true, "../outside");
        Assert.Contains("name", f.Shell.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [AvaloniaFact]
    public void File_menu_exposes_essential_file_commands()
    {
        using var f = new DesktopFixture();
        var file = Assert.IsType<Avalonia.Controls.MenuItem>(f.Window.ApplicationMenu.Items[0]);
        var ids = file.Items.OfType<Avalonia.Controls.MenuItem>().Select(item => item.Tag).ToList();
        foreach (var id in new[] { CommandIds.NewFile, CommandIds.NewWorkspaceFile, CommandIds.NewFolder, CommandIds.OpenFile, CommandIds.OpenFolder, CommandIds.Save, CommandIds.SaveAs, CommandIds.SaveAll, CommandIds.Print })
            Assert.Contains(id, ids);
        Assert.Equal("Ctrl+P", f.Shell.Registry.GetGesture(CommandIds.Print));
        Assert.Equal("Ctrl+K", f.Shell.Registry.GetGesture(CommandIds.QuickOpen));
    }

    [AvaloniaFact]
    public async Task Plain_source_keeps_regular_weight_after_a_heading()
    {
        using var f = new DesktopFixture();
        f.Shell.NewDocument("# Heading\n\nPlain paragraph\n");
        await Settle();
        var line = Assert.Single(f.Window.EditorControl.TextArea.TextView.VisualLines, v => v.FirstDocumentLine.LineNumber == 3);
        Assert.All(line.Elements, element => Assert.Equal(Avalonia.Media.FontWeight.Normal, element.TextRunProperties.Typeface.Weight));
    }

    [AvaloniaFact]
    public async Task Broken_link_repair_is_source_preserving_and_undoable()
    {
        using var f = new DesktopFixture();
        const string original = "# Keep  spacing\r\n\r\n[broken](missing.md#details) and **unchanged**\r\n";
        var source = await f.WriteAsync("source.md", original);
        var target = await f.WriteAsync("correct target.md", "# Details\n");
        await f.Shell.OpenWorkspaceAsync(f.Docs);
        var tab = await f.Shell.OpenFileAsync(source);
        await f.Window.RepairLinkAsync(tab!, new Tern.Desktop.ViewModels.LinkRowView("broken", "missing.md#details", 3, true, "x"), target);
        Assert.Equal(original.Replace("missing.md", "correct%20target.md", StringComparison.Ordinal), tab!.CurrentText);
        Assert.Equal(original, await File.ReadAllTextAsync(source));
        f.Window.EditorControl.Document.UndoStack.Undo();
        Assert.Equal(original, tab.CurrentText);
    }

    [AvaloniaFact]
    public async Task Saved_history_restores_a_separate_unsaved_copy()
    {
        using var f = new DesktopFixture();
        var path = await f.WriteAsync("history.md", "# Original\n");
        var original = await f.Shell.OpenFileAsync(path);
        original!.ApplyEdits([new Tern.Domain.Text.TextEdit(0, 0, "First save\n")]);
        Assert.True(await f.Shell.SaveTabAsync(original));
        original.ApplyEdits([new Tern.Domain.Text.TextEdit(0, 0, "Second save\n")]);
        Assert.True(await f.Shell.SaveTabAsync(original));
        var versions = await f.Shell.HistoryStore.ReadAsync(path);
        Assert.Equal(2, versions.Count);
        var showing = f.Window.RunCommandAsync(CommandIds.ShowHistory);
        await Settle();
        var history = Assert.Single(f.Window.OwnedWindows.OfType<HistoryWindow>());
        history.Close(versions[1].Text);
        await showing;
        Assert.NotSame(original, f.Shell.ActiveTab);
        Assert.True(f.Shell.ActiveTab!.IsUntitled);
        Assert.True(f.Shell.ActiveTab.IsDirty);
        Assert.Equal(versions[1].Text, f.Shell.ActiveTab.CurrentText);
        Assert.Equal(versions[0].Text, await File.ReadAllTextAsync(path));
    }

    [AvaloniaFact]
    public async Task Flagship_workspace_renders_in_both_themes()
    {
        using var f = new DesktopFixture();
        const string source = "# System architecture\n\nTern is a fully offline, local-first desktop application for editing and viewing Markdown documents. It is designed to be fast, reliable, and respectful of your data, with a modular architecture that separates the editor, rendering pipeline, and workspace services.\n\n## Core principles\n\n- 100% offline functionality\n- Local files remain under your control\n- Fast and responsive, even with large documents\n- Secure handling of untrusted content\n- A clean, flexible architecture for long-term maintenance\n\n## High-level components\n\nThe application is composed of several key subsystems:\n\n| Component | Responsibility | Key Technologies |\n|---|---|---|\n| Editor | Markdown editing, syntax highlighting | Avalonia, AvaloniaEdit |\n| Renderer | Parse and render Markdown | Markdig, native preview |\n| Workspace | File management, search, indexing | .NET, SQLite |\n\n## Example: Rendering a document\n\nThe following example shows how a Markdown document is parsed and rendered using the internal pipeline:\n\n```csharp\nusing Tern.Markdown;\nusing Tern.Rendering.Preview;\n\nvar document = PreviewBuilder.Build(source, options);\n```\n\n## Next steps\n\n- Review the [data flow](data-flow.md) for more details.\n- See the [architecture decisions](decisions.md).\n";
        var path = await f.WriteAsync("architecture/overview.md", source);
        await f.WriteAsync("architecture/data-flow.md", "# Data flow\n");
        await f.WriteAsync("architecture/decisions.md", "# Decisions\n");
        await f.WriteAsync("guides/getting-started.md", "# Getting started\n");
        await f.WriteAsync("api/api-guide.md", "# API guide\n");
        await f.Shell.OpenWorkspaceAsync(f.Docs);
        await f.Shell.OpenFileAsync(path);
        await f.Window.ShowNavAsync("Files");
        f.Window.Width = 1536;
        f.Window.Height = 1024;
        f.Shell.Settings.SidebarWidth = 226;
        f.Window.ApplySettings();
        f.Shell.Settings.Theme = ThemeChoice.Midnight;
        f.Window.ApplyTheme();
        await f.Window.RebuildPreviewAsync();
        await Settle();
        Save(f.Window, "tern-workspace-midnight.png");
        f.Shell.Settings.Theme = ThemeChoice.Paper;
        f.Window.ApplyTheme();
        await Settle();
        Save(f.Window, "tern-workspace-paper.png");
    }

    [AvaloniaFact]
    public async Task Focus_mode_restores_panel_widths_and_preserves_source()
    {
        using var f = new DesktopFixture();
        var tab = f.Shell.NewDocument("# Untouched\n");
        var left = f.Window.Body.ColumnDefinitions[0].Width;
        var right = f.Window.Body.ColumnDefinitions[4].Width;
        var revision = tab.Session.Revision;
        await f.Window.RunCommandAsync(CommandIds.ToggleFocus);
        Assert.True(f.Window.IsFocusMode);
        Assert.False(f.Window.Sidebar.IsVisible);
        Assert.False(f.Window.RightPanel.IsVisible);
        f.Window.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
        Assert.False(f.Window.IsFocusMode);
        Assert.Equal(left, f.Window.Body.ColumnDefinitions[0].Width);
        Assert.Equal(right, f.Window.Body.ColumnDefinitions[4].Width);
        Assert.Equal(revision, tab.Session.Revision);
    }

    [AvaloniaFact]
    public async Task Reference_pane_tracks_selected_document_without_switching_active_tab()
    {
        using var f = new DesktopFixture();
        var first = f.Shell.NewDocument("# First\n");
        var second = f.Shell.NewDocument("# Second\n");
        await f.Window.RunCommandAsync(CommandIds.ToggleReference);
        f.Window.ReferenceTabs.SelectedItem = first;
        Assert.Same(second, f.Shell.ActiveTab);
        Assert.Equal("# First\n", f.Window.ReferenceEditor.Text);
        first.ApplyEdits([new Tern.Domain.Text.TextEdit(0, 0, "Changed ")]);
        Assert.StartsWith("Changed", f.Window.ReferenceEditor.Text, StringComparison.Ordinal);
        Assert.True(f.Window.ReferenceEditor.IsReadOnly);
        await f.Window.RunCommandAsync(CommandIds.ToggleReference);
        Assert.False(f.Window.ReferencePane.IsVisible);
    }

    [AvaloniaFact]
    public async Task Preview_cannot_publish_an_old_tab_or_revision()
    {
        using var f = new DesktopFixture();
        f.Shell.NewDocument("# Old\n" + new string('x', 500_000));
        var oldWork = f.Window.RebuildPreviewAsync();
        var current = f.Shell.NewDocument("# New\n");
        var newerWork = f.Window.RebuildPreviewAsync();
        current.ApplyEdits([new Tern.Domain.Text.TextEdit(2, 3, "Latest")]);
        await f.Window.RebuildPreviewAsync();
        await Task.WhenAll(oldWork, newerWork);
        Assert.Equal("Latest", Assert.Single(f.Window.PreviewControl.Document!.Outline).Text);
    }

    [AvaloniaFact]
    public async Task File_drop_routes_from_editor_to_window()
    {
        using var f = new DesktopFixture();
        var path = await f.WriteAsync("routed.md", "# Routed\n");
        using var data = new DataTransfer();
        var item = await f.Window.StorageProvider.TryGetFileFromPathAsync(new Uri(path));
        Assert.NotNull(item);
        data.Add(DataTransferItem.CreateFile(item));
        var over = new DragEventArgs(DragDrop.DragOverEvent, data, f.Window.EditorControl, new Point(10, 10), KeyModifiers.None);
        f.Window.EditorControl.RaiseEvent(over);
        Assert.True(over.Handled);
        Assert.Equal(DragDropEffects.Copy, over.DragEffects);
        var drop = new DragEventArgs(DragDrop.DropEvent, data, f.Window.EditorControl, new Point(10, 10), KeyModifiers.None);
        f.Window.EditorControl.RaiseEvent(drop);
        await Settle();
        Assert.True(drop.Handled);
        Assert.Contains(f.Shell.Tabs, t => t.FilePath == path);
    }

    [AvaloniaFact]
    public async Task Dropping_an_unsupported_file_does_not_open_binary_data()
    {
        using var f = new DesktopFixture();
        var path = await f.WriteAsync("binary.exe", "not Markdown");
        await f.Window.OpenDroppedAsync([path]);
        Assert.Empty(f.Shell.Tabs);
        Assert.Contains("Markdown", f.Shell.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Desktop_networking_references_and_state_are_confined_to_reviewed_integrations()
    {
        var assembly = typeof(MainWindow).Assembly;
        // The user explicitly enabled actual remote image retrieval. This permits four
        // BCL assemblies for the one audited service, not general networking in the UI.
        var allowed = new[] { "System.Net.Http", "System.Net.Sockets", "System.Net.NameResolution", "System.Net.Primitives" };
        var networkReferences = assembly.GetReferencedAssemblies().Select(reference => reference.Name!)
            .Where(name => name.StartsWith("System.Net", StringComparison.Ordinal)).ToList();
        Assert.Contains("System.Net.Http", networkReferences);
        Assert.All(networkReferences, name => Assert.Contains(name, allowed));
        var networkFields = assembly.GetTypes().SelectMany(type => type.GetFields(System.Reflection.BindingFlags.Public
            | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static
            | System.Reflection.BindingFlags.DeclaredOnly)).Where(field => ContainsNetworkType(field.FieldType)).ToList();
        Assert.NotEmpty(networkFields);
        foreach (var field in networkFields)
        {
            var owner = field.DeclaringType!;
            while (owner.DeclaringType is { } parent) owner = parent;
            Assert.Contains(owner, new[] { typeof(Tern.Desktop.Services.RemoteImageLoader), typeof(Tern.Desktop.Services.AIConnectionClient), typeof(Tern.Desktop.Services.MCPConnectionClient), typeof(Tern.Desktop.Services.MarkdownUrlLoader) });
        }
        // The Security suite additionally audits actual IL calls/constructors and the
        // exact source-file exception, covering methods that do not retain network fields.
        static bool ContainsNetworkType(Type type) => type.Namespace?.StartsWith("System.Net", StringComparison.Ordinal) == true
            || type.IsArray && ContainsNetworkType(type.GetElementType()!)
            || type.IsGenericType && type.GetGenericArguments().Any(ContainsNetworkType);
    }

    private static object? Lookup(string key)
    {
        var app = Application.Current!;
        return app.TryGetResource(key, app.ActualThemeVariant, out var value) ? value : null;
    }

    private static void Save(Avalonia.Controls.Window window, string name)
    {
        Dispatcher.UIThread.RunJobs();
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        using var stream = File.Create(Path.Combine(Screenshots.Folder, name));
        frame.Save(stream, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }
}
