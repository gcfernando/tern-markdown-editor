using System.Text;
using Tern.Desktop.Services;

namespace Tern.Desktop.Tests;

public sealed class HtmlClipboardTests
{
    internal static byte[] WindowsHtml(string fragment)
    {
        const string template = "Version:1.0\r\nStartHTML:{0:0000000000}\r\nEndHTML:{1:0000000000}\r\nStartFragment:{2:0000000000}\r\nEndFragment:{3:0000000000}\r\n";
        var headerSize = Encoding.UTF8.GetByteCount(string.Format(template, 0, 0, 0, 0));
        const string prefix = "<html><body>caf\u00e9<!--StartFragment-->";
        const string suffix = "<!--EndFragment--></body></html>";
        var start = headerSize + Encoding.UTF8.GetByteCount(prefix);
        var end = start + Encoding.UTF8.GetByteCount(fragment);
        return Encoding.UTF8.GetBytes(string.Format(template, headerSize, end + Encoding.UTF8.GetByteCount(suffix), start, end) + prefix + fragment + suffix);
    }

    [Fact]
    public void Windows_fragment_uses_utf8_byte_offsets_not_character_offsets()
    {
        const string html = "<h1>R\u00e9sum\u00e9 \u4e2d\u6587</h1>";
        Assert.Equal(html, HtmlClipboard.ReadFragment(WindowsHtml(html), true));
        Assert.Equal(html, HtmlClipboard.ReadFragment(Encoding.UTF8.GetBytes(html), false));
    }

    [Fact]
    public void Invalid_offsets_or_utf8_are_rejected_without_partial_conversion()
    {
        Assert.Throws<InvalidDataException>(() => HtmlClipboard.ReadFragment(Encoding.UTF8.GetBytes("StartFragment:999999\r\nEndFragment:9999999"), true));
        Assert.Throws<DecoderFallbackException>(() => HtmlClipboard.ReadFragment([0xff, 0xfe], false));
    }

    [Fact]
    public void Actual_windows_provider_reports_a_synthetic_error_and_suggestions_without_dictionary_writes()
    {
        if (!OperatingSystem.IsWindows()) return;
        var issues = WindowsSpellChecker.Check("This is a beautifull sentence.", CancellationToken.None, "en-US");
        var issue = Assert.Single(issues, item => item.Word == "beautifull");
        Assert.Equal(10, issue.Start);
        Assert.Contains(issue.Suggestions, value => value.Equals("beautiful", StringComparison.OrdinalIgnoreCase));
    }
}
