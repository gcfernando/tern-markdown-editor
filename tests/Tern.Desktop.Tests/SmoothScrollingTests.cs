using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tern.App.Commands;
using Tern.Desktop.Services;
using Tern.PreviewView;
using Tern.Rendering.Preview;

namespace Tern.Desktop.Tests;

public sealed class SmoothScrollingTests
{
    [AvaloniaFact]
    public async Task Coarse_wheel_moves_through_intermediate_offsets_and_settles_without_editing()
    {
        var reader = MotionPolicy.SystemAnimationsReader;
        MotionPolicy.SystemAnimationsReader = () => true;
        MotionPolicy.Apply(false);
        var scroller = new ScrollViewer { Content = new Border { Height = 2400, Width = 900 },
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
        var window = new Window { Width = 500, Height = 400, Content = scroller };
        var trace = new List<object>(); var watch = System.Diagnostics.Stopwatch.StartNew();
        scroller.ScrollChanged += (_, _) => trace.Add(new { milliseconds = watch.Elapsed.TotalMilliseconds, offset = scroller.Offset.Y });
        try
        {
            window.Show(); Pump(window);
            window.MouseWheel(new Point(100, 100), new Vector(0, -2));
            Pump(window);
            var initial = scroller.Offset.Y;
            Assert.True(initial < 100, $"Coarse wheel jumped directly to {initial}.");
            await Task.Delay(35); Pump(window);
            var intermediate = scroller.Offset.Y;
            Assert.InRange(intermediate, .01, 99.99);
            await Task.Delay(160); Pump(window);
            Assert.InRange(scroller.Offset.Y, 99.5, 100.5);
        }
        finally
        {
            SaveTrace("normal-wheel.json", new { scope = "Headless routed wheel/real timer samples, not native frame pacing", samples = trace });
            window.Close(); MotionPolicy.SystemAnimationsReader = reader; MotionPolicy.Apply(false);
        }
    }

    [AvaloniaFact]
    public async Task Source_and_reading_use_the_same_rounded_scrollbar_grip()
    {
        using var fixture = new DesktopFixture();
        var text = string.Join("\n\n", Enumerable.Range(0, 180).Select(i => $"## Section {i}\n\nParagraph text."));
        fixture.Shell.NewDocument(text);
        await fixture.Window.RunCommandAsync(CommandIds.ViewSplit);
        await fixture.Window.RebuildPreviewAsync(); Pump(fixture.Window);
        var source = fixture.Window.EditorControl.GetVisualDescendants().OfType<ScrollBar>().Single(b => b.Orientation == Orientation.Vertical);
        var reading = fixture.Window.PreviewControl.GetVisualDescendants().OfType<ScrollBar>().Single(b => b.Orientation == Orientation.Vertical
            && b.GetVisualAncestors().OfType<ScrollViewer>().FirstOrDefault()?.Content is Border);
        foreach (var bar in new[] { source, reading })
        {
            Assert.InRange(bar.Bounds.Width, 20, 22);
            var thumb = Assert.Single(bar.GetVisualDescendants().OfType<Thumb>());
            Assert.True(thumb.Bounds.Height >= 40);
            var borders = thumb.GetVisualDescendants().OfType<Border>().ToArray(); Assert.NotEmpty(borders);
            Assert.All(borders, border => Assert.Equal(new CornerRadius(5), border.CornerRadius));
        }
        Assert.Equal(text, fixture.Shell.ActiveTab!.CurrentText);
    }

    [AvaloniaFact]
    public async Task Fractional_handoff_keeps_accepted_distance_and_held_or_modified_wheels_remain_native()
    {
        using var motion = new MotionScope();
        var scroller = PixelScroller();
        var window = Show(scroller);
        try
        {
            var point = new Point(100, 100);
            window.MouseWheel(point, new Vector(0, -.5)); Pump(window);
            Assert.InRange(scroller.Offset.Y, 24.5, 25.5);
            scroller.Offset = default;
            window.MouseWheel(point, new Vector(0, -1));
            window.MouseWheel(point, new Vector(0, -.5)); Pump(window);
            Assert.InRange(scroller.Offset.Y, 74.5, 75.5);
            await Settle(window);
            Assert.InRange(scroller.Offset.Y, 74.5, 75.5);
            scroller.Offset = default;
            window.MouseWheel(point, new Vector(0, -1), RawInputModifiers.Control); Pump(window);
            var modified = scroller.Offset.Y;
            await Settle(window); Assert.Equal(modified, scroller.Offset.Y);
            scroller.Offset = default;
            SmoothScrolling.SetIsEnabled(scroller, false);
            window.MouseDown(point, MouseButton.Left);
            window.MouseWheel(point, new Vector(0, -1), RawInputModifiers.LeftMouseButton); Pump(window);
            var nativeHeld = scroller.Offset.Y;
            window.MouseUp(point, MouseButton.Left);
            SmoothScrolling.SetIsEnabled(scroller, true); scroller.Offset = default;
            window.MouseDown(point, MouseButton.Left);
            window.MouseWheel(point, new Vector(0, -1), RawInputModifiers.LeftMouseButton); Pump(window);
            Assert.Equal(nativeHeld, scroller.Offset.Y);
            await Settle(window); Assert.Equal(nativeHeld, scroller.Offset.Y);
            window.MouseUp(point, MouseButton.Left);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Reversal_responds_from_the_current_position_and_cancels_an_unstarted_opposite_target()
    {
        using var motion = new MotionScope();
        var scroller = PixelScroller(); var window = Show(scroller);
        var clock = typeof(SmoothScrolling).GetProperty("TimestampReader", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        var originalClock = (Func<long>)clock.GetValue(null)!;
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        clock.SetValue(null, (Func<long>)(() => now));
        double accepted = -1;
        window.AddHandler(InputElement.PointerWheelChangedEvent, (_, e) => { if (e.Delta.Y > 0) accepted = scroller.Offset.Y; }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        try
        {
            window.MouseWheel(new Point(100, 100), new Vector(0, -8));
            now += (long)(System.Diagnostics.Stopwatch.Frequency * .035);
            for (var attempt = 0; attempt < 10 && scroller.Offset.Y < 1; attempt++) { await Task.Delay(20); Pump(window); }
            var turning = scroller.Offset.Y; Assert.True(turning > 1);
            window.MouseWheel(new Point(100, 100), new Vector(0, 1));
            var acceptedAtReversal = accepted;
            Assert.InRange(accepted, .01, 399.99);
            now += (long)(System.Diagnostics.Stopwatch.Frequency * .101);
            for (var attempt = 0; attempt < 10 && SmoothScrolling.IsActiveWithin(scroller); attempt++) { await Task.Delay(20); Pump(window); }
            Assert.InRange(scroller.Offset.Y, Math.Max(0, accepted - 50) - .5, Math.Max(0, accepted - 50) + .5);
            scroller.Offset = default;
            window.MouseWheel(new Point(100, 100), new Vector(0, -8));
            window.MouseWheel(new Point(100, 100), new Vector(0, 1));
            now += (long)(System.Diagnostics.Stopwatch.Frequency * .101);
            await Settle(window);
            Assert.InRange(scroller.Offset.Y, 0, .5);
            SaveTrace("reversal.json", new { clock = "Controlled monotonic timestamp; real routed wheel and DispatcherTimer", turning, acceptedAtReversal, unstartedAccepted = accepted, final = scroller.Offset.Y });
        }
        finally { window.Close(); clock.SetValue(null, originalClock); }
    }

    [AvaloniaFact]
    public async Task Nested_boundary_handoff_finishes_the_inner_request_before_scrolling_the_outer()
    {
        using var motion = new MotionScope();
        var inner = new ScrollViewer { Height = 140, Content = new Border { Height = 300 } };
        var outer = new ScrollViewer { Content = new StackPanel { Children = { inner, new Border { Height = 2000 } } } };
        var window = Show(outer);
        try
        {
            var point = inner.TranslatePoint(new Point(50, 50), window)!.Value;
            var maximum = inner.Extent.Height - inner.Viewport.Height;
            Assert.True(maximum > 0 && maximum < 200);
            window.MouseWheel(point, new Vector(0, -4));
            window.MouseWheel(point, new Vector(0, -1)); Pump(window);
            Assert.InRange(inner.Offset.Y, maximum - .5, maximum + .5);
            await Settle(window);
            Assert.InRange(outer.Offset.Y, 49.5, 50.5);
            Assert.InRange(inner.Offset.Y, maximum - .5, maximum + .5);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Horizontal_nested_input_and_native_value_controls_keep_their_own_intent()
    {
        using var motion = new MotionScope();
        var numeric = new NumericUpDown { Value = 5, Minimum = 0, Maximum = 10, Width = 180, Height = 40 };
        var inner = new ScrollViewer { Height = 100, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = new Border { Width = 1600, Height = 60 } };
        var outer = new ScrollViewer { Content = new StackPanel { Children = { inner, new Border { Height = 100 }, numeric, new Border { Height = 2000 } } } };
        var window = Show(outer);
        try
        {
            var point = inner.TranslatePoint(new Point(50, 30), window)!.Value;
            window.MouseWheel(point, new Vector(0, -1), RawInputModifiers.Shift);
            await Settle(window);
            Assert.InRange(inner.Offset.X, 49.5, 50.5); Assert.Equal(0, outer.Offset.Y);
            outer.Offset = new Vector(0, 100); Pump(window);
            var box = Assert.Single(numeric.GetVisualDescendants().OfType<TextBox>()); box.Focus();
            point = box.TranslatePoint(new Point(20, box.Bounds.Height / 2), window)!.Value;
            window.MouseWheel(point, new Vector(0, -1)); Pump(window);
            Assert.Equal(4, numeric.Value);
            await Settle(window); Assert.InRange(outer.Offset.Y, 99.5, 100.5);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Reduced_motion_settles_valid_pending_distance_and_per_view_reduction_overrides_normal_app_motion()
    {
        using var motion = new MotionScope();
        var scroller = PixelScroller(); var window = Show(scroller);
        try
        {
            window.MouseWheel(new Point(100, 100), new Vector(0, -3));
            MotionPolicy.Apply(true, true); Pump(window);
            Assert.InRange(scroller.Offset.Y, 149.5, 150.5);
            await Settle(window); Assert.InRange(scroller.Offset.Y, 149.5, 150.5);
            foreach (bool? systemAnimations in new bool?[] { false, null })
            {
                MotionPolicy.Apply(false, systemAnimations); scroller.Offset = default; Pump(window);
                window.MouseWheel(new Point(100, 100), new Vector(0, -2)); Pump(window);
                Assert.InRange(scroller.Offset.Y, 99.5, 100.5);
                Assert.False(SmoothScrolling.IsActiveWithin(scroller));
                await Settle(window); Assert.InRange(scroller.Offset.Y, 99.5, 100.5);
            }
        }
        finally { window.Close(); }
        MotionPolicy.Apply(false, true);
        var view = new Tern.PreviewView.PreviewView { ReduceMotion = true,
            Document = PreviewBuilder.Build(string.Join("\n\n", Enumerable.Repeat("A paragraph to read.", 100)), new PreviewOptions()) };
        window = Show(view);
        try
        {
            scroller = MainScroller(view);
            window.MouseWheel(new Point(100, 100), new Vector(0, -2)); Pump(window);
            Assert.InRange(scroller.Offset.Y, 99.5, 100.5);
            await Settle(window); Assert.InRange(scroller.Offset.Y, 99.5, 100.5);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Direct_navigation_document_replacement_and_detach_cancel_queued_zero_offset_motion()
    {
        using var motion = new MotionScope();
        var scroller = PixelScroller(); var window = Show(scroller);
        try
        {
            window.MouseWheel(new Point(100, 100), new Vector(0, -2));
            scroller.Offset = new Vector(0, 300);
            await Settle(window); Assert.InRange(scroller.Offset.Y, 299.5, 300.5);
            scroller.Offset = default;
            window.MouseWheel(new Point(100, 100), new Vector(0, -2));
            ((Control)scroller.Content!).BringIntoView(new Rect(0, 0, 10, 10));
            await Settle(window); Assert.InRange(scroller.Offset.Y, 0, .5);
            window.MouseWheel(new Point(100, 100), new Vector(0, -2));
            window.Close(); await Task.Delay(180); Dispatcher.UIThread.RunJobs();
            Assert.InRange(scroller.Offset.Y, 0, .5);
        }
        finally { window.Close(); }

        var oldDocument = PreviewBuilder.Build(string.Join("\n\n", Enumerable.Repeat("Old reading paragraph.", 150)), new PreviewOptions());
        var view = new Tern.PreviewView.PreviewView { Document = oldDocument };
        window = Show(view);
        try
        {
            scroller = MainScroller(view);
            window.MouseWheel(new Point(100, 100), new Vector(0, -2));
            view.Document = PreviewBuilder.Build(string.Join("\n\n", Enumerable.Repeat("New reading paragraph.", 150)), new PreviewOptions());
            await Settle(window); Assert.InRange(scroller.Offset.Y, 0, .5);
        }
        finally { window.Close(); }
        using var fixture = new DesktopFixture();
        fixture.Shell.NewDocument(string.Join('\n', Enumerable.Repeat("Old source line", 200)));
        await fixture.Window.RunCommandAsync(CommandIds.ViewSource); Pump(fixture.Window);
        var sourceViewer = fixture.Window.EditorControl.GetVisualDescendants().OfType<ScrollViewer>().Single();
        Assert.True(SmoothScrolling.GetPixelUnits(sourceViewer));
        var location = fixture.Window.EditorControl.TranslatePoint(new Point(50, 50), fixture.Window)!.Value;
        fixture.Window.MouseWheel(location, new Vector(0, -4));
        var text = string.Join('\n', Enumerable.Repeat("New source line", 200));
        fixture.Shell.NewDocument(text);
        await Settle(fixture.Window);
        Assert.InRange(fixture.Window.EditorControl.VerticalOffset, 0, .5);
        Assert.Equal(text, fixture.Shell.ActiveTab!.CurrentText);
    }

    [AvaloniaFact]
    public async Task Virtualized_list_preserves_scroll_units_realization_and_selection()
    {
        using var motion = new MotionScope();
        var list = new ListBox { ItemsSource = Enumerable.Range(0, 1000).Select(i => "Item " + i).ToArray(), SelectedIndex = 0 };
        var window = Show(list);
        try
        {
            var viewer = Assert.Single(list.GetVisualDescendants().OfType<ScrollViewer>());
            Assert.True(viewer.Extent.Height > viewer.Viewport.Height);
            var presenter = Assert.Single(viewer.GetVisualDescendants().OfType<Avalonia.Controls.Presenters.ScrollContentPresenter>());
            var logical = presenter.Child is ILogicalScrollable { IsLogicalScrollEnabled: true };
            window.MouseWheel(new Point(100, 100), new Vector(0, -2)); Pump(window);
            var immediate = viewer.Offset.Y;
            if (logical) Assert.True(immediate > 0);
            else Assert.True(immediate < 100);
            await Settle(window);
            if (logical) Assert.Equal(immediate, viewer.Offset.Y);
            else Assert.InRange(viewer.Offset.Y, 99.5, 100.5);
            Assert.Equal(0, list.SelectedIndex);
            Assert.InRange(list.GetVisualDescendants().OfType<ListBoxItem>().Count(), 1, 100);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Logical_content_keeps_its_native_item_units_without_fractional_animation()
    {
        using var motion = new MotionScope();
        var content = new LogicalItems(); var viewer = new ScrollViewer { Content = content }; var window = Show(viewer);
        try
        {
            window.MouseWheel(new Point(100, 100), new Vector(0, -2)); Pump(window);
            Assert.Equal(6, viewer.Offset.Y); Assert.Equal(6, content.Offset.Y);
            await Settle(window); Assert.Equal(6, viewer.Offset.Y);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Repeated_wheel_over_varied_virtualized_reading_keeps_requested_travel_and_settles()
    {
        using var motion = new MotionScope();
        var source = string.Join("\n\n", Enumerable.Range(0, 250).Select(i => $"## Section {i}\n\n" + string.Concat(Enumerable.Repeat("Variable text with **strong** words and wrapping. ", 1 + i % 14))));
        var view = new Tern.PreviewView.PreviewView { Document = PreviewBuilder.Build(source, new PreviewOptions()) };
        var window = Show(view); var viewer = MainScroller(view); var trace = new List<object>();
        try
        {
            for (var index = 0; index < 4; index++)
            {
                var before = viewer.Offset.Y;
                window.MouseWheel(new Point(100, 100), new Vector(0, -12));
                await Settle(window);
                trace.Add(new { index, before, after = viewer.Offset.Y, extent = viewer.Extent.Height, topLine = view.TopSourceLine });
                Assert.True(viewer.Offset.Y >= before + 500, $"Measured-height realization truncated wheel intent: {before} -> {viewer.Offset.Y}");
            }
            var settled = viewer.Offset.Y; await Settle(window); Assert.InRange(Math.Abs(viewer.Offset.Y - settled), 0, 2);
            SaveTrace("reading-realization.json", trace);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Newest_split_pane_wheel_owns_sync_without_rebound_or_source_edits(bool startInReading)
    {
        using var motion = new MotionScope(); using var fixture = new DesktopFixture();
        fixture.Shell.Settings.SyncScroll = true;
        var text = string.Join("\n\n", Enumerable.Range(0, 250).Select(i => $"## Section {i}\n\n" + new string('x', 40 + i % 13 * 30)));
        var path = await fixture.WriteAsync("wheel-sync.md", text);
        var before = File.ReadAllBytes(path);
        var tab = await fixture.Shell.OpenFileAsync(path);
        await fixture.Window.RunCommandAsync(CommandIds.ViewSplit); await fixture.Window.RebuildPreviewAsync(); Pump(fixture.Window);
        var couldUndo = fixture.Window.EditorControl.Document.UndoStack.CanUndo;
        var couldRedo = fixture.Window.EditorControl.Document.UndoStack.CanRedo;
        var reading = MainScroller(fixture.Window.PreviewControl);
        var source = fixture.Window.EditorControl.GetVisualDescendants().OfType<ScrollViewer>().Single();
        Assert.True(SmoothScrolling.GetPixelUnits(source));
        var sourcePoint = fixture.Window.EditorControl.TranslatePoint(new Point(80, 80), fixture.Window)!.Value;
        var readingPoint = fixture.Window.PreviewControl.TranslatePoint(new Point(80, 80), fixture.Window)!.Value;
        var trace = new List<object>(); var watch = System.Diagnostics.Stopwatch.StartNew();
        void Sample(string phase) => trace.Add(new { phase, milliseconds = watch.Elapsed.TotalMilliseconds,
            source = fixture.Window.EditorControl.VerticalOffset, reading = reading.Offset.Y, line = fixture.Window.PreviewControl.TopSourceLine });
        fixture.Window.MouseWheel(startInReading ? readingPoint : sourcePoint, new Vector(0, -8)); Sample("first accepted");
        await Task.Delay(25); Pump(fixture.Window); Sample("first frame");
        var targetBefore = startInReading ? fixture.Window.EditorControl.VerticalOffset : reading.Offset.Y;
        fixture.Window.MouseWheel(startInReading ? sourcePoint : readingPoint, new Vector(0, -4)); Sample("new pane accepted");
        await Settle(fixture.Window); Sample("settled");
        var targetAfter = startInReading ? fixture.Window.EditorControl.VerticalOffset : reading.Offset.Y;
        Assert.True(targetAfter >= targetBefore + 150, $"New wheel distance truncated: {targetBefore} -> {targetAfter}");
        var sourceAfter = fixture.Window.EditorControl.VerticalOffset; var readingAfter = reading.Offset.Y;
        await Settle(fixture.Window); Sample("quiet");
        Assert.InRange(Math.Abs(fixture.Window.EditorControl.VerticalOffset - sourceAfter), 0, 2);
        Assert.InRange(Math.Abs(reading.Offset.Y - readingAfter), 0, 2);
        Assert.False(SmoothScrolling.IsActiveWithin(fixture.Window));
        if (startInReading)
        {
            reading.Offset = new Vector(0, readingAfter + 1200); Pump(fixture.Window);
            await Settle(fixture.Window);
            Sample("direct Reading before assertion"); SaveTrace("split-" + startInReading + ".json", trace);
            Assert.True(fixture.Window.EditorControl.VerticalOffset > sourceAfter + 100,
                $"A retired Source wheel must not suppress later direct Reading synchronization: source {sourceAfter}->{fixture.Window.EditorControl.VerticalOffset}, Reading {readingAfter}->{reading.Offset.Y}.");
        }
        else
        {
            source.Offset = new Vector(source.Offset.X, sourceAfter + 1200); Pump(fixture.Window);
            await Settle(fixture.Window);
            Sample("direct Source before assertion"); SaveTrace("split-" + startInReading + ".json", trace);
            Assert.True(reading.Offset.Y > readingAfter + 100,
                $"A retired Reading wheel must not suppress later direct Source synchronization: source {sourceAfter}->{fixture.Window.EditorControl.VerticalOffset}, Reading {readingAfter}->{reading.Offset.Y}.");
        }
        Sample("post-wheel direct opposite pane");
        fixture.Window.MouseWheel(startInReading ? sourcePoint : readingPoint, new Vector(0, -8));
        fixture.Window.GoToLine(1, 1, focusEditor: false);
        await Settle(fixture.Window);
        Assert.InRange(fixture.Window.EditorControl.VerticalOffset, 0, 2);
        Assert.InRange(reading.Offset.Y, 0, 2);
        Assert.False(SmoothScrolling.IsActiveWithin(fixture.Window));
        Sample("direct GoToLine superseded wheel");
        Assert.Equal(text, tab!.CurrentText); Assert.False(tab.IsDirty); Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal(couldUndo, fixture.Window.EditorControl.Document.UndoStack.CanUndo);
        Assert.Equal(couldRedo, fixture.Window.EditorControl.Document.UndoStack.CanRedo);
        SaveTrace("split-" + startInReading + ".json", trace);
    }

    private static ScrollViewer PixelScroller() => new() { Content = new Border { Height = 2400, Width = 1800 }, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
    private static Window Show(Control control)
    {
        var window = new Window { Width = 500, Height = 400, Content = control }; window.Show(); Pump(window); return window;
    }
    private static ScrollViewer MainScroller(Control control) => control.GetVisualDescendants().OfType<ScrollViewer>().First();
    private static async Task Settle(Window window) { await Task.Delay(180); Pump(window); }
    private static void SaveTrace(string name, object trace)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Tern.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        var folder = Tern.Tests.Shared.TestOutput.Folder("screenshots", "reported-scrolling", "motion");
        Directory.CreateDirectory(folder); File.WriteAllText(Path.Combine(folder, name), System.Text.Json.JsonSerializer.Serialize(trace, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }
    private sealed class MotionScope : IDisposable
    {
        private readonly Func<bool?> _reader = MotionPolicy.SystemAnimationsReader;
        public MotionScope() { MotionPolicy.SystemAnimationsReader = () => true; MotionPolicy.Apply(false); }
        public void Dispose() { MotionPolicy.SystemAnimationsReader = _reader; MotionPolicy.Apply(false); }
    }

    private sealed class LogicalItems : Control, ILogicalScrollable
    {
        private Vector _offset;
        public bool CanHorizontallyScroll { get; set; }
        public bool CanVerticallyScroll { get; set; }
        public bool IsLogicalScrollEnabled => true;
        public Size Extent => new(10, 1000);
        public Size Viewport => new(10, 10);
        public Size ScrollSize => new(1, 3);
        public Size PageScrollSize => new(1, 10);
        public Vector Offset { get => _offset; set { _offset = new(0, Math.Floor(Math.Clamp(value.Y, 0, 990))); ScrollInvalidated?.Invoke(this, EventArgs.Empty); } }
        public event EventHandler? ScrollInvalidated;
        public bool BringIntoView(Control target, Rect targetRect) => false;
        public Control? GetControlInDirection(NavigationDirection direction, Control? from) => null;
        public void RaiseScrollInvalidated(EventArgs e) => ScrollInvalidated?.Invoke(this, e);
        protected override Size MeasureOverride(Size availableSize) => new(300, 300);
    }

    private static void Pump(Window window)
    {
        window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
    }
}
