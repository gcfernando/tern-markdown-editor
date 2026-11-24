using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Tern.App;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Tern.Desktop.Services;
using Tern.Desktop.Views;

namespace Tern.Desktop.Tests;

/// <summary>Each AI connection keeps its own "Characters to send at one time" value.</summary>
public sealed class ConnectionSendLimitTests
{
    private const string Label = SendLimitField.LabelText;
    private const int OllamaIndex = 2;

    [Theory]
    [InlineData("8000", 8_000)]
    [InlineData("8,000", 8_000)]
    [InlineData(" 256,000 ", 256_000)]
    [InlineData("1000000", 1_000_000)]
    [InlineData("1,000,000", 1_000_000)]
    public void Valid_numbers_are_accepted(string text, int expected)
    {
        Assert.True(AIConnectionRules.TryParseSendCharacters(text, out var value));
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("-8000")]
    [InlineData("abc")]
    [InlineData("12.5")]
    [InlineData("1,2,3")]
    [InlineData("7999")]
    [InlineData("1000001")]
    [InlineData("99999999999")]
    public void Invalid_numbers_are_refused(string? text) => Assert.False(AIConnectionRules.TryParseSendCharacters(text, out _));

    [AvaloniaFact]
    public void New_connection_shows_the_default_directly_under_model_with_a_tip_and_plain_words()
    {
        using var folder = new TempFolder();
        using var window = Open(folder);
        Next(window);
        var field = Named<TextBox>(window, Label);
        Assert.Equal("256,000", field.Text);
        Assert.True(field.IsEffectivelyVisible && field.IsEnabled);
        var boxes = window.GetLogicalDescendants().OfType<TextBox>().ToList();
        Assert.Equal(boxes.IndexOf(Named<TextBox>(window, "Model name")) + 1, boxes.IndexOf(field));
        var words = window.GetLogicalDescendants().OfType<TextBlock>().Where(text => text.IsEffectivelyVisible).Select(text => text.Text ?? "").ToArray();
        Assert.Contains(SendLimitField.TipText, words);
        Assert.Contains(Label, words);
        Assert.DoesNotContain("token", SendLimitField.TipText + AIConnectionRules.SendCharactersProblem + Label, StringComparison.OrdinalIgnoreCase);
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("many")]
    [InlineData("7999")]
    [InlineData("1000001")]
    public void Bad_value_shows_a_red_message_and_blocks_continue_and_save(string bad)
    {
        using var folder = new TempFolder();
        using var window = Open(folder);
        Next(window);
        Named<TextBox>(window, "Model name").Text = "test-model";
        var field = Named<TextBox>(window, Label);
        field.Text = bad;
        Pump(window);
        var bar = field.GetLogicalSiblings().OfType<MessageBar>().Single();
        Assert.True(bar.IsEffectivelyVisible);
        Assert.Equal(MessageTone.Error, bar.Tone);
        Assert.Equal(AIConnectionRules.SendCharactersProblem, bar.Label.Text);
        Assert.Contains("rename-error", field.Classes);
        Click(Named<Button>(window, "Next step"));
        Pump(window);
        Assert.False(Named<Button>(window, "Save connection").IsVisible);
        Assert.True(field.IsEffectivelyVisible);

        field.Text = "50,000";
        Pump(window);
        Assert.False(bar.IsVisible);
        Assert.DoesNotContain("rename-error", field.Classes);
        Click(Named<Button>(window, "Next step"));
        Pump(window);
        var save = Named<Button>(window, "Save connection");
        Assert.True(save.IsEffectivelyVisible && save.IsEnabled);
        field.Text = bad;
        Pump(window);
        Assert.False(save.IsEnabled);
        Click(save);
        Assert.False(File.Exists(new AIProfileStore(folder.Path).FilePath));
        field.Text = "50,000";
        window.Close();
    }

