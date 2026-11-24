using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Tern.Desktop.Services;
using Tern.App.Shell;

namespace Tern.Desktop.Views;

/// <summary>A focused, multiline writing prompt over the current document.</summary>
public sealed class AIPromptPanel : Border, IDisposable, IDismissibleOverlay
{
    public bool DismissWithEscape => false;

    private readonly CancellationTokenSource _lifetime = new();
    private bool _busy;
    private bool _disposed;
    private readonly TextBox _question;
    private readonly ComboBox _connections;
    private readonly bool _writingOnly;
    private bool _refreshingConnections;
    private Action _refreshStatus = () => { };
    internal ActivityScope? CurrentActivity { get; private set; }

    public AIPromptPanel(string documentName, AIWorkspaceService workspace, Func<string, string, CancellationToken, Task<string>> generate,
        Action close, Func<Task> configure, Action<string> select, string shortcut = "Ctrl+Shift+Space",
        Func<string, string, string?>? validate = null, Func<string, Task>? openTools = null,
        ComboBox? scope = null, string? initialAction = null, Action<string>? changeAction = null, bool writingOnly = false,
        bool isEmptyDraft = false, string? initialInstruction = null, ActivityFeed? activities = null)
    {
        _writingOnly = writingOnly;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Center;
        Margin = new Thickness(16);
        MaxWidth = 720;
        Padding = new Thickness(20);
        this[!CornerRadiusProperty] = GuideParts.Radius("Popup").ToBinding();
        BorderThickness = new Thickness(1);
        Classes.Add("ai-writing-workbench");
        this.Bind(BoxShadowProperty, this.GetResourceObservable(Themes.ThemeManager.Shadow));
        this.Bind(BackgroundProperty, this.GetResourceObservable("Tern.Surface"));
        this.Bind(BorderBrushProperty, this.GetResourceObservable("Tern.Border"));
        var question = _question = new TextBox
        {
            AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 260, MaxHeight = 380,
            [!TextBox.FontSizeProperty] = GuideParts.Font("Lead").ToBinding(), Padding = new Thickness(12), MaxLength = AIConnectionRules.MaximumSendCharacters,
        };
        AutomationProperties.SetName(question, "AI writing question");
        var connections = _connections = new ComboBox
        {
            MaxWidth = 280,
            PlaceholderText = "Choose an AI connection", HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<WorkspaceConnection>((p, _) =>
                new TextBlock { Text = p is null ? "" : p.Name + " · " + p.Capabilities + " · " + p.Location, TextTrimming = TextTrimming.CharacterEllipsis }),
        };
        LoadConnections(workspace);
        AutomationProperties.SetName(connections, "Writing AI connection");
        var destination = GuideParts.Caption("");
        AutomationProperties.SetName(destination, "AI writing destination");
        var status = MessageTones.Follow(GuideParts.Body(""));
        AutomationProperties.SetName(status, "AI writing status");
        var validation = MessageTones.Follow(GuideParts.Body(""));
        AutomationProperties.SetName(validation, "AI writing validation");
        var progress = new ContentControl { IsVisible = false };
        AutomationProperties.SetName(progress, "AI generation progress");
        var send = new Button { Content = "Generate for review", MinHeight = 42 };
        send.Classes.Add("accent");
        AutomationProperties.SetName(send, "Generate AI suggestion");
        var cancel = new Button { Content = "Close" };
        var retry = new Button { Content = "Retry generation", IsVisible = false };
        var settings = new Button { Content = "Manage connections", Classes = { "flat" } };
        var tools = new Button { Content = "Open MCP tools", MinHeight = 42, IsVisible = false };
        tools.Classes.Add("accent"); AutomationProperties.SetName(tools, "Open selected MCP tools");
        var promptTitle = GuideParts.Title("");
        var promptHelp = GuideParts.Body("");
        AutomationProperties.SetName(promptHelp, "How to write an AI instruction");
        var scopeHelp = GuideParts.Caption("");
        AutomationProperties.SetName(scopeHelp, "What the selected text scope shares");
        var shortcuts = new WrapPanel { [!WrapPanel.ItemSpacingProperty] = GuideParts.Space(8).ToBinding(), [!WrapPanel.LineSpacingProperty] = GuideParts.Space(4).ToBinding() };
        var connectionField = GuideParts.Vertical(GuideParts.Caption("Connection"), connections);
        var setup = new Button { Content = "Set up connection", Classes = { "accent" } };
        var setupHint = GuideParts.Caption("Choose Set up connection to add a local or remote writing model.");
        string? failure = null;
        static string Starter(string? action) => action switch
        {
            "Rewrite" => "Rewrite this for clarity while keeping every fact. Preserve meaning, Markdown structure, links, and code. Return only the rewritten Markdown.",
            "Summarize" => "Summarize the supplied text in up to five concise Markdown bullets. Keep key facts, decisions, and caveats. Return only the bullets.",
            "Explain" => "Explain the supplied text in plain language for a beginner. Define essential terms and distinguish what the text says from anything uncertain. Return only the explanation in Markdown.",
            "Extract action items" => "Extract only explicit action items from the supplied text. Return a Markdown checklist with one item per task. Include owners and due dates only when stated. If there are no tasks, return 'No action items found.'",
            "Proofread" => "Correct spelling, grammar, and punctuation in the supplied text. Preserve tone, meaning, formatting, links, and code. Return only the corrected Markdown.",
            "Improve headings" => "Improve only the headings. Preserve their levels and all other Markdown.",
            _ => ""
        };
        question.Text = initialInstruction ?? Starter(initialAction);
        foreach (var (label, action, description) in new[]
        {
            ("Improve selected text", "Rewrite", "Suggest clearer wording for the selected text."),
            ("Summarize", "Summarize", "Turn the selected text into a shorter summary."),
            ("Explain", "Explain", "Explain the selected text in plain language."),
            ("Extract tasks", "Extract action items", "Turn action items in the selected text into a checklist."),
            ("Proofread", "Proofread", "Fix spelling and grammar while preserving your wording and tone.")
        })
        {
            var button = new Button { Content = label };
            ToolTip.SetTip(button, description);
            button.Click += (_, _) =>
            {
                changeAction?.Invoke(action);
                question.Text = Starter(action);
                question.Focus();
                question.CaretIndex = question.Text.Length;
            };
            shortcuts.Children.Add(button);
        }
        string? ValidationError()
        {
            if (_disposed) return "This writing panel is closed.";
            if (connections.SelectedItem is not WorkspaceConnection profile) return "Choose an AI connection before generating.";
            if (!profile.CanWrite) return "MCP connections provide tools. Open the tool workspace to select a tool and review its arguments.";
            var instruction = question.Text ?? "";
            if (string.IsNullOrWhiteSpace(instruction)) return "Enter a question or instruction before generating.";
            return failure ?? (validate is not null ? validate(profile.Id, instruction)
                : workspace.WritingValidationError(profile.Id, instruction));
        }
        void Refresh()
        {
            var profile = connections.SelectedItem as WorkspaceConnection;
            var error = ValidationError();
            send.IsEnabled = !_busy && error is null;
            ToolTip.SetTip(send, error ?? "Generate a suggestion to review before applying it.");
            ToolTip.SetShowOnDisabled(send, true);
            retry.IsVisible = failure is not null;
            retry.IsEnabled = !_busy && !_disposed;
            send.Opacity = send.IsEnabled ? 1 : .45;
            var isTool = profile is { CanWrite: false };
            var selected = Equals(scope?.SelectedItem, "Selected text");
            var needsSetup = connections.ItemCount == 0;
            promptTitle.Text = selected ? "What would you like me to do with this text?" : "What would you like to write or change?";
            promptHelp.Text = selected
                ? "Choose a starter to fill in an instruction you can edit, or type your own. Generate for review shows the result before you apply it."
                : isEmptyDraft
                    ? "Describe what you want to create, such as an outline or checklist. Generate for review shows a draft you can review before applying."
                    : "Describe what to write or change. For shortcuts like Rewrite or Summarize, close this panel, select text in Source, and reopen Write with AI.";
            promptHelp.IsVisible = !isTool;
            scopeHelp.Text = scope?.SelectedItem switch
            {
                "Selected text" => "Shares only the text you highlighted in Source. After review, Apply changes replaces that selection.",
                "Instruction only" => "Shares only your instruction. The generated result is inserted; existing note text is kept.",
                _ when isEmptyDraft => "This note is empty, so only your instruction is shared.",
                _ when scope is null => "Shares the entire note. To share only part of it, select that text in Source before opening Write with AI.",
                _ => "Shares all the text in this note. Choose a smaller scope to share less."
            };
            scopeHelp.IsVisible = !isTool;
            question.PlaceholderText = selected ? "e.g. Make this shorter and keep the warning" : "e.g. Draft a short introduction for this note";
            question.IsEnabled = !isTool;
            shortcuts.IsVisible = selected && !isTool;
            shortcuts.IsEnabled = !_busy;
            if (scope is not null) scope.IsEnabled = !_busy && !isTool;
            connectionField.IsVisible = !needsSetup;
            setupHint.IsVisible = setup.IsVisible = needsSetup;
            settings.IsVisible = !needsSetup;
            tools.IsVisible = isTool; tools.IsEnabled = !_busy && openTools is not null;
            send.IsVisible = !isTool && !needsSetup;
            validation.Text = isTool
                ? "Choose a tool, inspect its description and JSON arguments, then run only the call you approve. Tool results never replace your note automatically."
                : needsSetup ? "" : error ?? "";
            validation.IsVisible = !string.IsNullOrEmpty(validation.Text);
            status.IsVisible = !string.IsNullOrEmpty(status.Text);
            destination.Text = profile is null
                ? ""
                : isTool ? "Tool server: " + profile.Destination + ". Your note is not sent when opening tools."
                : $"{scope?.SelectedItem ?? "Entire document"} and your instruction go to:\n{workspace.Summary(profile).Description}"
                    + (string.Equals(profile.Kind, "copilot", StringComparison.OrdinalIgnoreCase) ? "\n" + CopilotConnectionWindow.CopilotInputNotice : "");
            destination.IsVisible = !string.IsNullOrEmpty(destination.Text);
        }
        connections.SelectionChanged += (_, _) =>
        {
            failure = null;
            if (!_refreshingConnections && connections.SelectedItem is WorkspaceConnection profile) select(profile.Id);
            Refresh();
        };
        question.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty) Refresh(); };
        if (scope is not null) scope.SelectionChanged += (_, _) => Refresh();
        tools.Click += async (_, _) =>
        {
            if (!_busy && connections.SelectedItem is WorkspaceConnection { CanWrite: false } selected && openTools is not null)
            { close(); await openTools(selected.Id); }
        };
        cancel.Click += (_, _) => { _lifetime.Cancel(); close(); };
        async Task ConfigureAsync()
        {
            if (_busy) return;
            await configure();
            if (!_disposed) FocusQuestion();
        }
        settings.Click += async (_, _) => await ConfigureAsync();
        setup.Click += async (_, _) => await ConfigureAsync();
        void Retry()
        {
            if (_busy || _disposed || failure is null) return;
            failure = null;
            status.Text = "";
            Refresh();
            if (send.IsEnabled) send.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        }
        retry.Click += (_, _) => Retry();
        send.Click += async (_, _) =>
        {
            if (_busy || ValidationError() is not null || connections.SelectedItem is not WorkspaceConnection profile) { Refresh(); return; }
            _busy = true; question.IsReadOnly = true; connections.IsEnabled = settings.IsEnabled = false;
            cancel.Content = "Cancel request"; send.Content = "Generating…"; status.Text = "Validating connection and model before generation…"; Refresh();
            using var activity = activities?.Begin("AI generation", documentName, cancel: () => { if (!_disposed) _lifetime.Cancel(); });
            CurrentActivity = activity;
            if (activity is not null)
            {
                // The shared indicator reports each phase and honors reduced motion.
                progress.Content = new LoadingState(activities!, activity.Id);
                progress.IsVisible = true;
                status.Text = "";
                Refresh();
            }
            try
            {
                status.Text = await generate(profile.Id, question.Text ?? "", _lifetime.Token);
                activity?.Complete(outcome: "Suggestion ready for review; not applied");
                if (!_disposed && !_lifetime.IsCancellationRequested) close();
            }
            catch (OperationCanceledException)
            {
                if (_lifetime.IsCancellationRequested)
                {
                    activity?.Cancel("Canceled; no suggestion was applied. Already transmitted text cannot be recalled.");
                    status.Text = "Request canceled. Your note has not changed.";
                }
                else
                {
                    activity?.Fail("The model request timed out. No suggestion was applied.");
                    status.Text = failure = "The model request timed out. Your note has not changed. Retry after checking the connection.";
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException
                or System.Text.Json.JsonException or System.ComponentModel.Win32Exception or PlatformNotSupportedException)
            {
                activity?.Fail(ex.Message);
                status.Text = "Generation failed. Your note has not changed. " + Tern.App.Shell.UserProblem.Plain(ex);
                failure = "Generation stopped. " + Tern.App.Shell.UserProblem.Plain(ex);
                activities?.Post("AI generation failed. Your note has not changed.", ActivitySeverity.Error,
                    [new BannerAction("Retry", Retry), new BannerAction("Manage connections", async () => { if (!_disposed) await ConfigureAsync(); })],
                    Tern.App.Shell.UserProblem.Plain(ex));
            }
            catch (Exception ex) { activity?.Fail(ex.Message); throw; }
            finally
            {
                CurrentActivity = null;
                progress.Content = null; progress.IsVisible = false;
                _busy = false; question.IsReadOnly = false; connections.IsEnabled = settings.IsEnabled = true;
                cancel.Content = "Close"; send.Content = "Generate for review"; Refresh();
            }
        };
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Escape) { _lifetime.Cancel(); close(); e.Handled = true; }
            else if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.Control)
            { send.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); e.Handled = true; }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        DetachedFromVisualTree += (_, _) => Dispose();
        var heading = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), [!Grid.ColumnSpacingProperty] = GuideParts.Space(16).ToBinding(), Children =
        {
            GuideParts.Vertical(GuideParts.Body("✦  Write with AI"), GuideParts.Caption(documentName)),
            new TextBlock { Text = shortcut, Classes = { "muted" }, VerticalAlignment = VerticalAlignment.Center },
        } };
        Grid.SetColumn(heading.Children[1], 1);
        var options = new WrapPanel { [!WrapPanel.ItemSpacingProperty] = GuideParts.Space(12).ToBinding(), [!WrapPanel.LineSpacingProperty] = GuideParts.Space(8).ToBinding() };
        if (scope is not null) options.Children.Add(GuideParts.Vertical(GuideParts.Caption("Text to share"), scope));
        options.Children.Add(connectionField);
        var contentScroll = new ScrollViewer
        {
            Content = GuideParts.Vertical(
                promptTitle, promptHelp, shortcuts, question, options, scopeHelp, setupHint, destination, validation, status),
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        };
        var actions = GuideParts.ButtonRow(settings, cancel, setup, tools, retry, send);
        var footer = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 10, 0, 0),
            Child = GuideParts.Vertical(progress, actions) };
        footer.Bind(BorderBrushProperty, this.GetResourceObservable("Tern.Border"));
        Child = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), [!Grid.RowSpacingProperty] = GuideParts.Space(12).ToBinding(), Children = { heading, contentScroll, footer } };
        Grid.SetRow(contentScroll, 1); Grid.SetRow(footer, 2);
        Refresh();
        _refreshStatus = Refresh;
        AttachedToVisualTree += (_, _) => FocusQuestion();
    }

    internal void RefreshConnections(AIWorkspaceService workspace)
    {
        if (_busy || _disposed) return;
        _refreshingConnections = true;
        try { LoadConnections(workspace); }
        finally { _refreshingConnections = false; }
        _refreshStatus();
    }

    private void LoadConnections(AIWorkspaceService workspace)
    {
        var available = _writingOnly ? workspace.Connections.Where(profile => profile.CanWrite).ToArray() : workspace.Connections;
        _connections.ItemsSource = available;
        _connections.SelectedItem = workspace.SelectedConnection is { } current && available.Contains(current)
            ? current : null;
    }

    internal void FocusQuestion() => DispatcherTimer.RunOnce(() =>
    {
        if (!_disposed) _question.Focus();
    }, TimeSpan.FromMilliseconds(80));

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        _lifetime.Dispose();
        GC.SuppressFinalize(this);
    }
}
