using System.Text;

namespace Tern.PreviewView;

/// <summary>Presentation only. Source buffers, exported HTML text and Copy retain their original tabs.</summary>
internal static class CodeDisplay
{
    internal const int TabColumns = 4;

    internal static string ExpandTabs(string source, CancellationToken cancellationToken = default)
    {
        if (!source.Contains('\t')) return source;
        var result = new StringBuilder(source.Length);
        var column = 0;
        for (var index = 0; index < source.Length; index++)
        {
            if ((index & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
            var value = source[index];
            if (value == '\t')
            {
                var count = TabColumns - column % TabColumns;
                result.Append(' ', count); column += count;
            }
            else
            {
                result.Append(value);
                if (value is '\r' or '\n') column = 0;
                else if (!char.IsLowSurrogate(value) || index == 0 || !char.IsHighSurrogate(source[index - 1])) column++;
            }
        }
        return result.ToString();
    }
}
