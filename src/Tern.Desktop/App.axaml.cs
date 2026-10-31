using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Tern.App.Shell;
using Tern.Desktop.Services;
using Tern.Desktop.Themes;
using Tern.Desktop.Views;

namespace Tern.Desktop;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var dialogs = new DesktopDialogs();
            var shell = new ShellViewModel(AppPaths.Default(), dialogs, new SystemLauncher());
            ThemeManager.Apply(this, shell.Settings.Theme);

            var window = new MainWindow(shell, dialogs, new RemoteImageLoader());
            var args = desktop.Args ?? [];
            window.Opened += async (_, _) =>
            {
                StartupMeasurements.Mark("window-opened");
                Dispatcher.UIThread.Post(() => StartupMeasurements.Mark("first-render-dispatch"), DispatcherPriority.Background);
                // Let the initial window render before session I/O or recovery inspection.
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                await Startup.RunAsync(window, shell, args);
                await Dispatcher.UIThread.InvokeAsync(() => StartupMeasurements.Mark("startup-complete-dispatch"), DispatcherPriority.Background);
            };
            desktop.MainWindow = window;
            desktop.Exit += (_, _) => shell.Dispose();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
