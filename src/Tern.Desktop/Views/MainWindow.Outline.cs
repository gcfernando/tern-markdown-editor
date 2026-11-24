using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using AvaloniaEdit.Folding;
using Tern.App.Commands;
using Tern.Desktop.ViewModels;
using Tern.Markdown;

namespace Tern.Desktop.Views;

public partial class MainWindow
{
    private FoldingManager? _headingFolds;
    private MarkdownAnalysis? _foldAnalysis;
    internal FoldingManager? HeadingFolds => _headingFolds;

    private void DetachHeadingFolds()
    {
        if (_headingFolds is not { } manager) return;
        FoldingManager.Uninstall(manager);
        _headingFolds = null;
        _foldAnalysis = null;
    }

    private void RefreshHeadingFolds()
    {
        if (_shown is not { Analysis: { } analysis } tab || tab.AnalysisRevision != tab.Session.Revision) return;
        if (ReferenceEquals(_foldAnalysis, analysis)) return;
        _headingFolds ??= FoldingManager.Install(Editor.TextArea);
        var text = tab.CurrentText;
        var foldings = analysis.Headings.Select((heading, index) =>
        {
            var start = analysis.Lines.LineEnd(text, heading.Line);
            return new NewFolding(start, OutlineEdits.End(analysis, index, Editor.Document.TextLength)) { Name = "…" };
        }).Where(folding => folding.EndOffset > folding.StartOffset).OrderBy(folding => folding.StartOffset);
        _headingFolds.UpdateFoldings(foldings, -1);
        _foldAnalysis = analysis;
    }

    private void ApplyOutlineCommand(string id, int? headingLine = null)
    {
        if (!CanRunCommand(id) || _shown is not { } tab) return;
        var snapshot = tab.Session.GetSnapshot();
        var analysis = tab.AnalysisRevision == snapshot.Revision && tab.Analysis is { } current
            ? current : MarkdownAnalyzer.Analyze(snapshot.Text, tab.Profile);
        var heading = headingLine is { } selected ? analysis.Headings.FirstOrDefault(item => item.Line == selected)
            : analysis.Headings.LastOrDefault(item => item.Line <= Editor.TextArea.Caret.Line);
        if (heading is null) { _shell.SetStatus("Select a heading in Outline first.", Tern.App.Shell.StatusSeverity.Info); return; }
        if (id == CommandIds.FoldSection)
        {
            RefreshHeadingFolds();
            var start = analysis.Lines.LineEnd(snapshot.Text, heading.Line);
            var folding = _headingFolds?.AllFoldings.FirstOrDefault(item => item.StartOffset == start);
            if (folding is null) { _shell.SetStatus("This heading has no body to fold.", Tern.App.Shell.StatusSeverity.Info); return; }
            Editor.CaretOffset = analysis.Lines.LineStart(heading.Line);
            folding.IsFolded = !folding.IsFolded;
            return;
        }
        var change = id is CommandIds.MoveSectionUp or CommandIds.MoveSectionDown
            ? OutlineEdits.Move(snapshot.Text, analysis, heading.Line, id == CommandIds.MoveSectionDown, tab.Session.Format.NewLineText)
            : OutlineEdits.Shift(snapshot.Text, analysis, heading.Line, id == CommandIds.PromoteSection ? -1 : 1);
        if (change.Refusal is { } reason) { _shell.SetStatus(reason, Tern.App.Shell.StatusSeverity.Info); return; }
        if (!tab.Session.IsCurrent(snapshot.Revision) || !ReferenceEquals(_shown, tab) || tab.IsReadOnly)
        { _shell.SetStatus("The section changed. Select it again before editing.", Tern.App.Shell.StatusSeverity.Warning); return; }
        ContextFor(tab).Buffer.Apply(change.Edits);
        Editor.CaretOffset = Math.Clamp(change.Caret, 0, Editor.Document.TextLength);
    }

    private void OnOutlineContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        var row = (e.Source as Control)?.GetSelfAndVisualAncestors().OfType<Control>()
            .Select(control => control.DataContext).OfType<OutlineRow>().FirstOrDefault() ?? OutlineList.SelectedItem as OutlineRow;
        if (row is null || _shown is not { } tab) return;
        OutlineList.SelectedItem = row;
        var revision = tab.Session.Revision;
        var menu = new ContextMenu();
        foreach (var id in new[] { CommandIds.FoldSection, CommandIds.MoveSectionUp, CommandIds.MoveSectionDown, CommandIds.PromoteSection, CommandIds.DemoteSection })
        {
            var item = new MenuItem { Header = _shell.Registry.Commands.Single(command => command.Id == id).Title, IsEnabled = CanRunCommand(id) };
            item.Click += (_, _) =>
            {
                if (!ReferenceEquals(tab, _shown) || !tab.Session.IsCurrent(revision))
                { _shell.SetStatus("The section changed. Open its menu again.", Tern.App.Shell.StatusSeverity.Warning); return; }
                ApplyOutlineCommand(id, row.Line);
            };
            menu.Items.Add(item);
        }
        OutlineList.ContextMenu = menu;
        menu.Open(OutlineList);
        e.Handled = true;
    }
}
