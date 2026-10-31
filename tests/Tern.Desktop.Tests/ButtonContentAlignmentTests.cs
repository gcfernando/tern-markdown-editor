using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tern.Desktop.Views;

namespace Tern.Desktop.Tests;

public sealed class ButtonContentAlignmentTests
{
    [AvaloniaTheory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void Dialog_action_labels_are_centered_inside_rendered_buttons_at_narrow_widths(double scale)
    {
        using var fixture = new DesktopFixture();
        fixture.Shell.Settings.UiScale = scale;
        fixture.Window.ApplySettings();
        Window[] windows =
        [
            new HelpWindow(fixture.Shell.Registry, openFeature: _ => Task.CompletedTask),
            new AboutWindow(),
            new AIConnectionsWindow(openCopilot: _ => Task.CompletedTask),
            new CopilotConnectionWindow(),
            new MCPConnectionsWindow(),
            new OpenUrlWindow(),
            new SettingsWindow(fixture.Shell.Settings, fixture.Shell.Registry),
            new ShortcutsWindow(fixture.Shell.Registry),
            new ResetWindow(fixture.Shell),
            new PromptWindow("Unsaved changes", "Save changes before closing this document?", ["Save", "Don't save", "Cancel"], 0, 2)
        ];
        var failures = new List<string>();
        try
        {
            foreach (var window in windows)
            {
                try
                {
                    window.Width = window.MinWidth;
                    window.Height = Math.Max(window.MinHeight, 700);
                    window.Show(fixture.Window);
                    Dispatcher.UIThread.RunJobs();
                    window.UpdateLayout();
                    Dispatcher.UIThread.RunJobs();
                    var buttons = window.GetVisualDescendants().OfType<Button>()
                        .Where(b => b is not CheckBox && b is not RadioButton && b.IsEffectivelyVisible && b.Content is string s && !string.IsNullOrWhiteSpace(s))
                        .ToArray();
                    Assert.NotEmpty(buttons);
                    foreach (var button in buttons)
                    {
                        try { AssertTextCentered(window, button, scale); }
                        catch (Xunit.Sdk.XunitException error) { failures.Add(error.Message); }
                    }
                    Capture(window, $"{window.GetType().Name.ToLowerInvariant()}-{(int)(scale * 100)}");
                    foreach (var disclosure in buttons.Where(b => b.Name == "ExpanderHeader"))
                    {
                        disclosure.BringIntoView();
                        Dispatcher.UIThread.RunJobs();
                        window.UpdateLayout();
                        Capture(window, $"{window.GetType().Name.ToLowerInvariant()}-disclosure-{(int)(scale * 100)}");
                    }
                }
                finally { window.Close(); }
            }
            Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
        }
        finally
        {
            fixture.Shell.Settings.UiScale = 1;
            fixture.Window.ApplySettings();
        }
    }
    [AvaloniaTheory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public async Task Status_action_labels_are_centered_at_each_text_scale(double scale)
    {
        using var fixture = new DesktopFixture();
        fixture.Shell.Settings.UiScale = scale;
        fixture.Window.ApplySettings();
        var path = await fixture.WriteAsync("status-alignment.md", "# Status alignment\r\n");
        await fixture.Shell.OpenFileAsync(path);
        fixture.Window.Width = 1000;
        Dispatcher.UIThread.RunJobs();
        fixture.Window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        foreach (var name in new[] { "StatusEncoding", "StatusLineEnding", "StatusProfile" })
        {
            var button = fixture.Window.FindControl<Button>(name);
            Assert.NotNull(button);
            Assert.True(button.IsEffectivelyVisible);
            Assert.IsType<string>(button.Content);
            AssertTextCentered(fixture.Window, button, scale);
        }
        Capture(fixture.Window, $"main-status-{(int)(scale * 100)}");
        fixture.Shell.Settings.UiScale = 1;
        fixture.Window.ApplySettings();
    }

    private static void Capture(Window window, string filename)
    {
        var folder = Path.Combine(Screenshots.Folder, "button-alignment");
        Directory.CreateDirectory(folder);
        using var bitmap = window.CaptureRenderedFrame();
        Assert.NotNull(bitmap);
        using var stream = File.Create(Path.Combine(folder, filename + ".png"));
        bitmap.Save(stream, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }

    private static void AssertTextCentered(Window window, Button button, double scale)
    {
                        var label = Assert.Single(button.GetVisualDescendants().OfType<TextBlock>(),
                            t => t.IsEffectivelyVisible && t.Text == (string)button.Content!);
                        Visual textArea = button;
                        // Fluent disclosure headers reserve a separate right-hand chevron column.
                        // Their label belongs in the text column, not over the chevron's space.
                        if (button.Name == "ExpanderHeader")
                        {
                            var presenter = Assert.Single(button.GetVisualDescendants().OfType<ContentPresenter>(),
                                p => p.Name == "PART_ContentPresenter");
                            var chevron = Assert.Single(button.GetVisualDescendants().OfType<Border>(),
                                b => b.Name == "ExpandCollapseChevronBorder");
                            var labelEnd = label.TranslatePoint(new Point(label.Bounds.Width, 0), button);
                            var chevronStart = chevron.TranslatePoint(default, button);
                            Assert.True(labelEnd.HasValue && chevronStart.HasValue);
                            Assert.True(labelEnd.Value.X <= chevronStart.Value.X,
                                $"{window.Title}: '{button.Content}' overlaps its disclosure chevron");
                            textArea = presenter;
                        }
                        var origin = label.TranslatePoint(default, textArea);
                        Assert.True(origin.HasValue, $"{window.Title}: {button.Content} has no rendered text origin");
                        // Measure the laid-out text itself, rather than accepting a centered outer button.
                        var textWidth = label.TextLayout.Width;
                        var textHeight = label.TextLayout.Height;
                        Assert.True(textWidth > 0 && textHeight > 0);
                        foreach (var line in label.TextLayout.TextLines)
                        {
                            var left = origin.Value.X + line.Start;
                            var right = left + line.Width;
                            var horizontalError = Math.Abs((left + right) / 2 - textArea.Bounds.Width / 2);
                            Assert.True(left >= -2 && right <= textArea.Bounds.Width + 2,
                                $"{window.Title}: '{button.Content}' at {scale:P0}: text line clips [{left:F2}, {right:F2}] outside width {textArea.Bounds.Width:F2}");
                            Assert.True(horizontalError <= 2,
                                $"{window.Title}: '{button.Content}' at {scale:P0}: horizontal text-center error {horizontalError:F2}");
                        }
                        var verticalError = Math.Abs(origin.Value.Y + textHeight / 2 - textArea.Bounds.Height / 2);
                        Assert.True(origin.Value.Y >= -2 && origin.Value.Y + textHeight <= textArea.Bounds.Height + 2,
                            $"{window.Title}: '{button.Content}' at {scale:P0}: text height clips");
                        Assert.DoesNotContain(label.TextLayout.TextLines, line => line.HasCollapsed);
                        Assert.True(verticalError <= 2,
                            $"{window.Title}: '{button.Content}' at {scale:P0}: vertical text-center error {verticalError:F2}");
    }
}
