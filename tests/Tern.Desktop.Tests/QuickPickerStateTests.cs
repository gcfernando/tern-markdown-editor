using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Tern.Desktop.Views;

namespace Tern.Desktop.Tests;

public sealed class QuickPickerStateTests
{
    [AvaloniaFact]
    public async Task Pending_rows_cannot_be_accepted_and_late_results_cannot_replace_the_new_query()
    {
        var picker = new QuickPicker();
        var window = new Window { Width = 800, Height = 600, Content = picker };
        window.Show();
        var old = new TaskCompletionSource<IReadOnlyList<PickerItem>>();
        var current = new TaskCompletionSource<IReadOnlyList<PickerItem>>();
        var accepted = new List<PickerItem>();
        picker.Open("Search synthetic notes", "old", _ => [], accepted.Add, () => { },
            asyncProvider: (q, _) => q == "old" ? old.Task : current.Task);
        await Task.Delay(130);
        var oldWork = picker.QueryWork;
        Assert.Equal(PickerPresentationState.Searching, picker.State);
        picker.Input.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        Assert.Empty(accepted);
        picker.Input.Text = "current";
        await Task.Delay(130);
        current.SetResult([new("Current result", "Current snippet", "", null)]);
        await picker.QueryWork;
        Assert.Equal(PickerPresentationState.Results, picker.State);
        old.SetResult([new("Stale result", "", "", null)]);
        await oldWork;
        Assert.Equal("Current result", Assert.IsType<PickerItem>(picker.Results.SelectedItem).Title);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Fault_and_workspace_lifetime_are_distinct_from_completed_empty_results()
    {
        var picker = new QuickPicker();
        var current = true;
        picker.Open("Workspace content", "word", _ => [], _ => throw new Exception("Must not accept a stale result"), () => { },
            asyncProvider: (_, _) => Task.FromException<IReadOnlyList<PickerItem>>(new IOException("Index cannot be read")), isCurrent: () => current);
        await picker.QueryWork;
        Assert.Equal(PickerPresentationState.Error, picker.State);
        picker.Open("Workspace content", "word", _ => [new("Result", "", "", null)], _ => { }, () => { }, isCurrent: () => current);
        current = false;
        picker.InvalidateScopeIfChanged();
        Assert.Equal(PickerPresentationState.Error, picker.State);
        Assert.Equal(0, picker.Results.ItemCount);
    }
}
