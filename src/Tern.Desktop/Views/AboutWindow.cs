using System.Reflection;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Layout;

namespace Tern.Desktop.Views;

public sealed class AboutWindow : Window
{
    /// <summary>Components shipped with Tern. Licences are those of the published packages.</summary>
    public static IReadOnlyList<(string Name, string License)> ThirdParty { get; } =
    [
        ("Avalonia UI", "MIT"),
        ("AvaloniaEdit", "MIT"),
        ("Inter typeface (Avalonia.Fonts.Inter)", "SIL Open Font License 1.1"),
        ("SkiaSharp and HarfBuzzSharp", "MIT"),
        ("Markdig", "BSD 2-Clause"),
        ("CommunityToolkit.Mvvm", "MIT"),
        ("Microsoft.Data.Sqlite and SQLite", "MIT / Public domain"),
        ("PDFsharp", "MIT"),
    ];

    /// <summary>The level comes from the result. A folder called Error Reports must not turn a success red.</summary>
    internal static (MessageTone Tone, string Text) OpenResult(string? problem, string path) =>
        problem is null ? (MessageTone.Success, "Opened " + path) : (MessageTone.Error, problem);

    public AboutWindow()
    {
        DialogParts.Setup(this, "About Tern", 560, 560, DialogGuidance.About);
        MinHeight = 360;
        var version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "1.0.6";
        var close = new Button { Content = "Close", IsDefault = true, IsCancel = true };
        close.Classes.Add("accent");
        AutomationProperties.SetName(close, "Close about");
        close.Click += (_, _) => Close();
        var licenses = new StackPanel { [!StackPanel.SpacingProperty] = GuideParts.Space(8).ToBinding() };
        foreach (var component in ThirdParty)
            licenses.Children.Add(GuideParts.Body($"{component.Name} — {component.License}"));
        AutomationProperties.SetName(licenses, "Third-party licences");
        var details = new Expander
        {
            Header = "Licenses and privacy", IsExpanded = false, HorizontalAlignment = HorizontalAlignment.Stretch,
            Content = GuideParts.Vertical(
                GuideParts.Body("Tern keeps your documents local. Editing works offline. Open from URL downloads Markdown and its directly referenced supported files and images into a local cache for offline reopening. Other online image links in ordinary local documents load from the network when displayed."),
                GuideParts.Body("Usage is not reported. Tern does not check for updates or sync documents to a cloud service."),
                GuideParts.Body("Optional AI requests send only the content you explicitly review and approve."),
                GuideParts.Caption("Included components and their licences"), licenses),
        };
        AutomationProperties.SetName(details, "Third-party details");
        var header = GuideParts.Title("Tern");
        var local = new Button { Content = "Guides and licenses" };
        var localStatus = GuideParts.Caption("");
        var localBar = new MessageBar(localStatus, followText: true);
        local.Click += async (_, _) =>
        {
            local.IsEnabled = false;
            try
            {
                var path = await Services.DistributionContent.LocationAsync();
                var (tone, text) = OpenResult(new Services.SystemLauncher().TryOpenFile(path), path);
                localBar.Show(tone, text);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.ComponentModel.Win32Exception)
            { localBar.Show(MessageTone.Error, "Could not open local documentation: " + Tern.App.Shell.UserProblem.Plain(ex)); }
            finally { local.IsEnabled = true; }
        };
        var body = GuideParts.Vertical(
            GuideParts.Caption("Version " + version), GuideParts.Body("Developed by Gehan Fernando"),
            GuideParts.Body("Write and read Markdown files on your computer. Use Split to see your writing beside its formatted preview, or Reading to focus on the finished document."),
            details, local, localBar);
        Content = GuideParts.Frame(header, new ScrollViewer { Content = body, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled }, GuideParts.Actions(close));
    }
}
