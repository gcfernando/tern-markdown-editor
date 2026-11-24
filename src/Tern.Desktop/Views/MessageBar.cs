using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Tern.Desktop.Themes;

namespace Tern.Desktop.Views;

/// <summary>Tern's four message levels, following the Windows InfoBar severity model.</summary>
internal enum MessageTone { Info, Success, Warning, Error }

internal static partial class MessageTones
{
    public static string Foreground(MessageTone tone) => tone switch
    {
        MessageTone.Success => ThemeManager.Success,
        MessageTone.Warning => ThemeManager.Warning,
        MessageTone.Error => ThemeManager.Danger,
        _ => ThemeManager.Info,
    };

    public static string Background(MessageTone tone) => tone switch
    {
        MessageTone.Success => ThemeManager.SuccessBackground,
        MessageTone.Warning => ThemeManager.WarningBackground,
        MessageTone.Error => ThemeManager.ErrorBackground,
        _ => ThemeManager.InfoBackground,
    };

    /// <summary>Chooses a level for existing status text. Work in progress is always information.</summary>
    public static MessageTone Classify(string text)
    {
        var value = text.Trim();
        if (value.EndsWith('…') || value.EndsWith("...", StringComparison.Ordinal)) return MessageTone.Info;
        // A file called failed-report.pdf says nothing about whether this step worked.
        value = FileNames().Replace(value, "file");
        if (WaitWords().IsMatch(value)) return MessageTone.Warning;
        if (NoProblemWords().IsMatch(value)) return MessageTone.Success;
        if (ErrorWords().IsMatch(value)) return MessageTone.Error;
        if (WarningWords().IsMatch(value)) return MessageTone.Warning;
        if (NothingDoneWords().IsMatch(value)) return MessageTone.Info;
        return SuccessWords().IsMatch(value) ? MessageTone.Success : MessageTone.Info;
    }

    /// <summary>Keeps a status label's color matched to the meaning of its text.</summary>
    public static TextBlock Follow(TextBlock label)
    {
        void Apply()
        {
            if (string.IsNullOrWhiteSpace(label.Text)) { label.ClearValue(TextBlock.ForegroundProperty); return; }
            label[!TextBlock.ForegroundProperty] = label.GetResourceObservable(Foreground(Classify(label.Text))).ToBinding();
        }
        label.PropertyChanged += (_, e) => { if (e.Property == TextBlock.TextProperty) Apply(); };
        Apply();
        return label;
    }

    [GeneratedRegex(@"(?:[A-Za-z]:[\\/]|\\\\)\S+|\b[\w-]+(?:\.[\w-]+)*\.(?:md|markdown|txt|pdf|html?|png|jpe?g|gif|webp|bmp|json|csv|docx?)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 100)]
    private static partial Regex FileNames();

    [GeneratedRegex(@"\b(wait (until|a minute|a moment|before)|asked you to wait|rate limit(ing)?|too many requests)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 100)]
    private static partial Regex WaitWords();

    [GeneratedRegex(@"\bno (errors?|problems?)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 100)]
    private static partial Regex NoProblemWords();

    [GeneratedRegex(@"\b(no|nothing|not)\b[^.]{0,40}\b(saved|applied|sent|created|copied|exported|printed|done)\b|\bcancell?ed\b|\bpending\b|\bchoose apply\b|^getting\b|^ready to\b|^result received\b|\bsaved (settings|mcp)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 100)]
    private static partial Regex NothingDoneWords();

    [GeneratedRegex(@"\b(could not|couldn't|cannot|can't|failed|failure|error|invalid|denied|unable|unavailable|not a valid|not valid|no longer valid|timed out|took too long|expired|rejected|no remaining credits|exited with code|having a problem|reply was too long)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 100)]
    private static partial Regex ErrorWords();

    [GeneratedRegex(@"\b(warning|must|needs?|required|please|too (long|large|many|much)|not (saved|available|ready|found|connected)|no longer available|does not|(?<=\w )stopped|incomplete|would not work|no printer|select less|offline|unsaved|wait)\b|(?<!\b(?:not|no \w+(?: was| were)?) )\bchanged\b|^(enter|choose|select|add)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 100)]
    private static partial Regex WarningWords();

    [GeneratedRegex(@"\b(saved|done|ready|copied|connected|complete|completed|exported|sent|passed|applied|created|printed|fixed|valid|succeeded|response received)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 100)]
    private static partial Regex SuccessWords();
}

/// <summary>An inline message with color, an icon and words, so meaning never depends on color alone.</summary>
internal sealed class MessageBar : Border
{
    private readonly Border _badge = new() { Width = 20, Height = 20, CornerRadius = new CornerRadius(10), VerticalAlignment = VerticalAlignment.Top };
    private readonly TextBlock _glyph = new() { FontWeight = FontWeight.Bold, FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };

    private bool _settingText;

    public MessageBar(TextBlock? label = null, bool followText = false)
    {
        Label = label ?? GuideParts.Body("");
        Label.TextWrapping = TextWrapping.Wrap;
        Label.VerticalAlignment = VerticalAlignment.Center;
        BorderThickness = new Thickness(3, 1, 1, 1);
        CornerRadius = new CornerRadius(6);
        Padding = new Thickness(10, 7);
        IsVisible = false;
        _badge.Child = _glyph;
        _glyph[!TextBlock.ForegroundProperty] = this.GetResourceObservable(ThemeManager.OnDanger).ToBinding();
        var row = new Grid { ColumnDefinitions = new("Auto,*"), ColumnSpacing = 8 };
        row.Children.Add(_badge);
        Grid.SetColumn(Label, 1);
        row.Children.Add(Label);
        Child = row;
        if (!followText) { Label.IsVisible = false; return; }
        Label.PropertyChanged += (_, e) =>
        {
            if (e.Property != TextBlock.TextProperty || _settingText) return;
            if (string.IsNullOrWhiteSpace(Label.Text)) IsVisible = false;
            else { ApplyTone(MessageTones.Classify(Label.Text)); Label.IsVisible = IsVisible = true; }
        };
    }

    public TextBlock Label { get; }
    public MessageTone Tone { get; private set; }

    public void Show(MessageTone tone, string text)
    {
        ApplyTone(tone);
        // The caller knows the level, so a following label must not guess it again from the words.
        _settingText = true;
        try { Label.Text = text; }
        finally { _settingText = false; }
        Label.IsVisible = IsVisible = true;
    }

    public void Clear()
    {
        Label.Text = "";
        Label.IsVisible = IsVisible = false;
    }

    private void ApplyTone(MessageTone tone)
    {
        Tone = tone;
        _glyph.Text = tone switch { MessageTone.Success => "✓", MessageTone.Warning => "!", MessageTone.Error => "×", _ => "i" };
        var foreground = MessageTones.Foreground(tone);
        this[!BackgroundProperty] = this.GetResourceObservable(MessageTones.Background(tone)).ToBinding();
        this[!BorderBrushProperty] = this.GetResourceObservable(foreground).ToBinding();
        _badge[!BackgroundProperty] = this.GetResourceObservable(foreground).ToBinding();
        AutomationProperties.SetLiveSetting(Label, tone == MessageTone.Error ? AutomationLiveSetting.Assertive : AutomationLiveSetting.Polite);
    }
}
