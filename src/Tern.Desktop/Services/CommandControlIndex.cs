using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;

namespace Tern.Desktop.Services;

internal sealed class CommandControlIndex : IDisposable
{
    private sealed class Entry(ILogical node, ILogical? parent)
    {
        public ILogical Node { get; } = node;
        public ILogical? Parent { get; set; } = parent;
        public HashSet<ILogical> Children { get; } = new(ReferenceEqualityComparer.Instance);
        public NotifyCollectionChangedEventHandler Handler { get; set; } = null!;
    }

    private readonly ILogical _root;
    private readonly Func<Control, bool> _include;
    private readonly Dictionary<ILogical, Entry> _entries = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<Control> _controls = new(ReferenceEqualityComparer.Instance);
    private bool _disposed;

    public CommandControlIndex(ILogical root, Func<Control, bool> include)
    {
        _root = root;
        _include = include;
        Track(root, null);
    }

    public Control[] Snapshot() => [.. _controls];

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Untrack(_root, null);
    }

    private void Track(ILogical node, ILogical? parent)
    {
        if (_disposed) return;
        if (_entries.TryGetValue(node, out var previous))
        {
            previous.Parent = parent;
            return;
        }
        var entry = new Entry(node, parent);
        entry.Handler = (_, change) => ChildrenChanged(entry, change);
        _entries.Add(node, entry);
        node.LogicalChildren.CollectionChanged += entry.Handler;
        if (node is Control control)
        {
            control.PropertyChanged += TagChanged;
            Update(control);
        }
        foreach (var child in node.LogicalChildren)
        {
            entry.Children.Add(child);
            Track(child, node);
        }
    }

    private void Untrack(ILogical node, ILogical? parent)
    {
        if (!_entries.TryGetValue(node, out var entry) || !ReferenceEquals(entry.Parent, parent)) return;
        _entries.Remove(node);
        node.LogicalChildren.CollectionChanged -= entry.Handler;
        if (node is Control control)
        {
            control.PropertyChanged -= TagChanged;
            _controls.Remove(control);
        }
        foreach (var child in entry.Children) Untrack(child, node);
    }

    private void ChildrenChanged(Entry entry, NotifyCollectionChangedEventArgs change)
    {
        if (_disposed || change.Action == NotifyCollectionChangedAction.Move) return;
        if (change.Action == NotifyCollectionChangedAction.Reset)
        {
            var current = entry.Node.LogicalChildren.ToHashSet(ReferenceEqualityComparer.Instance);
            foreach (var child in entry.Children.Where(child => !current.Contains(child)).ToArray())
            {
                entry.Children.Remove(child);
                Untrack(child, entry.Node);
            }
            foreach (var child in entry.Node.LogicalChildren)
                if (entry.Children.Add(child)) Track(child, entry.Node);
            return;
        }
        if (change.OldItems is not null)
            foreach (ILogical child in change.OldItems)
                if (entry.Children.Remove(child)) Untrack(child, entry.Node);
        if (change.NewItems is not null)
            foreach (ILogical child in change.NewItems)
                if (entry.Children.Add(child)) Track(child, entry.Node);
    }

    private void TagChanged(object? sender, AvaloniaPropertyChangedEventArgs change)
    {
        if (change.Property == Control.TagProperty && sender is Control control) Update(control);
    }

    private void Update(Control control)
    {
        if (_include(control)) _controls.Add(control);
        else _controls.Remove(control);
    }
}
