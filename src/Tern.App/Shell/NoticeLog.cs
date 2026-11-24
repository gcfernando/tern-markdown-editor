using System.Collections;

namespace Tern.App.Shell;

/// <summary>Thread-safe list of workspace notices; background refresh and index threads append while the UI enumerates snapshots.</summary>
public sealed class NoticeLog : IReadOnlyCollection<string>
{
    private readonly Lock _gate = new();
    private readonly List<string> _items = [];

    public int Count { get { lock (_gate) return _items.Count; } }

    public void Add(string notice) { lock (_gate) _items.Add(notice); }

    public IEnumerator<string> GetEnumerator()
    {
        string[] snapshot;
        lock (_gate) snapshot = [.. _items];
        return ((IEnumerable<string>)snapshot).GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
