using System.Diagnostics;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Tern.PreviewView;

/// <summary>Optional wheel presentation. Direct navigation and native gestures remain authoritative.</summary>
public sealed class SmoothScrolling : AvaloniaObject
{
    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<SmoothScrolling, Control, bool>("IsEnabled", inherits: true);
    public static readonly AttachedProperty<TimeSpan> DurationProperty =
        AvaloniaProperty.RegisterAttached<SmoothScrolling, Control, TimeSpan>("Duration", TimeSpan.Zero, inherits: true);
    public static readonly AttachedProperty<bool> PixelUnitsProperty =
        AvaloniaProperty.RegisterAttached<SmoothScrolling, ScrollViewer, bool>("PixelUnits");
    public static readonly AttachedProperty<bool> SuppressMotionProperty =
        AvaloniaProperty.RegisterAttached<SmoothScrolling, Control, bool>("SuppressMotion", inherits: true);
    public static TimeSpan StandardDuration { get; } = TimeSpan.FromMilliseconds(100);
    internal static Func<long> TimestampReader { get; set; } = Stopwatch.GetTimestamp;
    private static readonly ConditionalWeakTable<ScrollViewer, WheelState> States = new();
    private static readonly ConditionalWeakTable<TopLevel, WheelHub> Hubs = new();

    static SmoothScrolling()
    {
        IsEnabledProperty.Changed.AddClassHandler<ScrollViewer>((viewer, _) =>
        {
            if (GetIsEnabled(viewer)) States.GetValue(viewer, value => new WheelState(value)).Enable();
            else if (States.TryGetValue(viewer, out var state)) state.Disable();
        });
        DurationProperty.Changed.AddClassHandler<ScrollViewer>((viewer, _) =>
        {
            if (GetDuration(viewer) <= TimeSpan.Zero && States.TryGetValue(viewer, out var state)) state.Stop(finish: true);
        });
        SuppressMotionProperty.Changed.AddClassHandler<ScrollViewer>((viewer, _) =>
        {
            if (GetSuppressMotion(viewer) && States.TryGetValue(viewer, out var state)) state.Stop(finish: true);
        });
    }

    public static bool GetIsEnabled(Control control) => control.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(Control control, bool value) => control.SetValue(IsEnabledProperty, value);
    public static TimeSpan GetDuration(Control control) => control.GetValue(DurationProperty);
    public static void SetDuration(Control control, TimeSpan value) => control.SetValue(DurationProperty, value);
    public static bool GetPixelUnits(ScrollViewer control) => control.GetValue(PixelUnitsProperty);
    public static void SetPixelUnits(ScrollViewer control, bool value) => control.SetValue(PixelUnitsProperty, value);
    public static bool GetSuppressMotion(Control control) => control.GetValue(SuppressMotionProperty);
    public static void SetSuppressMotion(Control control, bool value) => control.SetValue(SuppressMotionProperty, value);

    public static void CancelWithin(Control control)
    {
        foreach (var viewer in control.GetSelfAndVisualDescendants().OfType<ScrollViewer>())
            if (States.TryGetValue(viewer, out var state)) state.Stop();
    }

    internal static bool IsActive(ScrollViewer viewer) => States.TryGetValue(viewer, out var state) && state.Active;

    public static bool IsActiveWithin(Control control) => TopLevel.GetTopLevel(control) is { } root
        && Hubs.TryGetValue(root, out var hub) && hub.IsActiveWithin(control);

    // Reading's measured-height correction changes the coordinate origin, not the user's intent.
    internal static void CorrectAnchor(ScrollViewer viewer, Vector offset)
    {
        if (States.TryGetValue(viewer, out var state) && state.Active) state.Rebase(offset);
        else viewer.SetCurrentValue(ScrollViewer.OffsetProperty, offset);
    }

