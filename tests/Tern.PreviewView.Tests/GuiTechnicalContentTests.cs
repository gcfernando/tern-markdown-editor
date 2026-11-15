using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Tern.Rendering.Preview;

namespace Tern.PreviewView.Tests;

public sealed class GuiTechnicalContentTests
{
    [AvaloniaFact]
    public void Larger_diagram_reuses_layout_and_supports_zoom_fit_reset_and_keyboard_exit()
    {
        var document = Harness.Build("```mermaid\nflowchart LR\n A[Long descriptive first step] --> B[Review the result]\n```");
        var (window, preview) = Harness.Show(document);
        try
        {
            var open = Harness.All<Button>(preview).Single(b => Equals(b.Content, "Open larger view"));
            open.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var larger = Assert.IsType<DiagramWindow>(Assert.Single(window.OwnedWindows)); Harness.Pump(larger);
            var diagram = Assert.Single(Harness.All<DiagramControl>(larger));
            void Click(string label) => Harness.All<Button>(larger).Single(b => Equals(b.Content, label)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Click("Zoom in"); Assert.True(diagram.Zoom > 1);
            Click("Zoom out"); Assert.Equal(1, diagram.Zoom);
            Click("Fit width"); Assert.Null(diagram.Zoom);
            Click("Reset"); Assert.Equal(1, diagram.Zoom);
            larger.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
            Assert.Empty(window.OwnedWindows); Assert.Same(document, preview.Document);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Unsupported_diagram_exposes_source_and_a_current_source_line_action()
    {
        var (window, preview) = Harness.Show(Harness.Build("# Example\n\n```mermaid\nunsupportedDiagram abc\n```"));
        try
        {
            int? line = null; preview.SourceLineActivated += (_, e) => line = e.Line;
            var code = Assert.Single(Harness.All<CodeBlockView>(preview));
            Assert.Contains("unsupportedDiagram abc", code.Code, StringComparison.Ordinal);
            Harness.All<Button>(preview).Single(b => Equals(b.Content, "Edit source")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(3, line);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Task_markers_in_an_informational_preview_cannot_request_mutation()
    {
        var document = Harness.Build("- [ ] A task\n");
        var (window, preview) = Harness.Show(document);
        try
        {
            var called = false; preview.TaskActivated += (_, _) => called = true;
            var marker = Assert.Single(Harness.All<CheckBox>(preview));
            Assert.False(preview.CanToggleTasks); Assert.False(marker.IsEnabled);
            marker.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.False(called); Assert.Same(document, preview.Document);
        }
        finally { window.Close(); }
    }
}
