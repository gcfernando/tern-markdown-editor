using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tern.App.Help;
using Tern.App.Settings;
using Tern.Domain.Text;
using Tern.Desktop.Views;

namespace Tern.Desktop.Tests;

public sealed class SettingsNavigationTests
{
    [AvaloniaFact]
    public void Expanded_settings_keep_all_original_preferences_and_deliberate_navigation_destinations()
    {
        var original = new[]
        {
            nameof(AppSettings.Theme),
            nameof(AppSettings.EditorFontFamily),
            nameof(AppSettings.EditorFontSize),
            nameof(AppSettings.PreviewFontSize),
            nameof(AppSettings.PreviewFullWidth),
            nameof(AppSettings.ReadingWidth),
            nameof(AppSettings.UiScale),
            nameof(AppSettings.ScaleDocumentText),
            nameof(AppSettings.ReduceMotion),
            nameof(AppSettings.ShowContextGuidance),
            nameof(AppSettings.WordWrap),
            nameof(AppSettings.ShowLineNumbers),
            nameof(AppSettings.HighlightCurrentLine),
            nameof(AppSettings.SpellCheckEnabled),
            nameof(AppSettings.HybridSourceEnabled),
            nameof(AppSettings.DefaultEncoding),
            nameof(AppSettings.DefaultEncodingHasBom),
            nameof(AppSettings.DefaultLineEnding),
            nameof(AppSettings.AutoSaveEnabled),
            nameof(AppSettings.AutoSaveIdleSeconds),
            nameof(AppSettings.ExportPreferences),
            nameof(AppSettings.TabSize),
            nameof(AppSettings.InsertSpacesForTab),
            nameof(AppSettings.SyncScroll),
            nameof(AppSettings.DefaultViewMode),
            nameof(AppSettings.DefaultProfile),
            nameof(AppSettings.RestoreSession),
            nameof(AppSettings.AutoReloadCleanDocuments),
            nameof(AppSettings.EnsureFinalNewlineOnSave),
            nameof(AppSettings.TrimTrailingWhitespaceOnSave),
            nameof(AppSettings.AllowExternalLinkOpening),
            nameof(AppSettings.RemoteImages),
            nameof(AppSettings.RemoteImageTrustedFiles),
            nameof(AppSettings.OfflineMode),
            nameof(AppSettings.KnowledgeAssistantEnabled),
            nameof(AppSettings.KnowledgeAllowRemote),
            nameof(AppSettings.HistoryBudgetMiB),
            nameof(AppSettings.SidebarVisible),
            nameof(AppSettings.SidebarWidth),
            nameof(AppSettings.InspectorVisible),
            nameof(AppSettings.InspectorWidth),
            nameof(AppSettings.SourceShare),
            nameof(AppSettings.OnboardingDismissed),
            nameof(AppSettings.WorkspaceExclusions),
            nameof(AppSettings.SavedWorkspaceSearches),
            nameof(AppSettings.KeyBindings)
        };
        var added = new[]
        {
            nameof(AppSettings.ShowSpaces),
            nameof(AppSettings.ShowTabs),
            nameof(AppSettings.ShowEndOfLine),
            nameof(AppSettings.ShowColumnRuler),
            nameof(AppSettings.ColumnRulerPosition),
            nameof(AppSettings.EditorLineHeight),
            nameof(AppSettings.AllowScrollBelowDocument),
            nameof(AppSettings.CutCopyWholeLine),
            nameof(AppSettings.PreviewFontFamily),
            nameof(AppSettings.FindMatchCase),
            nameof(AppSettings.FindWholeWord),
            nameof(AppSettings.FindUseRegex),
            nameof(AppSettings.TaskGroupByFile)
        };
        Assert.Equal(46, original.Length);
        Assert.Equal(13, added.Length);
        Assert.Equal(original.Concat(added).Order(StringComparer.Ordinal), FeatureCatalog.SettingsFeatures.Select(feature => feature.Property).Order(StringComparer.Ordinal));
        Assert.Equal(59, FeatureCatalog.SettingsFeatures.Count);
        Assert.DoesNotContain(FeatureCatalog.SettingsFeatures, feature => feature.Property is "KnowledgeContextLimit" or "KnowledgeSourceLimit");
        Assert.Equal(11, SettingsNavigation.Categories.Count);
        Assert.All(FeatureCatalog.SettingsFeatures, feature => Assert.Contains(SettingsNavigation.Categories, category => category.Key == feature.Category));
    }