    [AvaloniaFact]
    public void Two_connections_keep_their_own_values_after_reopening()
    {
        using var folder = new TempFolder();
        using (var window = Open(folder))
        {
            SaveConnection(window, "Server A", "100,000");
            Click(Named<Button>(window, "Previous step")); Pump(window);
            Click(Named<Button>(window, "Previous step")); Pump(window);
            Click(Named<Button>(window, "New AI connection")); Pump(window);
            SaveConnection(window, "Server B", "50000");
            window.Close();
        }
        var saved = new AIProfileStore(folder.Path).Load();
        Assert.Equal(100_000, saved.Single(profile => profile.Name == "Server A").SendCharacters);
        Assert.Equal(50_000, saved.Single(profile => profile.Name == "Server B").SendCharacters);

        using var reopened = Open(folder);
        var list = Named<ComboBox>(reopened, "Saved AI connections");
        list.SelectedIndex = saved.ToList().FindIndex(profile => profile.Name == "Server A"); Pump(reopened);
        Assert.Equal("100,000", Named<TextBox>(reopened, Label).Text);
        list.SelectedIndex = saved.ToList().FindIndex(profile => profile.Name == "Server B"); Pump(reopened);
        Assert.Equal("50,000", Named<TextBox>(reopened, Label).Text);
        reopened.Close();
    }

    [Fact]
    public void Older_api_file_gets_the_default_once_and_a_changed_value_then_stays()
    {
        using var folder = new TempFolder();
        var store = new AIProfileStore(folder.Path);
        var id = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(folder.Path);
        File.WriteAllText(store.FilePath, $$"""[{"Id":"{{id}}","Name":"Old","Provider":"ollama-chat","Endpoint":"http://localhost:11434/api/chat","Model":"m"}]""");

        Assert.Equal(256_000, Assert.Single(store.Load()).SendCharacters);
        using (var file = JsonDocument.Parse(File.ReadAllText(store.FilePath)))
            Assert.Equal(256_000, file.RootElement[0].GetProperty("SendCharacters").GetInt32());

        store.Save([Assert.Single(store.Load()) with { SendCharacters = 40_000 }]);
        var written = File.GetLastWriteTimeUtc(store.FilePath);
        var content = File.ReadAllText(store.FilePath);
        Assert.Equal(40_000, Assert.Single(store.Load()).SendCharacters);
        Assert.Equal(content, File.ReadAllText(store.FilePath));
        Assert.Equal(written, File.GetLastWriteTimeUtc(store.FilePath));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("5000000")]
    [InlineData("\"lots\"")]
    [InlineData("null")]
    public void Damaged_saved_value_is_repaired_and_keeps_the_connection(string value)
    {
        using var folder = new TempFolder();
        var store = new AIProfileStore(folder.Path);
        Directory.CreateDirectory(folder.Path);
        File.WriteAllText(store.FilePath, $$"""[{"Id":"{{Guid.NewGuid():N}}","Name":"Old","Provider":"ollama-chat","Endpoint":"http://localhost:11434/api/chat","Model":"m","SendCharacters":{{value}}}]""");
        Assert.Equal(256_000, Assert.Single(store.Load()).SendCharacters);
        using var file = JsonDocument.Parse(File.ReadAllText(store.FilePath));
        Assert.Equal(256_000, file.RootElement[0].GetProperty("SendCharacters").GetInt32());
    }

