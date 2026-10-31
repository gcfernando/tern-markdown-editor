using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tern.App.Settings;
using Tern.Desktop.Services;
using Tern.Desktop.Views;
using Tern.Domain.Documents;
using Tern.Domain.Merging;
using Tern.Domain.Text;
using Tern.Markdown;
using Tern.Recovery;
using Tern.Workspaces;

namespace Tern.Desktop.Tests;

/// <summary>Actual rendered catalog of every custom dialog family; no network, provider or printer request is made.</summary>
public sealed class DialogSizingCatalogTests
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private sealed record CatalogEntry(string Id, Func<Window> Create, params string[] Actions);
    private sealed record DialogMeasurement(string Dialog, double Scale, string State, double Width, double Height, double ClientWidth, double ClientHeight);

    [AvaloniaTheory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public async Task Every_custom_dialog_uses_its_content_specific_default_and_keeps_primary_actions_inside_the_screen(double scale)
    {
        using var fixture = new DesktopFixture();
        SetScale(fixture, scale);
        var catalog = Catalog(fixture);
        var measurements = new List<DialogMeasurement>();
        var covered = new HashSet<Type>();
        try
        {
            foreach (var entry in catalog)
            {
                var dialog = entry.Create();
                try
                {
                    covered.Add(dialog.GetType());
                    dialog.Show(fixture.Window);
                    await Settle(dialog);
                    AssertBounded(dialog, entry.Id);
                    foreach (var label in entry.Actions) AssertActionInside(dialog, label);
                    if (dialog is OpenUrlWindow)
                    {
                        AssertInsideViewport(Named<TextBox>(dialog, "Markdown URL"));
                        AssertInsideViewport(Named<Expander>(dialog, "Markdown URL details"));
                    }
                    else if (dialog is AboutWindow) AssertInsideViewport(Named<Expander>(dialog, "Third-party details"));
                    Capture(dialog, entry.Id, scale, "default");
                    measurements.Add(Measure(dialog, entry.Id, scale, "default"));
                }
                finally { dialog.Close(); Dispatcher.UIThread.RunJobs(); }
            }

            var expected = typeof(MainWindow).Assembly.GetTypes()
                .Where(type => type.Namespace == typeof(MainWindow).Namespace && type.IsSubclassOf(typeof(Window)) && type != typeof(MainWindow))
                .ToHashSet();
            Assert.True(expected.SetEquals(covered),
                "Dialog catalog coverage differs. Missing: " + string.Join(", ", expected.Except(covered).Select(type => type.Name)));
            Assert.True(measurements.Select(item => Math.Round(item.Width)).Distinct().Count() >= 8,
                "The dialog families must retain distinct content-appropriate widths rather than a shared uniform size.");
            var table = measurements.Single(item => item.Dialog == "insert-table");
            var url = measurements.Single(item => item.Dialog == "open-url");
            var history = measurements.Single(item => item.Dialog == "version-history");
            Assert.True(table.Width < url.Width && url.Width < history.Width,
                "The small table form, URL form and multi-pane history comparison need different horizontal space.");
            await File.WriteAllTextAsync(Path.Combine(OutputDirectory, $"catalog-{(int)(scale * 100)}.json"),
                JsonSerializer.Serialize(measurements, JsonOptions));
        }
        finally { SetScale(fixture, 1); }
    }

    [AvaloniaTheory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public async Task Url_details_grow_and_shrink_the_default_dialog_repeatedly_without_blank_residue(double scale)
    {
        using var fixture = new DesktopFixture();
        SetScale(fixture, scale);
        var dialog = new OpenUrlWindow();
        var measurements = new List<DialogMeasurement>();
        try
        {
            dialog.Show(fixture.Window);
            await Settle(dialog);
            var emptyHeight = dialog.ClientSize.Height;
            var emptyStatus = Named<TextBlock>(dialog, "Markdown URL status").Text;
            measurements.Add(Measure(dialog, "open-url", scale, "empty"));
            Capture(dialog, "open-url", scale, "empty");
            var url = Named<TextBox>(dialog, "Markdown URL");
            url.Text = "not a URL";
            await Settle(dialog);
            Assert.NotEqual(emptyStatus, Named<TextBlock>(dialog, "Markdown URL status").Text);
            AssertBounded(dialog, "invalid URL");
            AssertActionInside(dialog, "Close");
            measurements.Add(Measure(dialog, "open-url", scale, "invalid"));
            Capture(dialog, "open-url", scale, "invalid");
            url.Text = "https://example.com/guide.md";
            await Settle(dialog);
            var details = Named<Expander>(dialog, "Markdown URL details");
            Assert.False(details.IsExpanded);
            var collapsed = dialog.ClientSize.Height;
            AssertInsideViewport(url);
            AssertInsideViewport(details);
            Capture(dialog, "open-url", scale, "details-collapsed");
            for (var cycle = 0; cycle < 2; cycle++)
            {
                if (dialog.Screens.ScreenFromWindow(dialog) is { } edgeScreen)
                    dialog.Position = new PixelPoint(edgeScreen.WorkingArea.X + 20, edgeScreen.WorkingArea.Bottom - 160);
                details.IsExpanded = true;
                await Settle(dialog, expansion: true);
                Assert.True(dialog.ClientSize.Height > collapsed + 16,
                    "Expanding URL details must visibly increase the default dialog instead of consuming an unchanged empty box.");
                AssertBounded(dialog, "expanded URL");
                AssertActionInside(dialog, "Download and Open");
                AssertActionInside(dialog, "Close");
                measurements.Add(Measure(dialog, "open-url", scale, "expanded-" + cycle));
                Capture(dialog, "open-url", scale, "details-expanded");
                details.IsExpanded = false;
                await Settle(dialog, expansion: true);
                Assert.InRange(Math.Abs(dialog.ClientSize.Height - collapsed), 0, 2);
                AssertActionInside(dialog, "Close");
                AssertInsideViewport(url);
                AssertInsideViewport(details);
            }
            Capture(dialog, "open-url", scale, "details-recollapsed");
            url.Text = string.Empty;
            await Settle(dialog);
            Assert.InRange(Math.Abs(dialog.ClientSize.Height - emptyHeight), 0, 2);
            Capture(dialog, "open-url", scale, "empty-restored");
            measurements.Add(Measure(dialog, "open-url", scale, "empty-restored"));
            await File.WriteAllTextAsync(Path.Combine(OutputDirectory, $"url-transitions-{(int)(scale * 100)}.json"),
                JsonSerializer.Serialize(measurements, JsonOptions));
        }
        finally { dialog.Close(); SetScale(fixture, 1); }
    }

    [AvaloniaTheory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public async Task About_details_and_table_validation_resize_for_the_content_that_is_actually_visible(double scale)
    {
        using var fixture = new DesktopFixture();
        SetScale(fixture, scale);
        var about = new AboutWindow();
        var table = new InsertTableWindow();
        try
        {
            about.Show(fixture.Window);
            await Settle(about);
            var collapsed = about.ClientSize.Height;
            var details = Named<Expander>(about, "Third-party details");
            details.IsExpanded = true;
            await Settle(about, expansion: true);
            Assert.True(about.ClientSize.Height > collapsed + 16);
            AssertBounded(about, "expanded About");
            AssertActionInside(about, "Close about");
            Capture(about, "about", scale, "details-expanded");
            details.IsExpanded = false;
            await Settle(about, expansion: true);
            Assert.InRange(Math.Abs(about.ClientSize.Height - collapsed), 0, 2);
            Capture(about, "about", scale, "details-recollapsed");
            about.Close();

            table.Show(fixture.Window);
            await Settle(table);
            var validHeight = table.ClientSize.Height;
            table.RowsInput.Text = "0";
            await Settle(table);
            Assert.False(table.InsertButton.IsEnabled);
            Assert.True(table.ClientSize.Height > validHeight);
            AssertActionInside(table, "Insert table");
            Capture(table, "insert-table", scale, "validation");
            table.RowsInput.Text = "3";
            await Settle(table);
            Assert.InRange(Math.Abs(table.ClientSize.Height - validHeight), 0, 2);
            Capture(table, "insert-table", scale, "validation-cleared");
        }
        finally { about.Close(); table.Close(); SetScale(fixture, 1); }
    }

    private static CatalogEntry[] Catalog(DesktopFixture fixture)
    {
        const string current = "# Meeting notes\n\nCurrent saved document.\n";
        var record = new RecoveryRecord(Guid.NewGuid(), Path.Combine(fixture.Docs, "meeting.md"), "meeting.md", 4,
            DateTime.UtcNow, null, TextFileFormat.NewFileDefault, current + "Unsaved local edit.\n");
        var versions = new[] { new DocumentVersion(DateTime.UtcNow.AddHours(-1), "# Meeting notes\n\nEarlier save.\n", "sample") };
        var repair = new RepairPlan([new FileRepair(Path.Combine(fixture.Docs, "index.md"), null, "[Meeting](old.md)",
            [new TextEdit(10, 6, "meeting.md")], [new LinkEditPreview(1, "[Meeting](old.md)", "[Meeting](meeting.md)", 1)])], []);
        return
        [
            new("destination", () => new AddDestinationWindow("Company", fixture.Docs, true, "Create document"), "Cancel destination choice"),
            new("about", () => new AboutWindow(), "Close about"),
            new("ai-connections", () => new AIConnectionsWindow(Path.Combine(fixture.AppData, "catalog-ai"), new EmptyCredentials()), "AI setup next", "Close"),
            new("copilot", () => new CopilotConnectionWindow(), "Send reviewed text", "Close"),
            new("create-item", () => new CreateItemWindow(CreateItemKind.Workspace, fixture.Docs, true), "Create", "Cancel creation"),
            new("export", () => new ExportWindow("meeting.md", null, current, MarkdownProfile.Extended, null, ExportFormat.Html), "Export document", "Close export"),
            new("insert-table", () => new InsertTableWindow(), "Insert table", "Cancel"),
            new("version-history", () => new HistoryWindow("meeting.md", versions, current), "Open selected version as unsaved copy", "Close"),
            new("feature-guide", () => new HelpWindow(fixture.Shell.Registry), "Exit guide", "Next"),
            new("filename", () => new FileNameWindow(false, fixture.Docs, true, "meeting.md"), "Rename", "Cancel"),
            new("mcp", () => new MCPConnectionsWindow(), "Run reviewed tool", "Close"),
            new("merge", () => new MergeWindow("meeting.md", ThreeWayMerger.Merge("one\ntwo\n", "one\nmy change\n", "one\ndisk change\n")), "Accept merged text", "Cancel"),
            new("open-url", () => new OpenUrlWindow(), "Download and Open", "Close"),
            new("prompt", () => new PromptWindow("Close document", "Save changes to meeting.md before closing?", ["Save", "Don't save", "Cancel"], 0, 2), "Save", "Cancel"),
            new("rename-links", () => new RenameLinksWindow(repair), "Apply reviewed link updates", "Keep links unchanged"),
            new("recovery", () => new RecoveryReviewWindow(record, current), "Restore as unsaved copy", "Ask later"),
            new("reset", () => new ResetWindow(fixture.Shell), "Reset selected application data", "Cancel"),
            new("settings", () => new SettingsWindow(new AppSettings { UiScale = fixture.Shell.Settings.UiScale }, fixture.Shell.Registry), "Close settings"),
            new("shortcuts", () => new ShortcutsWindow(fixture.Shell.Registry), "Close shortcuts"),
        ];
    }

    [AvaloniaTheory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public async Task Merge_summary_comparison_choices_and_editable_result_never_overlap(double scale)
    {
        using var fixture = new DesktopFixture();
        SetScale(fixture, scale);
        var dialog = new MergeWindow("meeting.md", ThreeWayMerger.Merge("one\ntwo\n", "one\nmy change\n", "one\ndisk change\n"));
        try
        {
            dialog.Show(fixture.Window);
            await Settle(dialog);
            var summary = Assert.Single(dialog.GetVisualDescendants().OfType<TextBlock>(),
                label => label.Text?.StartsWith("1 conflicting region", StringComparison.Ordinal) == true);
            var panes = new[] { "Common ancestor", "My version", "Version on disk" }.Select(name => Named<TextBox>(dialog, name)).ToArray();
            var choices = new[] { "Keep mine", "Take disk", "Both", "Original" }.Select(name => Named<Button>(dialog, name)).ToArray();
            var resultLabel = Assert.Single(dialog.GetVisualDescendants().OfType<TextBlock>(), label => label.Text == "Merged result (editable)");
            var result = Named<TextBox>(dialog, "Merged result");
            foreach (var pane in panes)
            {
                AssertBefore(summary, pane, dialog);
                foreach (var choice in choices) AssertBefore(pane, choice, dialog);
            }
            foreach (var choice in choices) AssertBefore(choice, resultLabel, dialog);
            AssertBefore(resultLabel, result, dialog);
            AssertBefore(result, Named<Button>(dialog, "Accept merged text"), dialog);
            AssertActionInside(dialog, "Accept merged text");
            Capture(dialog, "merge", scale, "row-geometry");
        }
        finally { dialog.Close(); SetScale(fixture, 1); }
    }

    private static void AssertBefore(Control upper, Control lower, Window dialog)
    {
        var upperOrigin = upper.TranslatePoint(default, dialog)!.Value;
        var lowerOrigin = lower.TranslatePoint(default, dialog)!.Value;
        Assert.True(upperOrigin.Y + upper.Bounds.Height <= lowerOrigin.Y + 1,
            $"{AutomationProperties.GetName(upper)} overlaps {AutomationProperties.GetName(lower)} in {dialog.Title}.");
    }

    private static void AssertBounded(Window dialog, string name)
    {
        Assert.True(dialog.ClientSize.Width > 0 && dialog.ClientSize.Height > 0, name + " did not receive a usable viewport.");
        Assert.True(dialog.Width <= dialog.MaxWidth + 1 && dialog.Height <= dialog.MaxHeight + 1, name + " exceeds its screen bounds.");
        if (dialog.Screens.ScreenFromWindow(dialog) is { } screen)
        {
            Assert.True(dialog.Width <= screen.WorkingArea.Width / screen.Scaling + 1, name + " exceeds the screen width.");
            Assert.True(dialog.Height <= screen.WorkingArea.Height / screen.Scaling + 1, name + " exceeds the screen height.");
            var frame = dialog.FrameSize ?? dialog.ClientSize;
            Assert.True(dialog.Position.X >= screen.WorkingArea.X && dialog.Position.Y >= screen.WorkingArea.Y,
                name + " is positioned above or left of the usable screen.");
            Assert.True(dialog.Position.X + frame.Width * screen.Scaling <= screen.WorkingArea.Right + 1
                && dialog.Position.Y + frame.Height * screen.Scaling <= screen.WorkingArea.Bottom + 1,
                name + " extends below or right of the usable screen after resizing.");
        }
    }

    private static void AssertActionInside(Window dialog, string name)
    {
        var action = Assert.Single(dialog.GetVisualDescendants().OfType<Button>(),
            button => AutomationProperties.GetName(button) == name || Equals(button.Content, name));
        Assert.True(action.IsEffectivelyVisible, $"{dialog.Title}: {name} is hidden.");
        var point = action.TranslatePoint(default, dialog)!.Value;
        Assert.True(action.Bounds.Width > 0 && action.Bounds.Height > 0, $"{dialog.Title}: {name} has no clickable area.");
        Assert.True(point.X >= -1 && point.Y >= -1
            && point.X + action.Bounds.Width <= dialog.ClientSize.Width + 1
            && point.Y + action.Bounds.Height <= dialog.ClientSize.Height + 1,
            $"{dialog.Title}: {name} is outside the client area ({point}, {action.Bounds}, client {dialog.ClientSize}).");
    }

    private static T Named<T>(Window dialog, string name) where T : Control =>
        Assert.Single(dialog.GetVisualDescendants().OfType<T>(), control => AutomationProperties.GetName(control) == name);

    private static void AssertInsideViewport(Control control)
    {
        var viewport = control.GetVisualAncestors().OfType<ScrollViewer>().FirstOrDefault();
        if (viewport is null) return;
        var point = control.TranslatePoint(default, viewport)!.Value;
        Assert.True(point.Y >= -1 && point.Y + control.Bounds.Height <= viewport.Bounds.Height + 1,
            $"{AutomationProperties.GetName(control)} should fit the default compact form, rather than being scrolled out of view.");
    }

    private static void SetScale(DesktopFixture fixture, double scale)
    {
        fixture.Shell.Settings.UiScale = scale;
        fixture.Shell.Settings.Theme = ThemeChoice.Midnight;
        fixture.Window.ApplySettings();
        fixture.Window.ApplyTheme();
    }

    private static async Task Settle(Window dialog, bool expansion = false)
    {
        await Task.Delay(expansion ? 250 : 40);
        for (var i = 0; i < 6; i++) { dialog.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
    }

    private static DialogMeasurement Measure(Window dialog, string id, double scale, string state) =>
        new(id, scale, state, dialog.Width, dialog.Height, dialog.ClientSize.Width, dialog.ClientSize.Height);

    private static string OutputDirectory
    {
        get
        {
            var directory = Path.Combine(Screenshots.Folder, "dialog-sizing-catalog");
            Directory.CreateDirectory(directory);
            return directory;
        }
    }

    private static void Capture(Window dialog, string id, double scale, string state)
    {
        using var frame = dialog.CaptureRenderedFrame();
        Assert.NotNull(frame);
        using var output = File.Create(Path.Combine(OutputDirectory, $"{id}-{(int)(scale * 100)}-{state}.png"));
        frame.Save(output, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }

    private sealed class EmptyCredentials : IAICredentialStore
    {
        public void Save(string profileId, string secret) => throw new InvalidOperationException("Catalog never saves credentials.");
        public string? Read(string profileId) => null;
        public void Remove(string profileId) => throw new InvalidOperationException("Catalog never removes credentials.");
    }
}
