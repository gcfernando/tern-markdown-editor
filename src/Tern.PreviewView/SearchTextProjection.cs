namespace Tern.PreviewView;

/// <summary>Maps leaf source boundaries to rendered glyphs once, rather than re-scanning text for every match.</summary>
internal sealed class SearchTextProjection
{
    private readonly int[] _from;
    private readonly int[] _to;

    public SearchTextProjection(string raw, string displayed)
    {
        _from = new int[raw.Length + 1];
        _to = new int[raw.Length + 1];
        var normalized = raw.Replace("\r\n", "\n", StringComparison.Ordinal);
        var contentStart = normalized.IndexOf(displayed, StringComparison.Ordinal);
        if (contentStart >= 0)
        {
            var position = 0;
            for (var i = 0; i <= raw.Length; i++)
            {
                _from[i] = _to[i] = Math.Clamp(position - contentStart, 0, displayed.Length);
                if (i < raw.Length && !(raw[i] == '\n' && i > 0 && raw[i - 1] == '\r')) position++;
            }
            return;
        }

        // Code panels expand tabs for four-column display without changing their source.
        // Keep Find anchored to original UTF-16 positions, including text after a tab.
        var expanded = CodeDisplay.ExpandTabs(raw);
        contentStart = expanded.IndexOf(displayed, StringComparison.Ordinal);
        if (contentStart >= 0)
        {
            var position = 0; var column = 0;
            for (var i = 0; i < raw.Length; i++)
            {
                _from[i] = Math.Clamp(position - contentStart, 0, displayed.Length);
                var ch = raw[i];
                if (ch == '\t') { var count = CodeDisplay.TabColumns - column % CodeDisplay.TabColumns; position += count; column += count; }
                else
                {
                    position++;
                    if (ch is '\r' or '\n') column = 0;
                    else if (!char.IsLowSurrogate(ch) || i == 0 || !char.IsHighSurrogate(raw[i - 1])) column++;
                }
                _to[i + 1] = Math.Clamp(position - contentStart, 0, displayed.Length);
            }
            _from[raw.Length] = _to[raw.Length];
            return;
        }

        var decoded = new System.Text.StringBuilder();
        for (var i = 0; i < raw.Length;)
        {
            var start = i;
            var displayStart = decoded.Length;
            string value;
            if (raw[i] == '&' && raw.IndexOf(';', i, Math.Min(33, raw.Length - i)) is var semi && semi > i
                && System.Net.WebUtility.HtmlDecode(raw[i..(semi + 1)]) is var entity && entity != raw[i..(semi + 1)])
            { value = entity; i = semi + 1; }
            else if (raw[i] == '\\' && i + 1 < raw.Length && raw[i + 1] is >= '!' and <= '~' && !char.IsLetterOrDigit(raw[i + 1]))
            { value = raw[(i + 1)..(i + 2)]; i += 2; }
            else if (raw[i] == '\r' && i + 1 < raw.Length && raw[i + 1] == '\n')
            { value = "\n"; i += 2; }
            else { value = raw[i++].ToString(); }
            decoded.Append(value);
            for (var j = start; j < i; j++)
            {
                _from[j] = displayStart;
                _to[j + 1] = decoded.Length;
            }
        }
        _from[raw.Length] = decoded.Length;
        if (decoded.ToString() != displayed)
        {
            // Unsupported transformations must not put a highlight on unrelated text.
            Array.Clear(_from);
            Array.Clear(_to);
        }
    }

    public (int Start, int Length) Project(int start, int end)
    {
        var from = _from[Math.Clamp(start, 0, _from.Length - 1)];
        var to = _to[Math.Clamp(end, 0, _to.Length - 1)];
        return (from, Math.Max(0, to - from));
    }
}
