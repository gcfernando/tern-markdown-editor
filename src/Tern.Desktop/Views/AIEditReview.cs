using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Tern.Domain.Text;
using Tern.App.Shell;

namespace Tern.Desktop.Views;

internal sealed class AIEditReview : Border
{
    private readonly List<Control> _tabStops = [];
    private Action _refreshTargetState = () => { };
    internal void RefreshTargetState() => _refreshTargetState();
    internal ActivityFeed? Activities { get; set; }
    internal string? ActivityIdentity { get; set; }

    public AIEditReview(string original, string proposed, int offset, Func<IReadOnlyList<TextEdit>, bool> apply,
        Action close, Action<string> copy, Action back, bool insertResult = false, string? copyText = null,
        Func<CancellationToken, Task<bool>>? validateApply = null, Func<bool>? canApply = null, Action? reanchorInsert = null)
    {
        Margin = new Thickness(24);
        Padding = new Thickness(22);
        MaxWidth = 1360;
        MaxHeight = 960;
        VerticalAlignment = VerticalAlignment.Stretch;
        this[!CornerRadiusProperty] = GuideParts.Radius("Popup").ToBinding();
        BorderThickness = new Thickness(1);
        this.Bind(BackgroundProperty, this.GetResourceObservable("Tern.Surface"));
        this.Bind(BorderBrushProperty, this.GetResourceObservable("Tern.Border"));
        var status = MessageTones.Follow(GuideParts.Body(""));
        var changes = insertResult ? [TextEdit.Insert(offset, proposed)] : Changes(original, proposed, offset);
        var rows = new StackPanel { [!StackPanel.SpacingProperty] = GuideParts.Space(12).ToBinding() };
        ScrollViewer? viewport = null;
        Action fitComparison = () => { };
        if (changes.Count == 0) rows.Children.Add(GuideParts.Body("AI returned the same text."));
        if (changes.Count > 0)
        {
            var before = Code(original, "Original Markdown");
            var after = Code(copyText ?? proposed, "Proposed Markdown");
            var proposedLabel = GuideParts.Caption(insertResult ? "AFTER · New content to insert" : "AFTER · Complete proposed text");
            proposedLabel.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("Tern.Accent"));
            var comparison = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,*"),
                RowDefinitions = new RowDefinitions("Auto,Auto"),
                [!Grid.ColumnSpacingProperty] = GuideParts.Space(12).ToBinding(),
                [!Grid.RowSpacingProperty] = GuideParts.Space(8).ToBinding()
            };
            comparison.Children.Add(GuideParts.Caption("BEFORE · Your current text"));
            Grid.SetRow(before, 1);
            comparison.Children.Add(before);
            Grid.SetColumn(proposedLabel, 1);
            Grid.SetColumn(after, 1);
            Grid.SetRow(after, 1);
            comparison.Children.Add(proposedLabel);
            comparison.Children.Add(after);
            _tabStops.AddRange([before, after]);
            rows.Children.Add(comparison);
            fitComparison = () =>
            {
                var compact = Bounds.Width < 680;
                comparison.ColumnDefinitions = new ColumnDefinitions(compact ? "*" : "*,*");
                comparison.RowDefinitions = new RowDefinitions(compact ? "Auto,Auto,Auto,Auto" : "Auto,*");
                Grid.SetColumn(proposedLabel, compact ? 0 : 1); Grid.SetRow(proposedLabel, compact ? 2 : 0);
                Grid.SetColumn(after, compact ? 0 : 1); Grid.SetRow(after, compact ? 3 : 1);
                before.MaxHeight = after.MaxHeight = double.PositiveInfinity;
                var available = viewport?.Viewport.Height ?? Math.Max(200, Bounds.Height - 300);
                before.Height = after.Height = Math.Clamp((available - 72) / (compact ? 2 : 1), 100, 760);
            };
            SizeChanged += (_, _) => fitComparison();
        }

        var applyButton = new Button { Content = insertResult ? "Insert result" : "Apply changes", IsEnabled = changes.Count > 0, Classes = { "accent" } };
        var closeButton = new Button { Content = "Close" };
        var copyButton = new Button { Content = "Copy result" };
        var backButton = new Button { Content = "Back" };
        var reanchorButton = new Button { Content = "Insert at cursor", IsVisible = false };
        reanchorButton.Click += (_, _) => reanchorInsert?.Invoke();
        _tabStops.AddRange([backButton, copyButton, reanchorButton, applyButton, closeButton]);
        AutomationProperties.SetName(backButton, "Back to AI instruction");
        backButton.Click += (_, _) => back();
        CancellationTokenSource? validating = null;
        const string staleNotice = "Your note changed. Apply is disabled. Copy the result or choose Insert at cursor to review an insertion into the current note.";
        _refreshTargetState = () =>
        {
            var current = canApply?.Invoke() ?? true;
            applyButton.IsEnabled = changes.Count > 0 && current && validating is null;
            reanchorButton.IsVisible = !current && reanchorInsert is not null;
            if (!current) status.Text = staleNotice;
            else if (status.Text == staleNotice) status.Text = "";
        };
        RefreshTargetState();
        DetachedFromVisualTree += (_, _) => validating?.Cancel();
        applyButton.Click += async (_, _) =>
        {
            RefreshTargetState();
            if (!applyButton.IsEnabled) return;
            using var validation = new CancellationTokenSource();
            validating = validation;
            applyButton.IsEnabled = false;
            using var activity = Activities?.Begin("Applying approved AI suggestion", ActivityIdentity,
                cancel: validateApply is null ? null : validation.Cancel);
            try
            {
                activity?.Report("Validating the current target and approved suggestion");
                if (validateApply is null || await validateApply(validation.Token))
                {
                    validation.Token.ThrowIfCancellationRequested();
                    activity?.Report("Applying the approved changes to the open buffer");
                    if (apply(changes))
                    {
                        activity?.Complete(outcome: "Suggestion applied to buffer; not saved");
                        close();
                        return;
                    }
                }
                activity?.Fail("The suggestion or its target is out of date. The suggestion was not applied.");
                status.Text = "This suggestion is out of date. Reopen AI for the current version of the note.";
            }
            catch (OperationCanceledException) when (validation.IsCancellationRequested)
            { activity?.Cancel("Canceled; no suggestion was applied."); status.Text = "Review canceled. Your note was not changed."; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
            { activity?.Fail(ex.Message); status.Text = "Cannot apply this suggestion: " + Tern.App.Shell.UserProblem.Plain(ex); }
            catch (Exception ex) { activity?.Fail(ex.Message); throw; }
            finally { validating = null; }
        };
        closeButton.Click += (_, _) => close();
        copyButton.Click += (_, _) => copy(copyText ?? proposed);
        var scroll = new ScrollViewer { Content = rows, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        viewport = scroll;
        scroll.SizeChanged += (_, _) => fitComparison();
        var footer = GuideParts.Vertical(status, GuideParts.Actions(applyButton, closeButton, backButton, copyButton, reanchorButton));
        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), [!Grid.RowSpacingProperty] = GuideParts.Space(12).ToBinding() };
        var modeText = GuideParts.Caption(insertResult ? "Adds new content" : "Replaces this text");
        modeText.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("Tern.Text"));
        var mode = new Border { Padding = new Thickness(10, 4), VerticalAlignment = VerticalAlignment.Center, Child = modeText,
            [!Border.CornerRadiusProperty] = GuideParts.Radius("Control").ToBinding() };
        mode.Bind(Border.BackgroundProperty, this.GetResourceObservable("Tern.AccentSoft"));
        AutomationProperties.SetName(mode, insertResult ? "Result adds new content" : "Result replaces this text");
        var titleRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Children = { GuideParts.Title("Review AI suggestion"), mode } };
        Grid.SetColumn(mode, 1);
        grid.Children.Add(GuideParts.Vertical(titleRow,
            GuideParts.Body("1. Compare your current text with the complete proposed result below. 2. Use Back to change your instruction. 3. Apply only when the result is right; Close keeps your note unchanged."),
            GuideParts.Caption(insertResult ? "Insert result adds the new content and keeps your original text. It stays unsaved and can be undone."
                : $"{changes.Count} changed section{(changes.Count == 1 ? "" : "s")}. Apply changes replaces the text on the left with the complete suggestion. It stays unsaved and can be undone.")));
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
            MaxHeight = 260, Padding = new Thickness(14, 12), VerticalAlignment = VerticalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Top };
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
