using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tern.Desktop.Views;

namespace Tern.Desktop.Tests;

public sealed class ResetActiveOperationWindowTests
{
    [AvaloniaFact]
    public void Pending_status_only_download_disables_reset_with_explanation_and_completion_refreshes_it_without_checkbox_toggling()
    {
        using var fixture = new DesktopFixture();
        using var operation = fixture.Shell.BeginStatusOperation();
        var window = new ResetWindow(fixture.Shell);
        window.Show(fixture.Window);
        try
        {
            Pump(window);
            var remote = Assert.Single(window.GetVisualDescendants().OfType<CheckBox>(), box => AutomationProperties.GetName(box) == "Cached online documents and assets");
            remote.IsChecked = true;
            Pump(window);
            var reset = Assert.Single(window.GetVisualDescendants().OfType<Button>(), button => AutomationProperties.GetName(button) == "Reset selected application data");
            var status = Assert.Single(window.GetVisualDescendants().OfType<TextBlock>(), text => AutomationProperties.GetName(text) == "Reset selection status");
            Assert.False(reset.IsEnabled);
            Assert.Contains("download", status.Text, StringComparison.Ordinal);
            operation.Dispose();
            Pump(window);
            Assert.True(reset.IsEnabled);
            Assert.True(remote.IsChecked);
            Assert.Contains("2 categories selected", status.Text, StringComparison.Ordinal);
        }
        finally { window.Close(); }
    }

    private static void Pump(Window window)
    { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
}
