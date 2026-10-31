using System.Net;
using System.Net.Http;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tern.Desktop.Services;
using Tern.Desktop.Views;

namespace Tern.Desktop.Tests;

public sealed class AIConnectionWizardTests
{
    [AvaloniaFact]
    public void Saving_and_reloading_an_untested_profile_makes_no_request_and_stores_no_message()
    {
        using var f = new DesktopFixture(); var calls = 0; var vault = new Vault();
        using (var window = Create(f, (_, _) => { calls++; return Task.FromResult(Json("{}")); }, vault))
        {
            Next(window); Fill(window);
            Find<TextBox>(window, "Connection name").Text = "Untested service";
            Find<TextBox>(window, "Exact message to send").Text = "Private message that must not be saved";
            Click(Find<Button>(window, "Save AI connection"));
            Assert.Contains("Connection saved", Find<TextBlock>(window, "AI connection status").Text, StringComparison.Ordinal);
            Assert.Equal(0, calls); Assert.True(f.Window.AIBar.IsVisible);
            var metadata = File.ReadAllText(new AIProfileStore(f.Shell.Paths.SettingsDirectory).FilePath);
            Assert.DoesNotContain("Private message", metadata, StringComparison.Ordinal);
            Assert.DoesNotContain("test-only-key", metadata, StringComparison.Ordinal);
            window.Close();
        }
        using var loaded = Create(f, (_, _) => { calls++; return Task.FromResult(Json("{}")); }, vault);
        Find<ComboBox>(loaded, "Saved AI connections").SelectedIndex = 0; Pump(loaded);
        Assert.Equal(0, calls);
        Assert.Contains("Not tested", Find<TextBlock>(loaded, "AI validation status").Text, StringComparison.Ordinal);
        Assert.Empty(Find<TextBox>(loaded, "API key (saved only when you choose Save connection)").Text!);
        loaded.Close();
    }

    [AvaloniaFact]
    public void Required_fields_and_blank_messages_cannot_be_sent_and_manual_models_are_not_claimed_verified()
    {
        using var f = new DesktopFixture(); var calls = 0;
        using var window = Create(f, (_, _) => { calls++; return Task.FromResult(Json("{}")); });
        Next(window); Next(window);
        Assert.StartsWith("Step 2", Find<TextBlock>(window, "AI setup step").Text, StringComparison.Ordinal);
        Assert.True(Find<TextBlock>(window, "AI model error").IsVisible);
        Assert.True(Find<TextBlock>(window, "AI credential error").IsVisible);
        Fill(window); Next(window);
        Assert.Contains("Model not verified", Find<TextBlock>(window, "Reviewed validation status").Text, StringComparison.Ordinal);
        Find<TextBox>(window, "Exact message to send").Text = " \n "; Pump(window);
        Consent(window); Pump(window);
        Assert.False(Button(window, "Send request").IsEnabled);
        Click(Button(window, "Send request")); Assert.Equal(0, calls);
        window.Close();
    }

    [AvaloniaFact]
    public void Custom_endpoint_overrides_reset_and_provider_switches_resolve_unsaved_edits()
    {
        using var f = new DesktopFixture(); var discard = false; var decisions = 0;
        using var window = new AIConnectionsWindow(f.Shell.Paths.SettingsDirectory, new Vault(),
            confirmDiscard: _ => { decisions++; return Task.FromResult(discard); });
        window.Show(f.Window); Next(window); Advanced(window);
        var custom = Find<CheckBox>(window, "Use custom AI endpoint");
        custom.IsChecked = true;
        var endpoint = Find<TextBox>(window, "Full AI endpoint"); endpoint.Text = "https://custom.test/team/chat";
        custom.IsChecked = false;
        Assert.Equal(AIServices.Get("openai").Endpoint, endpoint.Text);
        custom.IsChecked = true;
        Assert.Equal("https://custom.test/team/chat", endpoint.Text);
        Find<ComboBox>(window, "AI service preset").SelectedIndex = 1;
        Assert.Equal(1, decisions);
        Assert.Equal("OpenAI API", Find<ComboBox>(window, "AI service preset").SelectedItem);
        Assert.Equal("https://custom.test/team/chat", endpoint.Text);
        Find<TextBox>(window, "API key (saved only when you choose Save connection)").Text = "test-openai-key";
        discard = true; Find<ComboBox>(window, "AI service preset").SelectedIndex = 1;
        Assert.Equal(AIServices.Get("anthropic").Endpoint, endpoint.Text);
        Assert.Empty(Find<TextBox>(window, "API key (saved only when you choose Save connection)").Text!);
        Assert.Contains("Anthropic", Assert.IsType<string>(Find<ComboBox>(window, "AI provider protocol").SelectedItem), StringComparison.Ordinal);
        Advanced(window); custom.IsChecked = true; endpoint.Text = "https://custom.test/messages";
        Click(Find<Button>(window, "Reset AI endpoint to default"));
        Assert.False(custom.IsChecked);
        Assert.Equal(AIServices.Get("anthropic").Endpoint, endpoint.Text);
        window.Close();
    }

