namespace Tern.Markdown;

/// <summary>Maps between UTF-16 offsets and 1-based line numbers, treating \r\n, \n and a lone \r as line breaks.</summary>
public sealed class LineIndex
{
    private readonly List<int> _starts = [0];

    public LineIndex(string text)
    {
        Length = text.Length;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                _starts.Add(i + 1);
            }
            else if (c == '\n')
            {
                _starts.Add(i + 1);
            }
        }
    }

    public int Length { get; }

    public int LineCount => _starts.Count;

    public int LineOf(int offset)
    {
        var clamped = Math.Clamp(offset, 0, Length);
        var index = _starts.BinarySearch(clamped);
        return (index >= 0 ? index : ~index - 1) + 1;
    }

    public int ColumnOf(int offset) => Math.Clamp(offset, 0, Length) - LineStart(LineOf(offset));

    public int LineStart(int line) => _starts[Math.Clamp(line, 1, _starts.Count) - 1];

    /// <summary>Offset just past the last character of the line, excluding its terminator.</summary>
    public int LineEnd(string text, int line)
    {
        var next = line >= _starts.Count ? text.Length : _starts[line];
        var end = next;
        while (end > LineStart(line) && (text[end - 1] == '\n' || text[end - 1] == '\r'))
        {
            end--;
        }

        return end;
    }
}
