using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using AvaloniaEdit;

namespace Tern.Desktop.Editing;

/// <summary>
/// Exposes the canonical editor document as an editable UI Automation value, rather than
/// exposing only the editor template's decorative list and scroll controls.
/// Avalonia's public automation API currently provides Value, not text-range/caret patterns;
/// native assistive-technology acceptance is still required.
/// </summary>
public sealed class AccessibleMarkdownEditor : TextEditor
{
    private MarkdownEditorAutomationPeer? _automationPeer;

    protected override Type StyleKeyOverride => typeof(TextEditor);

    protected override AutomationPeer OnCreateAutomationPeer() =>
        _automationPeer = new MarkdownEditorAutomationPeer(this);

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        _automationPeer?.NotifyTextChanged();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsReadOnlyProperty)
        {
            _automationPeer?.RaisePropertyChangedEvent(ValuePatternIdentifiers.IsReadOnlyProperty,
                change.OldValue, change.NewValue);
        }
    }

    private sealed class MarkdownEditorAutomationPeer(AccessibleMarkdownEditor editor)
        : ControlAutomationPeer(editor), IValueProvider
    {
        public bool IsReadOnly => editor.IsReadOnly;

        public string Value => editor.Document?.Text ?? string.Empty;

        public void SetValue(string? value)
        {
            editor.VerifyAccess();
            ArgumentNullException.ThrowIfNull(value);
            if (!editor.IsEnabled) throw new InvalidOperationException("The editor is disabled.");
            if (editor.IsReadOnly) throw new InvalidOperationException("The document is read-only.");
            var document = editor.Document ?? throw new InvalidOperationException("No document is attached.");
            if (string.Equals(document.Text, value, StringComparison.Ordinal)) return;

            // TextEditor.Text would reset document history. Replace the attached document's
            // range instead, so the session observes this explicit automation edit and Undo
            // restores the complete previous source in one operation.
            using (document.RunUpdate())
            {
                document.Replace(0, document.TextLength, value);
            }
        }

        public void NotifyTextChanged() =>
            RaisePropertyChangedEvent(ValuePatternIdentifiers.ValueProperty, null, Value);

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Edit;

        protected override string GetClassNameCore() => nameof(AccessibleMarkdownEditor);

        protected override string GetNameCore()
        {
            var name = base.GetNameCore();
            return string.IsNullOrWhiteSpace(name)
                ? editor.IsReadOnly ? "Markdown reference document" : "Markdown source editor"
                : name;
        }

        protected override string GetHelpTextCore()
        {
            var help = base.GetHelpTextCore();
            return string.IsNullOrWhiteSpace(help)
                ? "Multiline Markdown source. Positions and selections use UTF-16 text offsets."
                : help;
        }

        protected override bool HasKeyboardFocusCore() => editor.TextArea.IsKeyboardFocusWithin;

        protected override bool IsKeyboardFocusableCore() => editor.TextArea.Focusable && editor.IsEnabled;

        protected override void SetFocusCore() => editor.TextArea.Focus();

        protected override IReadOnlyList<AutomationPeer> GetChildrenCore() => [];

        protected override object? GetProviderCore(Type providerType) =>
            providerType == typeof(IValueProvider) ? this : base.GetProviderCore(providerType);
    }
}
