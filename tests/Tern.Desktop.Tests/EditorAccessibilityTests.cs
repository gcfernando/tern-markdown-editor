using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tern.App.Commands;
using Tern.Desktop.Editing;
using Tern.Desktop.Views;
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
    public async Task Value_change_notifications_are_coalesced_after_idle_and_focus_follows_the_editable_surface()
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
            for (var index = 0; index < 100; index++) editor.Document.Insert(editor.Document.TextLength, "x");
            Assert.DoesNotContain(ValuePatternIdentifiers.ValueProperty, properties);
            Assert.Equal("after" + new string('x', 100), peer.GetProvider<IValueProvider>()!.Value);
            await Task.Delay(350, TestContext.Current.CancellationToken);
            editor.Document.Insert(editor.Document.TextLength, " latest");
            await Task.Delay(250, TestContext.Current.CancellationToken);
            Dispatcher.UIThread.RunJobs();
            Assert.DoesNotContain(ValuePatternIdentifiers.ValueProperty, properties);
            Assert.EndsWith(" latest", peer.GetProvider<IValueProvider>()!.Value);
            await Task.Delay(400, TestContext.Current.CancellationToken);
            Dispatcher.UIThread.RunJobs();
            Assert.Single(properties, property => property == ValuePatternIdentifiers.ValueProperty);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Pending_value_notification_is_cancelled_when_the_editor_is_detached()
    {
        var editor = new AccessibleMarkdownEditor { Text = "before" };
        var window = new Window { Content = editor };
        window.Show();
        var peer = ControlAutomationPeer.CreatePeerForElement(editor)!;
        var values = 0;
        peer.PropertyChanged += (_, e) => { if (e.Property == ValuePatternIdentifiers.ValueProperty) values++; };
        editor.Document.Insert(0, "edit ");
        window.Close();
        await Task.Delay(650, TestContext.Current.CancellationToken);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, values);
    }

    [AvaloniaFact]
    public async Task Read_commands_have_polite_private_feedback_without_modifying_source_or_focus()
    {
        using var f = new DesktopFixture();
        var tab = f.Shell.NewDocument("# Title\nCafé and an emoji 😀\n");
        f.Window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
        f.Window.Editor.CaretOffset = "# Title\nCafé ".Length;
        Assert.True(f.Window.Editor.TextArea.Focus());
        var original = tab.CurrentText;
        foreach (var id in new[] { "view.readCurrentLine", "view.readDocumentStatus" })
        {
            Assert.True(f.Shell.Registry.TryGet(id, out _), $"Missing accessibility command: {id}");
            Assert.True(Gesture.TryParse(f.Shell.Registry.GetGesture(id), out var gesture));
            Assert.False(gesture.CollidesWithAltGr);
        }
        await f.Window.RunCommandAsync("view.readCurrentLine");
        Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(f.Window.StatusPosition));
        Assert.Contains("Café and an emoji 😀", AutomationProperties.GetName(f.Window.StatusPosition));
        Assert.Contains("Line 2", AutomationProperties.GetName(f.Window.StatusPosition));
        var peer = ControlAutomationPeer.CreatePeerForElement(f.Window.StatusPosition)!;
        var repeats = 0;
        peer.PropertyChanged += (_, e) => { if (e.Property == AutomationElementIdentifiers.NameProperty) repeats++; };
        await f.Window.RunCommandAsync("view.readCurrentLine");
        Assert.True(repeats > 0);
        await f.Window.RunCommandAsync("view.readDocumentStatus");
        Assert.Contains("unsaved", AutomationProperties.GetName(f.Window.StatusPosition), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("line 2 of 3", AutomationProperties.GetName(f.Window.StatusPosition), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(original, tab.CurrentText);
        Assert.True(f.Window.Editor.TextArea.IsKeyboardFocusWithin);
        Assert.DoesNotContain(f.Shell.Activities.Items, entry => entry.Phase.Contains("Café", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public async Task Very_long_line_announcement_is_explicitly_bounded_without_splitting_an_emoji()
    {
        using var f = new DesktopFixture();
        var source = new string('x', 1999) + "😀 omitted end";
        var tab = f.Shell.NewDocument(source);
        await f.Window.RunCommandAsync(CommandIds.ReadCurrentLine);
        var announcement = AutomationProperties.GetName(f.Window.StatusPosition)!;
        Assert.True(announcement.Length < 2100);
        Assert.Contains("shortened", announcement);
        Assert.DoesNotContain("omitted end", announcement);
        Assert.DoesNotContain('\ud83d', announcement);
        Assert.Equal(source, tab.CurrentText);
    }

    [AvaloniaFact]
    public async Task Main_vector_actions_keep_names_tooltips_geometry_and_the_button_foreground()
    {
        using var f = new DesktopFixture();
        f.Shell.NewDocument("Find this.");
        await f.Window.RunCommandAsync(CommandIds.Find);
        f.Window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
        var buttons = f.Window.GetVisualDescendants().OfType<Button>()
            .Where(button => button.Content is VectorIcon).ToArray();
        Assert.True(buttons.Length >= 12);
        foreach (var button in buttons)
        {
            Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(button)));
            Assert.NotNull(ToolTip.GetTip(button));
            var icon = Assert.IsType<VectorIcon>(button.Content);
            Assert.NotNull(icon.Data);
            if (button.IsEffectivelyVisible)
                Assert.True(Equals(button.Foreground, icon.Stroke),
                    $"{AutomationProperties.GetName(button)} ({icon.Kind}): button {button.Foreground}, icon {icon.Stroke}");
            Assert.Equal(AccessibilityView.Raw, AutomationProperties.GetAccessibilityView(icon));
        }
        var next = buttons.Single(button => AutomationProperties.GetName(button) == "Next match");
        next.Foreground = Brushes.Fuchsia;
        Assert.Equal(Brushes.Fuchsia, Assert.IsType<VectorIcon>(next.Content).Stroke);
        f.Window.WindowState = WindowState.Maximized;
        Assert.Equal("Restore", Assert.IsType<VectorIcon>(f.Window.MaximizeButton.Content).Kind);
        f.Window.WindowState = WindowState.Normal;
        Assert.Equal("Maximize", Assert.IsType<VectorIcon>(f.Window.MaximizeButton.Content).Kind);
    }

    [AvaloniaFact]
    public void Reused_picker_input_identifies_each_navigation_and_settings_mode()
    {
        var picker = new QuickPicker();
        var window = new Window { Content = picker };
        window.Show();
        try
        {
            foreach (var (watermark, name) in new[]
            {
                ("Command palette: type a command…", "Command palette"),
                ("Line number", "Line number"),
                ("Choose encoding for the next save", "encoding"),
                ("Markdown profile for this document", "Markdown profile"),
                ("Go to file…  (# for headings)", "Go to file"),
                ("Open documents", "Open documents"),
            })
            {
                picker.Open(watermark, "", _ => [], _ => { }, () => { });
                Assert.Contains(name, AutomationProperties.GetName(picker.Input));
                Assert.True(picker.Input.IsKeyboardFocusWithin);
            }
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Checklist_heading_completion_is_expressed_in_words_and_a_polite_region()
    {
        using var f = new DesktopFixture();
        var tab = f.Shell.NewDocument();
        Assert.Equal("Write a heading, not done", AutomationProperties.GetName(f.Window.FirstHeadingStep));
        f.Window.Editor.Document.Insert(0, "# Actual heading\n");
        for (var attempt = 0; attempt < 150 && tab.Analysis?.Headings.Count is not > 0; attempt++)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
            Dispatcher.UIThread.RunJobs();
        }
        Assert.Equal("Write a heading, done", AutomationProperties.GetName(f.Window.FirstHeadingStep));
        Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(f.Window.FirstHeadingStep));
        Assert.Equal("Save your note, not done", AutomationProperties.GetName(f.Window.FirstSaveStep));
    }
}