    [Fact]
    public void Older_copilot_file_gets_the_default_and_mcp_never_gets_a_value()
    {
        using var folder = new TempFolder();
        var store = new IntegrationProfileStore(folder.Path, new MemoryVault());
        Directory.CreateDirectory(folder.Path);
        var executable = JsonEncodedText.Encode(Path.Combine(folder.Path, "copilot.exe"));
        File.WriteAllText(store.FilePath, $$"""[{"Id":"{{Guid.NewGuid():N}}","Kind":"copilot","Name":"Old Copilot","Destination":"{{executable}}","Model":"auto"},{"Id":"{{Guid.NewGuid():N}}","Kind":"mcp","Name":"Tools","Destination":"https://tools.example/mcp","Model":"auto"}]""");
        var loaded = store.Load();
        Assert.Equal(256_000, loaded.Single(profile => profile.Kind == "copilot").SendCharacters);
        Assert.Null(loaded.Single(profile => profile.Kind == "mcp").SendCharacters);
        using var file = JsonDocument.Parse(File.ReadAllText(store.FilePath));
        var items = file.RootElement.EnumerateArray().ToArray();
        Assert.Equal(256_000, items.Single(item => item.GetProperty("Kind").GetString() == "copilot").GetProperty("SendCharacters").GetInt32());
        Assert.False(items.Single(item => item.GetProperty("Kind").GetString() == "mcp").TryGetProperty("SendCharacters", out _));
    }

    [Fact]
    public void Stores_refuse_to_save_an_invalid_value()
    {
        using var folder = new TempFolder();
        var profile = new AIConnectionProfile(Guid.NewGuid().ToString("N"), "Local", "ollama-chat", "http://localhost:11434/api/chat", "m");
        Assert.Equal(256_000, profile.SendCharacters);
        Assert.Throws<InvalidOperationException>(() => new AIProfileStore(folder.Path).Save([profile with { SendCharacters = 0 }]));
        var copilot = new IntegrationProfile(Guid.NewGuid().ToString("N"), "copilot", "Copilot", Path.Combine(folder.Path, "copilot.exe"));
        Assert.Throws<InvalidOperationException>(() => new IntegrationProfileStore(folder.Path, new MemoryVault()).Save(copilot with { SendCharacters = 1_000_001 }, ""));
    }

    [Fact]
    public void Workspace_connection_carries_the_saved_value()
    {
        using var folder = new TempFolder();
        new AIProfileStore(folder.Path).Save([new AIConnectionProfile(Guid.NewGuid().ToString("N"), "Local", "ollama-chat", "http://localhost:11434/api/chat", "m", SendCharacters: 75_000)]);
        var integrations = new IntegrationProfileStore(folder.Path, new MemoryVault());
        integrations.Save(new IntegrationProfile(Guid.NewGuid().ToString("N"), "copilot", "Copilot", Path.Combine(folder.Path, "copilot.exe"), SendCharacters: 90_000), "");
        var service = new AIWorkspaceService(folder.Path, new MemoryVault());
        service.Reload();
        Assert.Equal(75_000, service.Connections.Single(connection => connection.Kind == "api").SendCharacters);
        Assert.Equal(90_000, service.Connections.Single(connection => connection.Kind == "copilot").SendCharacters);
    }

