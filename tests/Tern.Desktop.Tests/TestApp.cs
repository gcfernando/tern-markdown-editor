using Avalonia;
using Avalonia.Headless;
using Tern.Desktop.Tests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace Tern.Desktop.Tests;

public static class TestAppBuilder
{
    // The real application class, so the tests see the same theme and styles as the shipped app.
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<Tern.Desktop.App>()
        .WithInterFont()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