    private sealed class WheelHub
    {
        private readonly HashSet<WheelState> _active = [];
        public WheelHub(TopLevel root)
        {
            root.AddHandler(InputElement.PointerPressedEvent, (_, _) => StopAll(), RoutingStrategies.Tunnel, handledEventsToo: true);
            root.AddHandler(InputElement.KeyDownEvent, (_, _) => StopAll(), RoutingStrategies.Tunnel, handledEventsToo: true);
            root.AddHandler(InputElement.ScrollGestureEvent, (_, _) => StopAll(), RoutingStrategies.Tunnel, handledEventsToo: true);
        }
        public void Activate(WheelState state, Vector step)
        {
            foreach (var other in _active.ToArray())
                if (!ReferenceEquals(other, state)) other.Stop(finish: other.BoundaryHandoffTo(state, step));
            _active.Add(state);
        }
        public void Remove(WheelState state) => _active.Remove(state);
        public bool IsActiveWithin(Control control) => _active.Any(state => state.IsWithin(control));
        public void StopAll() { foreach (var state in _active.ToArray()) state.Stop(); }
        public void HandoffNative(Visual source, PointerWheelEventArgs e)
        {
            foreach (var state in _active.ToArray())
            {
                state.Stop(finish: state.Contains(source) && state.PendingDirection(e) > 0);
            }
        }
        public void Unconsumed(Visual source, PointerWheelEventArgs e)
        {
            foreach (var state in _active.ToArray())
            {
                if (!state.Contains(source) || state.PendingDirection(e) < 0) state.Stop();
            }
        }
    }

    private sealed class WheelState
    {
        private readonly ScrollViewer _viewer;
        private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(16) };
        private bool _enabled, _writing;
        private Control? _child;
        private ScrollContentPresenter? _presenter;
        private WheelHub? _hub;
        private Vector _start, _target;
        private long _started;
        private TimeSpan _duration;
        public bool Active => _timer.IsEnabled;

