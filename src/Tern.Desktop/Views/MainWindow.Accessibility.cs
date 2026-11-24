using Avalonia.Automation;
using Avalonia.Automation.Peers;

namespace Tern.Desktop.Views;

public partial class MainWindow
{
    private string CurrentSourceLineAnnouncement()
    {
        var caret = Editor.TextArea.Caret;
        var line = Editor.Document.GetLineByNumber(Math.Clamp(caret.Line, 1, Editor.Document.LineCount));
        const int limit = 2000;
        var text = Editor.Document.GetText(line.Offset, Math.Min(line.Length, limit));
        if (text.Length > 0 && char.IsHighSurrogate(text[^1]) && line.Length > text.Length) text = text[..^1];
        return $"Line {caret.Line}, column {caret.Column}: " + (text.Length == 0 ? "Blank line" : text)
            + (line.Length > limit ? " (line shortened to 2,000 characters)" : "");
    }

    private void AnnounceDocumentInformation(bool currentLine)
    {
        if (_shown is not { } tab) return;
        var text = currentLine ? CurrentSourceLineAnnouncement()
            : $"{tab.DisplayName}, {(tab.IsDirty || tab.IsUntitled ? "unsaved" : "saved")}{(tab.IsReadOnly ? ", read-only" : "")}, "
              + $"line {Editor.TextArea.Caret.Line} of {Editor.Document.LineCount}, "
              + (tab.Analysis is { } analysis ? $"last analyzed count {analysis.WordCount:N0} words." : "word count updating.");
        var peer = ControlAutomationPeer.CreatePeerForElement(StatusPosition);
        var previous = AutomationProperties.GetName(StatusPosition);
        AutomationProperties.SetName(StatusPosition, text);
        if (string.Equals(previous, text, StringComparison.Ordinal))
            peer?.RaisePropertyChangedEvent(AutomationElementIdentifiers.NameProperty, null, text);
    }
}
