namespace Tern.Domain.Documents;

/// <summary>
/// What the file looked like when Tern last read or wrote it. A save is only allowed to
/// replace the file while the disk still matches this baseline.
/// </summary>
public sealed record DiskBaseline(long Length, DateTime LastWriteUtc, string Sha256)
{
    /// <summary>Content, not timestamps, decides equality: touching a file must not raise a conflict.</summary>
    public bool SameContentAs(DiskBaseline other) => Length == other.Length && Sha256 == other.Sha256;
}
