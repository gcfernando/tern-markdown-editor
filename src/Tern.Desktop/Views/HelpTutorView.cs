using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tern.App.Commands;
using Tern.App.Help;
using Tern.Desktop.Services;

namespace Tern.Desktop.Views;

/// <summary>A window-owned, bounded tutor conversation. Only explicitly submitted text is shared.</summary>
internal sealed class HelpTutorView : UserControl, IDisposable
{
    internal ComboBox Connections { get; } = new() { PlaceholderText = "Offline guides (no AI connection)", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
    internal TextBox Question { get; } = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 160, MaxHeight = 240, MaxLength = 8000, PlaceholderText = "Ask about a Tern feature or describe the screen you need help with…" };
    internal Button SendButton { get; } = new() { Content = "Send question", Classes = { "accent" } };
    internal HelpAnswerView Transcript { get; } = new();
    private readonly GuidanceStrip _guidance = new("Choose a connection, or ask using the installed offline guides.", "help");
    private readonly TextBlock _destination = GuideParts.Caption("");
    private readonly TextBlock _offlineNotice = GuideParts.Body("");
    private readonly TextBlock _status = GuideParts.Body("Type a Tern question, such as ‘How do I export a PDF?’, to get started.");
    private readonly StackPanel _references = new() { Spacing = 8 };
    private readonly AIWorkspaceService _workspace;
    private readonly CommandRegistry _registry;
    private readonly List<(string Question, string Answer)> _turns = [];
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _request;
    private bool _refreshing;
    private bool _disposed;
    private int _generation;
    private string? _conversationProfile;

