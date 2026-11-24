using Avalonia.Controls;
using Avalonia.Threading;
using AvaloniaEdit.Rendering;
using Tern.App.Editing;
using Tern.App.Shell;
using Tern.Desktop.Services;
using Tern.Domain.Text;

namespace Tern.Desktop.Views;

public partial class MainWindow
{
    private readonly SpellingRenderer _spelling = new();
    private readonly SemaphoreSlim _spellingGate = new(1, 1);
    private CancellationTokenSource? _spellingCancellation;
    private bool _spellingUnavailable;
    private long _spellingRevision = -1;
    internal Func<string, CancellationToken, IReadOnlyList<SpellingIssue>> SpellCheck { get; set; } =
        (text, token) => WindowsSpellChecker.Check(text, token);
    internal IReadOnlyList<SpellingIssue> SpellingIssues => _spelling.Issues;

    private void ScheduleSpelling()
    {
        _spellingCancellation?.Cancel();
        _spelling.Issues = [];
        _spellingRevision = -1;
        Editor.TextArea.TextView.InvalidateLayer(KnownLayer.Selection);
        if (_windowClosed || _spellingUnavailable || !_shell.Settings.SpellCheckEnabled || !OperatingSystem.IsWindows()
            || _shown is not { Analysis: { } analysis } tab || tab.AnalysisRevision != tab.Session.Revision) return;
        var revision = tab.Session.Revision;
        var text = tab.CurrentText;
        var profile = tab.Profile;
        var view = Editor.TextArea.TextView;
        var visible = view.VisualLinesValid && view.VisualLines.Count > 0 ? view.VisualLines[0].FirstDocumentLine.Offset : Editor.CaretOffset;
        var start = Math.Clamp(visible - 8000, 0, text.Length);
        // Check a bounded window around the viewport; scrolling schedules the next window.
        var end = Math.Min(text.Length, start + 64000);
        if (start > 0)
            while (start < end && !char.IsWhiteSpace(text[start - 1])) start++;
        if (end < text.Length)
            while (end > start && !char.IsWhiteSpace(text[end])) end--;
        var cancellation = new CancellationTokenSource();
        _spellingCancellation = cancellation;
        _ = CheckAsync();

        async Task CheckAsync()
        {
            try
            {
                await Task.Delay(600, cancellation.Token);
                await _spellingGate.WaitAsync(cancellation.Token);
                IReadOnlyList<SpellingIssue> issues;
                try
                {
                    issues = await Task.Run(() => SpellCheck(SpellingText.Prepare(text, analysis, start, end - start), cancellation.Token), cancellation.Token);
                }
                finally { _spellingGate.Release(); }
                if (cancellation.IsCancellationRequested || _windowClosed || !ReferenceEquals(tab, _shown)
                    || !tab.Session.IsCurrent(revision) || tab.Profile != profile || !_shell.Settings.SpellCheckEnabled) return;
                _spelling.Issues = issues.Select(issue => issue with { Start = issue.Start + start })
                    .Where(issue => issue.Start >= start && issue.Length > 0 && issue.Start <= end - issue.Length)
                    .OrderBy(issue => issue.Start).ToList();
                _spellingRevision = revision;
                Editor.TextArea.TextView.InvalidateLayer(KnownLayer.Selection);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            catch (Exception ex)
            {
                if (!_windowClosed && !cancellation.IsCancellationRequested)
                {
                    _spellingUnavailable = true;
                    _shell.SetStatus("Windows spell checking is unavailable. " + _shell.PlainProblem(ex), StatusSeverity.Warning);
                }
            }
            finally
            {
                if (ReferenceEquals(_spellingCancellation, cancellation)) _spellingCancellation = null;
                cancellation.Dispose();
            }
        }
    }

    private void PopulateSpellingMenu(MenuItem menu)
    {
        menu.Items.Clear();
        menu.IsVisible = _shell.Settings.SpellCheckEnabled;
        if (_shown is not { } tab) return;
        var revision = _spellingRevision;
        var issue = _spelling.Issues.FirstOrDefault(item => Editor.CaretOffset >= item.Start && Editor.CaretOffset <= item.Start + item.Length);
        if (issue is null || !tab.Session.IsCurrent(revision))
        {
            menu.Items.Add(new MenuItem { Header = _spellingUnavailable ? "Windows Spelling Unavailable; Check Settings" : "No Spelling Suggestions at Cursor", IsEnabled = false });
            return;
        }
        if (issue.Suggestions.Count == 0)
            menu.Items.Add(new MenuItem { Header = "No Suggestions for " + issue.Word, IsEnabled = false });
        foreach (var suggestion in issue.Suggestions)
        {
            var item = new MenuItem { Header = suggestion.Length == 0 ? "Remove Repeated Word" : suggestion, IsEnabled = !tab.IsReadOnly && !_shell.IsBusy };
            item.Click += (_, _) =>
            {
                if (_windowClosed || !ReferenceEquals(tab, _shown) || !tab.Session.IsCurrent(revision) || tab.IsReadOnly || _shell.IsBusy)
                { _shell.SetStatus("The document changed. Open spelling suggestions again.", StatusSeverity.Info); return; }
                ApplyEdit(new Tern.Markdown.EditResult([new TextEdit(issue.Start, issue.Length, suggestion)], issue.Start + suggestion.Length, 0));
            };
            menu.Items.Add(item);
        }
    }
}
