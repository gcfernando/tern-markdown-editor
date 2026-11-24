using Tern.App.Settings;
using Tern.App.Shell;

namespace Tern.App.Tests;

public sealed class SingleInstanceCoordinatorTests
{
    [Fact]
    public async Task Secondary_launches_queue_arguments_until_the_primary_handler_is_ready()
    {
        using var root = new TempDir();
        var paths = AppPaths.Under(root.File("appdata"));
        using var primary = new PrimaryHost(paths);
        using var secondary = new SingleInstanceCoordinator(paths);
        Assert.False(secondary.IsPrimary);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var first = root.File("first note.md");
        var second = root.File("second.md");
        await secondary.ForwardAsync([first, second], timeout.Token);
        await secondary.ForwardAsync([], timeout.Token);
        var received = new List<IReadOnlyList<string>>();
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        primary.Coordinator.SetArgumentHandler(arguments =>
        {
            received.Add(arguments);
            if (received.Count == 2) completed.SetResult();
            return Task.CompletedTask;
        }, error => completed.TrySetException(error));
        await completed.Task.WaitAsync(timeout.Token);
        Assert.Equal(new[] { first, second }, received[0]);
        Assert.Empty(received[1]);
    }

    [Fact]
    public async Task Separate_data_roots_are_isolated_and_handler_failures_are_reported_without_losing_later_requests()
    {
        using var root = new TempDir();
        var paths = AppPaths.Under(root.File("one"));
        using var primary = new PrimaryHost(paths);
        using var other = new PrimaryHost(AppPaths.Under(root.File("two")));
        using var secondary = new SingleInstanceCoordinator(paths);
        var reported = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        primary.Coordinator.SetArgumentHandler(_ =>
        {
            if (++calls == 1) throw new IOException("Synthetic open failure");
            handled.SetResult();
            return Task.CompletedTask;
        }, error => reported.TrySetResult(error));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await secondary.ForwardAsync([], timeout.Token);
        Assert.Equal("Synthetic open failure", (await reported.Task.WaitAsync(timeout.Token)).Message);
        await secondary.ForwardAsync([], timeout.Token);
        await handled.Task.WaitAsync(timeout.Token);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Settings_writes_do_not_share_a_fixed_temp_file()
    {
        using var root = new TempDir();
        var store = new SettingsStore(root.Path);
        store.Save(new AppSettings());
        var oldTemp = store.FilePath + ".tmp";
        await File.WriteAllTextAsync(oldTemp, "unrelated existing temp");
        using (new FileStream(oldTemp, FileMode.Open, FileAccess.Read, FileShare.None))
            await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
                new SettingsStore(root.Path).Save(new AppSettings { Theme = ThemeChoice.Paper }))));
        Assert.Equal(ThemeChoice.Paper, store.Load().Theme);
        Assert.Null(store.LastLoadProblem);
        Assert.Equal("unrelated existing temp", await File.ReadAllTextAsync(oldTemp));
        Assert.Single(Directory.GetFiles(root.Path, "*.tmp"));
    }

    private sealed class PrimaryHost : IDisposable
    {
        private readonly ManualResetEventSlim _close = new();
        private readonly Thread _thread;
        private Exception? _failure;
        private readonly TaskCompletionSource<SingleInstanceCoordinator> _ready =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public SingleInstanceCoordinator Coordinator { get; }

        public PrimaryHost(AppPaths paths)
        {
            _thread = new Thread(() =>
            {
                try
                {
                    using var coordinator = new SingleInstanceCoordinator(paths);
                    if (!coordinator.IsPrimary) throw new InvalidOperationException("The isolated primary mutex was already owned.");
                    _ready.SetResult(coordinator);
                    _close.Wait();
                }
                catch (Exception error) { _failure = error; _ready.TrySetException(error); }
            }) { IsBackground = true };
            _thread.Start();
            Coordinator = _ready.Task.GetAwaiter().GetResult();
        }

        public void Dispose()
        {
            _close.Set();
            Assert.True(_thread.Join(TimeSpan.FromSeconds(5)), "The primary instance did not release its mutex and pipe.");
            Assert.Null(_failure);
            _close.Dispose();
        }
    }
}
