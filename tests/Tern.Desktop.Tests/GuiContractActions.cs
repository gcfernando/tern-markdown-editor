using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace Tern.Desktop.Tests;

internal static class GuiContractActions
{
    internal static void InvokeFileReferenceAction(Button button, Window host)
    {
        var menu = Assert.IsType<MenuFlyout>(button.Flyout);
        menu.ShowAt(button); host.UpdateLayout(); Dispatcher.UIThread.RunJobs();
        var action = Assert.Single(menu.Items.OfType<MenuItem>());
        Assert.Contains("forget", action.Header!.ToString()!, StringComparison.OrdinalIgnoreCase);
        action.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        menu.Hide(); Dispatcher.UIThread.RunJobs(); host.UpdateLayout();
    }
}
