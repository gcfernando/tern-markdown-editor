using System.Globalization;
using System.Text;
using Avalonia.Input;
using Tern.Rendering.Html;

namespace Tern.Desktop.Services;

internal static class HtmlClipboard
{
    internal static readonly DataFormat<byte[]> WindowsFormat = DataFormat.CreateBytesPlatformFormat("HTML Format");
    internal static readonly DataFormat<byte[]> MimeFormat = DataFormat.CreateBytesPlatformFormat("text/html");

    internal static string ReadFragment(byte[] bytes, bool windows)
    {
        if (bytes.Length > HtmlToMarkdown.MaxInputLength * 4)
            throw new InvalidDataException("The HTML clipboard is too large. Use Paste Plain Text instead.");
        var encoding = new UTF8Encoding(false, true);
        if (!windows) return encoding.GetString(bytes).TrimEnd('\0');
        var header = Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 4096));
        var start = Offset("StartFragment:");
        var end = Offset("EndFragment:");
        if (start < 0 || end < start || end > bytes.Length)
            throw new InvalidDataException("The HTML clipboard has invalid fragment offsets. Use Paste Plain Text instead.");
        return encoding.GetString(bytes, start, end - start);

        int Offset(string label)
        {
            var position = header.IndexOf(label, StringComparison.Ordinal);
            if (position < 0) return -1;
            position += label.Length;
            var length = 0;
            while (position + length < header.Length && char.IsAsciiDigit(header[position + length])) length++;
            return int.TryParse(header.AsSpan(position, length), NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : -1;
        }
    }
}
