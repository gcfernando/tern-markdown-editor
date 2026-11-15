using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Tern.App.Commands;

namespace Tern.Desktop.Views;

public partial class MainWindow
{
    private readonly List<Control> _formatControls = [];
    private readonly List<MenuItem> _formatOverflow = [];
    private bool _taskActivationBusy;

    private void ConfigureAuthoringToolbar()
    {
        var headings = new MenuFlyout();
        foreach (var (title, id) in new[] { ("Heading 1 · title", CommandIds.Heading1), ("Heading 2 · section", CommandIds.Heading2), ("Heading 3 · subsection", CommandIds.Heading3) })
        {
            var item = FormatMenu(title, id); headings.Items.Add(item);
        }
        HeadingAction.Flyout = headings;
        var more = new MenuFlyout();
        foreach (var (title, id) in new[]
        {
            ("Heading 1 · title", CommandIds.Heading1), ("Heading 2 · section", CommandIds.Heading2), ("Heading 3 · subsection", CommandIds.Heading3),
            ("Bullet list", CommandIds.ToggleBullet), ("Numbered list", CommandIds.ToggleNumbered), ("Checklist", CommandIds.ToggleTask),
            ("Insert link…", CommandIds.InsertLink), ("Insert image…", CommandIds.InsertImage), ("Insert table…", CommandIds.InsertTable),
            ("Code block", CommandIds.InsertCodeBlock), ("Strikethrough", CommandIds.Strikethrough), ("Horizontal rule", CommandIds.InsertRule), ("Write with AI…", CommandIds.AskAI)
        })
        {
            var item = FormatMenu(title, id); more.Items.Add(item); _formatOverflow.Add(item);
        }
        AuthoringMore.Flyout = more;
        AuthoringToolbar.SizeChanged += (_, _) => RefreshAuthoringState();
        PreviewHost.SizeChanged += (_, _) => RefreshAuthoringState();
        _formatControls.AddRange(AuthoringExtras.Children.OfType<Control>());
        _formatControls.Add(AuthoringBold); _formatControls.Add(AuthoringItalic);
        RefreshAuthoringState();
    }

    private MenuItem FormatMenu(string title, string id)
    {
        var item = new MenuItem { Header = title, Tag = id };
        AutomationProperties.SetName(item, title);
        item.Click += OnMenuCommandClick;
        _formatControls.Add(item);
        return item;
    }

    private void RefreshAuthoringState()
    {
        if (AuthoringToolbar is null) return;
        var width = AuthoringToolbar.Bounds.Width;
        ReadingNavActions.IsVisible = PreviewHost.Bounds.Width >= 500 * _shell.Settings.UiScale;
        ReadingPaneLabel.IsVisible = PreviewHost.Bounds.Width >= 700 * _shell.Settings.UiScale;
        AuthoringExtras.IsVisible = width >= 700 * _shell.Settings.UiScale;
        HeadingAction.IsVisible = width >= 360 * _shell.Settings.UiScale;
        foreach (var control in _formatControls)
        {
            if (control.Tag is not string id) continue;
            control.IsEnabled = CanRunCommand(id);
            ToolTip.SetTip(control, CommandHint(id));
            if (control is MenuItem item) item.InputGesture = DesktopGesture(_shell.Registry.GetGesture(id));
        }
        foreach (var item in _formatOverflow)
        {
            var id = item.Tag as string;
            item.IsVisible = id switch
            {
                CommandIds.Strikethrough or CommandIds.InsertRule or CommandIds.AskAI => true,
                CommandIds.Heading1 or CommandIds.Heading2 or CommandIds.Heading3 => !HeadingAction.IsVisible,
                _ => !AuthoringExtras.IsVisible
            };
        }
        SourceReadOnlyBadge.IsVisible = _shown?.IsReadOnly == true;
        AuthoringBold.IsChecked = MarkedSelection("**");
        AuthoringItalic.IsChecked = MarkedSelection("*") && !MarkedSelection("**");
        HeadingAction.Content = "Heading";
        if (HasValidAuthoringSelection() && !Editor.SelectedText.Contains('\n'))
        {
            var heading = _shown.Analysis?.Headings.FirstOrDefault(h => h.Line == Editor.TextArea.Caret.Line);
            if (heading is not null) HeadingAction.Content = "Heading " + heading.Level;
        }
    }

    private bool MarkedSelection(string marker)
    {
        if (!HasValidAuthoringSelection() || Editor.SelectionLength == 0 || Editor.SelectedText.Contains('\n')) return false;
        var selected = Editor.SelectedText;
        if (selected.Length > marker.Length * 2 && selected.StartsWith(marker, StringComparison.Ordinal) && selected.EndsWith(marker, StringComparison.Ordinal)) return true;
        var start = Editor.SelectionStart;
        var end = start + Editor.SelectionLength;
        var text = _shown.CurrentText;
        return start >= marker.Length && end + marker.Length <= text.Length
            && text.AsSpan(start - marker.Length, marker.Length).SequenceEqual(marker)
            && text.AsSpan(end, marker.Length).SequenceEqual(marker);
    }

    [System.Diagnostics.CodeAnalysis.MemberNotNullWhen(true, nameof(_shown))]
    private bool HasValidAuthoringSelection() => _shown is not null && Editor.Document is { } document
        && Editor.SelectionStart >= 0 && Editor.SelectionLength >= 0
        && Editor.SelectionStart <= document.TextLength - Editor.SelectionLength
        && document.TextLength == _shown.CurrentText.Length;

    private void UpdateCurrentSection(int? readingLine = null)
    {
        if (OutlineCurrent is null) return;
        var line = readingLine ?? (_shell.ViewMode == Tern.App.Settings.ViewMode.Reading ? Preview.TopSourceLine : Editor.TextArea.Caret.Line);
        var section = _shown?.Analysis?.Headings.LastOrDefault(h => h.Line <= line);
        OutlineCurrent.Text = section is null ? "Beginning of note" : "Current section · " + section.Text;
    }

    private void OnSyncScrollingClick(object? sender, RoutedEventArgs e)
    {
        _shell.Settings.SyncScroll = SyncScrollingCheck.IsChecked == true;
        _shell.SaveSettings();
    }

    private void OnReadingWidthClick(object? sender, RoutedEventArgs e)
    {
        _shell.Settings.PreviewFullWidth = FullReadingWidthCheck.IsChecked == true;
        Preview.UseFullWidth = _shell.Settings.PreviewFullWidth;
        _shell.SaveSettings();
    }
}
