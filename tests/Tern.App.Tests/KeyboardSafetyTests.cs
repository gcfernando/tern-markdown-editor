using Tern.App.Commands;

namespace Tern.App.Tests;

public sealed class KeyboardSafetyTests
{
    [Fact]
    public void No_default_gesture_collides_with_AltGr_or_the_Windows_system_menu()
    {
        var defaults = CommandRegistry.DefaultCommands().Where(c => c.DefaultGesture is not null).ToList();
        foreach (var command in defaults)
        {
            Assert.True(Gesture.TryParse(command.DefaultGesture, out var gesture), command.Id);
            Assert.False(gesture.CollidesWithAltGr, $"{command.Id} default {command.DefaultGesture} is Ctrl+Alt(+Shift)+printable");
        }
        Assert.Equal("Ctrl+Shift+Space", CommandRegistry.CreateDefault().GetGesture(CommandIds.AskAI));
        Assert.DoesNotContain(defaults, c => c.DefaultGesture == "Alt+Space");
        Assert.Equal(defaults.Count, defaults.Select(c => Gesture.TryParse(c.DefaultGesture, out var g) ? g : default).Distinct().Count());
    }

    [Theory]
    [InlineData("Ctrl+Alt+S", true)]
    [InlineData("Ctrl+Alt+Shift+H", true)]
    [InlineData("Ctrl+Alt+2", true)]
    [InlineData("Ctrl+Alt+Space", true)]
    [InlineData("Ctrl+Alt+OemComma", true)]
    [InlineData("Ctrl+Alt+F10", false)]
    [InlineData("Ctrl+Alt+Up", false)]
    [InlineData("Ctrl+Alt+Shift+Back", false)]
    [InlineData("Ctrl+Shift+S", false)]
    [InlineData("Alt+S", false)]
    public void Ctrl_Alt_printable_gestures_are_flagged(string text, bool collides)
    {
        Assert.True(Gesture.TryParse(text, out var gesture));
        Assert.Equal(collides, gesture.CollidesWithAltGr);
    }

    [Fact]
    public void Saved_former_defaults_follow_the_new_default_and_custom_bindings_are_kept()
    {
        var registry = CommandRegistry.CreateDefault();
        var rejected = registry.LoadOverrides(new Dictionary<string, string>
        {
            [CommandIds.SaveAll] = "Ctrl+Alt+S",
            [CommandIds.AskAI] = "Alt+Space",
            [CommandIds.ClearRecent] = "Ctrl+Alt+Shift+H",
            [CommandIds.PasteImage] = "Ctrl+Alt+F9",
        });
        Assert.Empty(rejected);
        Assert.Equal("Ctrl+Shift+F10", registry.GetGesture(CommandIds.SaveAll));
        Assert.Equal("Ctrl+Shift+Space", registry.GetGesture(CommandIds.AskAI));
        Assert.Equal("Ctrl+Alt+Shift+F8", registry.GetGesture(CommandIds.ClearRecent));
        Assert.Equal("Ctrl+Alt+F9", registry.GetGesture(CommandIds.PasteImage));
        Assert.Equal([CommandIds.PasteImage], registry.Overrides.Keys.ToArray());
    }

    [Fact]
    public void A_new_default_never_displaces_a_gesture_the_user_already_assigned()
    {
        var registry = CommandRegistry.CreateDefault();
        registry.LoadOverrides(new Dictionary<string, string> { [CommandIds.Bold] = "Ctrl+Shift+F10" });
        Assert.Equal("Ctrl+Shift+F10", registry.GetGesture(CommandIds.Bold));
        Assert.Null(registry.GetGesture(CommandIds.SaveAll));
    }

    [Fact]
    public void A_deliberately_cleared_binding_stays_cleared()
    {
        var registry = CommandRegistry.CreateDefault();
        registry.LoadOverrides(new Dictionary<string, string> { [CommandIds.SaveAll] = string.Empty });
        Assert.Null(registry.GetGesture(CommandIds.SaveAll));
    }
}
