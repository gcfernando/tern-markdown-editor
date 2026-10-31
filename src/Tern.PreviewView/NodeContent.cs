using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using Tern.Rendering.Preview;

namespace Tern.PreviewView;

/// <summary>
/// A 64-bit structural hash of a preview node that ignores source positions. Two blocks that differ only in where they sit
/// in the file (because lines were added above them) hash the same, so their rendered controls can be reused.
/// </summary>
internal static class NodeContent
{
    private const ulong Offset = 14695981039346656037UL;
    private const ulong Prime = 1099511628211UL;

    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> Properties = new();

    public static ulong Hash(object? value)
    {
        var hash = Offset;
        Mix(ref hash, value);
        return hash;
    }

    private static void Mix(ref ulong hash, object? value)
    {
        switch (value)
        {
            case null:
                Add(ref hash, 0x9E37);
                return;
            case string text:
                Add(ref hash, 0x5354);
                foreach (var ch in text)
                {
                    Add(ref hash, ch);
                }

                return;
            case byte[] bytes:
                Add(ref hash, 0x4259);
                foreach (var b in bytes)
                {
                    Add(ref hash, b);
                }

                return;
            case SourceLocation:
                return;
            case bool flag:
                Add(ref hash, flag ? 1UL : 2UL);
                return;
            case int number:
                Add(ref hash, unchecked((ulong)number));
                return;
            case Enum enumeration:
                Add(ref hash, Convert.ToUInt64(enumeration, System.Globalization.CultureInfo.InvariantCulture));
                return;
            case Uri uri:
                Mix(ref hash, uri.OriginalString);
                return;
            case IEnumerable sequence:
                Add(ref hash, 0x4C53);
                foreach (var item in sequence)
                {
                    Mix(ref hash, item);
                }

                Add(ref hash, 0x454C);
                return;
        }

        var type = value.GetType();
        if (type.IsPrimitive)
        {
            Mix(ref hash, Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture));
            return;
        }

        Add(ref hash, (ulong)type.Name.Length);
        foreach (var ch in type.Name)
        {
            Add(ref hash, ch);
        }

        foreach (var property in PropertiesOf(type))
        {
            Mix(ref hash, property.GetValue(value));
        }
    }

    private static PropertyInfo[] PropertiesOf(Type type) => Properties.GetOrAdd(type, static t =>
        [.. t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetIndexParameters().Length == 0 && p.CanRead && p.Name != "EqualityContract" && p.PropertyType != typeof(SourceLocation))]);

    private static void Add(ref ulong hash, ulong value)
    {
        hash = (hash ^ value) * Prime;
    }
}