    [AvaloniaFact]
    public void Saved_custom_urls_and_blank_stored_credentials_survive_rename_and_deletion_can_be_canceled()
    {
        using var f = new DesktopFixture(); var vault = new Vault(); var delete = false;
        var profile = Profile() with { Endpoint = "https://custom.test/team/chat", UseCustomEndpoint = true, ServiceId = "openai" };
        var store = new AIProfileStore(f.Shell.Paths.SettingsDirectory); store.Save([profile]); vault.Save(profile.Id, "test-stored-secret");
        using var window = new AIConnectionsWindow(f.Shell.Paths.SettingsDirectory, vault,
            confirmDiscard: _ => Task.FromResult(true), confirmDelete: _ => Task.FromResult(delete));
        window.Show(f.Window); Find<ComboBox>(window, "Saved AI connections").SelectedIndex = 0; Pump(window);
        Assert.Equal(profile.Endpoint, Find<TextBox>(window, "Full AI endpoint").Text);
        Assert.True(Find<CheckBox>(window, "Use custom AI endpoint").IsChecked);
        Assert.Empty(Find<TextBox>(window, "API key (saved only when you choose Save connection)").Text!);
        Find<TextBox>(window, "Connection name").Text = "Renamed";
        Click(Find<Button>(window, "Save AI connection"));
        Assert.Equal("test-stored-secret", vault.Read(profile.Id));
        var saved = Assert.Single(store.Load());
        Assert.Equal(profile.Id, saved.Id); Assert.Equal(profile.Endpoint, saved.Endpoint);
        Assert.Equal("openai", saved.ServiceId); Assert.Equal("chat-completions", saved.Provider);
        Assert.DoesNotContain("test-stored-secret", File.ReadAllText(store.FilePath), StringComparison.Ordinal);
        Click(Find<Button>(window, "Delete AI connection"));
        Assert.Single(store.Load()); Assert.Equal("test-stored-secret", vault.Read(profile.Id));
        delete = true; Click(Find<Button>(window, "Delete AI connection"));
        Assert.Empty(store.Load()); Assert.Null(vault.Read(profile.Id));
        Assert.False(f.Window.AIBar.IsVisible);
        window.Close();
    }

    [AvaloniaFact]
    public void A_stored_credential_cannot_be_forwarded_to_a_new_origin_without_explicit_permission()
    {
        using var f = new DesktopFixture(); var vault = new Vault(); var calls = 0;
        var profile = Profile(); var store = new AIProfileStore(f.Shell.Paths.SettingsDirectory);
        store.Save([profile]); vault.Save(profile.Id, "test-stored-secret");
        using var window = Create(f, (_, _) => { calls++; return Task.FromResult(Json("{}")); }, vault);
        Find<ComboBox>(window, "Saved AI connections").SelectedIndex = 0; Pump(window); Advanced(window);
        Find<CheckBox>(window, "Use custom AI endpoint").IsChecked = true;
        Find<TextBox>(window, "Full AI endpoint").Text = "https://changed.test/team/chat";
        Click(Find<Button>(window, "Validate AI connection"));
        Assert.Equal(0, calls);
        Assert.Contains("origin", Find<TextBlock>(window, "AI credential error").Text, StringComparison.Ordinal);
        Click(Find<Button>(window, "Save AI connection"));
        Assert.Equal(profile.Endpoint, Assert.Single(store.Load()).Endpoint);
        Find<CheckBox>(window, "Allow credential at changed destination").IsChecked = true;
        Click(Find<Button>(window, "Save AI connection"));
        Assert.Equal("https://changed.test/team/chat", Assert.Single(store.Load()).Endpoint);
        Assert.Equal("test-stored-secret", vault.Read(profile.Id));
        window.Close();
    }

