namespace Tern.App.Tests;

using Tern.App.Shell;

public sealed class Oct29StatusTests
{
    // U-01 / SB-01 explicitly raises the minimum display time from 1.5 s to 6 s + 60 ms per character,
    // so these timing waits follow ShellViewModel.MinimumDisplayTime instead of the old 1.8 s.
    private static Task WaitPastMinimumAsync(string message) =>
        Task.Delay(ShellViewModel.MinimumDisplayTime(message) + TimeSpan.FromMilliseconds(1500));

    [Fact]
    public async Task Operation_scope_prevents_a_newer_action_from_showing_ready_until_work_finishes()
    {
        using var fixture = new ShellFixture();
        var operation = fixture.Shell.BeginStatusOperation();
        fixture.Shell.StatusMessage = "Loading a remote file…";
        fixture.Shell.StatusMessage = "Selected another document";
        await WaitPastMinimumAsync("Selected another document");
        Assert.Equal("Selected another document", fixture.Shell.StatusMessage);
        operation.Dispose();
        operation.Dispose(); // A duplicate dispose cannot release another active scope.
        fixture.Shell.StatusMessage = "Downloaded online file";
        await WaitPastMinimumAsync("Downloaded online file");
        Assert.Equal("Ready", fixture.Shell.StatusMessage);
    }

    [Fact]
    public async Task Completion_returns_ready_and_an_old_timer_cannot_replace_a_new_diagnostic()
    {
        using var fixture = new ShellFixture();
        fixture.Shell.StatusMessage = "Saved example.md";
        await WaitPastMinimumAsync("Saved example.md");
        Assert.Equal("Ready", fixture.Shell.StatusMessage);
        fixture.Shell.StatusMessage = "Inserted table";
        await Task.Delay(500);
        fixture.Shell.StatusMessage = "Could not save: permission denied.";
        await WaitPastMinimumAsync("Could not save: permission denied.");
        Assert.Equal("Could not save: permission denied.", fixture.Shell.StatusMessage);
    }
}

