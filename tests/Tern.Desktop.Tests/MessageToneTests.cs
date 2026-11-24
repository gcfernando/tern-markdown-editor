using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Tern.Desktop.Services;
using Tern.Desktop.Themes;
using Tern.Desktop.Views;

namespace Tern.Desktop.Tests;

public sealed class MessageToneTests
{
    [Theory]
    [InlineData("Could not save the file.", "Error")]
    [InlineData("Connection failed.", "Error")]
    [InlineData("Enter a model name.", "Warning")]
    [InlineData("This file is not saved.", "Warning")]
    [InlineData("Saved as report.pdf.", "Success")]
    [InlineData("Connected.", "Success")]
    [InlineData("No request has been made.", "Info")]
    [InlineData("Could not stop checking…", "Info")]
    public void Status_text_gets_the_right_level(string text, string tone) => Assert.Equal(tone, MessageTones.Classify(text).ToString());

    [AvaloniaTheory]
    [InlineData("Dark")]
    [InlineData("Light")]
    public void Every_level_is_distinct_and_readable(string theme)
    {
        var variant = theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;
        Color Read(string key)
        {
            Assert.True(Application.Current!.TryGetResource(key, variant, out var value), key);
            return Assert.IsAssignableFrom<ISolidColorBrush>(value).Color;
        }
        var tones = Enum.GetValues<MessageTone>();
        Assert.Equal(tones.Length, tones.Select(tone => Read(MessageTones.Foreground(tone))).Distinct().Count());
        Assert.Equal(tones.Length, tones.Select(tone => Read(MessageTones.Background(tone))).Distinct().Count());
        foreach (var tone in tones)
            Assert.True(Contrast(Read(MessageTones.Foreground(tone)), Read(MessageTones.Background(tone))) >= 4.5, tone + " contrast");
    }

    [AvaloniaFact]
    public void Message_bar_shows_words_and_hides_when_cleared()
    {
        var bar = new MessageBar();
        Assert.False(bar.IsVisible);
        bar.Show(MessageTone.Error, "Something went wrong.");
        Assert.True(bar.IsVisible && bar.Label.IsVisible);
        Assert.Equal(MessageTone.Error, bar.Tone);
        Assert.Equal("Something went wrong.", bar.Label.Text);
        bar.Clear();
        Assert.False(bar.IsVisible);
    }

    [Fact]
    public void At_signs_in_notes_survive_the_trip_to_copilot()
    {
        const string text = "Ask sara@example.com or @marco";
        var sent = text.Replace('@', '＠');
        Assert.DoesNotContain('@', sent);
        Assert.Equal(text, AIWorkspaceService.RestoreNoteText(sent));
    }

    private static double Contrast(Color first, Color second)
    {
        static double Light(Color c)
        {
            static double Channel(byte value)
            {
                var v = value / 255d;
                return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
            }
            return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
        }
        var (a, b) = (Light(first), Light(second));
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }
}
