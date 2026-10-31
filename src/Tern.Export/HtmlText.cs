using System.Text;

namespace Tern.Export;

/// <summary>Escaping for text and attribute values in generated HTML.</summary>
internal static class HtmlText
{
    public static string Escape(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length + 8);
        Escape(builder, text);
        return builder.ToString();
    }

    public static void Escape(StringBuilder builder, string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        foreach (var c in ExportText.Clean(text))
        {
            switch (c)
            {
                case '&':
                    builder.Append("&amp;");
                    break;
                case '<':
                    builder.Append("&lt;");
                    break;
                case '>':
                    builder.Append("&gt;");
                    break;
                case '"':
                    builder.Append("&quot;");
                    break;
                case '\'':
                    builder.Append("&#39;");
                    break;
                default:
                    builder.Append(c);
                    break;
            }
        }
    }
}
