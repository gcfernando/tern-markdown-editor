using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tern.Desktop.Views;

namespace Tern.Desktop.Tests;

public sealed class PromptSafetyTests
{
    [AvaloniaFact]
    public void Short_message_uses_content_height_without_unnecessary_scrolling()
    {
        var prompt = new PromptWindow("Confirm", "Changed on disk.", ["Compare", "Cancel"], 1, 1);
        try
        {
            prompt.Show();
            prompt.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            var scroller = Assert.Single(prompt.GetVisualDescendants().OfType<ScrollViewer>(), s => s.Content is SelectableTextBlock);
            Assert.True(scroller.Extent.Height <= scroller.Viewport.Height + 1);
            Assert.True(prompt.ClientSize.Height < 300);
        }
        finally { prompt.Close(); }
    }

    [AvaloniaTheory]
    [InlineData("close")]
    [InlineData("escape")]
    [InlineData("owner")]
    public async Task Dismissal_never_selects_a_conflict_action(string dismissal)
    {
        var owner = new Window { Width = 700, Height = 500 };
        owner.Show();
        var pending = PromptWindow.AskAsync(owner, "External changes", "Choose how to handle changed disk content.",
            ["Keep mine", "Use disk", "Merge", "Save copy", "Cancel"], 2, 4);
        var prompt = Assert.Single(owner.OwnedWindows.OfType<PromptWindow>());
        try
        {
            if (dismissal == "escape")
                prompt.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
            else if (dismissal == "owner") owner.Close();
            else prompt.Close();
            Assert.Equal(4, await pending.WaitAsync(TimeSpan.FromSeconds(3)));
        }
        finally { prompt.Close(); owner.Close(); }
    }

    [AvaloniaFact]
    public async Task Explicit_conflict_choice_preserves_its_index()
    {
        var owner = new Window { Width = 700, Height = 500 };
        owner.Show();
        var pending = PromptWindow.AskAsync(owner, "External changes", "Changed on disk.", ["Keep mine", "Use disk", "Cancel"], 2, 2);
        var prompt = Assert.Single(owner.OwnedWindows.OfType<PromptWindow>());
        try
        {
            var button = Assert.Single(prompt.GetVisualDescendants().OfType<Button>(), b => AutomationProperties.GetName(b) == "Use disk");
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(1, await pending.WaitAsync(TimeSpan.FromSeconds(3)));
        }
        finally { prompt.Close(); owner.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(360, 300)]
    [InlineData(460, 360)]
    public void Long_conflict_message_keeps_all_actions_inside_a_bounded_dialog(double width, double height)
    {
        var prompt = new PromptWindow("External changes", string.Join(" ", Enumerable.Repeat("Changed document at a long folder path.", 80)),
            ["Keep mine", "Use disk", "Merge", "Save copy", "Cancel"], 2, 4)
        { Width = width, MaxHeight = height };
        try
        {
            prompt.Show();
            prompt.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            var buttons = prompt.GetVisualDescendants().OfType<Button>().Where(b => b.Content is string).ToArray();
            Assert.Equal(5, buttons.Length);
            foreach (var button in buttons)
            {
                var origin = button.TranslatePoint(default, prompt)!.Value;
                Assert.True(origin.Y >= 0 && origin.Y + button.Bounds.Height <= prompt.ClientSize.Height + 1,
                    $"Action {button.Content} is clipped below the dialog.");
                Assert.True(origin.X >= 0 && origin.X + button.Bounds.Width <= prompt.ClientSize.Width + 1);
            }
            var messageScroller = Assert.Single(prompt.GetVisualDescendants().OfType<ScrollViewer>(), s => s.Content is SelectableTextBlock);
            Assert.True(messageScroller.Extent.Height > messageScroller.Viewport.Height);
        }
        finally { prompt.Close(); }
    }
}
