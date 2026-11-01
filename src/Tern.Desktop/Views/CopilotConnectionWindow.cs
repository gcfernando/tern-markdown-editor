using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Tern.Desktop.Services;

namespace Tern.Desktop.Views;

public sealed class CopilotConnectionWindow : Window
{
    public CopilotConnectionWindow() : this(new CopilotTextClient()) { }
    public CopilotConnectionWindow(string? settingsDirectory, IAICredentialStore? credentialStore = null, string? selectedProfileId = null)
        : this(new CopilotTextClient(), settingsDirectory, credentialStore, selectedProfileId) { }
    internal CopilotConnectionWindow(CopilotTextClient client, string? settingsDirectory = null, IAICredentialStore? credentialStore = null, string? selectedProfileId = null)
    {
        DialogParts.SetupWizard(this, "GitHub Copilot");
        FontSize = GuideParts.Scaled(14); MinWidth = 640; MinHeight = 500;
        var executable = Field("Installed native copilot.exe", CopilotTextClient.DefaultExecutable);
        var model = Field("Copilot model", "auto"); model.MaxLength = 100;
        var token = Field("GitHub token with Copilot Requests permission", ""); token.PasswordChar = '●'; token.MaxLength = 4096;
        var prompt = Field("Exact reviewed text to send", ""); prompt.AcceptsReturn = true; prompt.TextWrapping = TextWrapping.Wrap; prompt.MinHeight = 150; prompt.MaxLength = CopilotTextClient.MaximumPromptCharacters;
        var response = Field("Copilot response", ""); response.IsReadOnly = true; response.AcceptsReturn = true; response.TextWrapping = TextWrapping.Wrap; response.MinHeight = 140;
        const string consentText = "Send only the reviewed text using this executable, model and account";
        var consent = new CheckBox { Content = Body(consentText), HorizontalContentAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(consent, consentText);
        var send = new Button { Content = "Send reviewed text", IsEnabled = false };
        var cancel = new Button { Content = "Cancel request", IsEnabled = false };
        var close = new Button { Content = "Close", IsCancel = true };
        var status = Body("No CLI process or model request has been started."); AutomationProperties.SetName(status, "Copilot connection status");
        var profiles = new IntegrationProfileEditor("copilot", executable, model, token, status, settingsDirectory, credentialStore);
        var next = new Button { Content = "Continue", Classes = { "accent" } };
        var back = new Button { Content = "Back" };
        var validate = new Button { Content = "Validate connection" };
        var validation = Body("Not tested yet.");
        (string Executable, string Model, string Token)? validated = null;
        int page = 0;
        CancellationTokenSource? operation = null;
        (string Executable, string Model, string Token, string Prompt)? reviewed = null;
        (string Executable, string Model, string Token, string Prompt) Inputs() => (executable.Text ?? "", model.Text ?? "", token.Text ?? "", prompt.Text ?? "");
        void InvalidateMessage() { reviewed = null; consent.IsChecked = false; send.IsEnabled = false; }
        void Invalidate() { InvalidateMessage(); validated = null; validation.Text = "Settings changed. Validate this connection again."; }
        executable.TextChanged += (_, _) => Invalidate(); model.TextChanged += (_, _) => Invalidate(); token.TextChanged += (_, _) => Invalidate(); prompt.TextChanged += (_, _) => InvalidateMessage();
        consent.IsCheckedChanged += (_, _) => { reviewed = operation is null && consent.IsChecked == true ? Inputs() : null; send.IsEnabled = reviewed is not null; };
        send.Click += async (_, _) =>
        {
            if (operation is not null || consent.IsChecked != true || reviewed is not { } approved || approved != Inputs())
            { InvalidateMessage(); status.Text = "The reviewed inputs changed. Review them again before sending."; return; }
            using var pending = new CancellationTokenSource(); operation = pending; send.IsEnabled = false; cancel.IsEnabled = true; cancel.IsVisible = true; next.IsEnabled = back.IsEnabled = false;
            status.Text = "Waiting for the installed Copilot CLI…";
            try
            {
                response.Text = await client.SendAsync(approved.Executable, approved.Model, approved.Token, approved.Prompt, pending.Token);
                status.Text = "Response received. Review it before copying text into a document.";
            }
            catch (OperationCanceledException) { status.Text = "Canceled or timed out. The provider may already have received the text; your documents are unchanged."; }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { status.Text = ex.Message; }
            catch (IOException) { status.Text = "The private Copilot session files could not be read or cleaned up. Check Windows access and the temporary Tern-Copilot-Text folder before retrying."; }
            catch (UnauthorizedAccessException) { status.Text = "Windows denied access to the CLI or its private temporary session folder."; }
            finally { operation = null; cancel.IsEnabled = false; cancel.IsVisible = false; next.IsEnabled = back.IsEnabled = true; InvalidateMessage(); }
        };
        cancel.Click += (_, _) => operation?.Cancel(); close.Click += (_, _) => Close();
        Closed += (_, _) => { operation?.Cancel(); token.Text = ""; prompt.Text = ""; response.Text = ""; reviewed = null; };
        var advanced = new Expander { Header = "Advanced: installed CLI and safety limits", HorizontalAlignment = HorizontalAlignment.Stretch,
            Content = GuideParts.Vertical(Label("Native copilot.exe location", executable),
                Body("Verified with CLI 1.0.95. Tern uses a fresh configuration and empty working folder; hooks, IDE context, custom instructions, built-in MCPs, remote export and auto-updates are disabled. Filesystem, shell and URL tools are removed. This is not an operating-system sandbox or a guarantee about future CLI versions."),
                Body("The token is passed only in the child process environment, never in command-line arguments or plaintext Tern settings. Temporary CLI session files are removed afterward; a crash can leave them. The external CLI and GitHub handle their own service data.")) };
        var pages = new Control[]
        {
            GuideParts.Card(GuideParts.Vertical(GuideParts.Title("Configure GitHub Copilot"), profiles.Content,
                Body("Use an account with Copilot access and the installed native Copilot CLI. Create a fine-grained personal access token with your personal account as Resource owner; under Account permissions, add Copilot Requests. Paste that token below. Leave Model at auto for a first test, and check the CLI location in Advanced."),
                Label("GitHub token (stored securely when you save)", token), Label("Model (auto lets Copilot choose)", model), advanced)),
            GuideParts.Card(GuideParts.Vertical(GuideParts.Title("Validate and save"),
                Body("Validate starts the installed CLI and sends this test: Reply with exactly: OK. It checks the executable, token and selected model. GitHub usage limits or charges may apply."),
                Body("Your documents and question are not included in this test."), validate, validation,
                Body("After a successful test, Save connection stores the settings on this device and protects the token in Windows Credential Manager."))),
            GuideParts.Vertical(GuideParts.Card(GuideParts.Vertical(GuideParts.Title("Try your connection"),
                Body("Connection saved. Only the reviewed text below is sent; no document or workspace is attached. @mentions and CLI commands are not accepted."),
                Label("Text you want Copilot to answer", prompt), consent)), GuideParts.Card(Label("Response (review before copying)", response)))
        };
        var stack = new StackPanel(); foreach (var item in pages) stack.Children.Add(item);
        var steps = new ContentControl();
        var scroll = new ScrollViewer { Content = stack, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        void Page(int selected)
        {
            page = selected; for (var i = 0; i < pages.Length; i++) pages[i].IsVisible = i == page;
            steps.Content = GuideParts.ConnectionSteps(page, "Configure", "Validate and save", "Try connection");
            next.IsVisible = page < 2; next.Content = page == 0 ? "Continue" : "Save connection";
            back.IsVisible = page > 0; send.IsVisible = page == 2; cancel.IsVisible = operation is not null; scroll.Offset = default;
        }
        next.Click += (_, _) =>
        {
            if (operation is not null) return;
            if (page == 0) { Page(1); return; }
            if (validated != (executable.Text ?? "", model.Text ?? "", token.Text ?? ""))
            { status.Text = "Validate these settings successfully before saving."; return; }
            if (profiles.Save()) Page(2);
        };
        back.Click += (_, _) => { if (operation is null) Page(Math.Max(0, page - 1)); };
        validate.Click += async (_, _) =>
        {
            if (operation is not null) return;
            validated = null; InvalidateMessage();
            var snapshot = (Executable: executable.Text ?? "", Model: model.Text ?? "", Token: token.Text ?? "");
            using var pending = new CancellationTokenSource(); operation = pending;
            next.IsEnabled = back.IsEnabled = validate.IsEnabled = false; cancel.IsEnabled = true; cancel.IsVisible = true;
            status.Text = validation.Text = "Testing the installed CLI, authentication and model…";
            try
            {
                var answer = await client.SendAsync(snapshot.Executable, snapshot.Model, snapshot.Token, AIConnectionClient.ValidationMessage, pending.Token);
                if (snapshot != (executable.Text ?? "", model.Text ?? "", token.Text ?? "")) throw new InvalidOperationException("Settings changed during validation. Validate again.");
                if (string.IsNullOrWhiteSpace(answer)) throw new InvalidOperationException("The CLI returned no text. Generation could not be verified.");
                validated = snapshot; status.Text = validation.Text = "Validation passed. The CLI returned a model response. You can save this connection.";
            }
            catch (OperationCanceledException) { status.Text = validation.Text = "Validation canceled or timed out. Check your connection and try again."; }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
            { status.Text = validation.Text = "Validation failed. " + ex.Message; }
            finally { operation = null; next.IsEnabled = back.IsEnabled = validate.IsEnabled = true; cancel.IsEnabled = false; cancel.IsVisible = false; }
        };
        Content = GuideParts.Frame(GuideParts.Vertical(GuideParts.Title("GitHub Copilot"), steps), scroll,
            GuideParts.Vertical(status, GuideParts.Actions(back, next, send, cancel, close)));
        if (selectedProfileId is not null) profiles.Select(selectedProfileId);
        Page(0);
    }
    private static TextBox Field(string name, string text)
    { var field = new TextBox { Text = text, PlaceholderText = name, FontSize = GuideParts.Scaled(14) }; AutomationProperties.SetName(field, name); return field; }
    private static Control Label(string name, Control control) => GuideParts.Vertical(new TextBlock { Text = name, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap }, control);
    private static TextBlock Body(string text) => new() { Text = text, FontSize = GuideParts.Scaled(14), LineHeight = GuideParts.Scaled(21), TextWrapping = TextWrapping.Wrap };
}
