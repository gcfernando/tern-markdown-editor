using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Tern.Desktop.Services;

namespace Tern.Desktop.Views;

/// <summary>Saved integration metadata and explicit secure-vault writes; never contacts a provider.</summary>
internal sealed class IntegrationProfileEditor
{
    private readonly IntegrationProfileStore _store;
    private readonly string _kind;
    private readonly TextBox _destination, _model, _secret;
    private readonly TextBox _name = new() { MaxLength = 100, PlaceholderText = "Connection name" };
    private readonly ComboBox _saved = new() { PlaceholderText = "New connection", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _status;
    private string? _id;
    private string? _secretDestination;
    private bool _loading;
    public Control Content { get; }
    public bool HasSelection => _id is not null;
    public bool Select(string id)
    {
        var profile = _saved.Items.OfType<IntegrationProfile>().FirstOrDefault(p => p.Id == id);
        if (profile is null) return false;
        _saved.SelectedItem = profile; return _id == id;
    }
    public bool Save()
    {
        try
        {
            var profile = new IntegrationProfile(_id ?? Guid.NewGuid().ToString("N"), _kind, (_name.Text ?? "").Trim(), _destination.Text ?? "", _model.Text ?? "auto");
            _store.Save(profile, _secret.Text ?? ""); _id = profile.Id;
            Reload(); _saved.SelectedItem = _saved.Items.OfType<IntegrationProfile>().First(p => p.Id == _id);
            _status.Text = "Connection saved on this device. Tokens are protected in Windows Credential Manager.";
            return true;
        }
        catch (Exception ex) when (Expected(ex)) { _status.Text = "Could not save connection. " + ex.Message; return false; }
    }
    public IntegrationProfileEditor(string kind, TextBox destination, TextBox model, TextBox secret, TextBlock status,
        string? directory = null, IAICredentialStore? vault = null)
    {
        _kind = kind; _destination = destination; _model = model; _secret = secret; _status = status;
        _store = new IntegrationProfileStore(directory ?? Tern.App.Shell.AppPaths.Default().SettingsDirectory, vault ?? new WindowsAICredentialStore());
        AutomationProperties.SetName(_saved, "Saved " + kind + " connections");
        AutomationProperties.SetName(_name, "Integration connection name");
        _name.PlaceholderText = "For example, My " + (kind == "mcp" ? "tool server" : "GitHub Copilot");
        var create = new Button { Content = "New connection" };
        var remove = new Button { Content = "Delete saved connection", IsEnabled = false };
        AutomationProperties.SetName(create, "Create integration connection"); AutomationProperties.SetName(remove, "Delete integration connection");
        create.Click += (_, _) => { _id = null; remove.IsEnabled = false; _saved.SelectedIndex = -1; _name.Text = ""; _secret.Text = ""; _secretDestination = null; _status.Text = "New connection. Enter its settings, validate, then save."; };
        remove.Click += async (_, _) =>
        {
            if (_id is null) return;
            var owner = TopLevel.GetTopLevel(Content) as Window;
            if (owner is null || await PromptWindow.AskAsync(owner, "Delete saved connection?", "Delete this connection and its stored token from this device?", ["Delete connection", "Cancel"], 1, 1) != 0) return;
            try { _store.Remove(_id); create.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); Reload(); _status.Text = "Saved connection and its token removed."; }
            catch (Exception ex) when (Expected(ex)) { _status.Text = "Could not delete connection. " + ex.Message; }
        };
        _saved.SelectionChanged += (_, _) =>
        {
            if (_loading || _saved.SelectedItem is not IntegrationProfile profile) return;
            _loading = true;
            try
            {
                var token = _store.ReadSecret(profile);
                _id = profile.Id; remove.IsEnabled = true; _name.Text = profile.Name; _destination.Text = profile.Destination; _model.Text = profile.Model;
                _secret.Text = token; _secretDestination = profile.Destination; _status.Text = "Saved settings loaded. Validate before saving changes.";
            }
            catch (Exception ex) when (Expected(ex)) { _secret.Text = ""; _status.Text = "Could not load saved token. " + ex.Message; }
            finally { _loading = false; }
        };
        destination.TextChanged += (_, _) =>
        {
            if (!_loading && _secretDestination is not null && _destination.Text != _secretDestination)
            { _secret.Text = ""; _secretDestination = null; _status.Text = "Destination changed. Enter a token for this destination before validating."; }
        };
        Content = GuideParts.Vertical(GuideParts.Caption("Saved connections — select one to modify or validate"), _saved,
            GuideParts.Actions(create, remove),
            GuideParts.Caption("Connection name"), _name);
        Reload();
    }
    private void Reload()
    {
        _loading = true;
        try { _saved.ItemsSource = _store.Load().Where(p => p.Kind == _kind).ToArray(); }
        catch (Exception ex) when (Expected(ex)) { _status.Text = "Saved settings are unavailable. " + ex.Message; }
        finally { _loading = false; }
    }
    private static bool Expected(Exception ex) => ex is System.Text.Json.JsonException or IOException or UnauthorizedAccessException
        or InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception or PlatformNotSupportedException;
}