    [AvaloniaFact]
    public void Copilot_shows_the_field_under_model_and_a_bad_value_blocks_continue()
    {
        using var folder = new TempFolder();
        var window = new CopilotConnectionWindow(folder.Path, new MemoryVault());
        try
        {
            window.Show(); Pump(window);
            var field = Named<TextBox>(window, Label);
            Assert.Equal("256,000", field.Text);
            var boxes = window.GetLogicalDescendants().OfType<TextBox>().ToList();
            Assert.Equal(boxes.IndexOf(Named<TextBox>(window, "Copilot model")) + 1, boxes.IndexOf(field));
            var next = window.GetLogicalDescendants().OfType<Button>().Single(button => Equals(button.Content, "Continue"));
            Assert.True(next.IsEnabled);
            field.Text = "0"; Pump(window);
            Assert.False(next.IsEnabled);
            var bar = field.GetLogicalSiblings().OfType<MessageBar>().Single();
            Assert.Equal(MessageTone.Error, bar.Tone);
            Assert.True(bar.IsEffectivelyVisible);
            field.Text = "120,000"; Pump(window);
            Assert.True(next.IsEnabled);
            Assert.False(bar.IsVisible);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Copilot_scrolls_the_red_message_into_view_when_the_value_becomes_bad()
    {
        using var folder = new TempFolder();
        var window = new CopilotConnectionWindow(folder.Path, new MemoryVault());
        try
        {
            window.Show(); Pump(window);
            var field = Named<TextBox>(window, Label);
            field.BringIntoView(); Pump(window);
            field.Text = "0";
            await Task.Delay(40);
            Pump(window); Pump(window);
            var bar = field.GetLogicalSiblings().OfType<MessageBar>().Single();
            var viewport = window.GetLogicalDescendants().OfType<ScrollViewer>().First(scroll => scroll.Content is StackPanel);
            var point = bar.TranslatePoint(default, viewport)!.Value;
            Assert.True(point.Y >= -1 && point.Y + bar.Bounds.Height <= viewport.Viewport.Height + 1,
                $"Message at {point.Y} + {bar.Bounds.Height} in a viewport of {viewport.Viewport.Height}.");
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Two_copilot_connections_keep_their_own_values()
    {
        using var folder = new TempFolder();
        var vault = new MemoryVault();
        var destination = new TextBox { Text = Path.Combine(folder.Path, "copilot.exe") };
        var model = new TextBox { Text = "auto" };
        var secret = new TextBox { Text = "synthetic-value" };
        var status = new TextBlock();
        var limit = new SendLimitField();
        var editor = new IntegrationProfileEditor("copilot", destination, model, secret, status, folder.Path, vault, limit);
        var name = editor.Content.GetLogicalDescendants().OfType<TextBox>().Single(box => AutomationProperties.GetName(box) == "Integration connection name");
        var create = editor.Content.GetLogicalDescendants().OfType<Button>().Single(button => AutomationProperties.GetName(button) == "Create integration connection");
        name.Text = "Copilot A"; limit.Input.Text = "20,000";
        Assert.True(editor.Save());
        create.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal("256,000", limit.Input.Text);
        name.Text = "Copilot B"; secret.Text = "synthetic-value"; limit.Input.Text = "30000";
        Assert.True(editor.Save());
        limit.Input.Text = "nope";
        Assert.False(editor.Save());
        var saved = new IntegrationProfileStore(folder.Path, vault).Load();
        Assert.Equal(20_000, saved.Single(profile => profile.Name == "Copilot A").SendCharacters);
        Assert.Equal(30_000, saved.Single(profile => profile.Name == "Copilot B").SendCharacters);
        Assert.True(editor.Select(saved.Single(profile => profile.Name == "Copilot A").Id));
        Assert.Equal("20,000", limit.Input.Text);
    }

    [AvaloniaFact]
    public void Mcp_screen_has_no_send_limit_field()
    {
        using var folder = new TempFolder();
        var window = new MCPConnectionsWindow(folder.Path, new MemoryVault());
        try
        {
            window.Show(); Pump(window);
            Assert.DoesNotContain(window.GetLogicalDescendants().OfType<TextBox>(), box => AutomationProperties.GetName(box) == Label);
            Assert.DoesNotContain(window.GetLogicalDescendants().OfType<TextBlock>(), text => text.Text == Label);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(50_000)]
    [InlineData(80_000)]
    public void Api_test_message_warns_before_send_using_this_connections_value(int limit)
    {
        using var folder = new TempFolder();
        using var window = Open(folder);
        Next(window);
        Named<TextBox>(window, "Model name").Text = "synthetic-model";
        Named<TextBox>(window, Label).Text = limit.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Next(window);
        window.GetLogicalDescendants().OfType<Expander>().Single(e => Equals(e.Header, "Try your own message (optional)")).IsExpanded = true;
        Named<TextBox>(window, "Exact message to send").Text = new string('é', limit - AIRequest.ConnectionTest("").CharacterCount + 1);
        Pump(window);
        Named<CheckBox>(window, AIConnectionsWindow.TestConsentText).IsChecked = true;
        var send = window.GetLogicalDescendants().OfType<Button>().Single(b => Equals(b.Content, "Send request"));
        Assert.False(send.IsEnabled);
        Assert.Contains(AIConnectionRules.FormatSendCharacters(limit), ToolTip.GetTip(send)?.ToString());
        Assert.Contains("Select less or raise the limit", Named<TextBlock>(window, "AI connection status").Text);
        Named<TextBox>(window, "Exact message to send").Text = new string('é', limit - AIRequest.ConnectionTest("").CharacterCount);
        Pump(window);
        Named<CheckBox>(window, AIConnectionsWindow.TestConsentText).IsChecked = true;
        Assert.True(send.IsEnabled);
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData(50_000)]
    [InlineData(80_000)]
    public void Copilot_test_message_warns_before_send_using_this_connections_value(int limit)
    {
        using var folder = new TempFolder();
        var window = new CopilotConnectionWindow(folder.Path, new MemoryVault());
        try
        {
            window.Show(); Pump(window);
            Named<TextBox>(window, Label).Text = limit.ToString(System.Globalization.CultureInfo.InvariantCulture);
            Named<TextBox>(window, "Exact reviewed text to send").Text = new string('é', limit - AIRequest.ConnectionTest("").CharacterCount + 1);
            Pump(window);
            Named<CheckBox>(window, CopilotConnectionWindow.TestConsentText).IsChecked = true;
            var send = window.GetLogicalDescendants().OfType<Button>().Single(b => Equals(b.Content, "Send reviewed text"));
            Assert.False(send.IsEnabled);
            Assert.Contains(AIConnectionRules.FormatSendCharacters(limit), ToolTip.GetTip(send)?.ToString());
            Named<TextBox>(window, "Exact reviewed text to send").Text = new string('é', limit - AIRequest.ConnectionTest("").CharacterCount);
            Pump(window);
            Named<CheckBox>(window, CopilotConnectionWindow.TestConsentText).IsChecked = true;
            Assert.True(send.IsEnabled);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(50_000)]
    [InlineData(80_000)]
    public async Task Api_connection_test_sends_the_full_saved_limit_and_blocks_larger_text(int limit)
    {
        using var folder = new TempFolder();
        var sent = new List<string>();
        using var client = new AIConnectionClient(new SyntheticTransport(async (request, token) =>
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            var messages = json.RootElement.GetProperty("messages").EnumerateArray().ToArray();
            Assert.Equal(new[] { "system", "user" }, messages.Select(item => item.GetProperty("role").GetString()));
            sent.Add(string.Concat(messages.Select(item => item.GetProperty("content").GetString())));
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("""{"message":{"content":"Synthetic answer"}}""", Encoding.UTF8, "application/json") };
        }));
        using var window = Open(folder, client);
        try
        {
            Next(window);
            Named<TextBox>(window, "Connection name").Text = "Synthetic server";
            Named<TextBox>(window, "Model name").Text = "synthetic-model";
            Named<TextBox>(window, Label).Text = limit.ToString(System.Globalization.CultureInfo.InvariantCulture);
            Next(window);
            Click(Named<Button>(window, "Save connection")); Pump(window);
            Assert.Equal(limit, Assert.Single(new AIProfileStore(folder.Path).Load()).SendCharacters);
            window.GetLogicalDescendants().OfType<Expander>().Single(e => Equals(e.Header, "Try your own message (optional)")).IsExpanded = true;
            var message = Named<TextBox>(window, "Exact message to send");
            var consent = Named<CheckBox>(window, AIConnectionsWindow.TestConsentText);
            var send = window.GetLogicalDescendants().OfType<Button>().Single(b => Equals(b.Content, "Send request"));
            var text = new string('é', limit - AIRequest.ConnectionTest("").CharacterCount);
            var instructions = window.GetLogicalDescendants().OfType<Expander>().Single(item => Equals(item.Header, "Tern's test instructions"));
            instructions.IsExpanded = true; Pump(window);
            Assert.Contains(window.GetLogicalDescendants().OfType<TextBlock>(), item => item.IsEffectivelyVisible && item.Text == AIRequest.ConnectionTest("").Instructions.Trim());
            message.Text = text; consent.IsChecked = true; Pump(window);
            Assert.True(send.IsEnabled);
            Click(send);
            await Until(window, () => Named<TextBox>(window, "AI response").Text == "Synthetic answer");
            Assert.Equal(AIRequest.ConnectionTest(text).Text, Assert.Single(sent));
            Assert.Equal(limit, sent[0].Length);

            message.Text = text + "é"; consent.IsChecked = true; Pump(window);
            Assert.False(send.IsEnabled);
            Assert.Contains("Select less or raise the limit", ToolTip.GetTip(send)?.ToString());
            Click(send); Pump(window);
            Assert.Single(sent);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(50_000)]
    [InlineData(80_000)]
    public async Task Copilot_connection_test_sends_the_full_saved_limit_and_blocks_larger_text(int limit)
    {
        using var folder = new TempFolder();
        var executable = Path.Combine(folder.Path, "copilot.exe");
        File.WriteAllText(executable, "Synthetic fixture; never launched.");
        var sent = new List<string>();
        var client = new CopilotTextClient((_, text, _) =>
        {
            sent.Add(text);
            return Task.FromResult(new CopilotProcessResult(0, "Synthetic answer"));
        });
        var vault = new MemoryVault();
        var window = new CopilotConnectionWindow(client, folder.Path, vault);
        try
        {
            window.Show(); Pump(window);
            Named<TextBox>(window, "Installed native copilot.exe").Text = executable;
            Named<TextBox>(window, "Integration connection name").Text = "Synthetic Copilot";
            Named<TextBox>(window, Label).Text = limit.ToString(System.Globalization.CultureInfo.InvariantCulture);
            // Settle destination/settings changes before entering permission for that destination.
            Pump(window);
            Named<TextBox>(window, "GitHub token with Copilot Requests permission").Text = "synthetic-test-key";
            Pump(window);
            Button Button(string label) => window.GetLogicalDescendants().OfType<Button>().Single(b => Equals(b.Content, label));
            string ValidationStatus() => (Named<TextBlock>(window, "Copilot connection status").Text ?? "No status")
                .Replace("synthetic-test-key", "[redacted]", StringComparison.Ordinal)
                .Replace(executable, "[synthetic executable]", StringComparison.Ordinal);
            Click(Button("Continue")); Pump(window);
            Assert.True(Button("Validate connection").IsEffectivelyVisible, ValidationStatus());
            Click(Button("Validate connection"));
            await Until(window, () => sent.Count > 0 && Button("Validate connection").IsEnabled
                && ValidationStatus().StartsWith("Validation passed.", StringComparison.Ordinal), ValidationStatus);
            Assert.Equal(AIRequest.ConnectionTest(AIConnectionClient.ValidationMessage).Text, Assert.Single(sent));
            Click(Button("Save connection")); Pump(window);
            Assert.Equal(limit, Assert.Single(new IntegrationProfileStore(folder.Path, vault).Load()).SendCharacters);
            sent.Clear();
            var prompt = Named<TextBox>(window, "Exact reviewed text to send");
            var consent = Named<CheckBox>(window, CopilotConnectionWindow.TestConsentText);
            var send = Button("Send reviewed text");
            Assert.True(send.IsEffectivelyVisible);
            Assert.False(send.IsEnabled);
            Assert.Equal("Enter text before sending.", Named<TextBlock>(window, "Copilot connection status").Text);
            var text = new string('é', limit - AIRequest.ConnectionTest("").CharacterCount);
            Assert.Contains(window.GetLogicalDescendants().OfType<Expander>(), item => Equals(item.Header, "Tern's test instructions"));
            // TextChanged revokes the previous review on the dispatcher. Finish that edit
            // before ticking permission, in the same order as separate user actions.
            prompt.Text = text; Pump(window);
            consent.IsChecked = true; Pump(window);
            Assert.True(consent.IsChecked);
            Assert.True(send.IsEnabled);
            Click(send);
            await Until(window, () => Named<TextBox>(window, "Copilot response").Text == "Synthetic answer");
            Assert.Equal(AIRequest.ConnectionTest(text).Text, Assert.Single(sent));
            Assert.Equal(limit, sent[0].Length);

            prompt.Text = text + "é"; Pump(window);
            consent.IsChecked = true; Pump(window);
            Assert.False(send.IsEnabled);
            Assert.Contains("Select less or raise the limit", ToolTip.GetTip(send)?.ToString());
            Click(send); Pump(window);
            Assert.Single(sent);

            prompt.Text = " \n "; Pump(window);
            consent.IsChecked = true; Pump(window);
            Assert.False(send.IsEnabled);
            Assert.Equal("Enter text before sending.", ToolTip.GetTip(send));
            Assert.Equal("Enter text before sending.", Named<TextBlock>(window, "Copilot connection status").Text);
            Click(send); Pump(window);
            Assert.Single(sent);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Blank_writing_instruction_explains_why_generation_is_disabled()
    {
        using var folder = new TempFolder();
        new AIProfileStore(folder.Path).Save([
            new(Guid.NewGuid().ToString("N"), "Synthetic server", "chat-completions", "https://example.test/v1/chat/completions", "synthetic-model", SendCharacters: 50_000)
        ]);
        var workspace = new AIWorkspaceService(folder.Path, new MemoryVault()); workspace.Reload();
        var calls = 0;
        using var panel = new AIPromptPanel("Synthetic note", workspace,
            (_, _, _) => { calls++; return Task.FromResult("Must not generate"); },
            () => { }, () => Task.CompletedTask, workspace.Select, initialInstruction: " \n ");
        var window = new Window { Content = panel };
        try
        {
            window.Show(); Pump(window);
            var send = Named<Button>(window, "Generate AI suggestion");
            const string reason = "Enter a question or instruction before generating.";
            Assert.False(send.IsEnabled);
            Assert.True(ToolTip.GetShowOnDisabled(send));
            Assert.Equal(reason, ToolTip.GetTip(send));
            var message = Named<TextBlock>(window, "AI writing validation");
            Assert.True(message.IsEffectivelyVisible);
            Assert.Equal(reason, message.Text);
            Click(send);
            Assert.Equal(0, calls);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(false, 1.0)]
    [InlineData(false, 1.5)]
    [InlineData(false, 2.0)]
    [InlineData(true, 1.0)]
    [InlineData(true, 1.5)]
    [InlineData(true, 2.0)]
    public async Task Field_message_and_main_button_stay_reachable_at_minimum_size(bool copilot, double scale)
    {
        using var fixture = new DesktopFixture();
        using var folder = new TempFolder();
        fixture.Shell.Settings.UiScale = scale;
        fixture.Window.ApplySettings();
        Window window = copilot ? new CopilotConnectionWindow(folder.Path, new MemoryVault()) : Open(folder);
        try
        {
            if (copilot) window.Show(fixture.Window);
            else Next(window);
            window.Width = window.MinWidth;
            window.Height = window.MinHeight;
            await Task.Delay(40);
            Pump(window); Pump(window);
            var field = Named<TextBox>(window, Label);
            field.Text = "0";
            var bar = field.GetLogicalSiblings().OfType<MessageBar>().Single();
            foreach (var control in new Control[] { field, bar })
            {
                control.BringIntoView();
                await Task.Delay(20);
                Pump(window); Pump(window);
                var point = control.TranslatePoint(default, window)!.Value;
                Assert.True(control.IsEffectivelyVisible && control.Bounds.Width > 0, control.GetType().Name + " is hidden.");
                Assert.True(point.X >= -1 && point.X + control.Bounds.Width <= window.ClientSize.Width + 1, $"{control.GetType().Name} runs past the side at {scale:P0}.");
                Assert.True(point.Y >= -1 && point.Y + Math.Min(control.Bounds.Height, window.ClientSize.Height) <= window.ClientSize.Height + 1, $"{control.GetType().Name} cannot be scrolled into view at {scale:P0}.");
            }
            var main = window.GetLogicalDescendants().OfType<Button>().Single(button => button.IsEffectivelyVisible && button.Classes.Contains("accent"));
            var origin = main.TranslatePoint(default, window)!.Value;
            Assert.True(origin.X >= -1 && origin.X + main.Bounds.Width <= window.ClientSize.Width + 1 && origin.Y >= -1 && origin.Y + main.Bounds.Height <= window.ClientSize.Height + 1,
                $"Main button is outside the window at {scale:P0}.");
            field.Text = "256,000";
        }
        finally { window.Close(); fixture.Shell.Settings.UiScale = 1; fixture.Window.ApplySettings(); }
    }

    private static AIConnectionsWindow Open(TempFolder folder, AIConnectionClient? client = null)
    {
        var window = new AIConnectionsWindow(folder.Path, new MemoryVault(folder.Secrets), connectionClient: client, confirmDiscard: _ => Task.FromResult(true));
        window.Show(); Pump(window);
        Named<ComboBox>(window, "AI service preset").SelectedIndex = OllamaIndex;
        Pump(window);
        return window;
    }

    private static void SaveConnection(AIConnectionsWindow window, string name, string limit)
    {
        Named<ComboBox>(window, "AI service preset").SelectedIndex = OllamaIndex; Pump(window);
        Next(window);
        Named<TextBox>(window, "Connection name").Text = name;
        Named<TextBox>(window, "Model name").Text = "test-model";
        Named<TextBox>(window, Label).Text = limit;
        Next(window);
        Click(Named<Button>(window, "Save connection"));
        Pump(window);
    }

    private static void Next(Window window) { Click(Named<Button>(window, "Next step")); Pump(window); }
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Pump(Window window) { window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
    private static async Task Until(Window window, Func<bool> ready, Func<string>? diagnostic = null)
    {
        for (var attempt = 0; attempt < 200 && !ready(); attempt++)
        { Pump(window); await Task.Delay(10, TestContext.Current.CancellationToken); }
        Pump(window); Assert.True(ready(), "The synthetic request did not finish."
            + (diagnostic is null ? "" : " Status: " + diagnostic()));
    }
    private static T Named<T>(Window window, string name) where T : Control =>
        window.GetLogicalDescendants().OfType<T>().Single(control => AutomationProperties.GetName(control) == name);

    private sealed class TempFolder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tern-send-limit-" + Guid.NewGuid().ToString("N"));
        public Dictionary<string, string> Secrets { get; } = [];
        public TempFolder() => Directory.CreateDirectory(Path);
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, true); }
    }

    private sealed class MemoryVault(Dictionary<string, string>? values = null) : IAICredentialStore
    {
        private readonly Dictionary<string, string> _values = values ?? [];
        public string? Read(string profileId) => _values.GetValueOrDefault(profileId);
        public void Save(string profileId, string secret) => _values[profileId] = secret;
        public void Remove(string profileId) => _values.Remove(profileId);
    }

    private sealed class SyntheticTransport(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}

internal static class SendLimitTestExtensions
{
    /// <summary>The controls that share the field's own group, such as its message bar.</summary>
    public static IEnumerable<ILogical> GetLogicalSiblings(this Control control) =>
        ((ILogical)control).LogicalParent is { } parent ? parent.LogicalChildren : Enumerable.Empty<ILogical>();
}
