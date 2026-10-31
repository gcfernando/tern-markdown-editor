using Tern.App.Commands;
using Tern.App.Help;
using Tern.App.Palette;

namespace Tern.App.Tests;

public class FeatureHelpTests
{
    [Theory]
    [InlineData("paste an image", "images")]
    [InlineData("print", "print")]
    [InlineData("recover work", "recover")]
    [InlineData("practice", "practice")]
    public void Everyday_questions_route_to_matching_beginner_lessons(string question, string lesson)
    {
        Assert.Equal(lesson, GuideQuery.Lesson(question));
        Assert.Contains(BeginnerLessons.For(CommandRegistry.CreateDefault()), l => l.Id == lesson);
    }

    [Fact]
    public void Image_output_and_format_reference_match_the_implemented_workflow()
    {
        var help = FeatureCatalog.Search(CommandRegistry.CreateDefault(), null);
        Assert.Equal("Format → Heading → Heading 1 (title)", help.Single(f => f.Id == CommandIds.Heading1).MenuPath);
        Assert.Equal("Format → Insert Code Block", help.Single(f => f.Id == CommandIds.InsertCodeBlock).MenuPath);
        Assert.Equal("Format → Checklist", help.Single(f => f.Id == CommandIds.ToggleTask).MenuPath);
        Assert.Contains("Save a writable document first", help.Single(f => f.Id == CommandIds.InsertImage).Prerequisite, StringComparison.Ordinal);
        Assert.All(new[] { CommandIds.Print, CommandIds.ExportHtml, CommandIds.ExportPdf }, id =>
            Assert.Contains("Opened files count without edits", help.Single(f => f.Id == id).Prerequisite, StringComparison.Ordinal));
        Assert.Contains("copied picture stays on disk", help.Single(f => f.Id == CommandIds.PasteImage).Description, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_registered_command_has_help_and_effective_shortcut()
    {
        var registry = CommandRegistry.CreateDefault();
        registry.SetBinding(CommandIds.Save, "Ctrl+Alt+F12");
        var help = FeatureCatalog.Search(registry, null);
        Assert.Equal(registry.Commands.Count + FeatureCatalog.Subfeatures.Count, help.Count);
        Assert.Equal(help.Count, help.Select(f => f.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.All(registry.Commands, c => Assert.Contains(help, f => f.Id == c.Id && f.LaunchCommandId == c.Id && f.Name == c.Title));
        Assert.All(help, f => { Assert.NotEmpty(f.Description); Assert.NotEmpty(f.Prerequisite); Assert.True(f.Steps.Count >= 3); });
        Assert.Equal("Ctrl+Alt+F12", help.Single(f => f.Id == CommandIds.Save).Shortcut);
    }

    [Theory]
    [InlineData("CRLF", CommandIds.ChangeLineEnding)]
    [InlineData("synchronized scrolling", CommandIds.ViewSplit)]
    [InlineData("encoding characters", CommandIds.ChangeEncoding)]
    public void Search_includes_subfeatures_and_meanings(string query, string expected)
        => Assert.Contains(FeatureCatalog.Search(CommandRegistry.CreateDefault(), query), f => f.Id == expected);

    [Fact]
    public void Palette_finds_line_endings_by_subfeature_name()
        => Assert.Contains(CommandPaletteModel.Filter(CommandRegistry.CreateDefault(), "CRLF"), f => f.CommandId == CommandIds.ChangeLineEnding);

    [Theory]
    [InlineData("BOM", "editor.encoding", CommandIds.ChangeEncoding)]
    [InlineData("reading width", "settings.ReadingWidth", CommandIds.Settings)]
    [InlineData("tags", "inspector.tags", CommandIds.ToggleRightPanel)]
    public void Individual_subfeatures_have_help_lessons_and_launch_their_parent(string query, string id, string command)
    {
        var registry = CommandRegistry.CreateDefault();
        var feature = Assert.Single(FeatureCatalog.Search(registry, query), f => f.Id == id);
        Assert.Equal(command, feature.LaunchCommandId);
        Assert.True(feature.Steps.Count >= 3);
        Assert.Contains(CommandPaletteModel.Filter(registry, query), f => f.CommandId == command);
    }

    [Fact]
    public void Every_exposed_setting_has_valid_property_individual_explanation_and_safe_steps()
    {
        var configurable = typeof(Tern.App.Settings.AppSettings).GetProperties().Where(p => p.CanWrite && p.Name != nameof(Tern.App.Settings.AppSettings.Version)).Select(p => p.Name).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(configurable, FeatureCatalog.SettingsFeatures.Select(f => f.Property).Order(StringComparer.Ordinal).ToArray());
        Assert.All(FeatureCatalog.SettingsFeatures, setting =>
        {
            Assert.NotNull(typeof(Tern.App.Settings.AppSettings).GetProperty(setting.Property));
            Assert.NotEmpty(setting.Description);
            Assert.NotEmpty(setting.How);
            Assert.Equal(4, setting.Help.Steps.Count);
            Assert.Equal(CommandIds.Settings, setting.Help.LaunchCommandId);
            Assert.Contains("Done", setting.Help.Steps[^1], StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Lessons_follow_effective_bindings_and_describe_unbound_commands_without_old_shortcuts()
    {
        var registry = CommandRegistry.CreateDefault();
        Assert.True(registry.SetBinding(CommandIds.CommandPalette, "Ctrl+Alt+F11").Succeeded);
        Assert.True(registry.SetBinding(CommandIds.NewFile, "Ctrl+Alt+F10").Succeeded);
        registry.ClearBinding(CommandIds.Save);
        var feature = FeatureCatalog.Search(registry, null).Single(f => f.Id == CommandIds.Find);
        Assert.Contains("Ctrl+Alt+F11", feature.Steps[1], StringComparison.Ordinal);
        Assert.DoesNotContain("Ctrl+Shift+P", string.Join(' ', feature.Steps), StringComparison.Ordinal);
        var lessons = FeatureCatalog.TutorialsFor(registry);
        Assert.Contains("Ctrl+Alt+F10", lessons[0].Steps[1], StringComparison.Ordinal);
        Assert.Contains("no keyboard shortcut", lessons[0].Steps[3], StringComparison.Ordinal);
        Assert.DoesNotContain("Ctrl+S", string.Join(' ', lessons[0].Steps), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("All Files", "library.files", "nav.libraryFiles")]
    [InlineData("Recent", "library.recent", "nav.libraryRecent")]
    [InlineData("Search", "library.search", "nav.librarySearch")]
    [InlineData("Backlinks", "library.backlinks", "nav.libraryBacklinks")]
    [InlineData("Broken Links", "library.brokenLinks", "nav.libraryBrokenLinks")]
    [InlineData("Tasks", "library.tasks", "nav.libraryTasks")]
    public void Every_library_surface_is_individually_discoverable_and_routes_to_navigation(string query, string id, string command)
    {
        var registry = CommandRegistry.CreateDefault();
        var help = Assert.Single(FeatureCatalog.Search(registry, query), f => f.Id == id);
        Assert.True(registry.TryGet(command, out _));
        Assert.Equal(command, help.LaunchCommandId);
        Assert.True(help.Steps.Count >= 3);
        Assert.NotEmpty(help.Prerequisite);
        Assert.Contains(CommandPaletteModel.Filter(registry, query), f => f.CommandId == command);
    }

    [Fact]
    public void Tutorials_have_steps_and_preserve_documents_by_design()
    {
        Assert.True(FeatureCatalog.Tutorials.Count >= 4);
        Assert.All(FeatureCatalog.Tutorials, t => Assert.True(t.Steps.Count >= 4));
        Assert.Contains("never change", FeatureCatalog.Tutorials[0].Steps[0], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(CommandIds.Save)]
    [InlineData(CommandIds.ClearRecent)]
    [InlineData(CommandIds.Rename)]
    [InlineData(CommandIds.CloseDocuments)]
    public void Tutorial_launch_never_executes_data_changing_commands(string command) => Assert.False(FeatureCatalog.CanLaunchFromGuide(command));

    [Fact]
    public void Purposeful_lessons_cover_workspace_choices_and_real_ai_connection_steps()
    {
        Assert.Contains(FeatureCatalog.Tutorials, t => t.Name == "Why use a workspace?" && t.Steps.Any(s => s.Contains("optional", StringComparison.Ordinal)));
        Assert.Contains(FeatureCatalog.Tutorials, t => t.Name == "Open and switch multiple workspaces" && t.Steps.Count >= 5);
        Assert.Contains(FeatureCatalog.Tutorials, t => t.Name == "Connect a local Ollama model" && t.Steps.Any(s => s.Contains("/api/chat", StringComparison.Ordinal)));
        Assert.Contains(FeatureCatalog.Tutorials, t => t.Name == "Connect a compatible cloud model" && t.Steps.Any(s => s.Contains("API access", StringComparison.Ordinal)));
    }

    [Fact]
    public void Copilot_and_MCP_have_individual_truthful_protocol_and_authorization_lessons()
    {
        var registry = CommandRegistry.CreateDefault();
        var copilot = Assert.Single(FeatureCatalog.Search(registry, "Copilot"), f => f.Id == "ai.copilot");
        Assert.Equal(CommandIds.AIConnections, copilot.LaunchCommandId);
        Assert.Equal(6, copilot.Steps.Count);
        Assert.Contains(copilot.Steps, s => s.Contains("copilot.exe", StringComparison.Ordinal));
        Assert.Contains(copilot.Steps, s => s.Contains("Copilot Requests", StringComparison.Ordinal));
        Assert.Contains(copilot.Steps, s => s.Contains("unverified", StringComparison.Ordinal));
        var mcp = Assert.Single(FeatureCatalog.Search(registry, "MCP"), f => f.Id == "ai.mcp");
        Assert.Contains("2025-11-25", mcp.Description, StringComparison.Ordinal);
        Assert.Contains(mcp.Steps, s => s.Contains("OAuth", StringComparison.Ordinal) && s.Contains("stdio", StringComparison.Ordinal));
        Assert.Contains(mcp.Steps, s => s.Contains("JSON object", StringComparison.Ordinal));
    }
}
