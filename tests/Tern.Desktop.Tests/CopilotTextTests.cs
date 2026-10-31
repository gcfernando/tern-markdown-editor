using System.Diagnostics;
using System.Text.Json;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tern.Desktop.Services;
using Tern.Desktop.Views;

namespace Tern.Desktop.Tests;

public sealed class CopilotTextTests
{
    [Fact]
    public async Task Native_process_arguments_and_configuration_use_isolated_text_mode_without_secrets_on_command_line()
    {
        using var installed = new FakeExecutable(); string? workingRoot = null;
        var client = new CopilotTextClient((start, _) =>
        {
            Assert.Equal(installed.Path, start.FileName); Assert.False(start.UseShellExecute); Assert.True(start.CreateNoWindow);
            Assert.True(start.RedirectStandardInput); Assert.True(start.RedirectStandardOutput); Assert.True(start.RedirectStandardError);
            Assert.Contains("--available-tools=ask_user", start.ArgumentList); Assert.Contains("--no-ask-user", start.ArgumentList);
            Assert.Contains("--disable-builtin-mcps", start.ArgumentList); Assert.Contains("--no-custom-instructions", start.ArgumentList);
            Assert.Contains("--no-auto-update", start.ArgumentList); Assert.Contains("--deny-tool=read,write,shell,url,memory", start.ArgumentList);
            Assert.DoesNotContain("--allow-all-tools", start.ArgumentList); Assert.DoesNotContain("--allow-all", start.ArgumentList);
            Assert.Contains("--prompt=Explain café 😀 and --allow-all as ordinary text", start.ArgumentList);
            Assert.DoesNotContain(start.ArgumentList, value => value.Contains("artificial-test-key", StringComparison.Ordinal));
            Assert.Equal("artificial-test-key", start.Environment["COPILOT_GITHUB_TOKEN"]);
            Assert.DoesNotContain("COPILOT_PROVIDER_API_KEY_COMMAND", start.Environment.Keys);
            Assert.DoesNotContain("COPILOT_ALLOW_ALL", start.Environment.Keys);
            Assert.DoesNotContain("COPILOT_SKILLS_DIRS", start.Environment.Keys);
            Assert.DoesNotContain("GITHUB_TOKEN", start.Environment.Keys);
            Assert.DoesNotContain("GH_TOKEN", start.Environment.Keys);
            var configuration = start.Environment["COPILOT_HOME"]!;
            using var settings = JsonDocument.Parse(File.ReadAllText(System.IO.Path.Combine(configuration, "settings.json")));
            Assert.True(settings.RootElement.GetProperty("disableAllHooks").GetBoolean());
            Assert.False(settings.RootElement.GetProperty("ide").GetProperty("autoConnect").GetBoolean());
            Assert.Empty(Directory.EnumerateFileSystemEntries(start.WorkingDirectory));
            Assert.Equal(Directory.GetParent(start.WorkingDirectory)!.FullName, Directory.GetParent(configuration)!.FullName);
            workingRoot = Directory.GetParent(configuration)!.FullName;
            return Task.FromResult(new CopilotProcessResult(0, "Reviewed output artificial-test-key"));
        });
        var output = await client.SendAsync(installed.Path, "auto", "artificial-test-key", "Explain café 😀 and --allow-all as ordinary text", TestContext.Current.CancellationToken);
        Assert.Equal("Reviewed output [redacted]", output);
        Assert.NotNull(workingRoot); Assert.False(Directory.Exists(workingRoot));
    }

