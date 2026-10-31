using Avalonia;
using Avalonia.Headless;
using Avalonia.Themes.Fluent;
using Tern.PreviewView;
using Tern.PreviewView.Tests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace Tern.PreviewView.Tests;

public sealed class TestApp : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        Resources.MergedDictionaries.Add(PreviewTheme.Midnight());
    }
}

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<TestApp>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
