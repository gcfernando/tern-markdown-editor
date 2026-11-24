using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System.Text.Json;
using Tern.App.Commands;
using Tern.App.Help;
using Tern.App.Settings;
using Tern.Markdown;
using Tern.Desktop.Services;
using Tern.Domain.Text;
using Tern.Filesystem;

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
    private TextBlock? _readingTypefaceSample;
    private CheckBox? _newFileBom;

    private void RefreshNewFileBom()
    {
        var format = WorkingSettings.NewFileFormat;
        WorkingSettings.DefaultEncodingHasBom = format.HasBom;
        if (_newFileBom is null) return;
        _newFileBom.IsEnabled = WorkingSettings.DefaultEncoding == TextEncodingKind.Utf8;
        _newFileBom.IsChecked = format.HasBom;
    }

    private void RefreshTypography()
    {
        if (_sourceFontSample is null || _readingFontSample is null) return;
        try { _sourceFontSample.FontFamily = new FontFamily(WorkingSettings.EditorFontFamily); } catch (ArgumentException) { }
        _sourceFontSample.FontSize = WorkingSettings.EditorFontSize * WorkingSettings.DocumentTextScale;
        _sourceFontSample.LineHeight = _sourceFontSample.FontSize * WorkingSettings.EditorLineHeight;
        _sourceFontSample.TextWrapping = WorkingSettings.WordWrap ? TextWrapping.Wrap : TextWrapping.NoWrap;
        _readingFontSample.FontSize = WorkingSettings.PreviewFontSize * WorkingSettings.DocumentTextScale;
        try
        {
            var family = new FontFamily(WorkingSettings.PreviewFontFamily);
            _readingFontSample.FontFamily = family;
            if (_readingTypefaceSample is not null) _readingTypefaceSample.FontFamily = family;
        }
        catch (ArgumentException) { }
        if (_readingTypefaceSample is not null) _readingTypefaceSample.FontSize = WorkingSettings.PreviewFontSize * WorkingSettings.DocumentTextScale;
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
        DialogParts.Setup(this, "Settings", 940, 760, DialogGuidance.Settings);
        MinWidth = 720;
        MinHeight = 520;
        var search = new TextBox { PlaceholderText = "Find a setting, such as wrap, font or restore…" };
        AutomationProperties.SetName(search, "Find a setting");
        var categoryKeys = SettingsNavigation.Categories.Select(category => category.Key).ToArray();
        var categoryTemplate = new FuncDataTemplate<string>((key, _) => new TextBlock
        {
            Text = SettingsNavigation.Title(key ?? ""), TextWrapping = TextWrapping.Wrap,
        });
        var navigation = new ListBox { ItemsSource = categoryKeys, ItemTemplate = categoryTemplate, SelectedIndex = 0 };
        // This short list scrolls as part of the sidebar; realize every category so
        // the outer viewport can measure and reach the final row at large text sizes.
        navigation.ItemsPanel = new FuncTemplate<Panel?>(() => new StackPanel());
        navigation.ContainerPrepared += (_, e) =>
        {
            if (e.Index >= 0 && e.Index < categoryKeys.Length)
                AutomationProperties.SetName(e.Container, SettingsNavigation.Title(categoryKeys[e.Index]));
        };
        AutomationProperties.SetName(navigation, "Settings categories");
        var sections = new StackPanel { [!StackPanel.SpacingProperty] = GuideParts.Space(24).ToBinding() };
        var cards = new List<(SettingFeature Feature, Control Card)>();
        var reloadEditors = new Dictionary<string, Action>();
        var groups = new Dictionary<string, (Control Heading, StackPanel Cards)>();
        var aiSection = new StackPanel { [!StackPanel.SpacingProperty] = GuideParts.Space(8).ToBinding() };
        var windowsSection = new StackPanel { [!StackPanel.SpacingProperty] = GuideParts.Space(8).ToBinding() };
        var results = GuideParts.Caption("All settings");
        AutomationProperties.SetName(results, "Settings result count");
        sections.Children.Add(results);
        foreach (var category in categoryKeys.Where(key => FeatureCatalog.SettingsFeatures.Any(feature => feature.Category == key)))
        {
            var heading = new TextBlock { Text = SettingsNavigation.Title(category), [!TextBlock.FontSizeProperty] = GuideParts.Font("Section").ToBinding(), FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
            var list = new StackPanel { [!StackPanel.SpacingProperty] = GuideParts.Space(12).ToBinding() };
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
                help.Click += (_, _) => HelpWindow.ShowFor(this, () => new HelpWindow(registry, feature.Id, async id =>
                {
                    if (id == CommandIds.Settings) { search.Text = feature.Name; Filter(); }
                    else if (id == CommandIds.Shortcuts) await new ShortcutsWindow(registry, _openFeature, _askAI, _answerAI).ShowDialog(this);
                    else if (_openFeature is not null) await _openFeature(id);
                }, _askAI, answerAI), feature.Id);
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
        var aiList = new StackPanel { [!StackPanel.SpacingProperty] = GuideParts.Space(8).ToBinding() };
        void RefreshAI()
        {
            var profiles = workspaceConnections?.Invoke() ?? (aiProfiles?.Invoke() ?? []).Select(p => new WorkspaceConnection(p.Id, p.Name, "api", p.Model, p.Endpoint, AIConnectionRules.NormalizeSendCharacters(p.SendCharacters))).ToArray();
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
        var integrationStatus = MessageTones.Follow(GuideParts.Body(WindowsFileAssociations.CurrentRegistration()));
        var register = new Button { Content = "Register this copy with Windows", IsEnabled = OperatingSystem.IsWindows(), Classes = { "accent" } };
        register.Click += (_, _) =>
        {
            try { WindowsFileAssociations.RegisterCurrentCopy(); integrationStatus.Text = "Registered this copy. Your default app was kept.\n" + WindowsFileAssociations.CurrentRegistration(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { integrationStatus.Text = "Could not register Tern: " + Tern.App.Shell.UserProblem.Plain(ex); }
        };
        var unregister = new Button { Content = "Remove Explorer integration", IsEnabled = OperatingSystem.IsWindows() };
        unregister.Click += (_, _) =>
        {
            try { WindowsFileAssociations.UnregisterCurrentUser(); integrationStatus.Text = "Removed Tern's Explorer integration for this user. Your default app and other applications were kept.\n" + WindowsFileAssociations.CurrentRegistration(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { integrationStatus.Text = "Could not remove Tern's Explorer integration: " + Tern.App.Shell.UserProblem.Plain(ex); }
        };
        windowsSection.Children.Add(GuideParts.Vertical(GuideParts.Title("Windows integration"), GuideParts.Card(GuideParts.Vertical(
            GuideParts.Body("Open Markdown files from any Explorer folder"),
            GuideParts.Caption("Adds Open with Tern for .md files and makes this copy available in Open with. Your current default app stays unchanged. Moving this EXE or portable folder requires re-registration."),
            GuideParts.Caption("Remove Explorer integration removes Tern's registration for this user immediately. It does not change your default app or another application's registration."),
            integrationStatus, GuideParts.ButtonRow(register, unregister)))));
        sections.Children.Add(windowsSection);
        RefreshAI();
        Activated += (_, _) => RefreshAI();
        void Filter()
        {
            var words = (search.Text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var (feature, card) in cards)
                card.IsVisible = (words.Length > 0 || feature.Category == navigation.SelectedItem as string)
                    && words.All(w => $"{feature.Name} {feature.Category} {SettingsNavigation.Title(feature.Category)} {feature.Description}".Contains(w, StringComparison.OrdinalIgnoreCase));
            windowsSection.IsVisible = words.Length == 0 ? navigation.SelectedItem as string == "Windows integration"
                : words.All(w => "Windows integration Explorer Markdown registration default app Open with Tern remove unregister".Contains(w, StringComparison.OrdinalIgnoreCase));
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
            results.Text = windowsSection.IsVisible ? "Windows integration · Changes take effect immediately" : aiSection.IsVisible ? "AI connections · Set up once, use throughout Tern"
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
        var applyStatus = MessageTones.Follow(GuideParts.Body("AI connection and credential changes are managed separately and take effect immediately."));
        string PendingState() => JsonSerializer.Serialize(new
        {
            Settings = settings,
            Bindings = registry.Overrides.OrderBy(binding => binding.Key, StringComparer.Ordinal)
        });
        var appliedState = PendingState();
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
                liveSettings.ExportPreferences = settings.ExportPreferences.Clone();
                liveSettings.SavedWorkspaceSearches = settings.SavedWorkspaceSearches
                    .ToDictionary(pair => pair.Key, pair => new List<string>(pair.Value ?? []));
                liveSettings.RemoteImageTrustedFiles = [.. settings.RemoteImageTrustedFiles];
                liveSettings.KeyBindings = registry.Overrides.ToDictionary();
                liveRegistry.LoadOverrides(liveSettings.KeyBindings);
                commit?.Invoke();
                settings.KeyBindings = registry.Overrides.ToDictionary();
                appliedState = PendingState();
                applyStatus.Text = "Settings applied."; return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
            {
                foreach (var property in typeof(AppSettings).GetProperties().Where(p => p.CanWrite)) property.SetValue(liveSettings, property.GetValue(previous));
                liveRegistry.LoadOverrides(previousBindings);
                applyStatus.Text = "Settings could not be saved. Pending changes are kept here for retry. " + Tern.App.Shell.UserProblem.Plain(ex);
                return false;
            }
        }
        apply.Click += (_, _) => Commit();
        close.Click += (_, _) => { if (Commit()) Close(); };
        cancel.Click += (_, _) => Close();
        resetCategory.Click += (_, _) =>
        {
            if (!resetCategory.IsEnabled) return;
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
        void UpdateResetScope() => resetCategory.IsEnabled = string.IsNullOrWhiteSpace(search.Text)
            && FeatureCatalog.SettingsFeatures.Any(feature => feature.Category == navigation.SelectedItem as string);
        search.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty) UpdateResetScope(); };
        navigation.SelectionChanged += (_, _) => UpdateResetScope();
        var sidebar = new ScrollViewer
        {
            Content = GuideParts.Vertical(GuideParts.Caption("CATEGORIES"), navigation, GuideParts.Caption("Make space for the way you write.\n\nAI connections are available across notes once configured.")),
            Margin = new Thickness(0, 0, 20, 0),
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        };
        AutomationProperties.SetName(sidebar, "Settings category viewport");
        var categoryChoice = new ComboBox { ItemsSource = categoryKeys, ItemTemplate = categoryTemplate, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        categoryChoice.ContainerPrepared += (_, e) =>
        {
            if (e.Index >= 0 && e.Index < categoryKeys.Length)
                AutomationProperties.SetName(e.Container, SettingsNavigation.Title(categoryKeys[e.Index]));
        };
        AutomationProperties.SetName(categoryChoice, "Choose settings category");
        categoryChoice.SelectionChanged += (_, _) => navigation.SelectedIndex = categoryChoice.SelectedIndex;
        navigation.SelectionChanged += (_, _) => categoryChoice.SelectedIndex = navigation.SelectedIndex;
        var contentScroll = new ScrollViewer { Content = sections, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        AutomationProperties.SetName(contentScroll, "Settings content viewport");
        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("240,*"), RowDefinitions = new RowDefinitions("Auto,*"), [!Grid.RowSpacingProperty] = GuideParts.Space(12).ToBinding(), Children = { sidebar, categoryChoice, contentScroll } };
        Grid.SetRowSpan(sidebar, 2);
        Grid.SetColumn(contentScroll, 1);
        Grid.SetRowSpan(contentScroll, 2);
        var description = GuideParts.Body("Your editor, your rhythm. Personalize your workspace and connect the AI services you use.");
        var footer = GuideParts.Vertical(applyStatus, GuideParts.Actions(close, cancel, resetCategory, apply));
        var layout = new Grid { Margin = new Thickness(28), RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"), [!Grid.RowSpacingProperty] = GuideParts.Space(16).ToBinding(), Children =
        {
            GuideParts.Vertical(GuideParts.Caption("SETTINGS"), GuideParts.Title("Make Tern yours"), description),
            search, body, footer,
        }};
        for (var i = 0; i < layout.Children.Count; i++) Grid.SetRow(layout.Children[i], i);
        void FitNavigation()
        {
            var compact = ClientSize.Width < GuideParts.Scaled(900);
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
        if (initialCategory is not null) navigation.SelectedItem = SettingsNavigation.ResolveKey(initialCategory);
        Filter();
        UpdateResetScope();
        appliedState = PendingState();
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key != Key.Enter || e.Source is not Control control) return;
            var input = control as TextBox ?? control.GetVisualAncestors().OfType<TextBox>().FirstOrDefault();
            if (input is not null && (!input.AcceptsReturn || e.KeyModifiers != KeyModifiers.None))
                e.Handled = true;
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        var closingDecision = false;
        var forceClose = false;
        Closing += async (_, e) =>
        {
            if (forceClose || PendingState() == appliedState) return;
            e.Cancel = true;
            if (closingDecision) return;
            closingDecision = true;
            try
            {
                if (await PromptWindow.AskAsync(this, "Discard unapplied changes?",
                    "Discard changes made since the last Apply? Previously applied settings and saved AI connections are kept.",
                    ["Discard changes", "Keep editing"], 1, 1, destructiveIndex: 0) == 0)
                {
                    forceClose = true;
                    Close();
                }
            }
            finally { closingDecision = false; }
        };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };
    }

    private Control CreateEditor(AppSettings settings, string property, CommandRegistry registry)
    {
        switch (property)
        {
            case nameof(AppSettings.RemoteImageTrustedFiles):
                var forgetImages = new Button { Content = "Forget saved image approvals" };
                forgetImages.Click += (_, _) => { settings.RemoteImageTrustedFiles.Clear(); forgetImages.Content = "Approvals cleared — choose Apply"; };
                return forgetImages;
            case nameof(AppSettings.RemoteImages): return Choice(settings.RemoteImages, v => settings.RemoteImages = v);
            case nameof(AppSettings.OfflineMode): return Check(settings.OfflineMode, v => settings.OfflineMode = v);
            case nameof(AppSettings.Theme): return Choice(settings.Theme, v => settings.Theme = v);
            case nameof(AppSettings.DefaultEncoding):
                var encodingOptions = TextFileCodec.AvailableEncodings.Where(option => !option.HasBom).ToArray();
                var encoding = new ComboBox
                {
                    ItemsSource = encodingOptions,
                    ItemTemplate = new FuncDataTemplate<EncodingOption>((option, _) => new TextBlock
                    { Text = option?.Name.Replace(" · no BOM", "", StringComparison.Ordinal), TextWrapping = TextWrapping.Wrap }),
                    SelectedItem = encodingOptions.FirstOrDefault(option => option.Kind == settings.DefaultEncoding),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                };
                encoding.SelectionChanged += (_, _) =>
                {
                    if (encoding.SelectedItem is not EncodingOption selected) return;
                    settings.DefaultEncoding = selected.Kind;
                    RefreshNewFileBom();
                };
                return encoding;
            case nameof(AppSettings.DefaultEncodingHasBom):
                var bom = _newFileBom = Check(settings.NewFileFormat.HasBom, v => settings.DefaultEncodingHasBom = v);
                RefreshNewFileBom();
                return bom;
            case nameof(AppSettings.DefaultLineEnding):
                var endings = new ComboBox { ItemsSource = new[] { LineEnding.CrLf, LineEnding.Lf, LineEnding.Cr },
                    ItemTemplate = new FuncDataTemplate<LineEnding>((value, _) => new TextBlock
                    { Text = value switch { LineEnding.CrLf => "CRLF (Windows)", LineEnding.Cr => "CR (classic Mac)", _ => "LF (Unix/macOS)" } }),
                    SelectedItem = settings.DefaultLineEnding, MinWidth = 220 };
                endings.SelectionChanged += (_, _) => { if (endings.SelectedItem is LineEnding value) settings.DefaultLineEnding = value; };
                return endings;
            case nameof(AppSettings.DefaultProfile): return Choice(settings.DefaultProfile, v => settings.DefaultProfile = v);
            case nameof(AppSettings.DefaultViewMode): return Choice(settings.DefaultViewMode, v => settings.DefaultViewMode = v);
            case nameof(AppSettings.PreviewFontFamily):
                var readingFont = new TextBox { Text = settings.PreviewFontFamily, HorizontalAlignment = HorizontalAlignment.Stretch };
                AutomationProperties.SetName(readingFont, "Reading font family");
                var readingChoices = new ComboBox { ItemsSource = FontManager.Current.SystemFonts.Select(f => f.Name).Order(StringComparer.OrdinalIgnoreCase).ToArray(),
                    PlaceholderText = "Choose an installed reading font", HorizontalAlignment = HorizontalAlignment.Stretch };
                AutomationProperties.SetName(readingChoices, "Installed reading fonts");
                readingChoices.SelectionChanged += (_, _) => { if (readingChoices.SelectedItem is string name) readingFont.Text = name; };
                var readingSample = _readingTypefaceSample = new TextBlock { Text = "A note worth keeping\nThe quick brown fox · 0123456789", TextWrapping = TextWrapping.Wrap };
                AutomationProperties.SetName(readingSample, "Reading font sample");
                readingFont.PropertyChanged += (_, e) =>
                {
                    if (e.Property != TextBox.TextProperty || string.IsNullOrWhiteSpace(readingFont.Text)) return;
                    settings.PreviewFontFamily = readingFont.Text;
                    RefreshTypography();
                };
                RefreshTypography();
                return GuideParts.Vertical(readingChoices, readingFont, readingSample, GuideParts.Caption("Installed fonts and fallback families affect Reading prose. Code blocks and exported files keep their own fonts."));
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
            case nameof(AppSettings.UiScale): return DialogParts.Number(settings.UiScale, AppSettings.MinimumUiScale, 2.5, 0.1, v => { settings.UiScale = v; RefreshTypography(); });
            case nameof(AppSettings.ScaleDocumentText): return Check(settings.ScaleDocumentText, v => { settings.ScaleDocumentText = v; RefreshTypography(); });
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
            case nameof(AppSettings.SavedWorkspaceSearches):
                return GuideParts.Caption("Manage individual saved queries with Save search and Library Shortcuts in the main window. Workspaces and search category reset forgets all saved queries; notes are kept.");
            case nameof(AppSettings.ExportPreferences):
                return ExportPreferencesEditor.Build(settings.ExportPreferences);
            case nameof(AppSettings.TabSize): return DialogParts.Number(settings.TabSize, 1, 16, 1, v => settings.TabSize = (int)v);
            case nameof(AppSettings.ReduceMotion): return Check(settings.ReduceMotion, v => settings.ReduceMotion = v);
            case nameof(AppSettings.SpellCheckEnabled): return Check(settings.SpellCheckEnabled, v => settings.SpellCheckEnabled = v);
            case nameof(AppSettings.HybridSourceEnabled): return Check(settings.HybridSourceEnabled, v => settings.HybridSourceEnabled = v);
            case nameof(AppSettings.AutoSaveEnabled): return Check(settings.AutoSaveEnabled, v => settings.AutoSaveEnabled = v);
            case nameof(AppSettings.AutoSaveIdleSeconds): return DialogParts.Number(settings.AutoSaveIdleSeconds, 30, 3600, 1, v => settings.AutoSaveIdleSeconds = (int)v);
            case nameof(AppSettings.ShowContextGuidance): return Check(settings.ShowContextGuidance, v => settings.ShowContextGuidance = v);
            case nameof(AppSettings.WordWrap): return Check(settings.WordWrap, v => { settings.WordWrap = v; RefreshTypography(); });
            case nameof(AppSettings.ShowLineNumbers): return Check(settings.ShowLineNumbers, v => settings.ShowLineNumbers = v);
            case nameof(AppSettings.HighlightCurrentLine): return Check(settings.HighlightCurrentLine, v => settings.HighlightCurrentLine = v);
            case nameof(AppSettings.ShowSpaces): return Check(settings.ShowSpaces, v => settings.ShowSpaces = v);
            case nameof(AppSettings.ShowTabs): return Check(settings.ShowTabs, v => settings.ShowTabs = v);
            case nameof(AppSettings.ShowEndOfLine): return Check(settings.ShowEndOfLine, v => settings.ShowEndOfLine = v);
            case nameof(AppSettings.ShowColumnRuler): return Check(settings.ShowColumnRuler, v => settings.ShowColumnRuler = v);
            case nameof(AppSettings.ColumnRulerPosition): return DialogParts.Number(settings.ColumnRulerPosition, 40, 240, 1, v => settings.ColumnRulerPosition = (int)v);
            case nameof(AppSettings.EditorLineHeight): return DialogParts.Number(settings.EditorLineHeight, 1, 2, .05, v => { settings.EditorLineHeight = v; RefreshTypography(); });
            case nameof(AppSettings.AllowScrollBelowDocument): return Check(settings.AllowScrollBelowDocument, v => settings.AllowScrollBelowDocument = v);
            case nameof(AppSettings.CutCopyWholeLine): return Check(settings.CutCopyWholeLine, v => settings.CutCopyWholeLine = v);
            case nameof(AppSettings.FindMatchCase): return Check(settings.FindMatchCase, v => settings.FindMatchCase = v);
            case nameof(AppSettings.FindWholeWord): return Check(settings.FindWholeWord, v => settings.FindWholeWord = v);
            case nameof(AppSettings.FindUseRegex): return Check(settings.FindUseRegex, v => settings.FindUseRegex = v);
            case nameof(AppSettings.TaskGroupByFile): return Check(settings.TaskGroupByFile, v => settings.TaskGroupByFile = v);
            case nameof(AppSettings.InsertSpacesForTab): return Check(settings.InsertSpacesForTab, v => settings.InsertSpacesForTab = v);
            case nameof(AppSettings.SyncScroll): return Check(settings.SyncScroll, v => settings.SyncScroll = v);
            case nameof(AppSettings.RestoreSession): return Check(settings.RestoreSession, v => settings.RestoreSession = v);
            case nameof(AppSettings.AutoReloadCleanDocuments): return Check(settings.AutoReloadCleanDocuments, v => settings.AutoReloadCleanDocuments = v);
            case nameof(AppSettings.EnsureFinalNewlineOnSave): return Check(settings.EnsureFinalNewlineOnSave, v => settings.EnsureFinalNewlineOnSave = v);
            case nameof(AppSettings.TrimTrailingWhitespaceOnSave): return Check(settings.TrimTrailingWhitespaceOnSave, v => settings.TrimTrailingWhitespaceOnSave = v);
            case nameof(AppSettings.AllowExternalLinkOpening): return Check(settings.AllowExternalLinkOpening, v => settings.AllowExternalLinkOpening = v);
            case nameof(AppSettings.HistoryBudgetMiB): return DialogParts.Number(settings.HistoryBudgetMiB, 1, 1024, 1, v => settings.HistoryBudgetMiB = (int)v);
            case nameof(AppSettings.KnowledgeAssistantEnabled): return Check(settings.KnowledgeAssistantEnabled, v => settings.KnowledgeAssistantEnabled = v);
            case nameof(AppSettings.KnowledgeAllowRemote): return Check(settings.KnowledgeAllowRemote, v => settings.KnowledgeAllowRemote = v);
            default: throw new ArgumentOutOfRangeException(nameof(property));
        }
    }

    private static CheckBox Check(bool value, Action<bool> apply) => DialogParts.Check("Enabled", value, apply);
    private static ComboBox Choice<T>(T selected, Action<T> apply) where T : struct, Enum
    {
        var box = new ComboBox { ItemsSource = Enum.GetValues<T>(), ItemTemplate = EnumChoices.Template<T>(), SelectedItem = selected, MinWidth = 220 };
        box.SelectionChanged += (_, _) => { if (box.SelectedItem is T value) apply(value); };
        return box;
    }
}
