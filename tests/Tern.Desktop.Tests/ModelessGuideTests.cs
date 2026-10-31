using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Tern.App.Commands;
using Tern.Desktop.Views;

namespace Tern.Desktop.Tests;

public sealed class ModelessGuideTests
{
    [AvaloniaFact]
    public async Task Opening_help_returns_to_the_document_and_reuses_the_current_lesson()
    {
        using var fixture = new DesktopFixture();
        var tab = fixture.Shell.NewDocument("Keep my writing");
        var opening = fixture.Window.RunCommandAsync(CommandIds.Help);
        Assert.True(opening.IsCompleted, "The guide must not block editing while a lesson is open.");
        await opening;
        var guide = Assert.Single(fixture.Window.OwnedWindows.OfType<HelpWindow>());
        try
        {
            var step = guide.GetVisualDescendants().OfType<TextBlock>().Single(t => AutomationProperties.GetName(t) == "Tutorial step");
            guide.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Next"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var currentStep = step.Text;
            await fixture.Window.RunCommandAsync(CommandIds.Help);
            Assert.Same(guide, Assert.Single(fixture.Window.OwnedWindows.OfType<HelpWindow>()));
            Assert.Equal(currentStep, step.Text);
            Assert.Equal("Keep my writing", tab.CurrentText);
            Assert.True(guide.IsVisible);
        }
        finally { guide.Close(); }
    }
}
