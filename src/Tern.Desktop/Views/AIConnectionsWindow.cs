using System.Diagnostics;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Tern.Desktop.Services;

namespace Tern.Desktop.Views;

/// <summary>Saved API connections, explicit non-generation checks, and exact-message review.</summary>
public sealed class AIConnectionsWindow : Window, IDisposable
{
    private readonly AIProfileStore _store;
    private readonly IAICredentialStore _vault;
    private readonly AIConnectionManager _manager;
    private readonly AIConnectionClient _client;
    private readonly Func<string, Task<bool>>? _confirmDiscard;
    private readonly Func<string, Task<bool>>? _confirmDelete;
    private readonly List<AIConnectionProfile> _profiles = [];
    private readonly ComboBox _saved = new() { PlaceholderText = "Choose a saved connection", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _service = new() { ItemsSource = AIServices.All.Select(s => s.Name).ToArray(), HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _protocol = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _models = new() { PlaceholderText = "Choose a discovered model, or enter its ID above", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _name = Field("Connection name", 100);
    private readonly TextBox _model = Field("Model name", 256);
    private readonly TextBox _key = Field("API key (saved only when you choose Save connection)", 4096);
    private readonly TextBox _endpoint = Field("Full AI endpoint", 2048);
    private readonly TextBox _discovery = Field("Model discovery URL", 2048);
    private readonly TextBox _workspace = Field("Anthropic workspace ID", 256);
    private readonly TextBox _message = Field("Exact message to send", AIConnectionClient.MaximumMessageCharacters);
    private readonly TextBox _response = Field("AI response", 1_048_576);
    private readonly CheckBox _custom = new() { Content = "Use custom endpoint" };
    private readonly CheckBox _reuseCredential = new();
    private readonly CheckBox _consent = new() { Content = "Send exactly the message above to the endpoint above" };
    private readonly TextBlock _step = GuideParts.Caption("");
    private readonly TextBlock _status = GuideParts.Body("No request has been made.");
    private readonly TextBlock _prerequisites = GuideParts.Body("");
    private readonly TextBlock _credentialStatus = GuideParts.Caption("");
    private readonly TextBlock _empty = GuideParts.Caption("No saved connections yet.");
    private readonly TextBlock _summary = GuideParts.Body("");
    private readonly TextBlock _checks = GuideParts.Caption("");
    private readonly TextBlock _validationDestination = GuideParts.Caption("");
    private readonly SelectableTextBlock _technical = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private readonly Expander _technicalExpander = new() { Header = "Technical details", IsVisible = false, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Expander _advanced = new() { Header = "Advanced: service address and protocol", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Dictionary<string, TextBlock> _errors = new(StringComparer.Ordinal);
    private readonly Button _next = new() { Content = "Configure connection" };
    private readonly Button _back = new() { Content = "Back" };
    private readonly Button _send = new() { Content = "Send request" };
    private readonly Button _cancel = new() { Content = "Cancel request", IsVisible = false };
    private readonly Button _save = new() { Content = "Save connection" };
    private readonly Button _delete = new() { Content = "Delete connection", IsEnabled = false };
    private readonly Button _validate = new() { Content = "Validate connection" };
    private readonly Button _removeKey = new() { Content = "Remove stored credential", IsVisible = false };
    private readonly Button _replaceKey = new() { Content = "Replace stored credential", IsVisible = false };
    private readonly Control[] _pages = new Control[3];
    private Control? _workspaceSection;
    private readonly ScrollViewer _scroll = new() { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
    private readonly Avalonia.Threading.DispatcherTimer _retryTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private AIConnectionInputs? _reviewedSettings;
    private string? _reviewedMessage;
    private AIConnectionCheck? _check;
    private CancellationTokenSource? _request;
    private string? _profileId;
    private string _storedKey = "";
    private string _storedKeyEndpoint = "";
    private string _enteredKeyEndpoint = "";
    private string _storedKeyProtocol = "";
    private string _enteredKeyProtocol = "";
    private string _customCache = "";
    private string? _blocked;
    private DateTimeOffset? _retryAt;
    private int _page;
    private int _generation;
    private int _serviceIndex;
    private int _savedIndex = -1;
    private bool _loading;
    private bool _dirty;
    private bool _busy;
    private bool _sending;
    private bool _switching;
    private bool _showErrors;
    private bool _removeStoredKey;
    private bool _persistenceAvailable = true;
    private bool _closingDecision;
    private bool _forceClose;
    private bool _disposed;
    private readonly string _writingShortcut;

    public AIConnectionsWindow(string? settingsDirectory = null, IAICredentialStore? credentialStore = null,
        Func<Window, Task>? openCopilot = null, AIConnectionClient? connectionClient = null, string? initialQuestion = null,
        Func<string, Task<bool>>? confirmDiscard = null, Func<string, Task<bool>>? confirmDelete = null, string? writingShortcut = null)
    {
        DialogParts.Setup(this, "AI Connections", 940, 790);
        MinWidth = 640; MinHeight = 500;
        _confirmDiscard = confirmDiscard; _confirmDelete = confirmDelete;
        _writingShortcut = writingShortcut ?? "Alt+Space";
        _store = new AIProfileStore(settingsDirectory ?? Tern.App.Shell.AppPaths.Default().SettingsDirectory);
        _vault = credentialStore ?? new WindowsAICredentialStore();
        _manager = new AIConnectionManager(_store, _vault);
        _client = connectionClient ?? new AIConnectionClient();
        _key.PasswordChar = '●'; _key.PlaceholderText = "Enter a key, or leave blank to keep the stored credential";
        _model.PlaceholderText = "Exact model ID from your provider or local server";
        _message.AcceptsReturn = _response.AcceptsReturn = true;
        _message.TextWrapping = _response.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
        _message.MinHeight = 120; _response.MinHeight = 140; _response.IsReadOnly = true;
        _message.Text = initialQuestion ?? "";
        _status.MaxHeight = GuideParts.Scaled(44); _status.ClipToBounds = true;
        _technicalExpander.Content = _technical;
        _technicalExpander.Classes.Add("ai-advanced");
        AccessibleName(_saved, "Saved AI connections"); AccessibleName(_service, "AI service preset"); AccessibleName(_protocol, "AI provider protocol");
        AccessibleName(_models, "Discovered AI models"); AccessibleName(_step, "AI setup step"); AccessibleName(_status, "AI connection status");
        AccessibleName(_summary, "Reviewed AI destination"); AccessibleName(_checks, "AI validation status"); AccessibleName(_prerequisites, "AI service prerequisites");
        AccessibleName(_next, "AI setup next"); AccessibleName(_back, "AI setup back"); AccessibleName(_save, "Save AI connection"); AccessibleName(_delete, "Delete AI connection");
        AccessibleName(_validate, "Validate AI connection"); AccessibleName(_custom, "Use custom AI endpoint"); AccessibleName(_reuseCredential, "Allow credential at changed destination");
        AccessibleName(_consent, "Send exactly the message above to the endpoint above");
        var showKey = new Button { Content = "Show", Classes = { "flat" } }; AccessibleName(showKey, "Show API key");
        showKey.Click += (_, _) =>
        {
            var reveal = _key.PasswordChar != '\0'; _key.PasswordChar = reveal ? '\0' : '●';
            showKey.Content = reveal ? "Hide" : "Show"; AccessibleName(showKey, reveal ? "Hide API key" : "Show API key");
        };
        var keyRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8, Children = { _key, showKey } };
        Grid.SetColumn(showKey, 1);
        var resetEndpoint = new Button { Content = "Reset to provider default", Classes = { "flat" } }; AccessibleName(resetEndpoint, "Reset AI endpoint to default");
        resetEndpoint.Click += (_, _) =>
        {
            if (Service.Id == "compatible") { Status("Other compatible APIs have no universal default. Enter the service's documented endpoint."); return; }
            _customCache = ""; _custom.IsChecked = false; _endpoint.Text = DefaultEndpoint;
        };
        var docs = new Button { Content = "Official setup documentation ↗", Classes = { "flat" } };
        docs.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo(Service.Documentation) { UseShellExecute = true }); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            { Status("Open the provider's official documentation in your browser: " + Service.Documentation); }
        };
        var mcp = new Button { Content = "MCP servers…" }; AccessibleName(mcp, "Open MCP server connections");
        mcp.Click += async (_, _) => await new MCPConnectionsWindow().ShowDialog(this);
        var copilot = new Button { Content = "GitHub Copilot…", IsEnabled = openCopilot is not null }; AccessibleName(copilot, "Open GitHub Copilot connection");
        copilot.Click += async (_, _) => { if (openCopilot is not null) await openCopilot(this); };
        var integrations = new Expander { Header = "Separate integrations", HorizontalAlignment = HorizontalAlignment.Stretch,
            Content = GuideParts.Vertical(GuideParts.Actions(mcp, copilot), GuideParts.Caption("Copilot uses its separate installed CLI connection. MCP manages tool servers; it does not choose a model or grant tools to these text requests.")) };
        integrations.Classes.Add("ai-advanced");
        _pages[0] = GuideParts.Vertical(
            GuideParts.Card(GuideParts.Vertical(GuideParts.Caption("SAVED CONNECTIONS"), _empty, _saved)),
            GuideParts.Card(GuideParts.Vertical(GuideParts.Title("Choose your AI service"), Label("AI service", _service), _prerequisites, docs)), integrations);
        _workspaceSection = Label("Anthropic workspace ID (optional)", _workspace, Error("workspace"));
        _advanced.Content = GuideParts.Vertical(_custom, Label("API endpoint (full URL)", _endpoint, Error("endpoint")), resetEndpoint,
            Label("Connection protocol", _protocol, Error("protocol")),
            GuideParts.Caption("A custom URL must accept this protocol and authentication. Changing the address does not convert APIs. Redirects are never followed."),
            Label("Model discovery URL (optional, full URL)", _discovery, Error("discovery")),
            GuideParts.Caption("Leave blank for documented provider defaults. Custom endpoints need their own documented model-list URL on the same origin; Tern does not guess paths."),
            _workspaceSection, _reuseCredential);
        _advanced.Classes.Add("ai-advanced");
        var newProfile = new Button { Content = "New connection" };
        newProfile.Click += async (_, _) => { if (await ResolveUnsavedAsync()) { ApplyService(_serviceIndex); SetPage(0); } };
        var saveCard = GuideParts.Card(GuideParts.Vertical(GuideParts.Body("Save for next time"),
            Label("Connection name", _name, Error("name")), GuideParts.Actions(newProfile, _delete, _save),
            GuideParts.Caption("Saving is optional and does not test or contact the service. Your API key is stored securely on this device when you choose Save.")));
        _pages[1] = GuideParts.Vertical(
            GuideParts.Card(GuideParts.Vertical(Label("Model ID", _model, Error("model")), _models,
                Label("API key", keyRow, Error("credential")), _credentialStatus, GuideParts.Actions(_replaceKey, _removeKey), _advanced)),
            GuideParts.Card(GuideParts.Vertical(_validationDestination, GuideParts.Actions(_validate),
                GuideParts.Caption("Checks local settings and, where supported, requests the model list. Your message is not sent; no generation request is made. Provider policies may apply."), _checks)), saveCard);
        var reviewChecks = GuideParts.Caption(""); AccessibleName(reviewChecks, "Reviewed validation status");
        _pages[2] = GuideParts.Vertical(
            GuideParts.Card(GuideParts.Vertical(GuideParts.Title("Review your request"), _summary, reviewChecks)),
            GuideParts.Card(GuideParts.Vertical(Label("Exact message to send", _message),
                GuideParts.Caption("One user message only. No system instructions, history, attachments or tool permissions are added. Your open documents are not read by this setup test."), _consent)),
            GuideParts.Card(GuideParts.Vertical(Label("Response", _response))));
        var allPages = GuideParts.Vertical(_pages); allPages.Children.Add(_technicalExpander); _scroll.Content = allPages;
        var close = new Button { Content = "Close", IsCancel = true }; close.Click += (_, _) => Close();
        _next.Classes.Add("accent"); _send.Classes.Add("accent"); _save.Classes.Add("accent");
        Content = GuideParts.Frame(GuideParts.Vertical(GuideParts.Title("AI connections"), _step), _scroll,
            GuideParts.Vertical(_status, GuideParts.Actions(_back, _next, _send, _cancel, close)));

        void RefreshReview() { _summary.Text = Summary(); reviewChecks.Text = _checks.Text; }
        foreach (var field in new[] { _model, _endpoint, _discovery, _workspace }) field.PropertyChanged += (_, e) =>
        {
            if (_loading || e.Property != TextBox.TextProperty) return;
            if (ReferenceEquals(field, _endpoint) && _custom.IsChecked == true) _customCache = _endpoint.Text ?? "";
            _reuseCredential.IsChecked = false; SettingsChanged(keepModels: ReferenceEquals(field, _model)); RefreshReview();
        };
        _key.PropertyChanged += (_, e) =>
        {
            if (_loading || e.Property != TextBox.TextProperty) return;
            _enteredKeyEndpoint = _endpoint.Text ?? ""; _enteredKeyProtocol = Protocol; _reuseCredential.IsChecked = false; SettingsChanged(); RefreshReview();
        };
        _name.PropertyChanged += (_, e) => { if (!_loading && e.Property == TextBox.TextProperty) { _dirty = true; UpdateControls(); } };
        _protocol.SelectionChanged += (_, _) =>
        {
            if (_loading) return;
            _reuseCredential.IsChecked = false;
            if (Service.Id == "ollama" && _custom.IsChecked != true) _endpoint.Text = DefaultEndpoint;
            SettingsChanged(); RefreshReview();
        };
        _custom.IsCheckedChanged += (_, _) =>
        {
            if (_loading) return;
            if (Service.Id == "compatible" && _custom.IsChecked != true) { _custom.IsChecked = true; return; }
            _endpoint.IsReadOnly = _custom.IsChecked != true;
            _endpoint.Text = _custom.IsChecked == true ? (_customCache.Length > 0 ? _customCache : DefaultEndpoint) : DefaultEndpoint;
            SettingsChanged(); RefreshReview();
        };
        _reuseCredential.IsCheckedChanged += (_, _) => { if (!_loading) { SettingsChanged(); RefreshReview(); } };
        _message.PropertyChanged += (_, e) => { if (!_loading && e.Property == TextBox.TextProperty) { InvalidateReview(); UpdateControls(); } };
        _consent.IsCheckedChanged += (_, _) =>
        {
            if (_consent.IsChecked == true && !_busy && LocalErrors().Count == 0 && _blocked is null)
            { _reviewedSettings = Inputs(); _reviewedMessage = _message.Text ?? ""; }
            else { _reviewedSettings = null; _reviewedMessage = null; }
            UpdateControls();
        };
        _models.SelectionChanged += (_, _) => { if (!_loading && _models.SelectedItem is string modelId) _model.Text = modelId; };
        _service.SelectionChanged += async (_, _) =>
        {
            if (_loading || _switching || _service.SelectedIndex < 0) return;
            var selected = _service.SelectedIndex; _switching = true;
            try
            {
                if (await ResolveUnsavedAsync()) ApplyService(selected);
                else { _loading = true; _service.SelectedIndex = _serviceIndex; _loading = false; }
            }
            finally { _switching = false; }
            RefreshReview();
        };
        _saved.SelectionChanged += async (_, _) =>
        {
            if (_loading || _switching || _saved.SelectedIndex < 0) return;
            var selected = _saved.SelectedIndex; _switching = true;
            try
            {
                if (await ResolveUnsavedAsync()) LoadProfile(selected);
                else { _loading = true; _saved.SelectedIndex = _savedIndex; _loading = false; }
            }
            finally { _switching = false; }
            RefreshReview();
        };
        _removeKey.Click += (_, _) =>
        {
            _storedKey = ""; _removeStoredKey = true; _key.Text = ""; SettingsChanged();
            _credentialStatus.Text = "Stored credential will be removed when you save. Enter a replacement if this service requires one.";
        };
        _replaceKey.Click += (_, _) => { _key.Focus(); _credentialStatus.Text = "Enter a replacement key. Leaving the field blank keeps the stored credential."; };
        _save.Click += (_, _) => { SaveProfile(); RefreshReview(); };
        _delete.Click += async (_, _) => { await DeleteProfileAsync(); RefreshReview(); };
        _validate.Click += async (_, _) => { await ValidateAsync(); RefreshReview(); };
        _send.Click += async (_, _) => { await SendAsync(); RefreshReview(); };
        _cancel.Click += (_, _) => _request?.Cancel();
        _back.Click += (_, _) => { if (!_busy) SetPage(_page - 1); };
        _next.Click += (_, _) =>
        {
            if (_busy) return;
            if (_page == 1 && !ShowLocalErrors()) return;
            InvalidateReview(); RefreshReview(); SetPage(_page + 1);
        };
        _retryTimer.Tick += (_, _) =>
        {
            if (_retryAt is null || DateTimeOffset.UtcNow >= _retryAt) { _retryTimer.Stop(); _retryAt = null; UpdateControls(); }
        };
        Closing += async (_, e) =>
        {
            if (_forceClose || !_dirty) { _request?.Cancel(); return; }
            e.Cancel = true;
            if (_closingDecision) return;
            _closingDecision = true;
            try { if (await ResolveUnsavedAsync()) { _forceClose = true; Close(); } }
            finally { _closingDecision = false; }
        };
        Closed += (_, _) => Dispose();
        try { _profiles.AddRange(_store.Load()); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException or ArgumentException)
        { _persistenceAvailable = false; Status("Saved connections could not be loaded and have been preserved. You can use an unsaved connection; saving is disabled."); }
        RefreshProfiles(); ApplyService(0); SetPage(0); RefreshReview();
    }

    private AIServiceDefinition Service => AIServices.All[_serviceIndex];
    private string Protocol => AIProviders.All.FirstOrDefault(p => p.Name == _protocol.SelectedItem as string)?.Id ?? Service.Protocol;
    private string DefaultEndpoint => AIServices.DefaultEndpoint(Service.Id, Protocol);
    private string CandidateKey => !string.IsNullOrEmpty(_key.Text) ? _key.Text : _storedKey;
    private bool OriginChanged => CandidateKey.Length > 0 && (!AIConnectionRules.SameOrigin(
        !string.IsNullOrEmpty(_key.Text) ? _enteredKeyEndpoint : _storedKeyEndpoint, _endpoint.Text ?? "")
        || Protocol != (!string.IsNullOrEmpty(_key.Text) ? _enteredKeyProtocol : _storedKeyProtocol));
    private string EffectiveKey => OriginChanged && _reuseCredential.IsChecked != true ? "" : CandidateKey;
    private const string NotTested = "Local settings: not checked\nConnectivity: Not tested\nAuthentication: Not tested\nModel: Model not verified\nGeneration: Not tested";

    private AIConnectionInputs Inputs() => new(Service.Id, Protocol, _endpoint.Text ?? "", _model.Text ?? "", EffectiveKey, _discovery.Text ?? "", _workspace.Text ?? "");

    private IReadOnlyDictionary<string, string> LocalErrors()
    {
        var errors = new Dictionary<string, string>(AIConnectionRules.Errors(Inputs()), StringComparer.Ordinal);
        if (OriginChanged && _reuseCredential.IsChecked != true)
            errors["credential"] = "The destination origin or authentication protocol changed. Explicitly allow this credential for the new destination and protocol, enter a replacement there, or remove the stored credential.";
        return errors;
    }

    private bool ShowLocalErrors(bool saving = false)
    {
        _showErrors = true;
        var errors = new Dictionary<string, string>(LocalErrors(), StringComparer.Ordinal);
        if (saving && string.IsNullOrWhiteSpace(_name.Text)) errors["name"] = "Give this connection a name before saving.";
        foreach (var (name, label) in _errors) { label.Text = errors.GetValueOrDefault(name) ?? ""; label.IsVisible = label.Text.Length > 0; }
        if (errors.Count > 0) { Status("Correct the highlighted settings before continuing."); return false; }
        return true;
    }

    private void SettingsChanged(bool keepModels = false)
    {
        if (_loading) return;
        _dirty = true; _generation++; _check = null; _blocked = null; _retryAt = null; _retryTimer.Stop();
        if (_busy && !_sending) _request?.Cancel();
        _checks.Text = NotTested; _technicalExpander.IsVisible = false;
        if (!keepModels) { _models.ItemsSource = Array.Empty<string>(); _models.IsVisible = false; }
        InvalidateReview(); UpdateControls();
        if (_showErrors) ShowLocalErrors();
    }

    private void InvalidateReview()
    {
        _reviewedSettings = null; _reviewedMessage = null; _consent.IsChecked = false;
    }

    private void SetPage(int page)
    {
        _page = Math.Clamp(page, 0, 2); _scroll.Offset = default;
        for (var i = 0; i < _pages.Length; i++) _pages[i].IsVisible = i == _page;
        _step.Text = _page switch { 0 => "Step 1 of 3 · Choose service", 1 => "Step 2 of 3 · Configure connection", _ => "Step 3 of 3 · Review and send" };
        _next.Content = _page == 0 ? "Configure connection" : "Review and send";
        _next.IsVisible = _page < 2; _send.IsVisible = _page == 2;
        UpdateControls();
    }

    private void UpdateControls()
    {
        var coolingDown = _retryAt is not null && DateTimeOffset.UtcNow < _retryAt;
        _next.IsEnabled = _back.IsEnabled = !_busy; _back.IsEnabled &= _page > 0;
        _save.IsEnabled = _persistenceAvailable && !_busy; _delete.IsEnabled = _persistenceAvailable && _profileId is not null && !_busy;
        _save.Content = _profileId is null ? "Save connection" : "Save changes";
        _validate.IsEnabled = !_busy && !coolingDown;
        _cancel.IsVisible = _cancel.IsEnabled = _busy;
        _send.IsEnabled = !_busy && !coolingDown && _blocked is null && !string.IsNullOrWhiteSpace(_message.Text)
            && _consent.IsChecked == true && _reviewedSettings is not null && LocalErrors().Count == 0;
        foreach (var input in new Control[] { _service, _saved, _protocol, _custom, _model, _endpoint, _key, _name, _discovery, _workspace, _reuseCredential, _removeKey, _replaceKey, _models })
            input.IsEnabled = !_sending;
        _protocol.IsEnabled &= Service.Id is "compatible" or "ollama";
        if (_workspaceSection is not null) _workspaceSection.IsVisible = Protocol == "anthropic-messages";
        _message.IsReadOnly = _sending;
        _reuseCredential.IsVisible = OriginChanged;
        _reuseCredential.Content = "Use this credential at " + Origin(_endpoint.Text ?? "") + " with " + AIProviders.Get(Protocol).Name + " (explicit permission)";
        _credentialStatus.Text = _storedKey.Length > 0 ? "Stored credential available. Leave the field blank to keep it, or enter a replacement."
            : _removeStoredKey ? "Stored credential will be removed when you save." : Service.RequiresCredential ? "An API key is required for this service." : "No API key is required by the default local setup; enter one only if your server requires it.";
        _removeKey.IsVisible = _storedKey.Length > 0;
        _replaceKey.IsVisible = _storedKey.Length > 0;
        _summary.Text = Summary();
        _validationDestination.Text = "Validation destination: " + (AIConnectionRules.ModelListEndpoint(Inputs()) ?? "local settings only; no documented model-list URL");
    }

    private string Summary() => $"Service: {Service.Name}\nProtocol: {AIProviders.Get(Protocol).Name}\nDestination: {_endpoint.Text}\nModel: {_model.Text}\nCredential: {(EffectiveKey.Length > 0 ? "provided; hidden" : "not provided")}"
        + (Protocol == "anthropic-messages" && !string.IsNullOrWhiteSpace(_workspace.Text) ? "\nWorkspace: " + _workspace.Text : "");

    private void SetProtocols(string serviceId, string selected)
    {
        _protocol.ItemsSource = AIProviders.All.Where(p => serviceId == "compatible" || serviceId == "ollama" && p.Id is "ollama-chat" or "chat-completions"
            || serviceId is "openai" or "anthropic" && p.Id == AIServices.Get(serviceId).Protocol).Select(p => p.Name).ToArray();
        _protocol.SelectedItem = AIProviders.Get(selected).Name;
    }

    private void ApplyService(int index)
    {
        _loading = true;
        _serviceIndex = index; _service.SelectedIndex = index; _savedIndex = -1; _saved.SelectedIndex = -1; _profileId = null;
        _storedKey = _storedKeyEndpoint = _enteredKeyEndpoint = _customCache = ""; _removeStoredKey = false;
        _storedKeyProtocol = _enteredKeyProtocol = "";
        _key.Text = _name.Text = _model.Text = _discovery.Text = _workspace.Text = "";
        SetProtocols(Service.Id, Service.Protocol); _endpoint.Text = DefaultEndpoint;
        _custom.IsChecked = Service.Id == "compatible"; _endpoint.IsReadOnly = _custom.IsChecked != true;
        _advanced.IsExpanded = Service.Id == "compatible"; _reuseCredential.IsChecked = false; _prerequisites.Text = Service.Prerequisites;
        _loading = false; ResetChecks(); _dirty = false; _showErrors = false; ClearErrors(); UpdateControls();
    }

    private void LoadProfile(int index)
    {
        if (index < 0 || index >= _profiles.Count) return;
        var profile = _profiles[index];
        try
        {
            var secret = _vault.Read(profile.Id) ?? "";
            _loading = true; _profileId = profile.Id; _savedIndex = index; _saved.SelectedIndex = index;
            _serviceIndex = AIServices.All.ToList().FindIndex(s => s.Id == AIServices.ServiceFor(profile));
            if (_serviceIndex < 0) _serviceIndex = AIServices.All.Count - 1;
            _service.SelectedIndex = _serviceIndex; SetProtocols(Service.Id, profile.Provider);
            _name.Text = profile.Name; _model.Text = profile.Model; _endpoint.Text = profile.Endpoint;
            _discovery.Text = profile.DiscoveryEndpoint ?? ""; _workspace.Text = profile.WorkspaceId ?? "";
            _key.Text = ""; _storedKey = secret; _storedKeyEndpoint = profile.Endpoint; _storedKeyProtocol = profile.Provider; _enteredKeyEndpoint = _enteredKeyProtocol = ""; _removeStoredKey = false;
            _customCache = profile.Endpoint; _custom.IsChecked = profile.UseCustomEndpoint || profile.Endpoint != DefaultEndpoint;
            _endpoint.IsReadOnly = _custom.IsChecked != true; _advanced.IsExpanded = _custom.IsChecked == true;
            _reuseCredential.IsChecked = false; _prerequisites.Text = Service.Prerequisites;
            _loading = false; ResetChecks(); _dirty = false; _showErrors = false; ClearErrors(); SetPage(1);
            Status("Connection loaded. Not tested in this session; no request was made.");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            _loading = true; _saved.SelectedIndex = _savedIndex; _loading = false;
            Status("The stored credential could not be read. Your saved connection has been kept. " + ex.Message);
        }
    }

    private void SaveProfile()
    {
        if (!_persistenceAvailable || _busy || !ShowLocalErrors(saving: true)) return;
        try
        {
            var creating = _profileId is null;
            var profile = new AIConnectionProfile(_profileId ?? Guid.NewGuid().ToString("N"), _name.Text!.Trim(), Protocol, _endpoint.Text!, _model.Text!,
                Service.Id, _custom.IsChecked == true, string.IsNullOrWhiteSpace(_discovery.Text) ? null : _discovery.Text,
                string.IsNullOrWhiteSpace(_workspace.Text) ? null : _workspace.Text);
            var key = !string.IsNullOrEmpty(_key.Text) ? _key.Text : _removeStoredKey || creating ? "" : null;
            var updated = _manager.Save(profile, key, _profiles, allowCredentialReuse: _reuseCredential.IsChecked == true || !string.IsNullOrEmpty(_key.Text));
            _profiles.Clear(); _profiles.AddRange(updated); _profileId = profile.Id;
            _storedKey = EffectiveKey; _storedKeyEndpoint = profile.Endpoint; _storedKeyProtocol = profile.Provider; _removeStoredKey = false;
            _loading = true; _key.Text = ""; RefreshProfiles(); _savedIndex = _profiles.FindIndex(p => p.Id == profile.Id); _saved.SelectedIndex = _savedIndex; _loading = false;
            _dirty = false; UpdateControls(); Status(creating ? "Connection saved. Not tested unless a check is shown above." : "Changes saved. Validation applies only to the settings checked in this session.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception or PlatformNotSupportedException)
        { _loading = false; Status("Connection could not be saved. Your edits are still here. " + ex.Message); }
    }

    private async Task DeleteProfileAsync()
    {
        if (_profileId is not { } id || _busy) return;
        var name = _profiles.First(p => p.Id == id).Name;
        var approved = _confirmDelete is not null ? await _confirmDelete(name)
            : await PromptWindow.AskAsync(this, "Delete AI connection?", "Delete “" + name + "” and its stored credential? Other connections and documents will be kept.", ["Delete connection", "Cancel"], 1, 1) == 0;
        if (!approved) return;
        try
        {
            var updated = _manager.Remove(id, _profiles); _profiles.Clear(); _profiles.AddRange(updated);
            _loading = true; RefreshProfiles(); _loading = false; ApplyService(_serviceIndex); SetPage(0); Status("Connection and its stored credential deleted.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception or PlatformNotSupportedException)
        { Status("Connection could not be deleted. " + ex.Message); }
    }

    private async Task<bool> ResolveUnsavedAsync()
    {
        if (!_dirty) return true;
        return _confirmDiscard is not null ? await _confirmDiscard("Discard unsaved connection changes?")
            : await PromptWindow.AskAsync(this, "Unsaved AI connection", "Keep editing, or discard the unsaved connection changes? Saved connections and credentials are kept.", ["Discard changes", "Keep editing"], 1, 1) == 0;
    }

    private async Task ValidateAsync()
    {
        if (_busy || !ShowLocalErrors()) return;
        var input = Inputs(); var generation = _generation;
        _request = new CancellationTokenSource(); _busy = true; UpdateControls(); Status("Checking settings and the documented model list…");
        try
        {
            var result = await _client.CheckAsync(input, _request.Token);
            if (_disposed || generation != _generation || input != Inputs()) return;
            _check = result; _checks.Text = result.Describe();
            _loading = true; _models.ItemsSource = result.Models; _models.IsVisible = result.Models.Count > 0; _models.SelectedIndex = -1; _loading = false;
            _blocked = result.ModelInvalid ? "Choose a model installed on this Ollama server before sending." : null;
            Status(_blocked ?? "Check complete. Model listing does not verify generation compatibility or credits.");
        }
        catch (OperationCanceledException) { if (generation == _generation && !_disposed) Status("Connection check canceled. No generation request was made."); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or System.Text.Json.JsonException)
        { if (generation == _generation && !_disposed) ReportFailure(ex); }
        finally { _request.Dispose(); _request = null; _busy = false; UpdateControls(); }
    }

    private async Task SendAsync()
    {
        if (_busy || !_send.IsEnabled || !ShowLocalErrors() || string.IsNullOrWhiteSpace(_message.Text)) return;
        if (_reviewedSettings is not { } input || input != Inputs() || _reviewedMessage != _message.Text)
        { InvalidateReview(); Status("Settings or message changed. Review the current request and check consent again."); UpdateControls(); return; }
        _request = new CancellationTokenSource(); _busy = _sending = true; UpdateControls(); Status("Waiting for the service…");
        try
        {
            var answer = await _client.SendAsync(new Uri(input.Endpoint), input.Model, input.Credential, _reviewedMessage!, AIProviders.Get(input.Protocol), _request.Token, input.WorkspaceId);
            if (string.IsNullOrWhiteSpace(answer)) throw new InvalidOperationException("The service returned an empty text answer. Generation compatibility is not verified.");
            _response.Text = answer;
            if (input == Inputs())
            {
                _check = new AIConnectionCheck(DateTimeOffset.Now, "Generation endpoint responded",
                    input.Credential.Length > 0 ? "Reviewed generation request succeeded with supplied credential" : "Reviewed request succeeded without a credential",
                    "Accepted for this reviewed request", "Text response received; future availability and credits are not guaranteed", _check?.Models ?? []);
                _checks.Text = _check.Describe();
            }
            Status($"Response received. This setup test does not edit a document. Use {_writingShortcut} in a note for writing updates.");
        }
        catch (OperationCanceledException) { Status("Canceled. The service may already have received the request or incurred usage."); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or System.Text.Json.JsonException)
        { ReportFailure(ex); }
        finally { _request.Dispose(); _request = null; _busy = _sending = false; InvalidateReview(); UpdateControls(); }
    }

    private void ReportFailure(Exception error)
    {
        _checks.Text = "Local settings: valid\nCheck failed. Model and generation are not verified.";
        Status(error.Message);
        if (error is AIServiceException service)
        {
            _technical.Text = service.Details; _technicalExpander.IsVisible = true;
            if (service.ModelInvalid || service.AuthenticationInvalid) _blocked = service.Message;
            _retryAt = service.RetryAt; if (_retryAt is not null) _retryTimer.Start();
        }
    }

    private void ResetChecks()
    {
        _generation++; _check = null; _blocked = null; _retryAt = null; _retryTimer.Stop();
        if (_busy && !_sending) _request?.Cancel();
        _checks.Text = NotTested; _models.ItemsSource = Array.Empty<string>(); _models.IsVisible = false; _technicalExpander.IsVisible = false; InvalidateReview();
    }
    private void RefreshProfiles()
    { _saved.ItemsSource = _profiles.Select(p => p.Name).ToArray(); _saved.IsVisible = _profiles.Count > 0; _empty.IsVisible = _profiles.Count == 0; }
    private void ClearErrors() { foreach (var label in _errors.Values) { label.Text = ""; label.IsVisible = false; } }
    private void Status(string text) { _status.Text = text; ToolTip.SetTip(_status, text); }
    private TextBlock Error(string field)
    {
        var label = GuideParts.Caption(""); label.IsVisible = false;
        label.Bind(TextBlock.ForegroundProperty, label.GetResourceObservable("Tern.Danger"));
        AccessibleName(label, "AI " + field + " error"); _errors[field] = label; return label;
    }
    private static StackPanel Label(string text, Control control, Control? error = null)
    {
        var label = GuideParts.Body(text);
        AutomationProperties.SetLabeledBy(control, label);
        return error is null ? GuideParts.Vertical(label, control) : GuideParts.Vertical(label, control, error);
    }
    private static TextBox Field(string name, int maximum) { var box = new TextBox { MaxLength = maximum, HorizontalAlignment = HorizontalAlignment.Stretch }; AccessibleName(box, name); return box; }
    private static void AccessibleName(Control control, string name) => AutomationProperties.SetName(control, name);
    private static string Origin(string text) => Uri.TryCreate(text, UriKind.Absolute, out var uri) ? uri.GetLeftPart(UriPartial.Authority) : "the new destination";

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _loading = true; _request?.Cancel(); _retryTimer.Stop(); _client.Dispose(); _storedKey = "";
        _reviewedSettings = null; _reviewedMessage = null; _key.Text = _message.Text = _response.Text = "";
        GC.SuppressFinalize(this);
    }
}
