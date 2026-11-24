using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tern.App.Settings;
using Tern.Desktop.Themes;
using Tern.Desktop.Views;
using Tern.Domain.Merging;
using Tern.Markdown;

namespace Tern.Desktop.Tests;

public sealed class GuiDocumentationCaptureTests
{
    [AvaloniaTheory]
    [InlineData(ThemeChoice.Paper)]
    [InlineData(ThemeChoice.Midnight)]
    public async Task Capture_actual_task_views_with_synthetic_content(ThemeChoice theme)
    {
        using var f = new DesktopFixture();
        f.Shell.Settings.Theme = theme; f.Window.ApplyTheme(); f.Window.ApplySettings();
        var tab = f.Shell.NewDocument("# Practice note\n\nThis synthetic note stays unsaved.\n");
        var image = new InsertImageWindow(GuiRemainingFeatureTests.ImageBytes(), "diagram.png", @"C:\ExampleNotes\meeting.md", @"C:\ExamplePictures\diagram.png");
        image.Show(f.Window); Capture(image, "insert-image", theme); image.Close();
        var merge = new MergeWindow("meeting.md", new MergeResult([new ConflictRegion("Agenda\n", "Agenda and notes\n", "Updated agenda\n")]));
        merge.Show(f.Window); Capture(merge, "merge-review", theme); merge.Close();
        tab.RecoveryError = "Synthetic checkpoint failure: the recovery folder could not be written. Your open text is kept.";
        f.Window.GetVisualDescendants().OfType<Button>().Single(b => Avalonia.Automation.AutomationProperties.GetName(b) == "Document save and recovery details")
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Capture(f.Window, "recovery-details", theme); f.Window.HideOverlay(); tab.RecoveryError = null;
        var export = new ExportWindow("meeting.md", null, "# Meeting\n\n![Agenda chart](missing-chart.png)", MarkdownProfile.Gfm, null, ExportFormat.Html) { Height = 900 };
        export.Show(f.Window);
        var guiPack = Tern.Tests.Shared.TestOutput.Folder("gui-pack");
        Assert.True(await export.ExportAsync(Path.Combine(guiPack, $"synthetic-export-{theme}.html")));
        Assert.NotEmpty(export.Warnings);
        Assert.Single(export.GetVisualDescendants().OfType<Expander>()).IsExpanded = true;
        Capture(export, "export-warnings", theme); export.Close();
        using var tutor = new HelpTutorView(f.Window.AIWorkspace, f.Shell.Registry, () => Task.CompletedTask, null, null, _ => { }, null,
            () => "A selected practice excerpt");
        tutor.Question.Text = "How do I add a checklist and save my note?";
        var help = new Window { Title = "Ask & learn", Content = tutor, Width = 850, Height = 740 };
        help.Show(f.Window); Capture(help, "tutor-setup", theme); help.Close();
    }

    private static void Capture(Window window, string name, ThemeChoice theme)
    {
        for (var i = 0; i < 4; i++) { window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
        using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame);
        var folder = Path.Combine(Screenshots.Folder, "gui-pack"); Directory.CreateDirectory(folder);
        frame.Save(Path.Combine(folder, name + "-" + theme + ".png"));
    }
}
