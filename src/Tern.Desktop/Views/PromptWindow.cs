using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Tern.Desktop.Themes;

namespace Tern.Desktop.Views;

/// <summary>A small modal window that asks a question and answers with the index of the pressed button.</summary>
public sealed class PromptWindow : Window
{
    private readonly List<Button> _buttons = [];

    public PromptWindow(string title, string message, IReadOnlyList<string> buttons, int defaultIndex = 0, int cancelIndex = -1)
    {
        DialogParts.Setup(this, title, 520, 360);
        MaxHeight = 720;
        MinWidth = 360;
        MinHeight = 120;

        var row = new WrapPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, ItemSpacing = 8, LineSpacing = 8 };
        for (var i = 0; i < buttons.Count; i++)
        {
            var index = i;
            var button = new Button { Content = buttons[i], MinWidth = 96, MinHeight = 34 };
            AutomationProperties.SetName(button, buttons[i]);
            button.Click += (_, _) => Close(index);
            if (i == defaultIndex)
            {
                button.Classes.Add("accent");
                button.IsDefault = true;
            }
            if (i == cancelIndex) button.IsCancel = true;

            _buttons.Add(button);
            row.Children.Add(button);
        }

        var messageScroller = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new SelectableTextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
        };
        // The native title already names the decision. Keep the message and its
        // actions together without duplicate headings or generic instruction text.
        var frame = new Grid
        {
            Margin = new Thickness(20), RowDefinitions = new RowDefinitions("*,Auto"), RowSpacing = 16,
            Children = { messageScroller, row },
        };
        Grid.SetRow(row, 1);
        frame.SizeChanged += (_, _) =>
        {
            row.Measure(new Size(Math.Max(1, frame.Bounds.Width), double.PositiveInfinity));
            MinHeight = Math.Min(MaxHeight, row.DesiredSize.Height + 56
                + Math.Min(24, ((Control)messageScroller.Content!).DesiredSize.Height));
        };
        Content = frame;

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close(cancelIndex);
                e.Handled = true;
            }
        };
        Opened += (_, _) => (_buttons.Count > defaultIndex && defaultIndex >= 0 ? _buttons[defaultIndex] : _buttons.FirstOrDefault())?.Focus();
    }

    public static async Task<int> AskAsync(Window owner, string title, string message, IReadOnlyList<string> buttons, int defaultIndex = 0, int cancelIndex = -1)
    {
        var prompt = new PromptWindow(title, message, buttons, defaultIndex, cancelIndex);
        if (owner.Screens.ScreenFromWindow(owner) is { } screen)
        {
            prompt.MaxWidth = Math.Max(1, screen.WorkingArea.Width / screen.Scaling - 32);
            prompt.MaxHeight = Math.Min(prompt.MaxHeight, Math.Max(1, screen.WorkingArea.Height / screen.Scaling - 64));
            prompt.Width = Math.Min(prompt.Width, prompt.MaxWidth);
        }
        return await prompt.ShowDialog<int?>(owner) ?? cancelIndex;
    }
}

internal static class DialogStyle
{
    public static WindowIcon? AppIcon()
    {
        using var stream = Avalonia.Platform.AssetLoader.Open(new Uri("avares://Tern/Assets/tern.ico"));
        return new WindowIcon(stream);
    }
}
