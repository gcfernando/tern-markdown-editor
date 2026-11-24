using Tern.App.Help;
using Tern.App.Settings;

namespace Tern.App.Tests;

public sealed class ExpandedPreferencesTests
{
    [Fact]
    public void Added_preferences_preserve_shipped_behavior_and_bound_display_values()
    {
        var settings = new AppSettings();
        Assert.False(settings.ShowSpaces);
        Assert.False(settings.ShowTabs);
        Assert.False(settings.ShowEndOfLine);
        Assert.False(settings.ShowColumnRuler);
        Assert.Equal(80, settings.ColumnRulerPosition);
        Assert.Equal(1.16, settings.EditorLineHeight);
        Assert.True(settings.AllowScrollBelowDocument);
        Assert.True(settings.CutCopyWholeLine);
        Assert.True(settings.TaskGroupByFile);
        Assert.False(settings.FindMatchCase);
        Assert.False(settings.FindWholeWord);
        Assert.False(settings.FindUseRegex);
        settings.PreviewFontFamily = " ";
        settings.ColumnRulerPosition = -12;
        settings.EditorLineHeight = double.NaN;
        settings.Normalize();
        Assert.Equal(new AppSettings().PreviewFontFamily, settings.PreviewFontFamily);
        Assert.Equal(40, settings.ColumnRulerPosition);
        Assert.Equal(1.16, settings.EditorLineHeight);
        settings.ColumnRulerPosition = 900;
        settings.EditorLineHeight = 8;
        settings.Normalize();
        Assert.Equal(240, settings.ColumnRulerPosition);
        Assert.Equal(2, settings.EditorLineHeight);
    }

    [Fact]
    public void Expanded_preferences_round_trip_and_legacy_files_keep_safe_defaults()
    {
        using var f = new ShellFixture();
        var store = new SettingsStore(f.Root.File("expanded-preferences"));
        store.Save(new AppSettings
        {
            ShowSpaces = true, ShowTabs = true, ShowEndOfLine = true, ShowColumnRuler = true,
            ColumnRulerPosition = 110, EditorLineHeight = 1.4, AllowScrollBelowDocument = false,
            CutCopyWholeLine = false, PreviewFontFamily = "Consolas", FindMatchCase = true,
            FindWholeWord = true, FindUseRegex = true, TaskGroupByFile = false,
        });
        var loaded = store.Load();
        Assert.True(loaded.ShowSpaces && loaded.ShowTabs && loaded.ShowEndOfLine && loaded.ShowColumnRuler);
        Assert.Equal(110, loaded.ColumnRulerPosition);
        Assert.Equal(1.4, loaded.EditorLineHeight);
        Assert.False(loaded.AllowScrollBelowDocument);
        Assert.False(loaded.CutCopyWholeLine);
        Assert.Equal("Consolas", loaded.PreviewFontFamily);
        Assert.True(loaded.FindMatchCase && loaded.FindWholeWord && loaded.FindUseRegex);
        Assert.False(loaded.TaskGroupByFile);
        File.WriteAllText(store.FilePath, "{\"EditorFontSize\":19,\"WordWrap\":false}");
        loaded = store.Load();
        Assert.Null(store.LastLoadProblem);
        Assert.Equal(19, loaded.EditorFontSize);
        Assert.False(loaded.WordWrap);
        Assert.True(loaded.CutCopyWholeLine && loaded.AllowScrollBelowDocument && loaded.TaskGroupByFile);
        Assert.Equal(80, loaded.ColumnRulerPosition);
        Assert.Equal(1.16, loaded.EditorLineHeight);
        Assert.False(loaded.FindUseRegex);
        Assert.Equal(59, FeatureCatalog.SettingsFeatures.Count);
    }
}
