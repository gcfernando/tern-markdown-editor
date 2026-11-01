using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Tern.Desktop.Services;

namespace Tern.Desktop.Views;

/// <summary>A focused, multiline writing prompt over the current document.</summary>
public sealed class AIPromptPanel : Border, IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private bool _busy;
    private bool _disposed;
    private readonly TextBox _question;
    private readonly ComboBox _connections;
    private bool _refreshingConnections;
    private Action _refreshStatus = () => { };

    public AIPromptPanel(string documentName, AIWorkspaceService workspace, Func<string, string, CancellationToken, Task<string>> generate,
        Action close, Func<Task> configure, Action<string> select, string shortcut = "Alt+Space", Func<string, string>? requestPreview = null,
        Func<string, string, string?>? validate = null, Func<string, Task>? openTools = null)
    {
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;
        Margin = new Thickness(24);
        MaxWidth = 1120;
        MaxHeight = 900;
        MinWidth = 320;
        Padding = new Thickness(28);
        CornerRadius = new CornerRadius(14);
        BorderThickness = new Thickness(1);
        Classes.Add("ai-writing-workbench");
        BoxShadow = new BoxShadows(new BoxShadow { Blur = 38, OffsetY = 12, Color = Color.Parse("#50000000") });
        this.Bind(BackgroundProperty, this.GetResourceObservable("Tern.Surface"));
        this.Bind(BorderBrushProperty, this.GetResourceObservable("Tern.Accent"));
        var question = _question = new TextBox
        {
            AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 280, MaxHeight = 520,
            FontSize = GuideParts.Scaled(16), Padding = new Thickness(18), MaxLength = 8000,
            PlaceholderText = "What would you like to write or change?\nFor example: Turn these notes into a clear project plan…",
        };
        AutomationProperties.SetName(question, "AI writing question");
        var connections = _connections = new ComboBox
        {
            ItemsSource = workspace.Connections, SelectedItem = workspace.SelectedConnection, MinWidth = 200,
            PlaceholderText = "Choose an AI connection", HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<WorkspaceConnection>((p, _) =>
                new TextBlock { Text = p is null ? "" : p.Name + " · " + p.ProviderLabel, TextTrimming = TextTrimming.CharacterEllipsis }),
        };
        AutomationProperties.SetName(connections, "Writing AI connection");
        var destination = GuideParts.Caption("");
        AutomationProperties.SetName(destination, "AI writing destination");
        var status = GuideParts.Body("");
        AutomationProperties.SetName(status, "AI writing status");
        var validation = GuideParts.Body("");
        AutomationProperties.SetName(validation, "AI writing validation");
        var send = new Button { Content = "Generate & update note", MinHeight = 42 };
        send.Classes.Add("accent");
        AutomationProperties.SetName(send, "Generate and update Markdown");
        var cancel = new Button { Content = "Close" };
        var settings = new Button { Content = "Manage connections", Classes = { "flat" } };
        var tools = new Button { Content = "Open MCP tools", MinHeight = 42, IsVisible = false };
        tools.Classes.Add("accent"); AutomationProperties.SetName(tools, "Open selected MCP tools");
        var requestText = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 240 };
        AutomationProperties.SetName(requestText, "Exact AI writing request preview");
        var requestDetails = new Expander { Header = "Review the exact request", IsVisible = requestPreview is not null,
            HorizontalAlignment = HorizontalAlignment.Stretch, Content = requestText, Classes = { "ai-advanced" } };
        string? failure = null;
        string? ValidationError()
        {
            if (_disposed) return "This writing panel is closed.";
            if (connections.SelectedItem is not WorkspaceConnection profile) return "Choose an AI connection before generating.";
            if (!profile.CanWrite) return "MCP connections provide tools. Open the tool workspace to select a tool and review its arguments.";
            if (string.IsNullOrWhiteSpace(question.Text)) return "Enter a question or instruction before generating.";
            return failure ?? (validate is not null ? validate(profile.Id, question.Text)
                : workspace.WritingValidationError(profile.Id, requestPreview?.Invoke(question.Text) ?? question.Text));
        }
        void Refresh()
        {
            var profile = connections.SelectedItem as WorkspaceConnection;
            var error = ValidationError();
            send.IsEnabled = !_busy && error is null;
            send.Opacity = send.IsEnabled ? 1 : .45;
            var isTool = profile is { CanWrite: false };
            question.IsEnabled = !isTool;
            requestDetails.IsVisible = requestPreview is not null && !isTool;
            tools.IsVisible = isTool; tools.IsEnabled = !_busy && openTools is not null;
            send.IsVisible = !isTool;
            validation.Text = isTool
                ? "Choose a tool, inspect its description and JSON arguments, then run only the call you approve. Tool results never replace your note automatically."
                : error ?? "The connection is checked before generation. The result updates this note in one undoable edit.";
            destination.Text = profile is null
                ? "Choose a saved connection, or use Manage connections to create one."
                : isTool ? "Tool server: " + profile.Destination + ". Your note is not sent when opening tools."
                : profile.Kind == "copilot" ? "GitHub Copilot receives this note and your instruction through the saved CLI connection."
                : Uri.TryCreate(profile.Destination, UriKind.Absolute, out var endpoint)
                    ? $"This note and your instruction will be sent to {endpoint.Host}."
                    : "The saved connection address is invalid. Correct it in Manage connections.";
        }
        connections.SelectionChanged += (_, _) =>
        {
            failure = null;
            if (!_refreshingConnections && connections.SelectedItem is WorkspaceConnection profile) select(profile.Id);
            Refresh();
        };
        question.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty) Refresh(); };
        tools.Click += async (_, _) =>
        {
            if (!_busy && connections.SelectedItem is WorkspaceConnection { CanWrite: false } selected && openTools is not null)
            { close(); await openTools(selected.Id); }
        };
        cancel.Click += (_, _) => { _lifetime.Cancel(); close(); };
        settings.Click += async (_, _) => { if (!_busy) { close(); await configure(); } };
        send.Click += async (_, _) =>
        {
            if (_busy || ValidationError() is not null || connections.SelectedItem is not WorkspaceConnection profile) { Refresh(); return; }
            _busy = true; question.IsReadOnly = true; connections.IsEnabled = settings.IsEnabled = false;
            cancel.Content = "Cancel request"; status.Text = "Validating connection and model before generation…"; Refresh();
            try
            {
                status.Text = await generate(profile.Id, question.Text ?? "", _lifetime.Token);
                if (!_lifetime.IsCancellationRequested) close();
            }
            catch (OperationCanceledException) { status.Text = "Request canceled. Your note has not changed."; }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException
                or System.Text.Json.JsonException or System.ComponentModel.Win32Exception or PlatformNotSupportedException)
            { status.Text = ex.Message; failure = "Generation stopped. Correct the problem in Manage connections, then reopen Write with AI. " + ex.Message; }
            finally
            {
                _busy = false; question.IsReadOnly = false; connections.IsEnabled = settings.IsEnabled = true;
                cancel.Content = "Close"; Refresh();
            }
        };
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Escape) { _lifetime.Cancel(); close(); e.Handled = true; }
            else if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.Control)
            { send.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); e.Handled = true; }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        DetachedFromVisualTree += (_, _) => Dispose();
        var heading = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 16, Children =
        {
            GuideParts.Vertical(GuideParts.Caption("WRITING WORKSPACE"), GuideParts.Title("Write with AI")),
            new TextBlock { Text = shortcut, Classes = { "muted" }, VerticalAlignment = VerticalAlignment.Center },
        } };
        Grid.SetColumn(heading.Children[1], 1);
        var header = GuideParts.Vertical(heading, GuideParts.Body("Working on “" + documentName + "”"));
        requestDetails.PropertyChanged += (_, e) =>
        { if (e.Property == Expander.IsExpandedProperty && requestDetails.IsExpanded) requestText.Text = requestPreview?.Invoke(question.Text ?? ""); };
        question.TextChanged += (_, _) => { if (requestDetails.IsExpanded) requestText.Text = requestPreview?.Invoke(question.Text ?? ""); };
        var contentScroll = new ScrollViewer
        {
            Content = GuideParts.Vertical(
                GuideParts.Card(GuideParts.Vertical(GuideParts.Caption("CONNECTION"), connections, destination)),
                GuideParts.Vertical(GuideParts.Body("What would you like to write or change?"), question),
                validation, requestDetails, status),
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        };
        var actions = GuideParts.Actions(settings, cancel, tools, send);
        var footer = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 16, 0, 0),
            Child = GuideParts.Vertical(GuideParts.Caption("Review the result before saving. Undo restores the previous draft."), actions) };
        footer.Bind(BorderBrushProperty, this.GetResourceObservable("Tern.Border"));
        Child = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), RowSpacing = 18, Children = { header, contentScroll, footer } };
        Grid.SetRow(contentScroll, 1); Grid.SetRow(footer, 2);
        Refresh();
        _refreshStatus = Refresh;
        AttachedToVisualTree += (_, _) => FocusQuestion();
    }

    internal void RefreshConnections(AIWorkspaceService workspace)
    {
        if (_busy || _disposed) return;
        _refreshingConnections = true;
        try { _connections.ItemsSource = workspace.Connections; _connections.SelectedItem = workspace.SelectedConnection; }
        finally { _refreshingConnections = false; }
        _refreshStatus();
    }

    internal void FocusQuestion() => DispatcherTimer.RunOnce(() => { if (!_disposed) _question.Focus(); }, TimeSpan.FromMilliseconds(80));

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        _lifetime.Dispose();
        GC.SuppressFinalize(this);
    }
}
