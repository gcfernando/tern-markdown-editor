using Avalonia;
using Avalonia.Controls;
using Tern.Rendering.Preview;

namespace Tern.PreviewView;

/// <summary>One top-level block of the document: its node, its (lazily created) control and its last known height.</summary>
internal sealed class BlockSlot(BlockNode node, ulong hash, double estimatedHeight)
{
    public BlockNode Node { get; set; } = node;

    public ulong Hash { get; } = hash;

    public BlockHost? Host { get; set; }

    public double Height { get; set; } = estimatedHeight;

    /// <summary>True once the height comes from a real measure rather than an estimate.</summary>
    public bool Measured { get; set; }
}

/// <summary>Wraps a realised top-level block so input handlers can find its slot.</summary>
internal sealed class BlockHost : Border
{
    public BlockHost(BlockSlot slot) => Slot = slot;

    public BlockSlot Slot { get; }
}

/// <summary>
/// Stacks the document's blocks vertically but only creates controls for those near the viewport. Blocks that have not
/// been measured yet use an estimated height; the owner corrects the scroll offset when estimates turn into real heights.
/// </summary>
internal sealed class BlockPanel : Panel
{
    private readonly List<BlockSlot> _slots = [];
    private double[] _tops = [0];
    private Func<BlockSlot, BlockHost>? _factory;
    private double _viewportTop;
    private double _viewportHeight = 900;
    private int _realizedFirst = -1;
    private int _realizedLast = -2;
    private bool _pendingPrune;

    /// <summary>Raised from layout after block heights changed; the owner restores its scroll anchor.</summary>
    public event EventHandler? HeightsChanged;

    public IReadOnlyList<BlockSlot> Slots => _slots;

    public int RealizedCount => Math.Max(0, _realizedLast - _realizedFirst + 1);

    public double TotalHeight => _tops[^1];

    public void SetFactory(Func<BlockSlot, BlockHost> factory) => _factory = factory;

    public void SetSlots(IEnumerable<BlockSlot> slots)
    {
        _slots.Clear();
        _slots.AddRange(slots);
        Children.Clear();
        _realizedFirst = -1;
        _realizedLast = -2;
        _pendingPrune = true;
        RecomputeTops();
        InvalidateMeasure();
    }

    public void SetViewport(double top, double height)
    {
        _viewportTop = Math.Max(0, top);
        _viewportHeight = height > 0 ? height : _viewportHeight;
        InvalidateMeasure();
    }

    /// <summary>Drops every realised control so the next layout rebuilds them (font or size changes).</summary>
    public void ResetControls()
    {
        foreach (var slot in _slots)
        {
            slot.Host = null;
        }

        Children.Clear();
        _realizedFirst = -1;
        _realizedLast = -2;
        InvalidateMeasure();
    }

    public double TopOf(int index) => _tops[Math.Clamp(index, 0, _slots.Count)];

    public double HeightOf(int index) => index < 0 || index >= _slots.Count ? 0 : _slots[index].Height;

    /// <summary>Index of the block that covers vertical offset <paramref name="y"/>.</summary>
    public int IndexAtOffset(double y)
    {
        if (_slots.Count == 0)
        {
            return -1;
        }

        var low = 0;
        var high = _slots.Count - 1;
        while (low < high)
        {
            var mid = (low + high + 1) / 2;
            if (_tops[mid] <= y)
            {
                low = mid;
            }
            else
            {
                high = mid - 1;
            }
        }

        return low;
    }

    public BlockSlot? RealizedSlotAt(int index) => index >= _realizedFirst && index <= _realizedLast ? _slots[index] : null;

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsFinite(availableSize.Width) ? availableSize.Width : 800;
        var heightsChanged = false;

        if (_slots.Count > 0 && _factory is not null)
        {
            var buffer = Math.Max(_viewportHeight, 600);
            var windowTop = Math.Max(0, _viewportTop - buffer);
            var windowBottom = _viewportTop + _viewportHeight + buffer;
            var first = IndexAtOffset(windowTop);
            var y = _tops[first];
            var last = first - 1;

            for (var i = first; i < _slots.Count && y < windowBottom; i++)
            {
                var slot = _slots[i];
                slot.Host ??= CreateHost(slot);
                slot.Host.Measure(new Size(width, double.PositiveInfinity));
                var height = slot.Host.DesiredSize.Height;
                if (Math.Abs(height - slot.Height) > 0.5)
                {
                    heightsChanged = true;
                }

                slot.Height = height;
                slot.Measured = true;
                y += height;
                last = i;
            }

            Realize(first, last);
            if (_pendingPrune)
            {
                _pendingPrune = false;
                for (var i = 0; i < _slots.Count; i++)
                {
                    if (i < first || i > last)
                    {
                        _slots[i].Host = null;
                    }
                }
            }
        }

        RecomputeTops();
        if (heightsChanged)
        {
            HeightsChanged?.Invoke(this, EventArgs.Empty);
        }

        return new Size(width, _tops[^1]);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        for (var i = _realizedFirst; i <= _realizedLast; i++)
        {
            var host = _slots[i].Host;
            host?.Arrange(new Rect(0, _tops[i], finalSize.Width, host.DesiredSize.Height));
        }

        return finalSize;
    }

    private BlockHost CreateHost(BlockSlot slot) => _factory!(slot);

    private void Realize(int first, int last)
    {
        if (first == _realizedFirst && last == _realizedLast)
        {
            return;
        }

        var disjoint = last < _realizedFirst || first > _realizedLast || _realizedLast < _realizedFirst;
        if (disjoint)
        {
            for (var i = _realizedFirst; i <= _realizedLast; i++)
            {
                _slots[i].Host = null;
            }

            Children.Clear();
            for (var i = first; i <= last; i++)
            {
                Children.Add(_slots[i].Host!);
            }
        }
        else
        {
            for (var i = _realizedFirst; i < first; i++)
            {
                Children.RemoveAt(0);
                _slots[i].Host = null;
            }

            for (var i = _realizedLast; i > last; i--)
            {
                Children.RemoveAt(Children.Count - 1);
                _slots[i].Host = null;
            }

            var existingFirst = Math.Max(first, _realizedFirst);
            var existingLast = Math.Min(last, _realizedLast);
            for (var i = existingFirst - 1; i >= first; i--)
            {
                Children.Insert(0, _slots[i].Host!);
            }

            for (var i = existingLast + 1; i <= last; i++)
            {
                Children.Add(_slots[i].Host!);
            }
        }

        _realizedFirst = first;
        _realizedLast = last;
    }

    private void RecomputeTops()
    {
        if (_tops.Length != _slots.Count + 1)
        {
            _tops = new double[_slots.Count + 1];
        }

        double y = 0;
        for (var i = 0; i < _slots.Count; i++)
        {
            _tops[i] = y;
            y += _slots[i].Height;
        }

        _tops[_slots.Count] = y;
    }
}