    [AvaloniaTheory]
    [InlineData("Files", "Files")]
    [InlineData("Editor", "Editor")]
    [InlineData("AI connections", "AI connections")]
    [InlineData("Reading and views", "Reading")]
    [InlineData("unknown category", "Appearance")]
    public void Existing_and_display_name_entry_points_select_a_real_destination(string initial, string expected)
    {
        using var f = new DesktopFixture();
        var window = new SettingsWindow(f.Shell.Settings, initialCategory: initial);
        window.Show(f.Window);
        try
        {
            Pump(window);
            var navigation = Find<ListBox>(window, "Settings categories");
            Assert.Equal(expected, navigation.SelectedItem);
            Assert.Equal(SettingsNavigation.Categories.Select(category => category.Key), navigation.Items.OfType<string>());
            var container = Assert.IsType<ListBoxItem>(navigation.ContainerFromIndex(navigation.SelectedIndex));
            Assert.Equal(SettingsNavigation.Title(expected), AutomationProperties.GetName(container));
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), block =>
                block.IsEffectivelyVisible && block.Text == SettingsNavigation.Title(expected));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Reading_and_privacy_resets_keep_unrelated_file_and_editor_preferences_private()
    {
        using var f = new DesktopFixture();
        var live = new AppSettings
        {
            EditorFontSize = 27, PreviewFontSize = 31, ReadingWidth = 1200, DefaultViewMode = ViewMode.Source,
            SyncScroll = false, DefaultEncoding = TextEncodingKind.Windows1252, AllowExternalLinkOpening = false,
        };
        var window = new SettingsWindow(live, initialCategory: "Reading");
        window.Show(f.Window);
        try
        {
            Pump(window);
            Click(window, "Reset this category to defaults");
            Assert.Equal(16, window.WorkingSettings.PreviewFontSize);
            Assert.Equal(760, window.WorkingSettings.ReadingWidth);
            Assert.Equal(ViewMode.Split, window.WorkingSettings.DefaultViewMode);
            Assert.True(window.WorkingSettings.SyncScroll);
            Assert.Equal(27, window.WorkingSettings.EditorFontSize);
            Assert.Equal(TextEncodingKind.Windows1252, window.WorkingSettings.DefaultEncoding);
            Assert.False(window.WorkingSettings.AllowExternalLinkOpening);
            Assert.Equal(31, live.PreviewFontSize);
            Click(window, "Apply");
            Assert.Equal(16, live.PreviewFontSize);
            Assert.Equal(27, live.EditorFontSize);
            Assert.Equal(TextEncodingKind.Windows1252, live.DefaultEncoding);
            Find<ListBox>(window, "Settings categories").SelectedItem = "Privacy";
            Pump(window);
            Click(window, "Reset this category to defaults");
            Assert.True(window.WorkingSettings.AllowExternalLinkOpening);
            Assert.False(live.AllowExternalLinkOpening);
            Assert.Equal(TextEncodingKind.Windows1252, window.WorkingSettings.DefaultEncoding);
        }
        finally { await Discard(window); }
        Assert.False(live.AllowExternalLinkOpening);
        Assert.Equal(16, live.PreviewFontSize);
    }

    [AvaloniaFact]
    public async Task Display_category_search_matches_its_settings_and_disables_cross_category_reset()
    {
        using var f = new DesktopFixture();
        var window = new SettingsWindow(f.Shell.Settings);
        window.Show(f.Window);
        try
        {
            Find<TextBox>(window, "Find a setting").Text = "Reading and views";
            Pump(window);
            foreach (var feature in FeatureCatalog.SettingsFeatures)
                Assert.Equal(feature.Category == "Reading", Find<Button>(window, "Help for " + feature.Name).IsEffectivelyVisible);
            Assert.False(Find<Button>(window, "Reset this category to defaults").IsEnabled);
            var navigation = Find<ListBox>(window, "Settings categories");
            navigation.SelectedItem = "Files";
            Pump(window);
            Assert.Equal("", Find<TextBox>(window, "Find a setting").Text);
            Assert.True(Find<Button>(window, "Reset this category to defaults").IsEnabled);
            Assert.True(Find<Button>(window, "Help for Default encoding (new files)").IsEffectivelyVisible);
        }
        finally { await Discard(window); }
    }

    [AvaloniaTheory]
    [InlineData(ThemeChoice.Paper, false)]
    [InlineData(ThemeChoice.Midnight, false)]
    [InlineData(ThemeChoice.Paper, true)]
    [InlineData(ThemeChoice.Midnight, true)]
    public async Task Capture_deliberate_sidebar_and_compact_destinations(ThemeChoice theme, bool compact)
    {
        using var f = new DesktopFixture();
        f.Shell.Settings.Theme = theme;
        f.Window.ApplyTheme();
        var window = new SettingsWindow(f.Shell.Settings, initialCategory: "Reading")
        { Width = compact ? 720 : 1100, Height = 850 };
        window.Show(f.Window);
        try
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
            Pump(window);
            var choice = Find<ComboBox>(window, "Choose settings category");
            var navigation = Find<ListBox>(window, "Settings categories");
            Assert.Equal(compact, choice.IsVisible);
            Assert.Equal("Reading", choice.SelectedItem);
            Assert.Equal(navigation.SelectedIndex, choice.SelectedIndex);
            if (compact)
            {
                choice.SelectedItem = "Privacy";
                Pump(window);
                Assert.Equal("Privacy", navigation.SelectedItem);
                Assert.True(Find<Button>(window, "Help for Allow opening links in other applications (asks first)").IsEffectivelyVisible);
            }
            var folder = Path.Combine(Screenshots.Folder, "reported-settings-navigation");
            Directory.CreateDirectory(folder);
            using var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            using var stream = File.Create(Path.Combine(folder, $"{theme}-{(compact ? "compact-privacy" : "wide-reading")}.png"));
            frame.Save(stream, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        }
        finally { await Discard(window); }
    }

    private static T Find<T>(Window root, string name) where T : Control => root.GetVisualDescendants().OfType<T>().Single(control =>
        AutomationProperties.GetName(control) == name || control is Button button && Equals(button.Content, name));
    private static void Click(Window window, string name) => Find<Button>(window, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Pump(Window window) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
    private static async Task Discard(Window window)
    {
        if (!window.IsVisible) return;
        window.Close();
        foreach (var prompt in window.OwnedWindows.OfType<PromptWindow>().ToArray()) Click(prompt, "Discard changes");
        await Task.Delay(20, TestContext.Current.CancellationToken);
        Dispatcher.UIThread.RunJobs();
    }
}
