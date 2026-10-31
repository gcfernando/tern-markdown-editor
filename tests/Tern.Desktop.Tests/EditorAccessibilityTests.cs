using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Tern.Desktop.Editing;
using Tern.Domain.Documents;
using Tern.Domain.Text;

namespace Tern.Desktop.Tests;

public sealed class EditorAccessibilityTests
{
    [AvaloniaFact]
    public void Source_peer_exposes_real_unicode_text_and_explicit_name()
    {
        var editor = new AccessibleMarkdownEditor { Text = "# Café 😀\r\nsecond\n" };
        AutomationProperties.SetName(editor, "Document source");
        var peer = ControlAutomationPeer.CreatePeerForElement(editor)!;
        var provider = Assert.IsAssignableFrom<IValueProvider>(peer.GetProvider<IValueProvider>());

        Assert.Equal(AutomationControlType.Edit, peer.GetAutomationControlType());
        Assert.Equal("Document source", peer.GetName());
        Assert.Equal("# Café 😀\r\nsecond\n", provider.Value);
        Assert.False(provider.IsReadOnly);
        Assert.Empty(peer.GetChildren()!);
    }

    [AvaloniaFact]
    public void Automation_edit_changes_the_canonical_buffer_as_one_undoable_transaction()
    {
        var buffer = new AvaloniaDocumentBuffer("original\r\n");
        var session = new DocumentSession(null, "original\r\n", TextFileFormat.NewFileDefault, null);
        session.AttachBuffer(buffer);
        var editor = new AccessibleMarkdownEditor { Document = buffer.Document };
        var peer = ControlAutomationPeer.CreatePeerForElement(editor)!;
        var provider = Assert.IsAssignableFrom<IValueProvider>(peer.GetProvider<IValueProvider>());

        provider.SetValue("explicit 😀 edit\r\n");

        Assert.Equal("explicit 😀 edit\r\n", session.GetSnapshot().Text);
        Assert.True(session.IsDirty);
        Assert.True(buffer.Undo());
        Assert.Equal("original\r\n", provider.Value);
        Assert.False(session.IsDirty);
        Assert.False(buffer.Document.UndoStack.CanUndo);
    }

    [AvaloniaFact]
    public void Reference_peer_refuses_writes_and_reports_read_only_changes()
    {
        var editor = new AccessibleMarkdownEditor { Text = "keep", IsReadOnly = true };
        var peer = ControlAutomationPeer.CreatePeerForElement(editor)!;
        var provider = Assert.IsAssignableFrom<IValueProvider>(peer.GetProvider<IValueProvider>());
        var properties = new List<AutomationProperty>();
        peer.PropertyChanged += (_, e) => properties.Add(e.Property);

        Assert.True(provider.IsReadOnly);
        Assert.Throws<InvalidOperationException>(() => provider.SetValue("replace"));
        Assert.Equal("keep", editor.Text);
        editor.IsReadOnly = false;
        Assert.False(provider.IsReadOnly);
        Assert.Contains(ValuePatternIdentifiers.IsReadOnlyProperty, properties);
    }

    [AvaloniaFact]
    public void No_op_automation_set_keeps_revision_and_undo_unchanged()
    {
        var buffer = new AvaloniaDocumentBuffer("same\n");
        var session = new DocumentSession(null, "same\n", TextFileFormat.NewFileDefault, null);
        session.AttachBuffer(buffer);
        var editor = new AccessibleMarkdownEditor { Document = buffer.Document };
        var peer = ControlAutomationPeer.CreatePeerForElement(editor)!;
        var provider = Assert.IsAssignableFrom<IValueProvider>(peer.GetProvider<IValueProvider>());
        provider.SetValue("same\n");
        Assert.Equal(0, session.Revision);
        Assert.False(session.IsDirty);
        Assert.False(buffer.Document.UndoStack.CanUndo);
    }

    [AvaloniaFact]
    public void Value_change_notifications_and_focus_follow_the_editable_surface()
    {
        var editor = new AccessibleMarkdownEditor { Text = "before" };
        var window = new Window { Content = editor };
        window.Show();
        try
        {
            var peer = ControlAutomationPeer.CreatePeerForElement(editor)!;
            var properties = new List<AutomationProperty>();
            peer.PropertyChanged += (_, e) => properties.Add(e.Property);
            peer.SetFocus();
            Assert.True(editor.TextArea.IsKeyboardFocusWithin);
            Assert.True(peer.HasKeyboardFocus());
            editor.Document.Replace(0, 6, "after");
            Assert.Contains(ValuePatternIdentifiers.ValueProperty, properties);
        }
        finally
        {
            window.Close();
        }
    }
}
