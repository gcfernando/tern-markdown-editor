using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Tern.Domain.Text;

namespace Tern.Desktop.Views;

internal sealed class AIEditReview : Border
{
    private readonly List<Control> _tabStops = [];

    public AIEditReview(string original, string proposed, int offset, Func<IReadOnlyList<TextEdit>, bool> apply,
        Action close, Action<string> copy, Action back, bool insertResult = false, string? copyText = null)
    {
        Margin = new Thickness(24);
        Padding = new Thickness(22);
        MaxWidth = 1080;
        MaxHeight = 680;
        VerticalAlignment = VerticalAlignment.Center;
        CornerRadius = new CornerRadius(14);
        BorderThickness = new Thickness(1);
        this.Bind(BackgroundProperty, this.GetResourceObservable("Tern.Surface"));
        this.Bind(BorderBrushProperty, this.GetResourceObservable("Tern.Border"));
        var status = GuideParts.Body("");
        var changes = insertResult ? [TextEdit.Insert(offset, proposed)] : Changes(original, proposed, offset);
        var rows = new StackPanel { Spacing = 12 };
        if (changes.Count == 0) rows.Children.Add(GuideParts.Body("AI returned the same text."));
        foreach (var change in changes)
        {
            var before = Code(insertResult ? original : original.Substring(change.Start - offset, change.Length), "Original Markdown");
            var after = Code(copyText ?? change.NewText, "Proposed Markdown");
            var proposedLabel = GuideParts.Caption(insertResult ? "Result to insert" : "Result");
            proposedLabel.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("Tern.Accent"));
            var comparison = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,*"),
                RowDefinitions = new RowDefinitions("Auto,Auto"),
                ColumnSpacing = 12,
                RowSpacing = 8
            };
            comparison.Children.Add(GuideParts.Caption("Original"));
            Grid.SetRow(before, 1);
            comparison.Children.Add(before);
            Grid.SetColumn(proposedLabel, 1);
            Grid.SetColumn(after, 1);
            Grid.SetRow(after, 1);
            comparison.Children.Add(proposedLabel);
            comparison.Children.Add(after);
            _tabStops.AddRange([before, after]);
            rows.Children.Add(GuideParts.Card(comparison));
        }

        var applyButton = new Button { Content = "Apply", IsEnabled = changes.Count > 0, Classes = { "accent" } };
        var closeButton = new Button { Content = "Close" };
        var copyButton = new Button { Content = "Copy result" };
        var backButton = new Button { Content = "Back" };
        _tabStops.AddRange([backButton, copyButton, applyButton, closeButton]);
        AutomationProperties.SetName(backButton, "Back to AI instruction");
        backButton.Click += (_, _) => back();
        applyButton.Click += (_, _) =>
        {
            if (!applyButton.IsEnabled) return;
            if (apply(changes)) close();
            else
            {
                status.Text = "This suggestion is out of date. Reopen AI for the current version of the note.";
                applyButton.IsEnabled = false;
            }
        };
        closeButton.Click += (_, _) => close();
        copyButton.Click += (_, _) => copy(copyText ?? proposed);
        var scroll = new ScrollViewer { Content = rows };
        var footer = GuideParts.Vertical(status, GuideParts.Actions(backButton, copyButton, applyButton, closeButton));
        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), RowSpacing = 14 };
        grid.Children.Add(GuideParts.Vertical(GuideParts.Title("Review AI suggestion"),
            GuideParts.Caption(insertResult ? "Compare the original text with the result. Apply inserts the result and keeps the original text."
                : "Compare the original with the suggestion. Apply uses all proposed changes.")));
        Grid.SetRow(scroll, 1);
        grid.Children.Add(scroll);
        Grid.SetRow(footer, 2);
        grid.Children.Add(footer);
        Child = grid;
        Loaded += (_, _) =>
        {
            if (!IsKeyboardFocusWithin) MoveTabFocus(backward: false);
        };
    }

    internal void MoveTabFocus(bool backward)
    {
        var available = _tabStops.Where(control => control.IsEffectivelyVisible && control.IsEffectivelyEnabled).ToArray();
        if (available.Length == 0) return;
        var current = Array.FindIndex(available, control => control.IsKeyboardFocusWithin);
        var next = current < 0
            ? backward ? available.Length - 1 : 0
            : (current + (backward ? -1 : 1) + available.Length) % available.Length;
        available[next].Focus(NavigationMethod.Tab, backward ? KeyModifiers.Shift : KeyModifiers.None);
        available[next].BringIntoView();
    }

    private static TextBox Code(string text, string name)
    {
        var box = new TextBox { Text = text, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            MaxHeight = 260, VerticalAlignment = VerticalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Top };
        AutomationProperties.SetName(box, name);
        return box;
    }

    internal static IReadOnlyList<TextEdit> Changes(string original, string proposed, int offset)
    {
        if (original == proposed) return [];
        var before = Lines(original);
        var after = Lines(proposed);
        if ((long)(before.Length + 1) * (after.Length + 1) > 1_000_000)
            return [new TextEdit(offset, original.Length, proposed)];

        var common = new int[before.Length + 1, after.Length + 1];
        for (var i = before.Length - 1; i >= 0; i--)
            for (var j = after.Length - 1; j >= 0; j--)
                common[i, j] = before[i] == after[j] ? 1 + common[i + 1, j + 1] : Math.Max(common[i + 1, j], common[i, j + 1]);

        var edits = new List<TextEdit>();
        var left = 0;
        var right = 0;
        while (left < before.Length || right < after.Length)
        {
            if (left < before.Length && right < after.Length && before[left] == after[right])
            {
                offset += before[left++].Length;
                right++;
                continue;
            }
            var start = offset;
            var added = right;
            while (left < before.Length || right < after.Length)
            {
                if (left < before.Length && right < after.Length && before[left] == after[right]) break;
                if (left < before.Length && (right == after.Length || common[left + 1, right] >= common[left, right + 1]))
                    offset += before[left++].Length;
                else
                    right++;
            }
            edits.Add(new TextEdit(start, offset - start, string.Concat(after[added..right])));
        }
        return edits;
    }

    private static string[] Lines(string text) => Regex.Matches(text, @"[^\r\n]+(?:\r\n|\r|\n)?|\r\n|\r|\n")
        .Select(match => match.Value).ToArray();
}
