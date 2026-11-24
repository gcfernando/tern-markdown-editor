using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Tern.App.Settings;
using Tern.Desktop.Views;

namespace Tern.Desktop.Tests;

public sealed class DialogFooterTests
{
    [AvaloniaFact]
    public void Wizard_primary_changes_move_the_default_action_without_duplicate_accent_buttons()
    {
        var next = new Button { Content = "Continue" };
        var send = new Button { Content = "Send reviewed text" };
        var close = new Button { Content = "Close" };
        var actions = GuideParts.Actions(next, close, send);
        actions.SetPrimary(send);
        Assert.True(send.IsDefault);
        Assert.Contains("accent", send.Classes);
        Assert.False(next.IsDefault);
        Assert.DoesNotContain("accent", next.Classes);
        actions.SetPrimary(next);
        Assert.True(next.IsDefault);
        Assert.False(send.IsDefault);
        Assert.True(close.IsCancel);
    }

    [AvaloniaFact]
    public void Explicit_guidance_is_independent_of_dynamic_window_titles()
    {
        var window = new Window { Content = new TextBox() };
        DialogParts.Setup(window, "A document with an unrelated title.md", 600, 400, DialogGuidance.Print);
        try
        {
            window.Show(); window.UpdateLayout();
            var guidance = Assert.Single(window.GetVisualDescendants().OfType<GuidanceStrip>());
            Assert.Contains("choose Print", guidance.Message);
            Assert.DoesNotContain("Review this screen", guidance.Message);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(700)]
    [InlineData(280)]
    public void Semantic_actions_keep_secondary_left_and_primary_before_cancel_without_clipping(double width)
    {
        var primary = new Button { Content = "Apply changes" };
        var cancel = new Button { Content = "Cancel" };
        var secondary = new Button { Content = "Reset everything", Classes = { "danger" } };
        var actions = GuideParts.Actions(primary, cancel, secondary);
        var window = new Window { Width = width, Height = 250, Content = actions };
        try
        {
            window.Show(); window.UpdateLayout();
            Assert.True(primary.IsDefault);
            Assert.Contains("accent", primary.Classes);
            Assert.True(cancel.IsCancel);
            Assert.False(secondary.IsDefault);
            Assert.DoesNotContain("accent", secondary.Classes);
            var p = primary.TranslatePoint(default, window)!.Value;
            var c = cancel.TranslatePoint(default, window)!.Value;
            var s = secondary.TranslatePoint(default, window)!.Value;
            Assert.True(s.Y < p.Y || s.X < p.X);
            Assert.True(p.Y < c.Y || p.X < c.X);
            foreach (var button in new[] { primary, cancel, secondary })
            {
                var point = button.TranslatePoint(default, window)!.Value;
                Assert.True(point.X >= 0 && point.X + button.Bounds.Width <= window.ClientSize.Width + 1);
            }
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Real_dialogs_have_one_safe_accent_default_and_cancel_last()
    {
        using var f = new DesktopFixture();
        Window[] windows = [new SettingsWindow(new AppSettings()), new ShortcutsWindow(f.Shell.Registry),
            new ResetWindow(f.Shell), new InsertTableWindow()];
        foreach (var window in windows)
        {
            try
            {
                window.Show(f.Window); window.UpdateLayout();
                var buttons = window.GetVisualDescendants().OfType<Button>().ToArray();
                var accent = Assert.Single(buttons, b => b.Classes.Contains("accent"));
                Assert.True(accent.IsDefault);
                Assert.DoesNotContain("danger", accent.Classes);
                var cancel = Assert.Single(buttons, b => b.IsCancel);
                var action = accent.TranslatePoint(default, window)!.Value;
                var cancellation = cancel.TranslatePoint(default, window)!.Value;
                Assert.True(ReferenceEquals(accent, cancel) || action.Y < cancellation.Y || action.X < cancellation.X);
                Assert.All(buttons.Where(b => b.Classes.Contains("danger")), b => Assert.False(b.IsDefault));
            }
            finally { window.Close(); }
        }
    }
}
