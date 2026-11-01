using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tern.Desktop.Views;

namespace Tern.Desktop.Tests;

public sealed class ConnectionWizardLayoutTests
{
    [AvaloniaFact]
    public void Wizard_keeps_its_viewport_when_steps_and_details_change()
    {
        var window = new Window();
        DialogParts.SetupWizard(window, "Connection setup");
        var text = GuideParts.Body("Choose a service.");
        var details = new Expander { Header = "Details", Content = GuideParts.Body(new string('x', 4000)) };
        window.Content = GuideParts.Frame(GuideParts.Title("Connection setup"),
            new ScrollViewer { Content = GuideParts.Vertical(text, details) }, GuideParts.Actions(new Button { Content = "Next" }));
        window.Show();
        try
        {
            Pump(window);
            var originalHeight = window.Height;
            text.Text = string.Join('\n', Enumerable.Repeat("A longer configuration step", 40));
            details.IsExpanded = true;
            Pump(window);
            Assert.Equal(originalHeight, window.Height);
            details.IsExpanded = false;
            text.Text = "Ready.";
            Pump(window);
            Assert.Equal(originalHeight, window.Height);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Nested_connection_setup_matches_owner_size_and_marks_current_step()
    {
        var owner = new Window();
        DialogParts.SetupWizard(owner, "AI connections");
        owner.Width = 840;
        owner.Height = 680;
        owner.Show();
        var child = new Window();
        DialogParts.SetupWizard(child, "GitHub Copilot");
        var steps = GuideParts.ConnectionSteps(1, "Choose service", "Configure", "Validate and save");
        child.Content = steps;
        child.Show(owner);
        try
        {
            Pump(owner); Pump(child);
            Assert.Equal(owner.Width, child.Width);
            Assert.Equal(owner.Height, child.Height);
            Assert.Equal(3, steps.ColumnDefinitions.Count);
            var current = Assert.Single(steps.Children.OfType<Border>(), b => b.Classes.Contains("current"));
            Assert.Contains("Step 2: Configure, current", AutomationProperties.GetName(current));
            Assert.Single(steps.Children.OfType<Border>(), b => b.Classes.Contains("complete"));
        }
        finally { child.Close(); owner.Close(); }
    }

    private static void Pump(Window window)
    {
        for (var i = 0; i < 5; i++) { window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
    }
}
