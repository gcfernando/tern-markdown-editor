using Tern.App.Commands;

namespace Tern.App.Tests;

public sealed class CompleteShortcutTests
{
    [Fact]
    public void Ai_help_and_lessons_follow_the_effective_writing_shortcut()
    {
        var registry = CommandRegistry.CreateDefault();
        Assert.True(registry.SetBinding(CommandIds.AskAI, "Ctrl+Alt+F10").Succeeded);
        var workflow = Tern.App.Help.FeatureCatalog.Search(registry, null).Single(f => f.Id == "ai.workflow");
        Assert.Contains(workflow.Steps, step => step.Contains("Ctrl+Alt+F10", StringComparison.Ordinal));
        Assert.DoesNotContain(workflow.Steps, step => step.Contains("Alt+Space", StringComparison.Ordinal));
        var lesson = Tern.App.Help.BeginnerLessons.For(registry).Single(l => l.Id == "ai");
        Assert.Contains(lesson.Steps, step => step.Action.Contains("Ctrl+Alt+F10", StringComparison.Ordinal));
        registry.ClearBinding(CommandIds.AskAI);
        Assert.DoesNotContain(Tern.App.Help.FeatureCatalog.Search(registry, null).Single(f => f.Id == "ai.workflow").Steps,
            step => step.Contains("Alt+Space", StringComparison.Ordinal));
    }

    [Fact]
    public void Swapped_and_moved_bindings_survive_reload()
    {
        var original = CommandRegistry.CreateDefault();
        original.ForceBinding(CommandIds.Save, "Ctrl+O");
        original.ForceBinding(CommandIds.OpenFile, "Ctrl+S");
        original.ForceBinding(CommandIds.Bold, "Ctrl+Z");
        var reloaded = CommandRegistry.CreateDefault();
        Assert.Empty(reloaded.LoadOverrides(original.Overrides));
        Assert.Equal("Ctrl+O", reloaded.GetGesture(CommandIds.Save));
        Assert.Equal("Ctrl+S", reloaded.GetGesture(CommandIds.OpenFile));
        Assert.Equal("Ctrl+Z", reloaded.GetGesture(CommandIds.Bold));
        Assert.Null(reloaded.GetGesture(CommandIds.Undo));
    }

    [Fact]
    public void Reset_reports_conflicts_without_creating_duplicate_bindings()
    {
        var registry = CommandRegistry.CreateDefault();
        registry.ForceBinding(CommandIds.Save, "Ctrl+N");
        var result = registry.ResetBinding(CommandIds.NewFile);
        Assert.Equal(BindingProblem.Conflict, result.Problem);
        Assert.Equal(CommandIds.Save, result.ConflictingCommandId);
        Assert.Null(registry.GetGesture(CommandIds.NewFile));
        Assert.Equal(CommandIds.Save, registry.FindCommandFor(new Gesture(true, false, false, false, "N")));
        registry.ForceBinding(CommandIds.NewFile, "Ctrl+N");
        Assert.True(registry.ResetBinding(CommandIds.NewFile).Succeeded);
        Assert.Null(registry.GetGesture(CommandIds.Save));
    }

    [Fact]
    public void Duplicate_saved_bindings_are_rejected_without_duplicate_fallbacks()
    {
        var registry = CommandRegistry.CreateDefault();
        Assert.Single(registry.LoadOverrides(new Dictionary<string, string>
        {
            [CommandIds.Save] = "Ctrl+O", [CommandIds.OpenFile] = "Ctrl+O",
        }));
        var effective = registry.Commands.Select(c => registry.GetGesture(c.Id)).Where(g => g is not null).ToList();
        Assert.Equal(effective.Count, effective.Distinct().Count());
    }

    [Fact]
    public void Every_registered_command_has_a_unique_valid_default_shortcut()
    {
        var registry = CommandRegistry.CreateDefault();
        var gestures = new HashSet<Gesture>();
        foreach (var command in registry.Commands)
        {
            Assert.True(Gesture.TryParse(command.DefaultGesture, out var gesture), command.Title);
            Assert.True(gestures.Add(gesture), $"Duplicate shortcut for {command.Title}");
            Assert.Equal(command.Id, registry.FindCommandFor(gesture));
        }
    }

    [Fact]
    public void Existing_custom_shortcut_wins_over_a_new_default_and_explicit_unbinding_is_kept()
    {
        var registry = CommandRegistry.CreateDefault();
        Assert.Empty(registry.LoadOverrides(new Dictionary<string, string>
        {
            [CommandIds.Save] = "Ctrl+Shift+E", [CommandIds.ViewSource] = "",
        }));
        Assert.Equal("Ctrl+Shift+E", registry.GetGesture(CommandIds.Save));
        Assert.Null(registry.GetGesture(CommandIds.ExportPdf));
        Assert.Null(registry.GetGesture(CommandIds.ViewSource));
        Assert.True(Gesture.TryParse("Ctrl+Shift+E", out var gesture));
        Assert.Equal(CommandIds.Save, registry.FindCommandFor(gesture));
    }
}
