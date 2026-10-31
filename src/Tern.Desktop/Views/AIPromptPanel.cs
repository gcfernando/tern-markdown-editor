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
        Func<string, string, string?>? validate = null)
    {
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Top;
        Margin = new Thickness(24, 45, 24, 24);
        MaxWidth = 780;
        Width = 760;
        MinWidth = 420;
        Padding = new Thickness(24);
        CornerRadius = new CornerRadius(18);
        BorderThickness = new Thickness(2);
        BoxShadow = new BoxShadows(new BoxShadow { Blur = 38, OffsetY = 12, Color = Color.Parse("#50000000") });
        this.Bind(BackgroundProperty, this.GetResourceObservable("Tern.Surface"));
        this.Bind(BorderBrushProperty, this.GetResourceObservable("Tern.Accent"));
        var question = _question = new TextBox
        {
            AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 240, MaxHeight = 400,
            FontSize = 17, Padding = new Thickness(14), MaxLength = 8000,
            PlaceholderText = "What would you like to write or change?\nFor example: Turn these notes into a clear project plan…",
        };
        AutomationProperties.SetName(question, "AI writing question");
        var connections = _connections = new ComboBox
        {
            ItemsSource = workspace.Profiles, SelectedItem = workspace.Selected, MinWidth = 200, MaxWidth = 420,
            PlaceholderText = "Choose an AI connection", HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<AIConnectionProfile>((p, _) =>
                new TextBlock { Text = p is null ? "" : p.Name + " · " + p.Model, TextTrimming = TextTrimming.CharacterEllipsis }),
        };
        AutomationProperties.SetName(connections, "Writing AI connection");
        var destination = GuideParts.Caption("");
        var status = GuideParts.Body("");
        AutomationProperties.SetName(status, "AI writing status");
        var validation = GuideParts.Body("");
        AutomationProperties.SetName(validation, "AI writing validation");
        var send = new Button { Content = "Generate & update note", MinHeight = 42 };
        send.Classes.Add("accent");
        AutomationProperties.SetName(send, "Generate and update Markdown");
        var cancel = new Button { Content = "Close" };
        var settings = new Button { Content = "Manage connections", Classes = { "flat" } };
        string? failure = null;
        string? ValidationError()
        {
            if (_disposed) return "This writing panel is closed.";
            if (connections.SelectedItem is not AIConnectionProfile profile) return "Choose an AI connection before generating.";
            if (string.IsNullOrWhiteSpace(question.Text)) return "Enter a question or instruction before generating.";
            return failure ?? (validate is not null ? validate(profile.Id, question.Text)
                : workspace.WritingValidationError(profile.Id, requestPreview?.Invoke(question.Text) ?? question.Text));
        }
        void Refresh()
        {
            var profile = connections.SelectedItem as AIConnectionProfile;
            var error = ValidationError();
            send.IsEnabled = !_busy && error is null;
            send.Opacity = send.IsEnabled ? 1 : .45;
            validation.Text = error ?? "Connection and model will be checked before generation. No note text is sent during this check.";
            destination.Text = profile is null
                ? "Set up a connection once in Settings to start writing with AI."
                : Uri.TryCreate(profile.Endpoint, UriKind.Absolute, out var endpoint)
                    ? $"Sends this note and your question to {endpoint.Host}. Changes are undoable; save when ready."
                    : "The saved connection address is invalid. Correct it in Settings.";
        }
        connections.SelectionChanged += (_, _) =>
        {
            failure = null;
            if (!_refreshingConnections && connections.SelectedItem is AIConnectionProfile profile) select(profile.Id);
            Refresh();
        };
        question.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty) Refresh(); };
        cancel.Click += (_, _) => { _lifetime.Cancel(); close(); };
        settings.Click += async (_, _) => { if (!_busy) { close(); await configure(); } };
        send.Click += async (_, _) =>
        {
            if (_busy || ValidationError() is not null || connections.SelectedItem is not AIConnectionProfile profile) { Refresh(); return; }
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
        var heading = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Children =
        {
            GuideParts.Vertical(GuideParts.Caption("WRITE WITH AI"), GuideParts.Title("A little direction. A better draft.")),
            new TextBlock { Text = shortcut, Classes = { "muted" }, VerticalAlignment = VerticalAlignment.Top },
        } };
        Grid.SetColumn(heading.Children[1], 1);
        var header = GuideParts.Vertical(heading, GuideParts.Caption("Updating “" + documentName + "”"));
        var requestText = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 200 };
        var requestDetails = new Expander { Header = "Review what will be sent", IsVisible = requestPreview is not null,
            HorizontalAlignment = HorizontalAlignment.Stretch, Content = requestText, Classes = { "ai-advanced" } };
        requestDetails.PropertyChanged += (_, e) =>
        { if (e.Property == Expander.IsExpandedProperty && requestDetails.IsExpanded) requestText.Text = requestPreview?.Invoke(question.Text ?? ""); };
        question.TextChanged += (_, _) => { if (requestDetails.IsExpanded) requestText.Text = requestPreview?.Invoke(question.Text ?? ""); };
        var contentScroll = new ScrollViewer
        {
            Content = GuideParts.Vertical(question, connections, destination, validation, requestDetails, status),
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        };
        var actions = GuideParts.Actions(settings, cancel, send);
        Child = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), RowSpacing = 14, Children = { header, contentScroll, actions } };
        Grid.SetRow(contentScroll, 1); Grid.SetRow(actions, 2);
        Refresh();
        _refreshStatus = Refresh;
        AttachedToVisualTree += (_, _) => FocusQuestion();
    }

    internal void RefreshConnections(AIWorkspaceService workspace)
    {
        if (_busy || _disposed) return;
        _refreshingConnections = true;
        try { _connections.ItemsSource = workspace.Profiles; _connections.SelectedItem = workspace.Selected; }
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
