namespace Tern.App.Shell;

public sealed partial class ShellViewModel
{
    private int _statusGeneration;
    private int _activeStatusOperations;
    public bool HasActiveStatusOperations => Volatile.Read(ref _activeStatusOperations) > 0;

    /// <summary>Keeps the status active across asynchronous work, including nested remote operations.</summary>
    public IDisposable BeginStatusOperation()
    {
        if (Interlocked.Increment(ref _activeStatusOperations) == 1)
            OnPropertyChanged(nameof(HasActiveStatusOperations));
        return new StatusOperation(this);
    }

    private sealed class StatusOperation(ShellViewModel owner) : IDisposable
    {
        private ShellViewModel? _owner = owner;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _owner, null) is { } shell)
            {
                if (Interlocked.Decrement(ref shell._activeStatusOperations) == 0)
                    shell.OnPropertyChanged(nameof(HasActiveStatusOperations));
            }
        }
    }

    partial void OnStatusMessageChanged(string value)
    {
        var generation = ++_statusGeneration;
        if (string.IsNullOrWhiteSpace(value) || value == "Ready" || IsActionableStatus(value)) return;
        _ = ReturnToReadyAsync(generation);
    }

    // Failures remain available until another action replaces them. The existing
    // status API accepts text; preserve diagnostics rather than expiring them.
    private static bool IsActionableStatus(string value) => new[]
    {
        "could not", "cannot", "failed", "unavailable", "error", "denied", "not found",
        "changed while", "changed during", "try again", "review", "missing", "exceeds",
    }.Any(word => value.Contains(word, StringComparison.OrdinalIgnoreCase));

    private async Task ReturnToReadyAsync(int generation)
    {
        while (!_disposed && generation == _statusGeneration)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(1500)).ConfigureAwait(true);
            if (_disposed || generation != _statusGeneration) return;
            if (IsBusy || IsIndexing || Volatile.Read(ref _activeStatusOperations) > 0 || StatusMessage.EndsWith('…')) continue;
            StatusMessage = "Ready";
            return;
        }
    }
}
