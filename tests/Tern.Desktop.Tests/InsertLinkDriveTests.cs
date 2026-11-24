using Avalonia.Headless.XUnit;
using Tern.Desktop.Views;

namespace Tern.Desktop.Tests;

public sealed class InsertLinkDriveTests
{
    [Theory]
    [InlineData(@"C:\notes\plan.md", @"D:\shared\my file.md", null)]
    [InlineData(@"C:\notes\plan.md", @"\\server\share\a.md", null)]
    [InlineData(@"C:\notes\plan.md", @"C:\notes\sub\my file.md", "sub/my%20file.md")]
    [InlineData(@"C:\notes\plan.md", @"C:\other\REPOSITORY_STANDARDS.md", "../other/REPOSITORY_STANDARDS.md")]
    public void Browse_builds_a_relative_link_or_refuses_another_drive(string source, string target, string? expected) =>
        Assert.Equal(expected, InsertLinkWindow.RelativeDestination(source, target));

    [Theory]
    [InlineData(@"D:\shared\a.md", null)]
    [InlineData("file:///D:/shared/a.md", null)]
    [InlineData("D%3A/shared/a.md", null)]
    [InlineData(@"C:\notes\sub\a.md", "sub/a.md")]
    [InlineData("file:///C:/notes/sub/a.md", "sub/a.md")]
    [InlineData("sub/a.md#part", "sub/a.md#part")]
    public void Typed_paths_become_working_links_or_are_refused(string typed, string? expected) =>
        Assert.Equal(expected, InsertLinkWindow.LocalDestination(@"C:\notes\plan.md", typed));

    [AvaloniaFact]
    public void Another_drive_is_warned_about_before_insert()
    {
        var folder = Path.Combine(Path.GetTempPath(), "tern-link-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var source = Path.Combine(folder, "note.md");
            File.WriteAllText(source, "# Note");
            var other = char.ToUpperInvariant(source[0]) == 'Z' ? "Y" : "Z";
            var window = new InsertLinkWindow("Plan", source, []);
            var local = window.GetLogicalDescendantsOfRadio("Local file");
            local.IsChecked = true;
            window.DestinationInput.Text = other + @":\shared\plan.md";
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.False(window.InsertButton.IsEnabled);
            Assert.Contains(window.LinkMessages(), text => text == InsertLinkWindow.OtherDriveWarning);
        }
        finally { Directory.Delete(folder, true); }
    }
}

internal static class InsertLinkWindowTestExtensions
{
    public static Avalonia.Controls.RadioButton GetLogicalDescendantsOfRadio(this Avalonia.Controls.Window window, string label) =>
        Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(window).OfType<Avalonia.Controls.RadioButton>().Single(button => Equals(button.Content, label));

    public static IEnumerable<string?> LinkMessages(this Avalonia.Controls.Window window) =>
        Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(window).OfType<Avalonia.Controls.TextBlock>().Select(text => text.Text);
}
