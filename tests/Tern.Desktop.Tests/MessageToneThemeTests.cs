using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;
using Tern.App.Settings;
using Tern.App.Shell;
using Tern.Desktop.Themes;
using Tern.Desktop.Views;

namespace Tern.Desktop.Tests;

/// <summary>Every message level must stay readable and distinct on every surface of every theme.</summary>
public sealed class MessageToneThemeTests
{
    private static readonly string[] Surfaces =
    [
        ThemeManager.Bg, ThemeManager.Panel, ThemeManager.Surface, ThemeManager.Hover, ThemeManager.AccentSoft,
        ThemeManager.LineHighlight, ThemeManager.CanvasBackground,
    ];

    private static readonly string[] ThemeNames = ["Midnight", "Paper", "Graphite", "Harbor", "Linen", "Mist"];

    public static TheoryData<string> Themes() => new(ThemeNames);

    private static ResourceDictionary Palette(string theme) => theme switch
    {
        "Midnight" => ThemeManager.Midnight(),
        "Paper" => ThemeManager.Paper(),
        "Graphite" => ThemeManager.Graphite(),
        "Harbor" => ThemeManager.Harbor(),
        "Linen" => ThemeManager.Linen(),
        _ => ThemeManager.Mist(),
    };

    [AvaloniaFact]
    public void The_list_covers_every_fixed_theme()
    {
        var expected = Enum.GetValues<ThemeChoice>().Where(theme => theme != ThemeChoice.System).Select(theme => theme.ToString()).Order();
        Assert.Equal(expected, ThemeNames.Order());
    }

    [AvaloniaTheory]
    [MemberData(nameof(Themes))]
    public void Each_level_is_readable_on_every_surface_and_on_its_own_bar(string theme)
    {
        var palette = Palette(theme);
        Color Read(string key) => Assert.IsAssignableFrom<ISolidColorBrush>(palette[key]).Color;
        foreach (var tone in Enum.GetValues<MessageTone>())
        {
            var foreground = Read(MessageTones.Foreground(tone));
            Assert.True(Contrast(foreground, Read(MessageTones.Background(tone))) >= 4.5, $"{theme} {tone} on its bar");
            Assert.True(Contrast(Read(ThemeManager.Text), Read(MessageTones.Background(tone))) >= 4.5, $"{theme} body text on {tone} bar");
            Assert.True(Contrast(Read(ThemeManager.OnDanger), foreground) >= 4.5, $"{theme} {tone} badge symbol");
            foreach (var surface in Surfaces)
                Assert.True(Contrast(foreground, Read(surface)) >= 4.5, $"{theme} {tone} on {surface}: {Contrast(foreground, Read(surface)):F2}");
        }
    }

    [AvaloniaTheory]
    [MemberData(nameof(Themes))]
    public void Levels_are_distinct_and_success_is_not_the_selection_color(string theme)
    {
        var palette = Palette(theme);
        Color Read(string key) => Assert.IsAssignableFrom<ISolidColorBrush>(palette[key]).Color;
        var tones = Enum.GetValues<MessageTone>();
        Assert.Equal(tones.Length, tones.Select(tone => Read(MessageTones.Foreground(tone))).Distinct().Count());
        Assert.Equal(tones.Length, tones.Select(tone => Read(MessageTones.Background(tone))).Distinct().Count());
        foreach (var first in tones)
            foreach (var second in tones.Where(tone => tone > first))
                Assert.True(HueGap(Read(MessageTones.Foreground(first)), Read(MessageTones.Foreground(second))) >= 20, $"{theme} {first}/{second}");
        Assert.NotEqual(Read(ThemeManager.AccentSoft), Read(ThemeManager.SuccessBackground));
    }

    [AvaloniaTheory]
    [MemberData(nameof(Themes))]
    public void Focused_dropdown_uses_the_theme_color_and_not_windows_blue(string theme)
    {
        var application = Application.Current!;
        var box = new ComboBox { ItemsSource = new[] { "One" }, SelectedIndex = 0 };
        var window = new Window { Content = box };
        try
        {
            ThemeManager.Apply(application, Enum.Parse<ThemeChoice>(theme));
            window.Show();
            box.Focus();
            window.UpdateLayout();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            var highlight = box.GetVisualDescendants().OfType<Border>().Single(border => border.Name == "HighlightBackground");
            var palette = Palette(theme);
            Assert.Equal(Assert.IsAssignableFrom<ISolidColorBrush>(palette[ThemeManager.AccentSoft]).Color, Assert.IsAssignableFrom<ISolidColorBrush>(highlight.Background).Color);
            Assert.True(Contrast(Assert.IsAssignableFrom<ISolidColorBrush>(palette[ThemeManager.Text]).Color, Assert.IsAssignableFrom<ISolidColorBrush>(highlight.Background).Color) >= 4.5);
        }
        finally { window.Close(); ThemeManager.Apply(application, ThemeChoice.Midnight); }
    }

    [AvaloniaFact]
    public void Message_bar_colors_follow_a_theme_switch()
    {
        var application = Application.Current!;
        var window = new Window();
        var bar = new MessageBar();
        window.Content = bar;
        try
        {
            window.Show();
            bar.Show(MessageTone.Warning, "Check this.");
            ThemeManager.Apply(application, ThemeChoice.Harbor);
            window.UpdateLayout();
            var harbor = Assert.IsAssignableFrom<ISolidColorBrush>(bar.Background).Color;
            ThemeManager.Apply(application, ThemeChoice.Linen);
            window.UpdateLayout();
            var linen = Assert.IsAssignableFrom<ISolidColorBrush>(bar.Background).Color;
            Assert.Equal(Assert.IsAssignableFrom<ISolidColorBrush>(ThemeManager.Linen()[ThemeManager.WarningBackground]).Color, linen);
            Assert.NotEqual(harbor, linen);
        }
        finally { window.Close(); ThemeManager.Apply(application, ThemeChoice.Midnight); }
    }

