using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Tern.Domain.Documents;

/// <summary>An immutable copy of the document text at one revision; safe to hand to background work.</summary>
public sealed record DocumentSnapshot(long Revision, string Text)
{
    public string ContentHash => ContentHasher.Hash(Text);
}

public static class ContentHasher
{
    public static string Hash(string text)
    {
        var bytes = MemoryMarshal.AsBytes(text.AsSpan());
        return Convert.ToHexString(SHA256.HashData(bytes));
    }
}
