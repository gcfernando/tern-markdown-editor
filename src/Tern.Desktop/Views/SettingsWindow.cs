using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using System.Text.Json;
using Tern.App.Commands;
using Tern.App.Help;
using Tern.App.Settings;
using Tern.Markdown;
using Tern.Desktop.Services;

namespace Tern.Desktop.Views;

/// <summary>Searchable preference cards with category navigation and contextual feature guides.</summary>
public sealed class SettingsWindow : Window
{
    private readonly Func<string, Task>? _openFeature;
    private readonly Func<string, Task>? _askAI;
    private readonly Func<string, CancellationToken, Task<string>>? _answerAI;
    internal AppSettings WorkingSettings { get; private set; } = null!;
    private TextBlock? _sourceFontSample;
    private TextBlock? _readingFontSample;

    private void RefreshTypography()
    {
        if (_sourceFontSample is null || _readingFontSample is null) return;
        try { _sourceFontSample.FontFamily = new FontFamily(WorkingSettings.EditorFontFamily); } catch (ArgumentException) { }
        _sourceFontSample.FontSize = WorkingSettings.EditorFontSize;
        _sourceFontSample.TextWrapping = WorkingSettings.WordWrap ? TextWrapping.Wrap : TextWrapping.NoWrap;
        _readingFontSample.FontSize = WorkingSettings.PreviewFontSize;
    }
    public SettingsWindow(AppSettings settings, CommandRegistry? registry = null, Func<string, Task>? openFeature = null, Func<string, Task>? askAI = null,
        Func<Window, Task>? configureAI = null, Func<IReadOnlyList<AIConnectionProfile>>? aiProfiles = null, string? initialCategory = null,
        Func<string, CancellationToken, Task<string>>? answerAI = null, Func<IReadOnlyList<WorkspaceConnection>>? workspaceConnections = null,
        Action? commit = null)
    {
        var liveSettings = settings;
        settings = WorkingSettings = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;
        var liveRegistry = registry ?? CommandRegistry.CreateDefault();
        registry = new CommandRegistry(liveRegistry.Commands);
        registry.LoadOverrides(liveRegistry.Overrides);
        _openFeature = openFeature;
        _askAI = askAI;
        _answerAI = answerAI;
        registry ??= CommandRegistry.CreateDefault();
        DialogParts.Setup(this, "Settings", 940, 760);
        MinWidth = 720;
        MinHeight = 520;
        var search = new TextBox { PlaceholderText = "Find a setting, such as wrap, font or restore…" };
        AutomationProperties.SetName(search, "Find a setting");
        var navigation = new ListBox { ItemsSource = FeatureCatalog.SettingsFeatures.Select(f => f.Category).Append("AI connections").Distinct(StringComparer.Ordinal).ToArray(), SelectedIndex = 0 };
        // This short list scrolls as part of the sidebar; realize every category so
        // the outer viewport can measure and reach the final row at large text sizes.
        navigation.ItemsPanel = new FuncTemplate<Panel?>(() => new StackPanel());
        AutomationProperties.SetName(navigation, "Settings categories");
        var sections = new StackPanel { Spacing = 24 };
        var cards = new List<(SettingFeature Feature, Control Card)>();
        var reloadEditors = new Dictionary<string, Action>();
        var groups = new Dictionary<string, (Control Heading, StackPanel Cards)>();
        var aiSection = new StackPanel { Spacing = 10 };
        var results = GuideParts.Caption("All settings");
        AutomationProperties.SetName(results, "Settings result count");
        sections.Children.Add(results);
        foreach (var category in FeatureCatalog.SettingsFeatures.Select(f => f.Category).Distinct(StringComparer.Ordinal))
        {
            var heading = new TextBlock { Text = category, FontSize = GuideParts.Scaled(18), FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
            var list = new StackPanel { Spacing = 12 };
            groups[category] = (heading, list);
            sections.Children.Add(GuideParts.Vertical(heading, list));
            foreach (var feature in FeatureCatalog.SettingsFeatures.Where(f => f.Category == category))
            {
                var editor = CreateEditor(settings, feature.Property, registry);
                if (editor is CheckBox check) check.Content = feature.Name;
                AutomationProperties.SetName(editor, feature.Property == nameof(AppSettings.EditorFontFamily) ? "Editor font options" : feature.Name);
                ToolTip.SetTip(editor, feature.Description);
                var help = new Button { Content = "How to use", HorizontalAlignment = HorizontalAlignment.Right };
                AutomationProperties.SetName(help, "Help for " + feature.Name);
                help.Click += (_, _) => new HelpWindow(registry, feature.Id, async id =>
                {
                    if (id == CommandIds.Settings) { search.Text = feature.Name; Filter(); }
                    else if (id == CommandIds.Shortcuts) await new ShortcutsWindow(registry, _openFeature, _askAI, _answerAI).ShowDialog(this);
                    else if (_openFeature is not null) await _openFeature(id);
                }, _askAI, answerAI).Show(this);
                var top = new DockPanel();
                DockPanel.SetDock(help, Dock.Right);
                top.Children.Add(help);
                top.Children.Add(new TextBlock { Text = feature.Name, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });
                var host = new Border { Child = editor };
                reloadEditors[feature.Property] = () =>
                {
                    var replacement = CreateEditor(settings, feature.Property, registry);
                    if (replacement is CheckBox check) check.Content = feature.Name;
                    AutomationProperties.SetName(replacement, feature.Property == nameof(AppSettings.EditorFontFamily) ? "Editor font options" : feature.Name); ToolTip.SetTip(replacement, feature.Description); host.Child = replacement;
                };
                var card = GuideParts.Card(GuideParts.Vertical(top, GuideParts.Body(feature.Description), host));
                cards.Add((feature, card));
                list.Children.Add(card);
            }
        }
        var aiSummary = GuideParts.Body("");
        var aiWritingDescription = GuideParts.Caption("");
        var aiList = new StackPanel { Spacing = 10 };
        void RefreshAI()
        {
            var profiles = workspaceConnections?.Invoke() ?? (aiProfiles?.Invoke() ?? []).Select(p => new WorkspaceConnection(p.Id, p.Name, "api", p.Model, p.Endpoint)).ToArray();
            var shortcut = Gesture.Describe(registry.GetGesture(CommandIds.AskAI));
            var writingAction = shortcut.Length > 0 ? "Press " + shortcut : "Choose Edit → Write with AI";
            aiWritingDescription.Text = "Choose a connection in the main toolbar. " + writingAction + " in Source, Reading preview or the Reference pane. Choose what to share, generate a suggestion and review it before applying changes. For an MCP connection, choose Open MCP tools to review and run a tool separately.";
            aiSummary.Text = profiles.Count == 0 ? "Connect a service once. Your saved connections will be ready throughout Tern."
                : $"{profiles.Count} saved {(profiles.Count == 1 ? "connection" : "connections")} · Switch between them from the main toolbar.";
            aiList.Children.Clear();
            foreach (var profile in profiles)
                aiList.Children.Add(GuideParts.Card(GuideParts.Vertical(new TextBlock { Text = profile.Name, FontWeight = FontWeight.SemiBold },
                    GuideParts.Caption(profile.ProviderLabel + " · " + profile.Summary))));
        }
        var connections = new Button { Content = "Manage AI connections", IsEnabled = configureAI is not null || openFeature is not null, Classes = { "accent" } };
        AutomationProperties.SetName(connections, "Configure AI connections");
        connections.Click += async (_, _) =>
        {
            if (configureAI is not null) await configureAI(this);
            else if (openFeature is not null) await openFeature(CommandIds.AIConnections);
            RefreshAI();
        };
        var aiHeading = GuideParts.Title("AI connections");
        var aiCards = GuideParts.Vertical(GuideParts.Card(GuideParts.Vertical(GuideParts.Caption("YOUR WRITING COMPANIONS"),
            aiSummary, connections)), aiList,
            GuideParts.Card(GuideParts.Vertical(GuideParts.Body("Write where you work"), aiWritingDescription)),
            GuideParts.Caption("Saved keys stay in Windows Credential Manager. Requests start only when you explicitly validate, generate, ask, connect or run a reviewed tool. AI edits remain unsaved until you save the note."));
        groups["AI connections"] = (aiHeading, aiCards);
        aiSection.Children.Add(aiHeading);
        aiSection.Children.Add(aiCards);
        sections.Children.Add(aiSection);
        RefreshAI();
        Activated += (_, _) => RefreshAI();
        void Filter()
        {
            var words = (search.Text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var (feature, card) in cards)
                card.IsVisible = (words.Length > 0 || feature.Category == navigation.SelectedItem as string)
                    && words.All(w => $"{feature.Name} {feature.Category} {feature.Description}".Contains(w, StringComparison.OrdinalIgnoreCase));
            aiSection.IsVisible = words.Length == 0 ? navigation.SelectedItem as string == "AI connections"
                : words.All(w => "AI connections providers models keys Ollama Claude writing".Contains(w, StringComparison.OrdinalIgnoreCase));
            foreach (var (category, group) in groups)
            {
                if (category == "AI connections") continue;
                var visible = group.Cards.Children.Any(c => c.IsVisible);
                group.Heading.IsVisible = visible;
                group.Cards.IsVisible = visible;
                if (group.Heading.Parent is Control section) section.IsVisible = visible;
            }
            var count = cards.Count(c => c.Card.IsVisible);
            results.Text = aiSection.IsVisible ? "AI connections · Set up once, use throughout Tern"
                : count == 0 ? "No settings match. Try another word." : $"{count} {(count == 1 ? "setting" : "settings")} · Apply or Done commits changes; Cancel discards pending changes";
        }
        search.TextChanged += (_, _) => Filter();
        navigation.SelectionChanged += (_, _) =>
        {
            search.Text = string.Empty;
            Filter();
            if (navigation.SelectedItem is string category)
                Dispatcher.UIThread.Post(() => { if (groups.TryGetValue(category, out var group)) group.Heading.BringIntoView(); }, DispatcherPriority.Background);
        };
        var close = new Button { Content = "Done", IsDefault = true, MinWidth = 96, HorizontalAlignment = HorizontalAlignment.Right };
        close.Classes.Add("accent");
        AutomationProperties.SetName(close, "Close settings");
        var apply = new Button { Content = "Apply" };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var resetCategory = new Button { Content = "Reset this category to defaults" };
        var applyStatus = GuideParts.Body("AI connection and credential changes are managed separately and take effect immediately.");
        bool Commit()
        {
            var previous = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(liveSettings))!;
            var previousBindings = liveRegistry.Overrides.ToDictionary();
            try
            {
                settings.Normalize();
                foreach (var feature in FeatureCatalog.SettingsFeatures)
                {
                    var property = typeof(AppSettings).GetProperty(feature.Property)!;
                    property.SetValue(liveSettings, property.GetValue(settings));
                }
                // Clone mutable collections before committing so further pending edits remain private.
                liveSettings.WorkspaceExclusions = [.. settings.WorkspaceExclusions];
                liveSettings.KeyBindings = registry.Overrides.ToDictionary();
                liveRegistry.LoadOverrides(liveSettings.KeyBindings);
                commit?.Invoke();
                applyStatus.Text = "Settings applied."; return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
            {
                foreach (var property in typeof(AppSettings).GetProperties().Where(p => p.CanWrite)) property.SetValue(liveSettings, property.GetValue(previous));
                liveRegistry.LoadOverrides(previousBindings);
                applyStatus.Text = "Settings could not be saved. Pending changes are kept here for retry. " + ex.Message;
                return false;
            }
        }
        apply.Click += (_, _) => Commit();
        close.Click += (_, _) => { if (Commit()) Close(); };
        cancel.Click += (_, _) => Close();
        resetCategory.Click += (_, _) =>
        {
            var defaults = new AppSettings();
            foreach (var feature in FeatureCatalog.SettingsFeatures.Where(f => f.Category == navigation.SelectedItem as string))
            {
                var property = typeof(AppSettings).GetProperty(feature.Property)!;
                property.SetValue(settings, property.GetValue(defaults));
                if (feature.Property == nameof(AppSettings.KeyBindings)) registry.ResetAll();
                reloadEditors[feature.Property]();
            }
            applyStatus.Text = "Category defaults are pending. Choose Apply or Done to commit them.";
        };
        var sidebar = new ScrollViewer
        {
            Content = GuideParts.Vertical(GuideParts.Caption("YOUR WORKSPACE"), navigation, GuideParts.Caption("Make space for the way you write.\n\nAI connections are available across notes once configured.")),
            Margin = new Thickness(0, 0, 20, 0),
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        };
        AutomationProperties.SetName(sidebar, "Settings category viewport");
        var categoryChoice = new ComboBox { ItemsSource = navigation.ItemsSource, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(categoryChoice, "Choose settings category");
        categoryChoice.SelectionChanged += (_, _) => navigation.SelectedIndex = categoryChoice.SelectedIndex;
        navigation.SelectionChanged += (_, _) => categoryChoice.SelectedIndex = navigation.SelectedIndex;
        var contentScroll = new ScrollViewer { Content = sections, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        AutomationProperties.SetName(contentScroll, "Settings content viewport");
        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("240,*"), RowDefinitions = new RowDefinitions("Auto,*"), RowSpacing = 12, Children = { sidebar, categoryChoice, contentScroll } };
        Grid.SetRowSpan(sidebar, 2);
        Grid.SetColumn(contentScroll, 1);
        Grid.SetRowSpan(contentScroll, 2);
        var description = GuideParts.Body("Your editor, your rhythm. Personalize your workspace and connect the AI services you use.");
        var footer = GuideParts.Vertical(applyStatus, GuideParts.Actions(resetCategory, cancel, apply, close));
        var layout = new Grid { Margin = new Thickness(28), RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"), RowSpacing = 18, Children =
        {
            GuideParts.Vertical(GuideParts.Caption("SETTINGS"), GuideParts.Title("Make Tern yours"), description),
            search, body, footer,
        }};
        for (var i = 0; i < layout.Children.Count; i++) Grid.SetRow(layout.Children[i], i);
        void FitNavigation()
        {
            var compact = ClientSize.Width < 900 || ClientSize.Width < GuideParts.Scaled(720);
            sidebar.IsVisible = !compact;
            categoryChoice.IsVisible = compact;
            description.IsVisible = !compact;
            body.ColumnDefinitions[0].Width = compact ? new GridLength(0) : new GridLength(Math.Clamp(GuideParts.Scaled(180), 220, 320));
            Grid.SetColumn(categoryChoice, compact ? 1 : 0);
            Grid.SetRow(contentScroll, compact ? 1 : 0);
            Grid.SetRowSpan(contentScroll, compact ? 1 : 2);
        }
        SizeChanged += (_, _) => FitNavigation();
        Opened += (_, _) => FitNavigation();
        Content = layout;
        if (initialCategory is not null) navigation.SelectedItem = initialCategory;
        Filter();
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }

    private Control CreateEditor(AppSettings settings, string property, CommandRegistry registry)
    {
        switch (property)
        {
            case nameof(AppSettings.Theme): return Choice(settings.Theme, v => settings.Theme = v);
            case nameof(AppSettings.DefaultProfile): return Choice(settings.DefaultProfile, v => settings.DefaultProfile = v);
            case nameof(AppSettings.DefaultViewMode): return Choice(settings.DefaultViewMode, v => settings.DefaultViewMode = v);
            case nameof(AppSettings.EditorFontFamily):
                var font = new TextBox { Text = settings.EditorFontFamily, HorizontalAlignment = HorizontalAlignment.Stretch };
                AutomationProperties.SetName(font, "Editor font");
                var choices = new ComboBox { ItemsSource = FontManager.Current.SystemFonts.Select(f => f.Name).Order(StringComparer.OrdinalIgnoreCase).ToArray(), PlaceholderText = "Choose an installed font", HorizontalAlignment = HorizontalAlignment.Stretch };
                choices.SelectionChanged += (_, _) => { if (choices.SelectedItem is string name) font.Text = name; };
                var sample = _sourceFontSample = new TextBlock { Text = "# A note worth keeping\nThe quick brown fox · 0123456789", TextWrapping = TextWrapping.Wrap };
                var reading = _readingFontSample = new TextBlock { Text = "A note worth keeping\nThe quick brown fox · 0123456789", TextWrapping = TextWrapping.Wrap };
                void PreviewFont()
                {
                    if (string.IsNullOrWhiteSpace(font.Text)) return;
                    settings.EditorFontFamily = font.Text;
                    RefreshTypography();
                }
                font.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty) PreviewFont(); };
                PreviewFont();
                return GuideParts.Vertical(choices, font, GuideParts.Caption("Source preview"), sample, GuideParts.Caption("Reading preview"), reading, GuideParts.Caption("Installed fonts only. A configured comma-separated fallback chain can be entered above."));
            case nameof(AppSettings.EditorFontSize): return DialogParts.Number(settings.EditorFontSize, 8, 48, 1, v => { settings.EditorFontSize = v; RefreshTypography(); });
            case nameof(AppSettings.PreviewFontSize): return DialogParts.Number(settings.PreviewFontSize, 8, 48, 1, v => { settings.PreviewFontSize = v; RefreshTypography(); });
            case nameof(AppSettings.ReadingWidth): return DialogParts.Number(settings.ReadingWidth, 360, 1600, 20, v => settings.ReadingWidth = v);
            case nameof(AppSettings.PreviewFullWidth): return Check(settings.PreviewFullWidth, v => settings.PreviewFullWidth = v);
            case nameof(AppSettings.UiScale): return DialogParts.Number(settings.UiScale, 0.75, 2.5, 0.1, v => settings.UiScale = v);
            case nameof(AppSettings.SidebarWidth): return DialogParts.Number(settings.SidebarWidth, 180, 800, 10, v => settings.SidebarWidth = v);
            case nameof(AppSettings.SidebarVisible): return Check(settings.SidebarVisible, v => settings.SidebarVisible = v);
            case nameof(AppSettings.InspectorVisible): return Check(settings.InspectorVisible, v => settings.InspectorVisible = v);
            case nameof(AppSettings.InspectorWidth): return DialogParts.Number(settings.InspectorWidth, 180, 800, 10, v => settings.InspectorWidth = v);
            case nameof(AppSettings.SourceShare): return DialogParts.Number(settings.SourceShare, .15, .85, .05, v => settings.SourceShare = v);
            case nameof(AppSettings.OnboardingDismissed): return Check(settings.OnboardingDismissed, v => settings.OnboardingDismissed = v);
            case nameof(AppSettings.WorkspaceExclusions):
                var exclusions = new TextBox { Text = string.Join('\n', settings.WorkspaceExclusions), AcceptsReturn = true, MinHeight = 90, TextWrapping = TextWrapping.Wrap };
                exclusions.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty) settings.WorkspaceExclusions = (exclusions.Text ?? "").Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList(); };
                return exclusions;
            case nameof(AppSettings.KeyBindings):
                var shortcuts = new Button { Content = "Edit keyboard shortcuts" };
                shortcuts.Click += async (_, _) => await new ShortcutsWindow(registry, _openFeature, _askAI, _answerAI).ShowDialog(this);
                return shortcuts;
            case nameof(AppSettings.TabSize): return DialogParts.Number(settings.TabSize, 1, 16, 1, v => settings.TabSize = (int)v);
            case nameof(AppSettings.ReduceMotion): return Check(settings.ReduceMotion, v => settings.ReduceMotion = v);
            case nameof(AppSettings.WordWrap): return Check(settings.WordWrap, v => { settings.WordWrap = v; RefreshTypography(); });
            case nameof(AppSettings.ShowLineNumbers): return Check(settings.ShowLineNumbers, v => settings.ShowLineNumbers = v);
            case nameof(AppSettings.HighlightCurrentLine): return Check(settings.HighlightCurrentLine, v => settings.HighlightCurrentLine = v);
            case nameof(AppSettings.InsertSpacesForTab): return Check(settings.InsertSpacesForTab, v => settings.InsertSpacesForTab = v);
            case nameof(AppSettings.SyncScroll): return Check(settings.SyncScroll, v => settings.SyncScroll = v);
            case nameof(AppSettings.RestoreSession): return Check(settings.RestoreSession, v => settings.RestoreSession = v);
            case nameof(AppSettings.AutoReloadCleanDocuments): return Check(settings.AutoReloadCleanDocuments, v => settings.AutoReloadCleanDocuments = v);
            case nameof(AppSettings.EnsureFinalNewlineOnSave): return Check(settings.EnsureFinalNewlineOnSave, v => settings.EnsureFinalNewlineOnSave = v);
            case nameof(AppSettings.TrimTrailingWhitespaceOnSave): return Check(settings.TrimTrailingWhitespaceOnSave, v => settings.TrimTrailingWhitespaceOnSave = v);
            case nameof(AppSettings.AllowExternalLinkOpening): return Check(settings.AllowExternalLinkOpening, v => settings.AllowExternalLinkOpening = v);
            default: throw new ArgumentOutOfRangeException(nameof(property));
        }
    }

    private static CheckBox Check(bool value, Action<bool> apply) => DialogParts.Check("Enabled", value, apply);
    private static ComboBox Choice<T>(T selected, Action<T> apply) where T : struct, Enum
    {
        var box = new ComboBox { ItemsSource = Enum.GetValues<T>(), SelectedItem = selected, MinWidth = 220 };
        box.SelectionChanged += (_, _) => { if (box.SelectedItem is T value) apply(value); };
        return box;
    }
}
