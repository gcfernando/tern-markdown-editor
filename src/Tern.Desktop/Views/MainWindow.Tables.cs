using Avalonia.Automation;
using Avalonia.Controls;
using Tern.App.Commands;
using Tern.App.Editing;
using Tern.App.Shell;
using Tern.Markdown;

namespace Tern.Desktop.Views;

public partial class MainWindow
{
    internal Func<Task>? BeforeTableApplyAsync { get; set; }

    private void ConfigureTableEditing()
    {
        var flyout = new MenuFlyout();
        foreach (var id in TableEditingService.Commands)
        {
            var item = new MenuItem { Tag = id };
            RefreshMenuCommand(item);
            item.Click += OnMenuCommandClick;
            flyout.Items.Add(item);
            _formatControls.Add(item);
        }
        TableActions.Flyout = flyout;
        var context = new ContextMenu();
        foreach (var id in new[] { CommandIds.Cut, CommandIds.Copy, CommandIds.Paste, CommandIds.PastePlain, CommandIds.PasteTable })
        {
            var item = new MenuItem { Tag = id };
            RefreshMenuCommand(item);
            item.Click += OnMenuCommandClick;
            context.Items.Add(item);
        }
        var tableMenu = new MenuItem { Header = "Table" };
        AutomationProperties.SetName(tableMenu, "Table editing commands");
        foreach (var id in TableEditingService.Commands)
        {
            var item = new MenuItem { Tag = id };
            RefreshMenuCommand(item);
            item.Click += OnMenuCommandClick;
            tableMenu.Items.Add(item);
        }
        context.Items.Add(new Separator());
        context.Items.Add(tableMenu);
        var spellingMenu = new MenuItem { Header = "Spelling" };
        context.Items.Add(spellingMenu);
        context.Opening += (_, _) =>
        {
            PopulateSpellingMenu(spellingMenu);
            tableMenu.IsVisible = CurrentTable() is not null;
            foreach (var item in context.Items.OfType<MenuItem>().Concat(tableMenu.Items.OfType<MenuItem>())) RefreshMenuCommand(item);
        };
        Editor.ContextMenu = context;
    }

    private TableInfo? CurrentTable() => _shown?.Analysis is { } analysis
        && Editor.Document is { } document && Editor.CaretOffset <= document.TextLength
            ? TableEditor.FindAt(analysis, Editor.CaretOffset) : null;

    private string? TableCommandDisabledReason(string id)
    {
        var info = CurrentTable();
        if (info is null) return TableEditingService.DisabledReason(id, null, 0, 0);
        var line = Editor.Document.GetLineByOffset(Editor.CaretOffset);
        var prefix = Editor.Document.GetText(line.Offset, Editor.CaretOffset - line.Offset);
        var column = 0;
        for (var i = 0; i < prefix.Length; i++)
            if (prefix[i] == '\\' && i + 1 < prefix.Length) i++;
            else if (prefix[i] == '|') column++;
        if (prefix.TrimStart().StartsWith('|')) column--;
        var row = line.LineNumber <= info.Line + 1 ? -1 : line.LineNumber - info.Line - 2;
        return TableEditingService.DisabledReason(id, info, row, Math.Clamp(column, 0, info.Columns - 1));
    }

    private async Task NavigateTableAsync(bool backward)
    {
        try { await EditTableAsync(null, backward); }
        catch (Exception ex) { _shell.ReportUnexpectedError("navigate table cells", ex); }
    }

    private async Task EditTableAsync(string? id, bool backward = false)
    {
        if (_shown is not { IsReadOnly: false } tab || _shell.IsBusy) return;
        var snapshot = tab.Session.GetSnapshot();
        var start = Editor.SelectionStart;
        var length = Editor.SelectionLength;
        var profile = tab.Profile;
        var newline = tab.Session.Format.NewLineText;
        var cached = tab.AnalysisRevision == snapshot.Revision && tab.Analysis?.Profile == profile ? tab.Analysis : null;
        var result = await Task.Run(() =>
        {
            var analysis = cached ?? MarkdownAnalyzer.Analyze(snapshot.Text, profile);
            return id is null ? TableEditingService.Navigate(snapshot.Text, start, analysis, newline, backward)
                : TableEditingService.Execute(id, snapshot.Text, start, analysis, newline);
        });
        if (BeforeTableApplyAsync is { } prepared) await prepared();
        if (_windowClosed || !ReferenceEquals(tab, _shown) || !tab.Session.IsCurrent(snapshot.Revision) || tab.Profile != profile || tab.IsReadOnly || _shell.IsBusy
            || Editor.SelectionStart != start || Editor.SelectionLength != length)
        { _shell.SetStatus("The document or selection changed. Run the table action again.", StatusSeverity.Info); return; }
        if (result is null)
        { _shell.SetStatus(id is null ? "Place the cursor in a Markdown table first." : TableCommandDisabledReason(id) ?? "This table action is unavailable.", StatusSeverity.Info); return; }
        if (result.Edits.Count > 0) ApplyEdit(result);
        else Editor.Select(result.SelectionStart, result.SelectionLength);
        Editor.Focus();
        if (id is not null && _shell.Registry.TryGet(id, out var command)) _shell.SetStatus(command.Title + " applied.", StatusSeverity.Info);
    }

    private bool IsListSelection()
    {
        var text = Editor.Document.Text;
        var lines = new LineIndex(text);
        var first = lines.LineOf(Editor.SelectionStart);
        var last = lines.LineOf(Editor.SelectionStart + Math.Max(0, Editor.SelectionLength - 1));
        var found = false;
        for (var line = first; line <= last; line++)
        {
            var start = lines.LineStart(line);
            if (_shown?.Analysis?.CodeBlocks.Any(block => block.Range.Contains(start)) == true) return false;
            var value = text[start..lines.LineEnd(text, line)].TrimStart(' ', '\t');
            if (value.Length == 0) continue;
            var marker = value[0] is '-' or '+' or '*' ? 1 : 0;
            if (marker == 0)
            {
                while (marker < value.Length && char.IsAsciiDigit(value[marker])) marker++;
                if (marker == 0 || marker >= value.Length || value[marker] is not '.' and not ')') return false;
                marker++;
            }
            if (marker >= value.Length || !char.IsWhiteSpace(value[marker])) return false;
            found = true;
        }
        return found;
    }

    internal Func<Task<TableDimensions?>>? TableDimensionsPicker { get; set; }

    private async Task InsertTableAsync()
    {
        if (_shown is not { IsReadOnly: false } tab) return;
        var revision = tab.Session.Revision;
        var start = Editor.SelectionStart;
        var length = Editor.SelectionLength;
        var dimensions = TableDimensionsPicker is { } picker ? await picker()
            : await new InsertTableWindow(length > 0).ShowDialog<TableDimensions?>(this);
        if (dimensions is null) return;
        if (!ReferenceEquals(tab, _shown) || !tab.Session.IsCurrent(revision) || !CanRunCommand(Tern.App.Commands.CommandIds.InsertTable))
        { _shell.StatusMessage = "The document changed while choosing table dimensions. Insert the table again."; return; }
        ApplyEdit(FormattingService.InsertTable(tab.CurrentText, start, length, tab.Session.Format.NewLineText,
            dimensions.Columns, dimensions.Rows - 1));
        _shell.StatusMessage = $"Inserted {dimensions.Rows} rows (including header) and {dimensions.Columns} columns.";
    }
}
