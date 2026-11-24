using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tern.App.Settings;
using Tern.App.Commands;
using Tern.Desktop.ViewModels;
using Tern.Desktop.Services;
using Tern.Desktop.Views;
using Tern.Domain.Text;
using Tern.Markdown;

namespace Tern.Desktop.Tests;

public sealed class AdvancedWritingWorkflowTests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Switching_between_long_selected_and_short_documents_preserves_each_selection_and_close_can_render_during_safety_copy(bool animated)
    {
        var reader = MotionPolicy.SystemAnimationsReader;
        MotionPolicy.SystemAnimationsReader = () => animated;
        try
        {
            using var f = new DesktopFixture();
            var shortTab = f.Shell.NewDocument("tiny");
            f.Window.EditorControl.Select(1, 2);
            var longTab = f.Shell.NewDocument("a substantially longer selected document");
            f.Window.EditorControl.Select(5, 25);
            Pump(f.Window);
            Assert.Equal(!animated, MotionPolicy.IsReduced);
            var tabPresenter = f.Window.FindControl<ListBox>("TabStrip")!.GetVisualDescendants().OfType<ListBoxItem>().First().GetVisualDescendants()
                .OfType<Avalonia.Controls.Presenters.ContentPresenter>().First(presenter => presenter.Name == "PART_ContentPresenter");
            Assert.Equal(animated, tabPresenter.Transitions?.Count > 0);
            for (var index = 0; index < 12; index++)
            {
                f.Shell.ActiveTab = shortTab;
                Pump(f.Window);
                Assert.Equal("in", f.Window.EditorControl.SelectedText);
                f.Shell.ActiveTab = longTab;
                Pump(f.Window);
                Assert.Equal(longTab.CurrentText.Substring(5, 25), f.Window.EditorControl.SelectedText);
                Assert.Equal(1, f.Window.EditorControl.Opacity);
                Assert.Equal(1, f.Window.EditorControl.TextArea.Opacity);
            }
            var originalLong = longTab.CurrentText;
            f.Window.EditorControl.SelectedText = "replacement";
            Assert.Equal("tiny", shortTab.CurrentText);
            Assert.NotEqual(originalLong, longTab.CurrentText);
            await f.Window.RunCommandAsync(CommandIds.Undo);
            Assert.Equal(originalLong, longTab.CurrentText);
            f.Shell.ActiveTab = shortTab;
            Pump(f.Window);
            f.Window.EditorControl.SelectedText = "Q";
            Assert.Equal("tQy", shortTab.CurrentText);
            Assert.Equal(originalLong, longTab.CurrentText);
            await f.Window.RunCommandAsync(CommandIds.Undo);
            Assert.Equal("tiny", shortTab.CurrentText);
            f.Shell.ActiveTab = longTab;
            f.Window.EditorControl.Select(5, 25);
            f.Dialogs.SaveChoice = Tern.App.Shell.SaveChoice.Discard;
            var closing = f.Shell.CloseDocumentsAsync();
            while (!closing.IsCompleted) { Pump(f.Window); await Task.Delay(10); }
            Assert.True(await closing);
            Pump(f.Window);
            Assert.Empty(f.Shell.Tabs);
            Assert.Equal(2, f.Shell.RecentlyDiscarded().Entries.Count);
        }
        finally { MotionPolicy.SystemAnimationsReader = reader; MotionPolicy.Apply(false); }
    }

    [AvaloniaFact]
    public async Task Hybrid_source_renders_real_safe_images_and_tasks_preserves_markdown_and_undo_and_can_be_disabled()
    {
        using var f = new DesktopFixture();
        using (var bitmap = new SkiaSharp.SKBitmap(32, 20))
        {
            bitmap.Erase(SkiaSharp.SKColors.SteelBlue);
            using var image = SkiaSharp.SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
            await File.WriteAllBytesAsync(Path.Combine(f.Docs, "dot.png"), data.ToArray());
        }
        const string original = "Introduction\n\n# Heading\n\n- [ ] actual task\n\nbefore ![dot](dot.png) after\n\n**bold**\n";
        f.Shell.Settings.HybridSourceEnabled = true;
        f.Window.ApplySettings();
        var tab = (await f.Shell.OpenFileAsync(await f.WriteAsync("hybrid.md", original)))!;
        await Ready(tab);
        Pump(f.Window);
        var editor = f.Window.EditorControl;
        var thumbnail = Assert.Single(editor.GetVisualDescendants().OfType<Tern.PreviewView.SourceImageThumbnail>());
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!thumbnail.IsLoaded && DateTime.UtcNow < deadline) { await Task.Delay(20); Pump(f.Window); }
        Assert.True(thumbnail.IsLoaded);
        var heading = editor.TextArea.TextView.VisualLines.Single(line => line.FirstDocumentLine.LineNumber == 3);
        Assert.Contains(heading.Elements, element => element.TextRunProperties.FontRenderingEmSize > editor.FontSize);
        Assert.True(Application.Current!.TryGetResource(Tern.Desktop.Themes.ThemeManager.Muted, Application.Current.ActualThemeVariant, out var muted));
        Assert.Equal(muted?.ToString(), heading.Elements[0].TextRunProperties.ForegroundBrush?.ToString());
        var checkbox = Find<CheckBox>(editor, "Source task: actual task");
        checkbox.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.Contains("- [x] actual task", tab.CurrentText);
        await f.Window.RunCommandAsync(CommandIds.Undo);
        Assert.Equal(original, tab.CurrentText);
        await Ready(tab);
        editor.CaretOffset = original.IndexOf("![dot]", StringComparison.Ordinal);
        Pump(f.Window);
        Assert.Empty(editor.GetVisualDescendants().OfType<Tern.PreviewView.SourceImageThumbnail>());
        editor.CaretOffset = 0;
        Pump(f.Window);
        Assert.Single(editor.GetVisualDescendants().OfType<Tern.PreviewView.SourceImageThumbnail>());
        f.Shell.Settings.HybridSourceEnabled = false;
        f.Window.ApplySettings();
        Pump(f.Window);
        Assert.Empty(editor.GetVisualDescendants().OfType<Tern.PreviewView.SourceImageThumbnail>());
        Assert.DoesNotContain(editor.GetVisualDescendants().OfType<CheckBox>(), box => AutomationProperties.GetName(box)?.StartsWith("Source task:", StringComparison.Ordinal) == true);
        Assert.Equal(original, tab.CurrentText);
    }

    [AvaloniaTheory]
    [InlineData(ExportFormat.Html, "html")]
    [InlineData(ExportFormat.Pdf, "pdf")]
    [InlineData(ExportFormat.Text, "txt")]
    public async Task Successful_exports_reuse_format_options_and_workspace_folder_without_bypassing_destination_choice(ExportFormat format, string extension)
    {
        using var f = new DesktopFixture();
        var preferences = new ExportPreferences();
        var commits = 0;
        var output = Path.Combine(f.Docs, "copy." + extension);
        var first = new ExportWindow("source.md", null, "# Note", MarkdownProfile.Extended, f.Docs, format,
            preferences: preferences, savePreferences: () => { commits++; return true; });
        first.Show(f.Window);
        try
        {
            Pump(first);
            Find<NumericUpDown>(first, "Margins · mm").Value = 27;
            Find<CheckBox>(first, "Include document properties (front matter)").IsChecked = true;
            Find<TextBox>(first, "Document language (for example en or sv)").Text = "sv";
            var expected = first.CaptureOptions();
            Assert.True(await first.ExportAsync(output));
            Assert.Equal(1, commits);
            var second = new ExportWindow("source.md", null, "# Note", MarkdownProfile.Extended, f.Docs, format, preferences: preferences);
            second.Show(f.Window);
            try
            {
                Assert.Equal(expected, second.CaptureOptions());
                Assert.Equal(f.Docs, second.SuggestedOutputFolder);
                var picked = false;
                second.PickOutputFile = _ => { picked = true; return Task.FromResult<string?>(null); };
                Assert.False(await second.RequestExportAsync());
                Assert.True(picked);
                var other = new ExportWindow("source.md", null, "# Note", MarkdownProfile.Extended,
                    Path.Combine(f.Docs, "another-workspace"), format, preferences: preferences);
                try { Assert.Null(other.SuggestedOutputFolder); }
                finally { other.Close(); }
            }
            finally { second.Close(); }
        }
        finally { first.Close(); }
    }

    [AvaloniaFact]
    public async Task Outline_menu_moves_and_shifts_the_entire_subtree_in_one_undo_and_folding_survives_tab_switches()
    {
        using var f = new DesktopFixture();
        const string original = "# Root\n\n## First\none\n### Child\nchild\n\n## Second\ntwo";
        var tab = f.Shell.NewDocument(original);
        await Ready(tab);
        var outline = f.Window.FindControl<ListBox>("OutlineList")!;
        var row = outline.Items.OfType<OutlineRow>().Single(item => item.Text == "First");
        outline.SelectedItem = row;
        outline.RaiseEvent(new Avalonia.Input.ContextRequestedEventArgs { RoutedEvent = Control.ContextRequestedEvent });
        var move = outline.ContextMenu!.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Move Section Down"));
        move.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
        outline.ContextMenu.Close();
        Assert.True(tab.CurrentText.IndexOf("## Second", StringComparison.Ordinal) < tab.CurrentText.IndexOf("## First", StringComparison.Ordinal));
        Assert.Contains("## First\none\n### Child\nchild", tab.CurrentText);
        await f.Window.RunCommandAsync(CommandIds.Undo);
        Assert.Equal(original, tab.CurrentText);
        f.Window.EditorControl.CaretOffset = original.IndexOf("## First", StringComparison.Ordinal);
        await f.Window.RunCommandAsync(CommandIds.DemoteSection);
        Assert.Contains("### First\none\n#### Child\nchild", tab.CurrentText);
        await f.Window.RunCommandAsync(CommandIds.Undo);
        Assert.Equal(original, tab.CurrentText);
        await Ready(tab);
        f.Window.EditorControl.CaretOffset = original.IndexOf("## First", StringComparison.Ordinal);
        await f.Window.RunCommandAsync(CommandIds.FoldSection);
        Assert.Contains(f.Window.HeadingFolds!.AllFoldings, folding => folding.IsFolded);
        f.Shell.NewDocument("# Other\nbody");
        f.Shell.ActiveTab = tab;
        await Ready(tab);
        Pump(f.Window);
        Assert.NotEmpty(f.Window.HeadingFolds!.AllFoldings);
        Assert.Equal(original, tab.CurrentText);
    }

    [AvaloniaFact]
    public async Task Selection_and_session_statistics_follow_real_edits_and_keep_the_initial_baseline_after_save()
    {
        using var f = new DesktopFixture();
        var tab = (await f.Shell.OpenFileAsync(await f.WriteAsync("writing.md", "---\ntarget-words: 10\n---\n# First\n\none two\n")))!;
        await Ready(tab);
        var baseline = tab.Analysis!.WordCount;
        f.Window.EditorControl.Select(tab.CurrentText.IndexOf("one two", StringComparison.Ordinal), 7);
        Pump(f.Window);
        Assert.Contains("2 selected", f.Window.FindControl<TextBlock>("StatusWords")!.Text);
        tab.ApplyEdits([TextEdit.Insert(tab.CurrentText.Length, "three four")]);
        await Ready(tab);
        Assert.Equal(2, tab.WordsWrittenThisSession);
        Assert.Equal("+2", f.Window.FindControl<TextBlock>("InfoSessionWords")!.Text);
        Assert.Equal($"{baseline + 2:N0} / 10", f.Window.FindControl<TextBlock>("InfoWordTarget")!.Text);
        Assert.True(await f.Shell.SaveTabAsync(tab));
        Assert.Equal(2, tab.WordsWrittenThisSession);
    }

    private static T Find<T>(Visual root, string name) where T : Control => root.GetVisualDescendants().OfType<T>()
        .Single(control => AutomationProperties.GetName(control) == name || control is ContentControl content && Equals(content.Content, name));
    private static void Pump(Window window) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
    private static async Task Ready(Tern.App.Documents.DocumentTab tab)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (tab.AnalysisRevision != tab.Session.Revision && DateTime.UtcNow < deadline)
        { await Task.Delay(20); Dispatcher.UIThread.RunJobs(); }
        Assert.Equal(tab.Session.Revision, tab.AnalysisRevision);
        Dispatcher.UIThread.RunJobs();
    }
}
