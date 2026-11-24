using System.ComponentModel;

namespace Tern.Workspaces.Tests;

public sealed class GitAwarenessStopTests
{
    public static TheoryData<Exception> ExitRaces => new()
    {
        new InvalidOperationException("Synthetic: the process already exited."),
        new Win32Exception(5),
        new AggregateException(new Win32Exception(5)),
    };

    [Theory]
    [MemberData(nameof(ExitRaces))]
    public void Git_exiting_during_the_kill_does_not_replace_the_real_result(Exception race)
    {
        var attempts = 0;
        GitAwareness.StopIfRunning(() => false, () => { attempts++; throw race; });
        Assert.Equal(1, attempts);
    }

    [Fact]
    public void Exit_check_failure_is_treated_as_already_stopped()
    {
        var attempts = 0;
        GitAwareness.StopIfRunning(() => throw new InvalidOperationException("Synthetic: no process."), () => attempts++);
        Assert.Equal(0, attempts);
    }

    [Fact]
    public void Finished_git_is_not_killed_and_running_git_is()
    {
        var attempts = 0;
        GitAwareness.StopIfRunning(() => true, () => attempts++);
        Assert.Equal(0, attempts);
        GitAwareness.StopIfRunning(() => false, () => attempts++);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public void Unrelated_failures_are_not_hidden() =>
        Assert.Throws<NotSupportedException>(() => GitAwareness.StopIfRunning(() => false, () => throw new NotSupportedException()));
}
