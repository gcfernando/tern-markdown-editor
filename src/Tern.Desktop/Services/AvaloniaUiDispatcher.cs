using Avalonia.Threading;
using Tern.App.Shell;

namespace Tern.Desktop.Services;

internal sealed class AvaloniaUiDispatcher : IUiDispatcher
{
    public bool CheckAccess() => Dispatcher.UIThread.CheckAccess();

    public void Post(Action action) => Dispatcher.UIThread.Post(action);

    public async Task YieldAsync() =>
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
}