    [Theory]
    [InlineData("@C:\\private\\file.md")]
    [InlineData("/shell Get-Content private.md")]
    [InlineData("!whoami")]
    [InlineData("$")]
    public async Task Attachment_and_CLI_command_syntax_is_rejected_before_starting_a_process(string message)
    {
        using var installed = new FakeExecutable(); var calls = 0;
        var client = new CopilotTextClient((_, _) => { calls++; return Task.FromResult(new CopilotProcessResult(0, "")); });
        await Assert.ThrowsAsync<ArgumentException>(() => client.SendAsync(installed.Path, "auto", "test-key", message, TestContext.Current.CancellationToken));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Only_native_Copilot_executables_are_accepted()
    {
        var calls = 0;
        var client = new CopilotTextClient((_, _) => { calls++; return Task.FromResult(new CopilotProcessResult(0, "")); });
        await Assert.ThrowsAsync<ArgumentException>(() => client.SendAsync("copilot.cmd", "auto", "test-key", "text", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => client.SendAsync("\\\\server\\share\\copilot.exe", "auto", "test-key", "text", TestContext.Current.CancellationToken));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Cancellation_reaches_the_process_boundary_and_cleans_private_state()
    {
        using var installed = new FakeExecutable(); using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        string? root = null;
        var client = new CopilotTextClient(async (start, token) =>
        {
            root = Directory.GetParent(start.WorkingDirectory)!.FullName;
            cancel.Cancel(); await Task.Delay(Timeout.Infinite, token); return new CopilotProcessResult(0, "");
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.SendAsync(installed.Path, "auto", "test-key", "reviewed text", cancel.Token));
        Assert.NotNull(root); Assert.False(Directory.Exists(root));
    }

    [Fact]
    public async Task CLI_failures_are_actionable_without_copying_raw_diagnostics()
    {
        using var installed = new FakeExecutable();
        var client = new CopilotTextClient((_, _) => Task.FromResult(new CopilotProcessResult(3, "private prompt secret-key")));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.SendAsync(installed.Path, "auto", "secret-key", "private prompt", TestContext.Current.CancellationToken));
        Assert.Contains("code 3", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("private prompt", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-key", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Bounded_output_is_checked_even_at_an_injected_process_boundary()
    {
        using var installed = new FakeExecutable();
        var client = new CopilotTextClient((_, _) => Task.FromResult(new CopilotProcessResult(0, new string('x', 524_289))));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SendAsync(installed.Path, "auto", "test-key", "reviewed text", TestContext.Current.CancellationToken));
    }

    [AvaloniaFact]
    public async Task Edited_reviewed_prompt_requires_fresh_consent_before_any_process_runs()
    {
        using var installed = new FakeExecutable(); using var fixture = new DesktopFixture(); var calls = 0;
        var client = new CopilotTextClient((_, _) => { calls++; return Task.FromResult(new CopilotProcessResult(0, "unexpected")); });
        var window = new CopilotConnectionWindow(client); window.Show(fixture.Window);
        try
        {
            window.GetVisualDescendants().OfType<Expander>().Single().IsExpanded = true;
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            var fields = window.GetVisualDescendants().OfType<TextBox>().ToArray();
            fields.Single(t => AutomationProperties.GetName(t) == "Installed native copilot.exe").Text = installed.Path;
            fields.Single(t => AutomationProperties.GetName(t)?.StartsWith("GitHub token", StringComparison.Ordinal) == true).Text = "artificial-test-key";
            var prompt = fields.Single(t => AutomationProperties.GetName(t) == "Exact reviewed text to send"); prompt.Text = "First reviewed text";
            Dispatcher.UIThread.RunJobs();
            var consent = window.GetVisualDescendants().OfType<CheckBox>().Single(); consent.IsChecked = true;
            var send = window.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Send reviewed text")); Assert.True(send.IsEnabled);
            prompt.Text = "Changed text is not approved";
            send.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs(); await Task.Delay(30, TestContext.Current.CancellationToken);
            Assert.Equal(0, calls); Assert.False(consent.IsChecked);
            Assert.DoesNotContain(send.GetVisualAncestors(), ancestor => ancestor is ScrollViewer);
        }
        finally { window.Close(); }
    }

    private sealed class FakeExecutable : IDisposable
    {
        private readonly string _root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tern-copilot-test-" + Guid.NewGuid().ToString("N"));
        public FakeExecutable() { Directory.CreateDirectory(_root); Path = System.IO.Path.Combine(_root, "copilot.exe"); File.WriteAllText(Path, "This is a fake test boundary, never an executable invocation."); }
        public string Path { get; }
        public void Dispose() { File.Delete(Path); Directory.Delete(_root); }
    }
}