    [AvaloniaFact]
    public async Task Late_validation_results_cannot_reverify_edited_settings()
    {
        using var f = new DesktopFixture();
        var completed = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var window = Create(f, (_, _) => completed.Task);
        Next(window); Fill(window); Pump(window);
        Click(Find<Button>(window, "Validate AI connection"));
        Find<TextBox>(window, "Model name").Text = "changed-model"; Pump(window);
        completed.SetResult(Json("{\"data\":[{\"id\":\"test-model\"}]}"));
        await Until(() => Find<Button>(window, "Validate AI connection").IsEnabled);
        Assert.Contains("Model not verified", Find<TextBlock>(window, "AI validation status").Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Model is listed", Find<TextBlock>(window, "AI validation status").Text, StringComparison.Ordinal);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Reviewed_whitespace_is_preserved_double_submit_sends_once_and_model_errors_block_until_corrected()
    {
        using var f = new DesktopFixture(); var calls = 0; string? sent = null;
        var completed = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var window = Create(f, async (request, token) =>
        {
            calls++; using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            sent = json.RootElement.GetProperty("messages")[0].GetProperty("content").GetString();
            return await completed.Task;
        });
        Next(window); Fill(window); Next(window);
        const string message = "  Exact question\nSecond line  \n";
        Find<TextBox>(window, "Exact message to send").Text = message; Pump(window); Consent(window);
        Click(Button(window, "Send request")); Click(Button(window, "Send request"));
        Assert.Equal(1, calls); Assert.Equal(message, sent);
        completed.SetResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{\"error\":{\"code\":\"model_not_found\",\"message\":\"model unavailable\"}}") });
        await Until(() => Find<TextBlock>(window, "AI connection status").Text?.Contains("model is unavailable", StringComparison.Ordinal) == true);
        Consent(window); Assert.False(Button(window, "Send request").IsEnabled);
        Click(Find<Button>(window, "AI setup back")); Find<TextBox>(window, "Model name").Text = "corrected-model";
        Next(window); Consent(window);
        Assert.True(Button(window, "Send request").IsEnabled);
        Assert.Contains("Model not verified", Find<TextBlock>(window, "Reviewed validation status").Text, StringComparison.Ordinal);
        window.Close();
    }

    private static AIConnectionProfile Profile() => new(Guid.NewGuid().ToString("N"), "Saved service", "chat-completions", AIServices.Get("openai").Endpoint, "test-model", "openai");
    private static AIConnectionsWindow Create(DesktopFixture f, Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send, Vault? vault = null)
    {
        var window = new AIConnectionsWindow(f.Shell.Paths.SettingsDirectory, vault ?? new Vault(), connectionClient: new AIConnectionClient(new Handler(send)), confirmDiscard: _ => Task.FromResult(true));
        window.Show(f.Window); Pump(window); return window;
    }
    private static void Fill(Window window)
    {
        Find<TextBox>(window, "Model name").Text = "ssss";
        Find<TextBox>(window, "API key (saved only when you choose Save connection)").Text = "test-only-key"; Pump(window);
    }
    private static void Advanced(Window window) { window.GetVisualDescendants().OfType<Expander>().Single(e => Equals(e.Header, "Advanced: service address and protocol")).IsExpanded = true; Pump(window); }
    private static void Next(Window window) { Click(Find<Button>(window, "AI setup next")); Pump(window); }
    private static void Consent(Window window) { Find<CheckBox>(window, "Send exactly the message above to the endpoint above").IsChecked = true; Pump(window); }
    private static T Find<T>(Visual root, string name) where T : Control => root.GetVisualDescendants().OfType<T>().Single(c => AutomationProperties.GetName(c) == name);
    private static Button Button(Visual root, string content) => root.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, content));
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
    private static void Pump(Window window) { window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };
    private static async Task Until(Func<bool> complete)
    {
        for (var i = 0; i < 150 && !complete(); i++) { await Task.Delay(20, TestContext.Current.CancellationToken); Dispatcher.UIThread.RunJobs(); }
        Assert.True(complete());
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken); }
    private sealed class Vault : IAICredentialStore
    {
        private readonly Dictionary<string, string> _values = [];
        public string? Read(string profileId) => _values.GetValueOrDefault(profileId);
        public void Save(string profileId, string secret) => _values[profileId] = secret;
        public void Remove(string profileId) => _values.Remove(profileId);
    }
}
