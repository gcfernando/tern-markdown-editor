using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Tern.App.Commands;
using Tern.App.Help;

namespace Tern.Desktop.Views;

/// <summary>Everyday lessons first, with the complete searchable reference one level deeper.</summary>
public sealed class HelpWindow : Window
{
    public HelpWindow(CommandRegistry registry, string? featureId = null, Func<string, Task>? openFeature = null, Func<string, Task>? askAI = null,
        Func<string, CancellationToken, Task<string>>? answerAI = null, string? initialQuestion = null)
    {
        DialogParts.Setup(this, "Learn Tern", 1080, 820);
        MinWidth = 620; MinHeight = 560;
        var lessons = BeginnerLessons.For(registry);
        var all = FeatureCatalog.Search(registry, null);
        var goals = new ListBox { ItemsSource = lessons };
        AutomationProperties.SetName(goals, "Choose what you want to do");
        goals.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<BeginnerLesson>((v, _) => v is null ? new TextBlock() : GuideParts.Vertical(new TextBlock { Text = v.Name, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap }, GuideParts.Caption(v.When)));
        var compactGoals = new ComboBox { ItemsSource = lessons, HorizontalAlignment = HorizontalAlignment.Stretch };
        compactGoals.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<BeginnerLesson>((v, _) => new TextBlock { Text = v?.Name, TextWrapping = TextWrapping.Wrap });
        AutomationProperties.SetName(compactGoals, "Choose a learning goal");
        var search = new TextBox { PlaceholderText = "Ask anything…\nGet help with Tern, Markdown, your next idea, or something new.",
            AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 82, MaxHeight = 150, MaxLength = 8000, FontSize = GuideParts.Scaled(16), Padding = new Thickness(12) };
        AutomationProperties.SetName(search, "Search feature help");
        var categories = new ComboBox { ItemsSource = new[] { "All features" }.Concat(all.Select(f => f.Category).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)).ToArray(), SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(categories, "Feature categories");
        var features = new ListBox();
        AutomationProperties.SetName(features, "Feature search results");
        features.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<FeatureHelp>((v, _) => v is null ? new TextBlock() : GuideParts.Vertical(new TextBlock { Text = v.Name, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap }, GuideParts.Caption(v.Category)));
        var compactFeatures = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, PlaceholderText = "Choose a matching feature…" };
        compactFeatures.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<FeatureHelp>((v, _) => new TextBlock { Text = v?.Name, TextWrapping = TextWrapping.Wrap });
        AutomationProperties.SetName(compactFeatures, "Choose a matching feature");
        var count = GuideParts.Caption(""); AutomationProperties.SetName(count, "Feature result count");
        var reference = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*"), RowSpacing = 12, Children = { categories, count, features } };
        for (var i = 0; i < reference.Children.Count; i++) Grid.SetRow(reference.Children[i], i);
        var goalTab = new TabItem { Header = "Start here", Content = goals, FontSize = GuideParts.Scaled(14) };
        var referenceTab = new TabItem { Header = "Feature reference", Content = reference, FontSize = GuideParts.Scaled(14) };
        var tabs = new TabControl { ItemsSource = new[] { goalTab, referenceTab }, SelectedIndex = 0 };
        AutomationProperties.SetName(tabs, "Guide sections");
        var title = GuideParts.Title(""); AutomationProperties.SetName(title, "Lesson title");
        var purpose = GuideParts.Body("");
        var need = GuideParts.Body(""); AutomationProperties.SetName(need, "Before you start");
        var step = GuideParts.Body(""); AutomationProperties.SetName(step, "Tutorial step");
        var example = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Cascadia Code, Consolas, monospace"), FontSize = GuideParts.Scaled(14) };
        AutomationProperties.SetName(example, "Lesson example");
        var sourceIllustration = GuideParts.Card(GuideParts.Vertical(GuideParts.Caption("WRITING MARKS"), GuideParts.Body("# My note\n\nRemember **tickets**.")));
        var previewIllustration = GuideParts.Card(GuideParts.Vertical(GuideParts.Caption("READING PREVIEW"), new TextBlock { Text = "My note", FontSize = GuideParts.Scaled(20), FontWeight = FontWeight.SemiBold }, GuideParts.Body("Remember tickets. (tickets appears bold)")));
        var illustration = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 10, IsVisible = false, Children = { sourceIllustration, previewIllustration } };
        Grid.SetColumn(previewIllustration, 1);
        AutomationProperties.SetName(illustration, "Source to preview illustration");
        var exampleCard = GuideParts.Card(GuideParts.Vertical(GuideParts.Caption("TRY THIS EXAMPLE — SELECT AND COPY THE TEXT"), example, illustration));
        var result = GuideParts.Body(""); AutomationProperties.SetName(result, "Expected lesson result");
        var troubleshooting = GuideParts.Body(""); AutomationProperties.SetName(troubleshooting, "Lesson troubleshooting");
        var trouble = new Expander { Header = "If something doesn't work", Content = troubleshooting, HorizontalAlignment = HorizontalAlignment.Stretch };
        var advancedText = GuideParts.Body("");
        var advanced = new Expander { Header = "Advanced details", Content = advancedText, IsVisible = false, HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(advanced, "Advanced feature details");
        var whatToDo = GuideParts.Caption("WHAT TO DO");
        var answerText = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap, FontSize = GuideParts.Scaled(15), LineHeight = GuideParts.Scaled(23) };
        AutomationProperties.SetName(answerText, "Guide AI answer");
        var answerCard = GuideParts.Card(GuideParts.Vertical(GuideParts.Caption("ANSWER"), answerText));
        answerCard.IsVisible = false;
        CancellationTokenSource? answerRequest = null;
        var cancelAnswer = new Button { Content = "Cancel answer", IsVisible = false };
        cancelAnswer.Click += (_, _) => answerRequest?.Cancel();
        Closed += (_, _) => answerRequest?.Cancel();
        var detail = new ScrollViewer { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, Content = new StackPanel { Spacing = 18, Children = { title, GuideParts.Card(GuideParts.Vertical(whatToDo, step)), need, purpose, exampleCard, result, trouble, advanced } } };
        AutomationProperties.SetName(detail, "Lesson instructions");
        var back = new Button { Content = "Back" }; var next = new Button { Content = "Next", IsDefault = true };
        var skip = new Button { Content = "Skip tutorial" }; var replay = new Button { Content = "Replay" };
        var launch = new Button { IsVisible = false }; AutomationProperties.SetName(launch, "Open guided feature");
        var close = new Button { Content = "Exit guide", IsCancel = true };
        ((StackPanel)detail.Content!).Children.Insert(0, answerCard);
        var ask = new Button { Content = "Ask question  ↗", IsEnabled = false, IsVisible = false, Classes = { "accent" } };
        var askingAI = false;
        AutomationProperties.SetName(ask, "Ask question");
        ToolTip.SetTip(ask, "Send this question using the AI connection selected on Tern's main screen. Your documents are not included.");
        var activeLesson = lessons[0]; IReadOnlyList<string> steps = []; string? launchId = null; var index = 0; var skipped = false; var launching = false;
        void ShowStep()
        {
            step.Text = skipped ? "Tutorial skipped. Choose another goal, or Replay when you want to try again. Your writing has not been changed." : $"Step {index + 1} of {steps.Count}\n{steps[index]}";
            back.IsEnabled = !skipped && index > 0; next.IsEnabled = !skipped && index < steps.Count - 1; skip.IsEnabled = !skipped;
            launch.IsVisible = openFeature is not null && FeatureCatalog.CanLaunchFromGuide(launchId);
            launch.Content = launchId == CommandIds.NewFile ? "Start a new document" : registry.TryGet(launchId ?? "", out var command) ? command.Title : "Open this tool";
        }
        void SelectLesson(BeginnerLesson lesson)
        {
            activeLesson = lesson; title.Text = lesson.Name; purpose.Text = lesson.When + "\n\n" + lesson.Why; need.Text = "Before you start: " + lesson.Prerequisite;
            example.Text = lesson.Example; exampleCard.IsVisible = true; illustration.IsVisible = lesson.Id == "practice"; result.Text = "When you're finished: " + lesson.Result; troubleshooting.Text = lesson.Troubleshooting; trouble.IsExpanded = false; advanced.IsVisible = false;
            steps = lesson.Steps.Select(s => s.Title + "\n" + s.Action + "\n\nWhat you'll see: " + s.Expected).ToArray(); launchId = lesson.LaunchCommandId;
            index = 0; skipped = false; detail.Offset = default; ShowStep();
        }
        void SelectFeature(FeatureHelp feature)
        {
            title.Text = feature.Name; purpose.Text = feature.Description; need.Text = "Before you start: " + feature.Prerequisite + (feature.Shortcut.Length > 0 ? "\nCurrent shortcut: " + feature.Shortcut : "");
            exampleCard.IsVisible = false; result.Text = "Follow the steps deliberately, then check the result before saving. This reference does not run actions automatically.";
            troubleshooting.Text = "If the feature is unavailable, check the requirements above. Open a document or folder when needed. Search another feature here, or return to Start here for an everyday lesson."; trouble.IsExpanded = false;
            advancedText.Text = feature.AdvancedDetails; advanced.IsVisible = !string.IsNullOrWhiteSpace(feature.AdvancedDetails); advanced.IsExpanded = false;
            steps = feature.Steps; launchId = feature.LaunchCommandId; index = 0; skipped = false; detail.Offset = default; ShowStep();
        }
        void Refresh()
        {
            var selected = features.SelectedItem as FeatureHelp;
            var rows = FeatureCatalog.Search(registry, search.Text).Where(f => categories.SelectedIndex == 0 || f.Category == categories.SelectedItem as string).ToArray();
            features.ItemsSource = rows; features.SelectedItem = rows.FirstOrDefault(f => f.Id == selected?.Id) ?? rows.FirstOrDefault();
            compactFeatures.ItemsSource = rows;
            compactFeatures.SelectedItem = features.SelectedItem;
            count.Text = rows.Length == 0 ? "No matches. Try a simpler word." : $"{rows.Length} features and settings";
            ask.IsEnabled = !askingAI && !string.IsNullOrWhiteSpace(search.Text);
            ask.IsVisible = !string.IsNullOrWhiteSpace(search.Text);
            if (rows.Length == 0)
            {
                title.Text = "Ask about anything";
                purpose.Text = "This question does not match an offline tutorial. Ask your selected AI connection for an answer here. Check its answer for accuracy.";
                need.Text = "Guide questions include your question and Tern's feature reference. Your notes are not attached. Choose an AI connection in the main toolbar before asking.";
                exampleCard.IsVisible = false; advanced.IsVisible = false; trouble.IsExpanded = false;
                result.Text = "Your documents have not changed. No question has been sent.";
                troubleshooting.Text = "Clear the search to browse all features, or choose Start here for everyday lessons.";
                steps = ["Choose Ask question for an AI answer, or clear the question to browse the offline tutorials. A guide answer does not modify a document."];
                launchId = null; index = 0; skipped = false; detail.Offset = default; ShowStep();
            }
            else if (!string.IsNullOrWhiteSpace(search.Text) && GuideQuery.Lesson(search.Text) is { } lessonId)
                SelectLesson(lessons.Single(l => l.Id == lessonId));
        }
        goals.SelectionChanged += (_, _) => { if (goals.SelectedItem is BeginnerLesson lesson) { compactGoals.SelectedItem = lesson; SelectLesson(lesson); } };
        compactGoals.SelectionChanged += (_, _) => { if (compactGoals.SelectedItem is BeginnerLesson lesson) { goals.SelectedItem = lesson; SelectLesson(lesson); } };
        features.SelectionChanged += (_, _) => { if (features.SelectedItem is FeatureHelp feature) { compactFeatures.SelectedItem = feature; SelectFeature(feature); } };
        compactFeatures.SelectionChanged += (_, _) => { if (compactFeatures.SelectedItem is FeatureHelp feature) { features.SelectedItem = feature; SelectFeature(feature); } };
        tabs.SelectionChanged += (_, e) => { if (!ReferenceEquals(e.Source, tabs)) return; if (tabs.SelectedIndex == 0) SelectLesson(goals.SelectedItem as BeginnerLesson ?? activeLesson); else if (features.SelectedItem is FeatureHelp feature) SelectFeature(feature); };
        search.TextChanged += (_, _) => { if (!string.IsNullOrWhiteSpace(search.Text)) tabs.SelectedIndex = 1; categories.SelectedIndex = 0; Refresh(); };
        ask.Click += async (_, _) =>
        {
            if (askingAI || string.IsNullOrWhiteSpace(search.Text)) return;
            var question = search.Text; askingAI = true; ask.IsEnabled = false;
            try
            {
                if (answerAI is not null)
                {
                    answerRequest = new CancellationTokenSource();
                    cancelAnswer.IsVisible = true;
                    answerCard.IsVisible = true; answerText.Text = "Thinking about your question…"; detail.Offset = default;
                    try { answerText.Text = await answerAI(question, answerRequest.Token); }
                    catch (OperationCanceledException) { answerText.Text = "The answer was canceled. You can ask again."; }
                    catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException
                        or System.Text.Json.JsonException or System.ComponentModel.Win32Exception or PlatformNotSupportedException)
                    { answerText.Text = ex.Message; }
                    finally { answerRequest.Dispose(); answerRequest = null; cancelAnswer.IsVisible = false; }
                }
                else if (askAI is not null) await askAI(question);
                else new AIConnectionsWindow(initialQuestion: question).Show(this);
            }
            finally { askingAI = false; ask.IsEnabled = !string.IsNullOrWhiteSpace(search.Text); }
        };
        search.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter && e.KeyModifiers == Avalonia.Input.KeyModifiers.Control)
            { ask.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); e.Handled = true; }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        categories.SelectionChanged += (_, _) => Refresh();
        back.Click += (_, _) => { index = Math.Max(0, index - 1); ShowStep(); }; next.Click += (_, _) => { index = Math.Min(steps.Count - 1, index + 1); ShowStep(); };
        replay.Click += (_, _) => { index = 0; skipped = false; ShowStep(); }; skip.Click += (_, _) => { skipped = true; ShowStep(); }; close.Click += (_, _) => Close();
        launch.Click += async (_, _) =>
        {
            if (launching || openFeature is null || !FeatureCatalog.CanLaunchFromGuide(launchId)) return;
            var command = launchId!; launching = true; launch.IsEnabled = false;
            try { await openFeature(command); }
            finally { launching = false; launch.IsEnabled = true; }
        };
        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("300,*"), ColumnSpacing = 24, Children = { tabs, detail } }; Grid.SetColumn(detail, 1);
        var intro = GuideParts.Caption("");
        var guideTitle = GuideParts.Title("A question is a good place to start.");
        var wasCompact = false;
        void Arrange()
        {
            var compact = Width < 900; body.ColumnDefinitions = new ColumnDefinitions(compact ? "*" : "300,*"); body.RowDefinitions = new RowDefinitions(compact ? "Auto,*" : "*"); body.RowSpacing = compact ? 18 : 0;
            if (compact && !wasCompact) categories.SelectedIndex = 0;
            wasCompact = compact;
            goalTab.Content = compact ? compactGoals : goals;
            referenceTab.Content = compact ? compactFeatures : reference;
            tabs.MaxHeight = compact ? 110 : double.PositiveInfinity; tabs.MinHeight = 0;
            guideTitle.IsVisible = !compact;
            intro.IsVisible = !compact;
            title.FontSize = GuideParts.Scaled(compact ? 18 : 22);
            whatToDo.IsVisible = !compact;
            intro.Text = compact ? "Ask a question or browse a tutorial." : "Ask your selected AI connection anything, or explore the practical guides below. Your notes stay out of guide questions.";
            Grid.SetColumn(detail, compact ? 0 : 1); Grid.SetRow(detail, compact ? 1 : 0);
        }
        SizeChanged += (_, _) => Arrange();
        var questionCard = GuideParts.Card(GuideParts.Vertical(search, GuideParts.Actions(cancelAnswer, ask)));
        Content = GuideParts.Frame(GuideParts.Vertical(guideTitle, intro, questionCard), body, GuideParts.Actions(back, next, skip, replay, launch, close));
        Refresh(); goals.SelectedIndex = 0; SelectLesson(lessons[0]); Arrange();
        if (featureId is not null && features.Items.OfType<FeatureHelp>().FirstOrDefault(f => f.Id == featureId) is { } initial)
        { tabs.SelectedIndex = 1; features.SelectedItem = initial; SelectFeature(initial); }
        if (initialQuestion is not null) search.Text = initialQuestion;
    }
}
