using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Tern.App.Shell;
using Tern.Desktop.Views;

namespace Tern.Desktop.Tests;

public sealed partial class AIWritingWorkflowTests
{
    [AvaloniaFact]
    public async Task Plain_folder_explains_the_next_step_and_one_click_makes_it_searchable_keeping_the_question()
    {
        using var f = Fixture((_, _) => Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)));
        await f.WriteAsync("launch.md", "# Launch\n\nWe decided to launch in spring.\n");
        Assert.True(await f.Shell.OpenFolderAsync(f.Docs));
        f.Window.ShowKnowledgeAssistant();
        var window = Assert.Single(f.Window.OwnedWindows.OfType<KnowledgeAssistantWindow>());
        var panel = window.Assistant;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Make this folder ready", NamedControl<TextBlock>(panel, "Next step").Text);
        Assert.Contains("not moved or changed", NamedControl<TextBlock>(panel, "Next step details").Text);
        var action = NamedControl<Button>(panel, "Next step action");
        Assert.True(action.IsVisible);
        Assert.Equal("Make this folder ready", action.Content);
        NamedControl<TextBox>(panel, "Your question").Text = "What did we decide about launch?";
        Dispatcher.UIThread.RunJobs();
        Assert.False(NamedControl<Button>(panel, "Find my notes").IsEnabled);

        action.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        for (var attempt = 0; attempt < 1500 && f.Shell.Workspace is not { IsNamedWorkspace: true, IsContentReady: true }; attempt++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }
        for (var attempt = 0; attempt < 200 && !NamedControl<Button>(panel, "Find my notes").IsEnabled; attempt++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }

        Assert.True(f.Shell.Workspace!.IsNamedWorkspace);
        Assert.Equal("What did we decide about launch?", NamedControl<TextBox>(panel, "Your question").Text);
        Assert.True(NamedControl<Button>(panel, "Find my notes").IsEnabled);
        Assert.False(NamedControl<TextBlock>(panel, "Next step").IsEffectivelyVisible);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Without_any_AI_the_feature_redirects_to_the_existing_AI_setup_screen()
    {
        using var f = new DesktopFixture();
        f.Window.Show();
        f.Window.ShowKnowledgeAssistant();
        for (var attempt = 0; attempt < 100 && !f.Window.OwnedWindows.OfType<AIConnectionsWindow>().Any(); attempt++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }
        Assert.Empty(f.Window.OwnedWindows.OfType<KnowledgeAssistantWindow>());
        var setup = Assert.Single(f.Window.OwnedWindows.OfType<AIConnectionsWindow>());
        setup.Close();
        for (var attempt = 0; attempt < 100 && !f.Shell.StatusMessage.Contains("needs an AI connection", StringComparison.Ordinal); attempt++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }
        Assert.Empty(f.Window.OwnedWindows.OfType<KnowledgeAssistantWindow>());
        Assert.Contains("needs an AI connection", f.Shell.StatusMessage);
    }

    private static T NamedControl<T>(Control root, string name) where T : Control =>
        root.GetLogicalDescendants().OfType<T>().Single(control => AutomationProperties.GetName(control) == name);
}

public sealed class UserProblemTests
{
    [Theory]
    [InlineData(typeof(UnauthorizedAccessException), "denied access")]
    [InlineData(typeof(DirectoryNotFoundException), "no longer at that location")]
    [InlineData(typeof(PathTooLongException), "longer than Windows allows")]
    [InlineData(typeof(TimeoutException), "took too long")]
    public void Errors_say_what_happened_why_and_what_to_do(Type type, string reason)
    {
        var problem = UserProblem.Explain((Exception)Activator.CreateInstance(type)!, "Could not open folder");
        Assert.StartsWith("Could not open folder.", problem.Text);
        Assert.Contains(reason, problem.Why);
        Assert.False(string.IsNullOrWhiteSpace(problem.Next));
    }

    [Theory]
    [InlineData("{\"type\":\"session.start\",\"data\":{}}\n{\"type\":\"assistant.message\",\"data\":{\"content\":\"One long line\"}}\n{\"type\":\"result\"}", "One long line")]
    [InlineData("Plain reply from an older CLI", "Plain reply from an older CLI")]
    public void Copilot_reply_uses_the_exact_message_text(string output, string expected) =>
        Assert.Equal(expected, Tern.Desktop.Services.CopilotTextClient.ExtractReply(output));

    [Fact]
    public void Sharing_and_full_disk_errors_are_recognised_and_private_messages_stay_out_of_visible_text()
    {
        Assert.Contains("Another program", UserProblem.Explain(new IOException("x", unchecked((int)0x80070020)), "Could not save").Why);
        Assert.Contains("drive is full", UserProblem.Explain(new IOException("x", unchecked((int)0x80070070)), "Could not save").Why);
        var hidden = UserProblem.Explain(new InvalidOperationException(@"C:\private\secret.md"), "Something went wrong", includeMessage: false);
        Assert.DoesNotContain("secret", hidden.Text);
    }
}