    public HelpTutorView(AIWorkspaceService workspace, CommandRegistry registry, Func<Task> configure,
        Func<string, Task>? open, Func<string, bool>? canOpen, Action<FeatureHelp> lesson, Action<string>? practice, Func<string?>? selectedText = null)
    {
        _workspace = workspace; _registry = registry;
        Connections.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<WorkspaceConnection>((p, _) => new TextBlock { Text = p?.Name, TextWrapping = TextWrapping.Wrap });
        AutomationProperties.SetName(Connections, "Help AI connection");
        AutomationProperties.SetName(Question, "Tutor question");
        AutomationProperties.SetName(Transcript, "Tutor conversation transcript");
        AutomationProperties.SetName(_destination, "Help AI destination and capability");
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        Question.TextChanged += (_, _) => UpdateDestination();
        var setup = new Button { Content = "Manage connections" };
        setup.Click += async (_, _) => { await configure(); workspace.Reload(); };
        var cancel = new Button { Content = "Cancel answer", IsEnabled = false };
        cancel.Click += (_, _) => _request?.Cancel();
        var clear = new Button { Content = "Start new conversation" };
        clear.Click += (_, _) => { _generation++; _request?.Cancel(); _turns.Clear(); Transcript.Text = ""; _references.Children.Clear(); Question.Text = ""; _status.Text = "New conversation. Enter a Tern question."; _guidance.Message = "Type a question about Tern, then choose Send question."; Question.Focus(); };
        var copy = new Button { Content = "Copy conversation" };
        clear.IsEnabled = copy.IsEnabled = false;
        Transcript.PropertyChanged += (_, e) => { if (e.Property == IsVisibleProperty) clear.IsEnabled = copy.IsEnabled = Transcript.IsVisible; };
        copy.Click += async (_, _) =>
        {
            try { if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(Transcript.Text ?? ""); }
            catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.ExternalException) { _status.Text = "Could not copy: " + ex.Message; }
        };
        Connections.SelectionChanged += (_, _) =>
        {
            if (_refreshing) return;
            var selected = Connections.SelectedItem as WorkspaceConnection;
            try { workspace.SelectHelp(selected?.Id); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _status.Text = "The Help selection could not be saved. " + ex.Message; }
            if (_conversationProfile != selected?.Id)
            {
                _generation++; _request?.Cancel(); _turns.Clear(); Transcript.Text = ""; Transcript.IsVisible = false; _references.Children.Clear();
                _conversationProfile = selected?.Id;

                _status.Text = "New conversation. Earlier provider history will not be shared.";
            }
            UpdateDestination();
        };
        SendButton.Click += async (_, _) =>
        {
            if (_request is not null || string.IsNullOrWhiteSpace(Question.Text)) return;
            var exactQuestion = Question.Text;
            if (!GuideQuestionPrompt.CanAnswer(registry, exactQuestion)) { _status.Text = GuideQuestionPrompt.ScopeMessage; return; }
            if (Connections.SelectedItem is not WorkspaceConnection { CanWrite: true } selected)
            { await ShowOfflineAnswer(exactQuestion); return; }
            var generation = ++_generation;
            using var request = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); _request = request;
            SendButton.IsEnabled = false; Connections.IsEnabled = false; cancel.IsEnabled = true;
            _status.Text = "Answering your Tern question…";
            _guidance.Message = "Preparing your answer. Cancel answer stops waiting and keeps your question.";
            try
            {
                var history = _turns.TakeLast(4).ToList();
                while (history.Sum(t => t.Question.Length + t.Answer.Length) > 6000) history.RemoveAt(0);
                string Build() => GuideQuestionPrompt.Build(registry, exactQuestion, selected.Kind == "copilot",
                    string.Join("\n\n", history.Select(t => "USER: " + t.Question + "\nASSISTANT: " + t.Answer)));
                string message;
                while (true)
                {
                    try { message = Build(); break; }
                    catch (InvalidOperationException) when (history.Count > 0) { history.RemoveAt(0); }
                }
                var answer = await workspace.SendWritingAsync(selected.Id, message,
                    () => !_disposed && generation == _generation && workspace.Connections.Contains(selected), request.Token);
                request.Token.ThrowIfCancellationRequested();
                if (_disposed || generation != _generation || !workspace.Connections.Contains(selected)) return;
                if (string.IsNullOrWhiteSpace(answer)) throw new InvalidOperationException("The model returned an empty answer. Your question is kept for retry.");
                var turns = _turns.Append((Question: exactQuestion, Answer: answer)).TakeLast(8).ToList();
                var display = string.Join("\n\n---\n\n", turns.AsEnumerable().Reverse().Select(t => "## You\n\n> " + t.Question.Replace("\n", "\n> ") + "\n\n## Tern answer\n\n" + t.Answer));
                if (!await Transcript.ShowAsync(display, request.Token, () => !_disposed && generation == _generation)) return;
                _turns.Clear(); _turns.AddRange(turns);

                _status.Text = "Answer received. Check it against the installed guides below. No note was changed.";
                _guidance.Message = "Read the formatted answer below. Ask a follow-up, copy the conversation, or open a related guide.";
                _references.Children.Clear();
                // References/actions come from the installed catalog, never model strings or links.
                foreach (var feature in GuideQuestionPrompt.Relevant(registry, exactQuestion).Take(3))
                {
                    var show = new Button { Content = "Show lesson · " + feature.Name };
                    show.Click += (_, _) => lesson(feature);
                    var actions = GuideParts.Actions(show);
                    if (FeatureCatalog.CanLaunchFromGuide(feature.LaunchCommandId) && open is not null)
                    {
                        var tool = new Button { Content = "Open this tool" };
                        tool.Click += async (_, _) => { if (canOpen?.Invoke(feature.LaunchCommandId!) != false) await open(feature.LaunchCommandId!); else _status.Text = "This tool is unavailable in the current document or workspace."; };
                        actions.Children.Add(tool);
                    }
                    var tryIt = new Button { Content = "Try in practice note", IsVisible = practice is not null };
                    tryIt.Click += (_, _) => practice?.Invoke("# Practice · " + feature.Name + "\n\n"); actions.Children.Add(tryIt);
                    _references.Children.Add(GuideParts.Card(GuideParts.Vertical(GuideParts.Caption(feature.Name + " · installed guide"), GuideParts.Body(feature.Description), actions)));
                }
            }
            catch (OperationCanceledException)
            {
                if (!_disposed && generation == _generation)
                {
                    if (request.IsCancellationRequested) _status.Text = "Answer canceled. Your question is kept for retry.";
                    else await ShowOfflineAnswer(exactQuestion, "The provider timed out.");
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException or PlatformNotSupportedException or System.ComponentModel.Win32Exception)
            { if (!_disposed && generation == _generation) await ShowOfflineAnswer(exactQuestion, ex.Message); }
            finally { if (ReferenceEquals(_request, request)) _request = null; if (!_disposed) { Connections.IsEnabled = true; cancel.IsEnabled = false; UpdateDestination(); } }
        };
        var privacyButton = new Button { Content = "What is shared?", Classes = { "flat" } };
        var privacyPopup = DialogParts.PanelFlyout(GuideParts.Caption("Only your question, installed Tern guide facts and bounded recent conversation are shared with the chosen connection. Notes, selections and paths are never attached."));
        privacyPopup.Content = new Border { MaxWidth = 400, Child = (Control)privacyPopup.Content! };
        privacyButton.Flyout = privacyPopup;
        cancel.IsVisible = false;
        cancel.PropertyChanged += (_, e) => { if (e.Property == Button.IsEnabledProperty) cancel.IsVisible = cancel.IsEnabled; };
        AutomationProperties.SetName(_status, "Tern help status");
        var connectionRow = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 8, Children = { Connections, setup } };
        Grid.SetColumn(setup, 1);
        var conversation = GuideParts.Actions(clear, copy, privacyButton); conversation.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
        AutomationProperties.SetName(conversation, "Conversation options");
        var browse = new Button { Content = "Browse installed guides" };
        browse.Click += (_, _) => { if (this.FindAncestorOfType<Window>() is { } window && window.Content is TabControl tabs) tabs.SelectedIndex = 0; };
        var notice = GuideParts.Card(GuideParts.Vertical(_offlineNotice, GuideParts.Caption("Offline help finds matching guide steps. It does not generate answers or interpret conversation history."), browse));
        var header = GuideParts.Vertical(GuideParts.Title("Ask about Tern"), notice, GuideParts.Caption("Help connection · optional"), connectionRow, _destination, conversation);
        var questionBody = GuideParts.Vertical(GuideParts.Caption("Your Tern question"), Question, GuideParts.Actions(cancel, SendButton));
        var questionPane = new ScrollViewer { Content = questionBody, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        void FitQuestion()
        {
            // Keep a generous composer before submission; give reading the larger
            // share once an answer arrives. The question remains available for follow-ups.
            notice.IsVisible = !Transcript.IsVisible;
            Question.MinHeight = Transcript.IsVisible ? 80 : 160;
            Question.MaxHeight = Transcript.IsVisible ? 112 : 240;
            _destination.IsVisible = !Transcript.IsVisible;
            questionPane.MaxHeight = Math.Max(100, Bounds.Height * (Transcript.IsVisible ? .28 : .38));
        }
        questionPane.MaxHeight = 280;
        SizeChanged += (_, _) => FitQuestion();
        Transcript.PropertyChanged += (_, e) => { if (e.Property == IsVisibleProperty) FitQuestion(); };
        var related = GuideParts.Details("Related installed guides", new ScrollViewer { MaxHeight = 140, Content = _references, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
        related.IsVisible = false;
        _references.Children.CollectionChanged += (_, _) => related.IsVisible = _references.Children.Count > 0;
        var empty = GuideParts.Body("Installed guidance or an AI answer will appear here after submission. Try a Tern question about search, export or printing.");
        AutomationProperties.SetName(empty, "Answer area guidance");
        var answers = new Grid { Children = { empty, Transcript } };
        Transcript.PropertyChanged += (_, e) => { if (e.Property == IsVisibleProperty) empty.IsVisible = !Transcript.IsVisible; };
        var answerPanel = new Grid { RowDefinitions = new("Auto,*,Auto"), RowSpacing = 8, Children = { _status, answers, related } };
        Grid.SetRow(answers, 1); Grid.SetRow(related, 2);
        var content = new Grid { RowDefinitions = new("Auto,*"), RowSpacing = 12, Children = { questionPane, GuideParts.Card(answerPanel) } };
        Grid.SetRow(content.Children[1], 1);
        Content = GuideParts.Frame(header, content, _guidance);
        workspace.Reloaded += OnReloaded; RefreshConnections();
    }

    private void OnReloaded(object? sender, EventArgs e) { if (!_disposed) Dispatcher.UIThread.Post(RefreshConnections); }
    private void RefreshConnections()
    {
        if (_disposed) return;
        var previous = Connections.SelectedItem as WorkspaceConnection;
        _refreshing = true;
        Connections.ItemsSource = _workspace.Connections.Where(c => c.CanWrite).ToArray();
        Connections.SelectedItem = _workspace.HelpSelectedConnection;
        _refreshing = false;
        if (_conversationProfile != _workspace.HelpSelectedId || previous is not null && !Equals(previous, Connections.SelectedItem))
        { _generation++; _request?.Cancel(); _turns.Clear(); Transcript.Text = ""; Transcript.IsVisible = false; _references.Children.Clear(); _conversationProfile = _workspace.HelpSelectedId; _status.Text = "The connection changed. Your question is kept; review the destination before sending."; }
        UpdateDestination();
    }
    private void UpdateDestination()
    {
        _destination.Text = Connections.SelectedItem is WorkspaceConnection profile ? profile.Name + " · " + profile.Kind + " · text generation" : "Offline guides selected. Choose an AI connection above for an AI answer.";
        var offline = Connections.SelectedItem is not WorkspaceConnection { CanWrite: true };
        _offlineNotice.Text = _workspace.Connections.Count == 0
            ? "No AI connection is configured. You can use the installed Tern guides for help. AI-generated answers require a connection."
            : offline ? "Offline guides are selected. AI-generated answers require a text-generation connection."
            : "An AI connection is selected. Your question is sent only when you choose Send question.";
        SendButton.Content = offline ? "Find guide steps" : "Send question";
        SendButton.IsEnabled = _request is null && !string.IsNullOrWhiteSpace(Question.Text);
    }
    private async Task ShowOfflineAnswer(string question, string? failure = null)
    {
        var generation = _generation;
        _references.Children.Clear();
        try
        {
            if (!await Transcript.ShowAsync("## Installed Tern guide · offline answer\n\n" + GuideQuestionPrompt.OfflineAnswer(_registry, question), _lifetime.Token, () => !_disposed && generation == _generation)) return;
        }
        catch (OperationCanceledException) when (_disposed || _lifetime.IsCancellationRequested) { return; }
        _status.Text = failure is null ? "Answered from the installed guides. No AI connection or network is needed."
            : "The AI connection failed. Tern recovered with an installed-guide answer below. Your question is kept. Connection details: " + failure;
        _guidance.Message = "Read the installed-guide answer below. You can copy it or choose a connection and send the question again.";
    }
    public void Dispose() { if (_disposed) return; _disposed = true; _workspace.Reloaded -= OnReloaded; _lifetime.Cancel(); _request?.Cancel(); _lifetime.Dispose(); }
}
