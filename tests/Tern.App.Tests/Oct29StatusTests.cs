namespace Tern.App.Tests;

public sealed class Oct29StatusTests
{
    [Fact]
    public async Task Operation_scope_prevents_a_newer_action_from_showing_ready_until_work_finishes()
    {
        using var fixture = new ShellFixture();
        var operation = fixture.Shell.BeginStatusOperation();
        fixture.Shell.StatusMessage = "Loading a remote file…";
        fixture.Shell.StatusMessage = "Selected another document";
        await Task.Delay(1800);
        Assert.Equal("Selected another document", fixture.Shell.StatusMessage);
        operation.Dispose();
        operation.Dispose(); // A duplicate dispose cannot release another active scope.
        fixture.Shell.StatusMessage = "Downloaded online file";
        await Task.Delay(1800);
        Assert.Equal("Ready", fixture.Shell.StatusMessage);
    }

    [Fact]
    public async Task Completion_returns_ready_and_an_old_timer_cannot_replace_a_new_diagnostic()
    {
        using var fixture = new ShellFixture();
        fixture.Shell.StatusMessage = "Saved example.md";
        await Task.Delay(1800);
        Assert.Equal("Ready", fixture.Shell.StatusMessage);
        fixture.Shell.StatusMessage = "Inserted table";
        await Task.Delay(500);
        fixture.Shell.StatusMessage = "Could not save: permission denied.";
        await Task.Delay(1800);
        Assert.Equal("Could not save: permission denied.", fixture.Shell.StatusMessage);
    }
}
