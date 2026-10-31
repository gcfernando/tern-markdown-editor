using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tern.App.Settings;
using Tern.Desktop.Views;

namespace Tern.Desktop.Tests;

public sealed class AddDestinationDialogTests
{
    [AvaloniaTheory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void Destination_and_compact_forms_fit_content_and_keep_actions_reachable_at_text_scales(double scale)
    {
        using var fixture = new DesktopFixture();
        fixture.Shell.Settings.UiScale = scale;
        fixture.Shell.Settings.Theme = ThemeChoice.Midnight;
        fixture.Window.ApplySettings();
        fixture.Window.ApplyTheme();
        string? parentProblem = null;
        Window[] dialogs =
        [
            new AddDestinationWindow("Company", @"C:\Gehan\My\Company", true, "Create document"),
            new PromptWindow("Remove reference", "Remove this reference from Recent? The saved file stays on disk.", ["Remove reference", "Cancel"], 1, 1),
            new InsertTableWindow(),
            new CreateItemWindow(CreateItemKind.Workspace, fixture.Docs, outside: true, validateParent: _ => parentProblem),
            new FileNameWindow(false, fixture.Docs, rename: true, initialName: "renamed.md",
                validateName: name => name == "exists.md" ? "That filename already exists." : null),
        ];
        try
        {
            foreach (var dialog in dialogs)
            {
                dialog.Show(fixture.Window);
                Pump(dialog);
                AssertButtonsInside(dialog);
                var content = Assert.IsAssignableFrom<Control>(dialog.Content);
                content.Measure(new Size(dialog.ClientSize.Width, double.PositiveInfinity));
                var naturalHeight = content.DesiredSize.Height;
                content.InvalidateMeasure();
                Pump(dialog);
                Assert.True(dialog.ClientSize.Height <= naturalHeight + 2,
                    $"{dialog.Title} has blank space beyond its content at text scale {scale}.");
                Capture(dialog, $"{dialog.GetType().Name}-{(int)(scale * 100)}.png");
                if (dialog is AddDestinationWindow)
                {
                    Assert.DoesNotContain(dialog.GetVisualDescendants().OfType<TextBlock>(),
                        label => label.Text == "REVIEW YOUR CHOICE" || label.Text == "Choose where to add");
                    Assert.Contains(dialog.GetVisualDescendants().OfType<TextBlock>(), label => label.Text == @"C:\Gehan\My\Company");
                    dialog.Width = 360;
                    dialog.Height = dialog.MinHeight;
                    Pump(dialog);
                    var scroller = Assert.Single(dialog.GetVisualDescendants().OfType<ScrollViewer>(), viewer => viewer.Content is StackPanel);
                    foreach (var name in new[] { "Use selected location", "Outside all workspaces" })
                    {
                        var button = FindButton(dialog, name);
                        button.BringIntoView();
                        Pump(dialog);
                        var point = button.TranslatePoint(default, scroller)!.Value;
                        Assert.True(point.Y >= -1 && point.Y + button.Bounds.Height <= scroller.Bounds.Height + 1,
                            $"Destination {name} cannot be fully shown at the minimum dialog size.");
                    }
                    AssertButtonsInside(dialog, skipDestinations: true);
                    Capture(dialog, $"destination-narrow-{(int)(scale * 100)}.png");
                }
                if (dialog is InsertTableWindow table)
                {
                    table.RowsInput.Text = "0";
                    Pump(dialog);
                    Assert.False(table.InsertButton.IsEnabled);
                    AssertButtonsInside(dialog);
                    Capture(dialog, $"table-validation-{(int)(scale * 100)}.png");
                }
                if (dialog is CreateItemWindow)
                {
                    var name = Assert.Single(dialog.GetVisualDescendants().OfType<TextBox>(),
                        input => AutomationProperties.GetName(input) == "Workspace name");
                    name.Text = "CON";
                    Pump(dialog);
                    Assert.False(FindButton(dialog, "Create").IsEnabled);
                    Capture(dialog, $"create-validation-{(int)(scale * 100)}.png");
                    AssertButtonsInside(dialog);
                    name.Text = "Review workspace";
                    Pump(dialog);
                    Assert.True(FindButton(dialog, "Create").IsEnabled);
                    parentProblem = "This parent is inside an open workspace or folder. Choose a separate parent directory outside all open roots.";
                    name.Text = "Another workspace";
                    Assert.False(FindButton(dialog, "Create").IsEnabled);
                    Pump(dialog);
                    var error = Assert.Single(dialog.GetVisualDescendants().OfType<TextBlock>(),
                        label => AutomationProperties.GetName(label) == "Creation validation error");
                    Assert.True(error.IsEffectivelyVisible);
                    Assert.Equal(parentProblem, error.Text);
                    var origin = error.TranslatePoint(default, dialog)!.Value;
                    Assert.InRange(origin.Y, 0, dialog.ClientSize.Height - error.Bounds.Height + 1);
                    AssertInputVisible(name);
                    AssertButtonsInside(dialog);
                    Capture(dialog, $"create-parent-validation-{(int)(scale * 100)}.png");
                    parentProblem = null;
                    name.Text = "Review workspace";
                    Assert.True(FindButton(dialog, "Create").IsEnabled);
                }
                if (dialog is FileNameWindow)
                {
                    var input = Assert.Single(dialog.GetVisualDescendants().OfType<TextBox>(),
                        field => AutomationProperties.GetName(field) == "File name");
                    var rename = Assert.Single(dialog.GetVisualDescendants().OfType<Button>(), button => Equals(button.Content, "Rename"));
                    var context = Assert.Single(dialog.GetVisualDescendants().OfType<TextBlock>(),
                        label => AutomationProperties.GetName(label) == "Target folder or document context");
                    Assert.Equal(fixture.Docs, context.Text);
                    Assert.Equal(fixture.Docs, ToolTip.GetTip(context));
                    Assert.Equal(Avalonia.Media.TextTrimming.None, context.TextTrimming);
                    input.Text = "exists.md";
                    Assert.False(rename.IsEnabled);
                    Pump(dialog);
                    AssertButtonsInside(dialog);
                    var visibleError = Assert.Single(dialog.GetVisualDescendants().OfType<TextBlock>(),
                        label => AutomationProperties.GetName(label) == "Filename validation error");
                    var errorOrigin = visibleError.TranslatePoint(default, dialog)!.Value;
                    Assert.InRange(errorOrigin.Y, 0, dialog.ClientSize.Height - visibleError.Bounds.Height + 1);
                    AssertInputVisible(input);
                    Capture(dialog, $"filename-validation-{(int)(scale * 100)}.png");
                    input.Text = "available.md";
                    Assert.True(rename.IsEnabled);
                    var error = Assert.Single(dialog.GetVisualDescendants().OfType<TextBlock>(),
                        label => AutomationProperties.GetName(label) == "Filename validation error");
                    Assert.False(error.IsVisible);
                }
                dialog.Close();
            }
        }
        finally
        {
            foreach (var dialog in dialogs) dialog.Close();
            fixture.Shell.Settings.UiScale = 1;
            fixture.Window.ApplySettings();
        }
    }

    [AvaloniaTheory]
    [InlineData("Use selected location", AddDestinationChoice.Selected)]
    [InlineData("Outside all workspaces", AddDestinationChoice.Outside)]
    [InlineData("Cancel destination choice", AddDestinationChoice.Cancel)]
    public async Task Only_an_explicit_destination_button_returns_its_choice(string buttonName, AddDestinationChoice expected)
    {
        using var fixture = new DesktopFixture();
        var pending = AddDestinationWindow.AskAsync(fixture.Window, "Company", fixture.Docs, true, "Create file");
        var dialog = Assert.Single(fixture.Window.OwnedWindows.OfType<AddDestinationWindow>());
        Pump(dialog);
        FindButton(dialog, buttonName).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(expected, await pending);
    }

    [AvaloniaFact]
    public async Task Native_close_cancels_destination_without_creating_files_or_tabs()
    {
        using var fixture = new DesktopFixture();
        var pending = AddDestinationWindow.AskAsync(fixture.Window, "Company", fixture.Docs, true);
        var dialog = Assert.Single(fixture.Window.OwnedWindows.OfType<AddDestinationWindow>());
        dialog.Close();
        Assert.Equal(AddDestinationChoice.Cancel, await pending);
        Assert.Empty(fixture.Shell.Tabs);
        Assert.Empty(Directory.GetFileSystemEntries(fixture.Docs));
    }

    private static Button FindButton(Window dialog, string name) =>
        Assert.Single(dialog.GetVisualDescendants().OfType<Button>(), button => AutomationProperties.GetName(button) == name);

    private static void AssertInputVisible(TextBox input)
    {
        var viewport = input.GetVisualAncestors().OfType<ScrollViewer>().First();
        var origin = input.TranslatePoint(default, viewport)!.Value;
        Assert.True(origin.Y >= -1 && origin.Y + input.Bounds.Height <= viewport.Bounds.Height + 1,
            "Validation must keep the focused name field available for correction.");
    }

    private static void AssertButtonsInside(Window dialog, bool skipDestinations = false)
    {
        foreach (var button in dialog.GetVisualDescendants().OfType<Button>().Where(button => button.Content is string || button.Content is StackPanel))
        {
            if (skipDestinations && AutomationProperties.GetName(button) is "Use selected location" or "Outside all workspaces") continue;
            var point = button.TranslatePoint(default, dialog)!.Value;
            Assert.InRange(point.X, 0, dialog.ClientSize.Width - button.Bounds.Width + 1);
            Assert.InRange(point.Y, 0, dialog.ClientSize.Height - button.Bounds.Height + 1);
        }
    }

    private static void Pump(Window window)
    {
        for (var i = 0; i < 5; i++) { window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
    }

    private static void Capture(Window dialog, string filename)
    {
        var directory = Path.Combine(Screenshots.Folder, "destination-dialogs");
        Directory.CreateDirectory(directory);
        using var frame = dialog.CaptureRenderedFrame();
        Assert.NotNull(frame);
        using var output = File.Create(Path.Combine(directory, filename));
        frame.Save(output, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }
}
