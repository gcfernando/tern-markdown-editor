using Tern.App.Help;
using Tern.App.Settings;

namespace Tern.App.Tests;

public sealed class SettingsNavigationTests
{
    [Fact]
    public void Every_setting_has_one_ordered_destination_and_help_uses_its_visible_title()
    {
        var categories = SettingsNavigation.Categories;
        Assert.Equal(11, categories.Count);
        Assert.Equal(categories.Count, categories.Select(category => category.Key).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(categories.Count, categories.Select(category => category.Title).Distinct(StringComparer.Ordinal).Count());
        Assert.All(FeatureCatalog.SettingsFeatures, feature =>
        {
            Assert.Single(categories, category => category.Key == feature.Category);
            Assert.Contains(SettingsNavigation.Title(feature.Category), feature.Help.Category, StringComparison.Ordinal);
            Assert.Contains(SettingsNavigation.Title(feature.Category), feature.Help.Steps[1], StringComparison.Ordinal);
        });
        Assert.Equal(59, FeatureCatalog.SettingsFeatures.Select(feature => feature.Property).Distinct(StringComparer.Ordinal).Count());
        string Category(string property) => Assert.Single(FeatureCatalog.SettingsFeatures, feature => feature.Property == property).Category;
        Assert.Equal("Editor", Category(nameof(AppSettings.EditorFontFamily)));
        Assert.Equal("Editor", Category(nameof(AppSettings.EditorFontSize)));
        Assert.Equal("Reading", Category(nameof(AppSettings.PreviewFontSize)));
        Assert.Equal("Reading", Category(nameof(AppSettings.DefaultViewMode)));
        Assert.Equal("Privacy", Category(nameof(AppSettings.AllowExternalLinkOpening)));
        Assert.Equal("Files", Category(nameof(AppSettings.DefaultEncoding)));
        Assert.Equal("Editor", Category(nameof(AppSettings.ShowSpaces)));
        Assert.Equal("Editor", Category(nameof(AppSettings.ColumnRulerPosition)));
        Assert.Equal("Reading", Category(nameof(AppSettings.PreviewFontFamily)));
        Assert.Equal("Workspace", Category(nameof(AppSettings.FindUseRegex)));
        Assert.Equal("Workspace", Category(nameof(AppSettings.TaskGroupByFile)));
    }
}
