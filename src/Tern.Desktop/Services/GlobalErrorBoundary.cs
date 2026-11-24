using Avalonia.Threading;
using Tern.App.Shell;

namespace Tern.Desktop.Services;

/// <summary>
/// Last-resort handlers (B-01): an unexpected exception becomes an Error banner instead of a crash, and a fatal one
/// still gets a bounded recovery flush so unsaved text survives.
/// </summary>
internal sealed class GlobalErrorBoundary : IDisposable
{
    private readonly ShellViewModel _shell;

    public GlobalErrorBoundary(ShellViewModel shell)
    {
        _shell = shell;
        Dispatcher.UIThread.UnhandledException += OnUiThreadException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTask;
        AppDomain.CurrentDomain.UnhandledException += OnFatal;
    }

    internal void HandleUiException(Exception exception, out bool handled)
    {
        handled = true;
        if (exception is OperationCanceledException) return;
        _shell.ReportUnexpectedError("an unexpected error occurred", exception);
    }

    private void OnUiThreadException(object? sender, DispatcherUnhandledExceptionEventArgs e)
    {
        HandleUiException(e.Exception, out var handled);
        e.Handled = handled;
    }

    private void OnUnobservedTask(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        e.SetObserved();
        if (e.Exception.InnerExceptions.All(inner => inner is OperationCanceledException)) return;
        _shell.ReportUnexpectedError("a background task failed", e.Exception.GetBaseException());
    }

    private void OnFatal(object? sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            try { _shell.ReportUnexpectedError("Tern must close", exception); } catch (Exception) { }
        }
        _shell.TryFlushRecoveryForCrash(TimeSpan.FromSeconds(3));
    }

    public void Dispose()
    {
        Dispatcher.UIThread.UnhandledException -= OnUiThreadException;
        TaskScheduler.UnobservedTaskException -= OnUnobservedTask;
        AppDomain.CurrentDomain.UnhandledException -= OnFatal;
    }
}
