using Avalonia.Automation;
using Avalonia.Controls;
using Tern.App.Shell;
using Tern.Desktop.Editing;
using Tern.Markdown;
using Tern.PreviewView;
using Tern.Rendering.Resources;

namespace Tern.Desktop.Views;

public partial class MainWindow
{
    private readonly HybridSourceGenerator _hybridSource = new();
    private MarkdownAnalysis? _hybridAnalysis;

    private void RefreshHybridSource(bool force = false)
    {
        var tab = _shown;
        var analysis = tab is not null && tab.AnalysisRevision == tab.Session.Revision ? tab.Analysis : null;
        var enabled = _shell.Settings.HybridSourceEnabled;
        _colorizer.HybridEnabled = enabled;
        if (!force && ReferenceEquals(_hybridAnalysis, analysis)) return;
        _hybridAnalysis = analysis;
        var items = new List<SourceDecoration>();
        if (enabled && analysis is not null && tab is not null)
        {
            var revision = tab.Session.Revision;
            var resolver = new SafeResourceResolver(new ResourceResolverOptions
            {
                DocumentDirectory = Path.GetDirectoryName(tab.FilePath ?? tab.ResourceDocumentPath),
                WorkspaceRoot = WorkspaceRootFor(tab),
                AdditionalImageFiles = tab.LinkedImageFiles,
                MaxImageBytes = 16 * 1024 * 1024,
            });
            foreach (var task in analysis.Tasks)
            {
                items.Add(new(task.Marker.Start, task.Marker.Length, task.Line, () =>
                {
                    var box = new CheckBox { IsChecked = task.Checked, IsEnabled = !tab.IsReadOnly, MinHeight = 22, MinWidth = 24 };
                    AutomationProperties.SetName(box, "Source task: " + task.Text);
                    box.Click += (_, _) =>
                    {
                        if (!_shell.Tabs.Contains(tab) || !ReferenceEquals(_shown, tab) || !tab.Session.IsCurrent(revision) || tab.IsReadOnly)
                        {
                            box.IsChecked = task.Checked;
                            _shell.SetStatus("This task changed. Wait a moment, then try again.", StatusSeverity.Warning);
                            return;
                        }
                        ContextFor(tab).Buffer.Apply([TaskEdits.Toggle(tab.CurrentText, task)]);
                    };
                    return box;
                }));
            }
            foreach (var image in analysis.Images.Where(image => image.Range.Length > 0
                && analysis.Lines.LineOf(image.Range.End - 1) == image.Line))
                items.Add(new(image.Range.Start, image.Range.Length, image.Line,
                    () => new SourceImageThumbnail(resolver, image.Destination, image.Alt)));
            _hybridSource.SetContent(items, () => !_windowClosed && _shell.Settings.HybridSourceEnabled
                && ReferenceEquals(tab, _shown) && tab.Session.IsCurrent(revision));
        }
        else _hybridSource.SetContent([], () => false);
        RefreshHybridCaret();
        Editor.TextArea.TextView.Redraw();
    }

    private void RefreshHybridCaret()
    {
        var previous = _hybridSource.CaretLine;
        var line = Editor.TextArea.Caret.Line;
        _hybridSource.CaretLine = _colorizer.CaretLine = line;
        if (!_shell.Settings.HybridSourceEnabled || previous == line) return;
        foreach (var number in new[] { previous, line }.Where(number => number > 0 && number <= Editor.Document.LineCount))
        {
            var documentLine = Editor.Document.GetLineByNumber(number);
            Editor.TextArea.TextView.Redraw(documentLine.Offset, documentLine.Length);
        }
    }
}
