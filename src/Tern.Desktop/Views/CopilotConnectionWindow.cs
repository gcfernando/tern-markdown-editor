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
    internal CopilotConnectionWindow(CopilotTextClient client)
    {
        DialogParts.Setup(this, "GitHub Copilot text connection", 820, 760);
        FontSize = GuideParts.Scaled(14); MinWidth = 640; MinHeight = 500;
        var executable = Field("Installed native copilot.exe", CopilotTextClient.DefaultExecutable);
        var model = Field("Copilot model", "auto"); model.MaxLength = 100;
        var token = Field("GitHub token with Copilot Requests permission (memory only)", ""); token.PasswordChar = '●'; token.MaxLength = 4096;
        var prompt = Field("Exact reviewed text to send", ""); prompt.AcceptsReturn = true; prompt.TextWrapping = TextWrapping.Wrap; prompt.MinHeight = 150; prompt.MaxLength = CopilotTextClient.MaximumPromptCharacters;
        var response = Field("Copilot response", ""); response.IsReadOnly = true; response.AcceptsReturn = true; response.TextWrapping = TextWrapping.Wrap; response.MinHeight = 140;
        const string consentText = "Send only the reviewed text using this executable, model and account";
        var consent = new CheckBox { Content = Body(consentText), HorizontalContentAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(consent, consentText);
        var send = new Button { Content = "Send reviewed text", IsEnabled = false };
        var cancel = new Button { Content = "Cancel request", IsEnabled = false };
        var close = new Button { Content = "Close", IsCancel = true };
        var status = Body("No CLI process or model request has been started."); AutomationProperties.SetName(status, "Copilot connection status");
        CancellationTokenSource? operation = null;
        (string Executable, string Model, string Token, string Prompt)? reviewed = null;
        (string Executable, string Model, string Token, string Prompt) Inputs() => (executable.Text ?? "", model.Text ?? "", token.Text ?? "", prompt.Text ?? "");
        void Invalidate() { reviewed = null; consent.IsChecked = false; send.IsEnabled = false; }
        executable.TextChanged += (_, _) => Invalidate(); model.TextChanged += (_, _) => Invalidate(); token.TextChanged += (_, _) => Invalidate(); prompt.TextChanged += (_, _) => Invalidate();
        consent.IsCheckedChanged += (_, _) => { reviewed = operation is null && consent.IsChecked == true ? Inputs() : null; send.IsEnabled = reviewed is not null; };
        send.Click += async (_, _) =>
        {
            if (operation is not null || consent.IsChecked != true || reviewed is not { } approved || approved != Inputs())
            { Invalidate(); status.Text = "The reviewed inputs changed. Review them again before sending."; return; }
            using var pending = new CancellationTokenSource(); operation = pending; send.IsEnabled = false; cancel.IsEnabled = true;
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
            finally { operation = null; cancel.IsEnabled = false; Invalidate(); }
        };
        cancel.Click += (_, _) => operation?.Cancel(); close.Click += (_, _) => Close();
        Closed += (_, _) => { operation?.Cancel(); token.Text = ""; prompt.Text = ""; response.Text = ""; reviewed = null; };
        var advanced = new Expander { Header = "Advanced: installed CLI and safety limits", HorizontalAlignment = HorizontalAlignment.Stretch,
            Content = GuideParts.Vertical(Label("Native copilot.exe location", executable),
                Body("Verified with CLI 1.0.95. Tern uses a fresh configuration and empty working folder; hooks, IDE context, custom instructions, built-in MCPs, remote export and auto-updates are disabled. Filesystem, shell and URL tools are removed. This is not an operating-system sandbox or a guarantee about future CLI versions."),
                Body("The token is passed only in the child process environment, never in command-line arguments or Tern settings. Temporary CLI session files are removed afterward; a crash can leave them. The external CLI and GitHub handle their own service data.")) };
        var contents = GuideParts.Vertical(
            GuideParts.Card(GuideParts.Vertical(Body("1. Prepare your Copilot access"),
                Body("You need GitHub Copilot access, the native Copilot CLI installed on this computer, and a GitHub token with Copilot Requests permission. Create that token in your GitHub account settings. This is separate from OpenAI or Claude API keys."),
                Label("GitHub token (secret; kept only in this window)", token), Label("Model (auto lets Copilot choose)", model), advanced)),
            GuideParts.Card(GuideParts.Vertical(Body("2. Paste and review your question"),
                Body("Only the text below is sent. No document or workspace is attached. Paste the text itself; @mentions and CLI commands are not accepted."),
                Label("Text you want Copilot to answer", prompt), consent)),
            GuideParts.Card(Label("3. Response (review before copying)", response)));
        Content = GuideParts.Frame(GuideParts.Title("GitHub Copilot"),
            new ScrollViewer { Content = contents, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled },
            GuideParts.Vertical(status, GuideParts.Actions(send, cancel, close)));
    }
    private static TextBox Field(string name, string text)
    { var field = new TextBox { Text = text, PlaceholderText = name, FontSize = GuideParts.Scaled(14) }; AutomationProperties.SetName(field, name); return field; }
    private static Control Label(string name, Control control) => GuideParts.Vertical(new TextBlock { Text = name, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap }, control);
    private static TextBlock Body(string text) => new() { Text = text, FontSize = GuideParts.Scaled(14), LineHeight = GuideParts.Scaled(21), TextWrapping = TextWrapping.Wrap };
}
