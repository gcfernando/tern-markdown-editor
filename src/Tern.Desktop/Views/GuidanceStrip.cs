using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Tern.App.Commands;
using Tern.Desktop.Themes;

namespace Tern.Desktop.Views;

internal enum DialogGuidance
{
    About, Connections, Export, Help, History, Image, Link, Table, Create,
    AIReview, ItemName, Metadata, Merge, Move, Print, OpenUrl, Confirmation, Recovery,
    Reset, SaveFragments, RenameLinks, Settings, Shortcuts, Templates
}

/// <summary>Persistent contextual help, shared by the main window and dialogs.</summary>
internal sealed class GuidanceStrip : Border
{
    private readonly TextBlock _message = GuideParts.Caption("");
    private readonly Button _dismiss = new() { Content = "Dismiss guidance", Classes = { "flat" }, IsVisible = false };
    internal void SetDismissAction(Action dismiss)
    {
        _dismiss.IsVisible = true;
        _dismiss.Click += (_, _) => dismiss();
    }
    public string Message { get => _message.Text ?? ""; set => _message.Text = value; }
    public GuidanceStrip() : this("Open a note or choose File → New document to start writing.") { }

    public GuidanceStrip(string message, string topic = "")
    {
        Name = "NextStepGuidance";
        Message = message;
        Padding = new Thickness(0, 4, 0, 0);
        BorderThickness = new Thickness(0, 1, 0, 0);
        this[!BorderBrushProperty] = this.GetResourceObservable(ThemeManager.Border).ToBinding();
        _message.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetName(_message, "Next step guidance");
        var more = new Button { Content = "Learn more", Classes = { "flat" }, [!Button.FontSizeProperty] = GuideParts.Font("Caption").ToBinding(), MinHeight = 24, Padding = new Thickness(8, 2) };
        more.IsVisible = topic != "Learn Tern";
        AutomationProperties.SetName(more, "Learn more about this screen");
        more.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(this) is not Window owner) return;
            var current = owner;
            while (current is not MainWindow && current.Owner is Window parent) current = parent;
            if (current is MainWindow main) await main.ShowContextGuidanceAsync(topic, owner);
            else HelpWindow.ShowFor(owner, () => new HelpWindow(CommandRegistry.CreateDefault()), question: topic.Length > 0 ? topic : null);
        };
        var grid = new Grid { ColumnDefinitions = new("*,Auto,Auto"), [!Grid.ColumnSpacingProperty] = GuideParts.Space(12).ToBinding(), Children = { _message, more, _dismiss } };
        Grid.SetColumn(more, 1); Grid.SetColumn(_dismiss, 2); Child = grid;
    }

    internal static void Install(Window window, DialogGuidance key)
    {
        if (window.Content is not Control content) return;
        if (content is HelpTutorView) return;
        var (message, topic) = key switch
        {
            DialogGuidance.Help => ("Choose a goal in Guides, or ask a Tern question in Ask & learn.", "Learn Tern"),
            DialogGuidance.About => ("Read the version and notices. Close returns to your document.", "About Tern"),
            DialogGuidance.Connections => ("Choose a service, configure its details, then validate and save the connection.", "AI connections"),
            DialogGuidance.Settings => ("Choose a category, review your changes, then use Apply or Done. Cancel asks before discarding unapplied changes.", "Settings"),
            DialogGuidance.Shortcuts => ("Edit shortcuts here, then use Apply or Done. Cancel keeps the last applied assignments.", "Keyboard shortcuts"),
            DialogGuidance.Print => ("Review the selected pages, then choose Print. Open selected PDF hands them to an external reader instead.", "Print preview"),
            DialogGuidance.Image => ("Choose an image, review its description and destination, then confirm the insertion.", "Insert image"),
            DialogGuidance.Link => ("Review the link label and exact destination before confirming.", "Insert link"),
            DialogGuidance.Table => ("Choose the table dimensions, check the preview, then insert it at the cursor or selection.", "Insert table"),
            DialogGuidance.Create => ("Choose a name and destination. Create makes only the item described above.", "New file"),
            DialogGuidance.ItemName => ("Enter a valid file or folder name. Cancel leaves the existing item unchanged.", "Rename document"),
            DialogGuidance.Export => ("Choose a format, scope and output options, then export a separate copy. The source stays unchanged.", "Export"),
            DialogGuidance.History => ("Select a saved version and compare it. Restoring opens a separate copy unless you explicitly replace the open document.", "Version history"),
            DialogGuidance.AIReview => ("Review the exact destination and text before sending. Apply only changes you have reviewed.", "AI writing"),
            DialogGuidance.Metadata => ("Edit the tags, then apply them to the document. Save is separate.", "Edit tags"),
            DialogGuidance.Merge => ("Review every conflict and the merged result before accepting. Cancel preserves your current edits.", "Merge changes"),
            DialogGuidance.Move => ("Choose the destination folder, then move the selected item. Cancel leaves it in place.", "Move"),
            DialogGuidance.OpenUrl => ("Review the URL. Open working copy uses the cache; Fetch latest makes a request; Save portable copy saves the document and its assets.", "Open Markdown URL"),
            DialogGuidance.Confirmation => ("Review the exact scope above. Cancel leaves the requested action unapproved.", "Confirmations"),
            DialogGuidance.Recovery => ("Compare recovered edits with the disk file. Restore as unsaved copy preserves the original.", "Recovery"),
            DialogGuidance.Reset => ("Select only the application data you intend to reset. Documents and open drafts are preserved.", "Reset Tern"),
            DialogGuidance.SaveFragments => ("Inspect and compare interrupted-save fragments before opening or deleting them.", "Recovered save fragments"),
            DialogGuidance.RenameLinks => ("Review each proposed link update. Keep links unchanged preserves the rename without applying link edits.", "Review links after rename"),
            DialogGuidance.Templates => ("Choose a template to create a separate dated draft. Template source files remain unchanged.", "New from Template"),
            _ => throw new ArgumentOutOfRangeException(nameof(key))
        };
        var strip = new GuidanceStrip(message, topic) { Margin = new Thickness(12, 0, 12, 8) };
        if (window is HelpWindow && content is TabControl sections)
        {
            strip.IsVisible = sections.SelectedIndex == 0;
            sections.SelectionChanged += (_, _) => strip.IsVisible = sections.SelectedIndex == 0;
        }
        if (content is Grid grid)
        {
            if (grid.RowDefinitions.Count == 0) grid.RowDefinitions.Add(new RowDefinition(GridLength.Star));
            Grid.SetRow(strip, grid.RowDefinitions.Count);
            Grid.SetColumnSpan(strip, Math.Max(1, grid.ColumnDefinitions.Count));
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto)); grid.Children.Add(strip);
        }
        else
        {
            window.Content = null;
            var frame = new Grid { RowDefinitions = new("*,Auto"), Children = { content, strip } };
            Grid.SetRow(strip, 1); window.Content = frame;
        }
    }
}