    [AvaloniaFact]
    public void A_stated_level_is_not_replaced_by_a_guess_from_the_words()
    {
        var label = new TextBlock();
        var bar = new MessageBar(label, followText: true);
        bar.Show(MessageTone.Success, "No error was found and the failed step now works.");
        Assert.Equal(MessageTone.Success, bar.Tone);
        label.Text = "Could not save the file.";
        Assert.Equal(MessageTone.Error, bar.Tone);
        bar.Clear();
        label.Text = "Saved.";
        Assert.True(bar.IsVisible && label.IsVisible);
    }

    [AvaloniaFact]
    public void A_valid_link_is_never_red_because_of_a_word_in_its_target()
    {
        var window = new InsertLinkWindow("Plan", null, [new Tern.Markdown.HeadingInfo(1, "Error handling", "error-handling", 1, new Tern.Markdown.SourceRange(0, 16))]);
        var heading = window.HeadingInput;
        window.GetLogicalDescendantsOfRadio("Heading in this document").IsChecked = true;
        heading.SelectedIndex = 0;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var bar = Assert.Single(Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(window).OfType<MessageBar>());
        Assert.Equal(MessageTone.Success, bar.Tone);
        Assert.Equal("Links to #error-handling", bar.Label.Text);
    }

    [Theory]
    [InlineData("Saved as failed-report.pdf.", "Success")]
    [InlineData("No errors found.", "Success")]
    [InlineData("Nothing was saved.", "Info")]
    [InlineData("Review canceled. Your note was not changed.", "Info")]
    [InlineData("Not connected. Check your AI connection.", "Warning")]
    [InlineData("No printer found. Add one in Windows Settings.", "Warning")]
    [InlineData("Could not finish. The AI asked you to wait a minute.", "Warning")]
    [InlineData("Rate limit reached. Try again later.", "Warning")]
    [InlineData("Your sign-in expired. Sign in again.", "Error")]
    [InlineData("The request timed out.", "Error")]
    [InlineData("Your API key was rejected.", "Error")]
    [InlineData("Response received. Review it before copying text into a document.", "Success")]
    [InlineData("Getting the rest of your notes ready…", "Info")]
    [InlineData("Your answer is ready.", "Success")]
    [InlineData("Export complete · 3 page(s) · incomplete", "Warning")]
    [InlineData("Saved settings are unavailable.", "Error")]
    [InlineData("Saved settings loaded.", "Info")]
    [InlineData("That is not a valid shortcut.", "Error")]
    [InlineData("The name is not valid.", "Error")]
    [InlineData("This link is no longer valid.", "Error")]
    [InlineData("Printing stopped before the last page.", "Warning")]
    [InlineData("Stopped. Your notes were not changed.", "Info")]
    [InlineData("Settings changed. Check them again.", "Warning")]
    [InlineData("Canceled. No document was opened or saved.", "Info")]
    [InlineData("Press Apply or Done. Pending changes are kept.", "Info")]
    public void Status_words_get_the_right_level(string text, string tone) => Assert.Equal(tone, MessageTones.Classify(text).ToString());

    [Fact]
    public void Opening_a_file_in_a_folder_named_error_reports_success()
    {
        Assert.Equal((MessageTone.Success, @"Opened C:\Users\Error Reports\guide.md"), AboutWindow.OpenResult(null, @"C:\Users\Error Reports\guide.md"));
        Assert.Equal(MessageTone.Error, AboutWindow.OpenResult("Windows could not open this file.", @"C:\guide.md").Tone);
    }

    [AvaloniaFact]
    public async Task Creating_a_file_named_error_log_is_a_success()
    {
        using var fixture = new DesktopFixture();
        await File.WriteAllTextAsync(Path.Combine(fixture.Docs, "seed.md"), "# Seed");
        Assert.True(await fixture.Shell.OpenWorkspaceAsync(fixture.Docs));
        await fixture.Window.CreateWorkspaceEntryAsync(folder: false, "error-log");
        Assert.True(File.Exists(Path.Combine(fixture.Docs, "error-log.md")));
        Assert.Equal("Created error-log.md", fixture.Shell.StatusMessage);
        Assert.Equal(StatusSeverity.Success, fixture.Shell.StatusSeverity);
    }

    [Fact]
    public void System_error_text_is_replaced_by_a_plain_reason()
    {
        Assert.Equal("Images must be at most 16 MiB.", UserProblem.Plain(new IOException("Images must be at most 16 MiB.")));
        Assert.Equal("Enter a name.", UserProblem.Plain(new ArgumentException("Enter a name", "name")));
        Assert.StartsWith("Windows denied access", UserProblem.Plain(new UnauthorizedAccessException(@"Access to the path 'C:\secret\a.md' is denied.")));
        Assert.DoesNotContain("Sequence", UserProblem.Plain(new InvalidOperationException("Sequence contains no elements")));
        Assert.DoesNotContain("secret", UserProblem.Plain(new System.ComponentModel.Win32Exception(5, @"C:\secret failed")));
    }

    private static double HueGap(Color first, Color second)
    {
        var gap = Math.Abs(first.ToHsl().H - second.ToHsl().H);
        return Math.Min(gap, 360 - gap);
    }

    private static double Contrast(Color first, Color second)
    {
        static double Channel(byte value)
        {
            var v = value / 255d;
            return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }
        static double Light(Color c) => 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
        var (a, b) = (Light(first), Light(second));
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }
}
