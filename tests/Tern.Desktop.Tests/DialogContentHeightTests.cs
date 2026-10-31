using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Tern.App.Settings;
using Tern.Desktop.Views;

namespace Tern.Desktop.Tests;

public sealed class DialogContentHeightTests
{
    [AvaloniaFact]
    public async Task Short_dialogs_fit_their_content_instead_of_the_requested_empty_height()
    {
        using var fixture = new DesktopFixture();
        fixture.Shell.Settings.Theme = ThemeChoice.Midnight;
        fixture.Window.ApplyTheme();
        Window[] dialogs =
        [
            new InsertTableWindow(),
            new ExportWindow("note.md", null, "# Note", Tern.Markdown.MarkdownProfile.Extended, null, ExportFormat.Html),
            new AboutWindow(), new ResetWindow(fixture.Shell), new OpenUrlWindow(),
            new PromptWindow("Confirm", "Keep this note?", ["Keep", "Cancel"], 0, 1),
            new SettingsWindow(new AppSettings()), new ShortcutsWindow(fixture.Shell.Registry), new HelpWindow(fixture.Shell.Registry),
        ];
        foreach (var dialog in dialogs)
        {
            try
            {
                dialog.Show(fixture.Window);
                await Task.Delay(100);
                Pump(dialog);
                var content = Assert.IsAssignableFrom<Control>(dialog.Content);
                content.Measure(new Size(dialog.ClientSize.Width, double.PositiveInfinity));
                var natural = Math.Ceiling(content.DesiredSize.Height);
                Assert.True(dialog.ClientSize.Height <= natural + 2,
                    $"{dialog.Title} leaves {dialog.ClientSize.Height - natural:F0}px beyond its content.");
                Assert.True(dialog.CanResize);
                Pump(dialog);
                if (dialog is InsertTableWindow) Capture(dialog, "insert-table-compact.png");
            }
            finally { dialog.Close(); }
        }
    }

    [AvaloniaFact]
    public async Task Table_validation_grows_and_shrinks_without_clipping_actions_or_leaving_blank_rows()
    {
        using var fixture = new DesktopFixture();
        fixture.Shell.Settings.Theme = ThemeChoice.Midnight;
        fixture.Window.ApplyTheme();
        var dialog = new InsertTableWindow();
        try
        {
            dialog.Show(fixture.Window); await Task.Delay(100); Pump(dialog);
            var cleanHeight = dialog.ClientSize.Height;
            Assert.True(cleanHeight < 260);
            dialog.RowsInput.Text = "0"; await Task.Delay(100); Pump(dialog);
            Assert.False(dialog.InsertButton.IsEnabled);
            Assert.True(dialog.ClientSize.Height > cleanHeight);
            var point = dialog.InsertButton.TranslatePoint(default, dialog)!.Value;
            Assert.InRange(point.Y + dialog.InsertButton.Bounds.Height, 0, dialog.ClientSize.Height - 12);
            Capture(dialog, "insert-table-validation.png");
            dialog.RowsInput.Text = "3"; Pump(dialog);
            Assert.True(dialog.InsertButton.IsEnabled);
            Assert.InRange(Math.Abs(dialog.ClientSize.Height - cleanHeight), 0, 2);
        }
        finally { dialog.Close(); }
    }

    private static void Pump(Window window)
    {
        for (var i = 0; i < 4; i++) { window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
    }

    private static void Capture(Window window, string name)
    {
        var directory = Path.Combine(Screenshots.Folder, "compact-dialogs");
        Directory.CreateDirectory(directory);
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        using var output = File.Create(Path.Combine(directory, name));
        frame.Save(output, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }
}