        public WheelState(ScrollViewer viewer)
        {
            _viewer = viewer;
            _timer.Tick += (_, _) => Tick();
        }
        public void Enable()
        {
            if (_enabled) return;
            _enabled = true;
            _viewer.AddHandler(InputElement.PointerWheelChangedEvent, Wheel, RoutingStrategies.Tunnel);
            _viewer.AddHandler(Control.RequestBringIntoViewEvent, BringIntoView, RoutingStrategies.Bubble, handledEventsToo: true);
            _viewer.AttachedToVisualTree += Attached;
            _viewer.DetachedFromVisualTree += Detached;
            _viewer.TemplateApplied += TemplateApplied;
            _viewer.PropertyChanged += Changed;
            RefreshChild();
        }
        public void Disable()
        {
            if (!_enabled) return;
            Stop(); _enabled = false;
            _viewer.RemoveHandler(InputElement.PointerWheelChangedEvent, Wheel);
            _viewer.RemoveHandler(Control.RequestBringIntoViewEvent, BringIntoView);
            _viewer.AttachedToVisualTree -= Attached;
            _viewer.DetachedFromVisualTree -= Detached;
            _viewer.TemplateApplied -= TemplateApplied;
            _viewer.PropertyChanged -= Changed;
            UnhookChild();
        }
        private void Attached(object? sender, VisualTreeAttachmentEventArgs e) => RefreshChild();
        private void Detached(object? sender, VisualTreeAttachmentEventArgs e) { Stop(); UnhookChild(); _presenter = null; }
        private void TemplateApplied(object? sender, TemplateAppliedEventArgs e) { Stop(); RefreshChild(); }
        private void RefreshChild()
        {
            _presenter = _viewer.GetVisualDescendants().OfType<ScrollContentPresenter>().FirstOrDefault(p =>
                p.GetVisualAncestors().OfType<ScrollViewer>().FirstOrDefault() == _viewer);
            var child = _presenter?.Child ?? _viewer.Content as Control;
            if (ReferenceEquals(child, _child)) return;
            UnhookChild(); _child = child;
            if (_child is not null) _child.PropertyChanged += ChildChanged;
        }
        private void UnhookChild() { if (_child is not null) _child.PropertyChanged -= ChildChanged; _child = null; }
        private void ChildChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property.Name is "Document" or "Text" or "FontSize" or "FontFamily" or "ItemsSource") Stop();
        }
        private void BringIntoView(object? sender, RequestBringIntoViewEventArgs e) => Stop();
        private void Changed(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == ScrollViewer.OffsetProperty && !_writing) Stop();
            else if (e.Property == ScrollViewer.ViewportProperty) Stop();
            else if (e.Property == ContentControl.ContentProperty) { Stop(); RefreshChild(); }
            else if (e.Property == ScrollViewer.ExtentProperty && Active) _target = Clamp(_viewer, _target);
        }
        private static Vector WheelDelta(PointerWheelEventArgs e, ScrollViewer viewer)
        {
            var delta = e.Delta;
            if (e.KeyModifiers == KeyModifiers.Shift && Math.Abs(delta.X) < .001) delta = new(delta.Y, 0);
            else if (viewer.FlowDirection == Avalonia.Media.FlowDirection.RightToLeft) delta = new(-delta.X, delta.Y);
            return delta;
        }
        private static bool Coarse(double value) => value == 0 || Math.Abs(value) >= 1 && Math.Abs(value - Math.Round(value)) < .001;
        private static Vector Step(ScrollViewer viewer, Vector delta)
        {
            var presenter = viewer.GetVisualDescendants().OfType<ScrollContentPresenter>().FirstOrDefault(p =>
                p.GetVisualAncestors().OfType<ScrollViewer>().FirstOrDefault() == viewer);
            var size = presenter?.Child is ILogicalScrollable { IsLogicalScrollEnabled: true } logical ? logical.ScrollSize : new Size(50, 50);
            return new(viewer.HorizontalScrollBarVisibility != ScrollBarVisibility.Disabled ? -delta.X * size.Width : 0,
                viewer.VerticalScrollBarVisibility != ScrollBarVisibility.Disabled ? -delta.Y * size.Height : 0);
        }
        private static ScrollViewer? Destination(Visual source, PointerWheelEventArgs e)
        {
            foreach (var visual in source.GetSelfAndVisualAncestors())
            {
                if (visual is not ScrollViewer viewer) continue;
                var step = Step(viewer, WheelDelta(e, viewer));
                var origin = States.TryGetValue(viewer, out var state) ? state.IntentOrigin(step) : viewer.Offset;
                if ((Clamp(viewer, origin + step) - origin).Length > .001 || !viewer.IsScrollChainingEnabled) return viewer;
            }
            return null;
        }
        private void Wheel(object? sender, PointerWheelEventArgs e)
        {
            if (e.Source is not Visual source || e.Handled || !GetIsEnabled(_viewer)) return;
            var root = TopLevel.GetTopLevel(_viewer);
            var hub = root is not null ? Hubs.GetValue(root, value => new WheelHub(value)) : null;
            if (source.GetSelfAndVisualAncestors().Any(visual => visual is ComboBox or NumericUpDown or Slider)) { hub?.StopAll(); return; }
            var buttons = e.GetCurrentPoint(_viewer).Properties;
            if ((e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta)) != 0
                || buttons.IsLeftButtonPressed || buttons.IsMiddleButtonPressed || buttons.IsRightButtonPressed
                || buttons.IsXButton1Pressed || buttons.IsXButton2Pressed) { hub?.StopAll(); return; }
            if (!Coarse(e.Delta.X) || !Coarse(e.Delta.Y)) { hub?.HandoffNative(source, e); return; }
            var destination = Destination(source, e);
            if (destination is null) { hub?.Unconsumed(source, e); return; }
            if (!ReferenceEquals(destination, _viewer)) return;
            RefreshChild();
            // Logical item panels retain their own units and virtualization. TextEditor opts in to pixel units.
            if (_presenter?.Child is ILogicalScrollable { IsLogicalScrollEnabled: true } && !GetPixelUnits(_viewer)) { hub?.StopAll(); return; }
            var step = Step(_viewer, WheelDelta(e, _viewer));
            var origin = IntentOrigin(step);
            var target = Clamp(_viewer, origin + step);
            if ((target - origin).Length < .001) { if (!_viewer.IsScrollChainingEnabled) e.Handled = true; return; }
            e.Handled = true;
            _hub = hub; hub?.Activate(this, step);
            _start = _viewer.Offset; _target = target;
            _duration = GetSuppressMotion(_viewer) ? TimeSpan.Zero : GetDuration(_viewer);
            if (_duration <= TimeSpan.Zero) { Write(target); Stop(); return; }
            _started = TimestampReader();
            _timer.Start();
        }
        private static Vector Clamp(ScrollViewer viewer, Vector value) => new(
            Math.Clamp(value.X, 0, Math.Max(0, viewer.Extent.Width - viewer.Viewport.Width)),
            Math.Clamp(value.Y, 0, Math.Max(0, viewer.Extent.Height - viewer.Viewport.Height)));
        private Vector IntentOrigin(Vector step)
        {
            var origin = Active ? _target : _viewer.Offset;
            if (step.X * (origin.X - _viewer.Offset.X) < 0) origin = origin.WithX(_viewer.Offset.X);
            if (step.Y * (origin.Y - _viewer.Offset.Y) < 0) origin = origin.WithY(_viewer.Offset.Y);
            return origin;
        }
        private bool AtBoundaryTarget(Vector step) =>
            step.Y > 0 && _target.Y >= _viewer.Extent.Height - _viewer.Viewport.Height - .001 && _target.Y > _viewer.Offset.Y
            || step.Y < 0 && _target.Y <= .001 && _target.Y < _viewer.Offset.Y
            || step.X > 0 && _target.X >= _viewer.Extent.Width - _viewer.Viewport.Width - .001 && _target.X > _viewer.Offset.X
            || step.X < 0 && _target.X <= .001 && _target.X < _viewer.Offset.X;
        public bool BoundaryHandoffTo(WheelState next, Vector step) => next._viewer.IsVisualAncestorOf(_viewer) && AtBoundaryTarget(step);
        public bool Contains(Visual source) => ReferenceEquals(_viewer, source) || _viewer.IsVisualAncestorOf(source);
        public bool IsWithin(Control control) => ReferenceEquals(control, _viewer) || control.IsVisualAncestorOf(_viewer);
        public double PendingDirection(PointerWheelEventArgs e)
        {
            var step = Step(_viewer, WheelDelta(e, _viewer)); var pending = _target - _viewer.Offset;
            return pending.X * step.X + pending.Y * step.Y;
        }
        private void Write(Vector offset)
        {
            _writing = true;
            try { _viewer.SetCurrentValue(ScrollViewer.OffsetProperty, Clamp(_viewer, offset)); }
            finally { _writing = false; }
        }
        private void Tick()
        {
            if (!Active) return;
            if (!_viewer.IsEffectivelyVisible || TopLevel.GetTopLevel(_viewer) is null) { Stop(); return; }
            var elapsed = Stopwatch.GetElapsedTime(_started, TimestampReader());
            var fraction = Math.Clamp(elapsed.TotalMilliseconds / _duration.TotalMilliseconds, 0, 1);
            var eased = 1 - Math.Pow(1 - fraction, 3);
            Write(_start + (_target - _start) * eased);
            if (fraction >= 1) Stop();
        }
        public void Rebase(Vector offset)
        {
            var shift = offset - _viewer.Offset;
            _start += shift; _target = Clamp(_viewer, _target + shift);
            Write(offset);
        }
        public void Stop(bool finish = false)
        {
            if (finish && Active) Write(_target);
            _timer.Stop(); _hub?.Remove(this); _hub = null;
        }
    }
}
