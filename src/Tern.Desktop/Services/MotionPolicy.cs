using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Styling;

namespace Tern.Desktop.Services;

internal enum MotionPurpose { Control, Panel, Enter, Exit, Notification, Tab, Confirmation, Scroll }

/// <summary>One optional, decorative motion policy. Business state and focus never await it.</summary>
internal static class MotionPolicy
{
    private static readonly ConditionalWeakTable<Control, CancellationTokenSource> Runs = new();
    private static bool _initialized;
    private static WeakReference<Application>? _resourceOwner;
    public static bool IsReduced { get; private set; } = true;
    /// <summary>
    /// Only Tern's own Reduce motion setting stops an indeterminate progress bar. It is essential
    /// status, so turning off decorative Windows animation effects must not hide it.
    /// </summary>
    public static bool ProgressMotionOff { get; private set; }
    public static event EventHandler? Changed;
    public static TimeSpan LoadingRevealDelay { get; } = TimeSpan.FromMilliseconds(200);
    internal static Func<bool?> SystemAnimationsReader { get; set; } = ReadSystemAnimations;

    public static TimeSpan Duration(MotionPurpose purpose) => IsReduced ? TimeSpan.Zero : StandardDuration(purpose);

    private static TimeSpan StandardDuration(MotionPurpose purpose) => purpose switch
    {
        MotionPurpose.Panel => TimeSpan.FromMilliseconds(180),
        MotionPurpose.Enter => TimeSpan.FromMilliseconds(160),
        MotionPurpose.Exit => TimeSpan.FromMilliseconds(120),
        MotionPurpose.Notification => TimeSpan.FromMilliseconds(180),
        MotionPurpose.Confirmation => TimeSpan.FromMilliseconds(200),
        MotionPurpose.Scroll => Tern.PreviewView.SmoothScrolling.StandardDuration,
        _ => TimeSpan.FromMilliseconds(100),
    };

    public static void Apply(bool reduceMotion) => Apply(reduceMotion, SystemAnimationsReader());

    internal static void Apply(bool reduceMotion, bool? systemAnimationsEnabled)
    {
        var reduced = reduceMotion || systemAnimationsEnabled != true;
        var policyChanged = !_initialized || IsReduced != reduced || ProgressMotionOff != reduceMotion;
        ProgressMotionOff = reduceMotion;
        var app = Application.Current;
        var resourcesInstalled = app is null || _resourceOwner is { } previous
            && previous.TryGetTarget(out var owner) && ReferenceEquals(owner, app);
        if (!policyChanged && resourcesInstalled) return;
        _initialized = true;
        IsReduced = reduced;
        if (app is not null)
        {
            foreach (var purpose in Enum.GetValues<MotionPurpose>())
                app.Resources["Tern.Motion." + purpose] = reduced ? TimeSpan.Zero : StandardDuration(purpose);
            app.Resources["Tern.Motion.ControlTransitions"] = ControlTransitions(MotionPurpose.Control);
            app.Resources["Tern.Motion.TabTransitions"] = ControlTransitions(MotionPurpose.Tab);
            _resourceOwner = new WeakReference<Application>(app);
        }
        if (policyChanged) Changed?.Invoke(null, EventArgs.Empty);
    }

    private static Transitions ControlTransitions(MotionPurpose purpose)
    {
        if (IsReduced) return [];
        var transitions = new Transitions
        {
            new BrushTransition { Property = ContentPresenter.BackgroundProperty, Duration = Duration(purpose) },
            new BrushTransition { Property = ContentPresenter.BorderBrushProperty, Duration = Duration(purpose) },
        };
        if (purpose == MotionPurpose.Control)
            transitions.Add(new DoubleTransition { Property = Visual.OpacityProperty, Duration = Duration(purpose) });
        return transitions;
    }

    public static void Enter(Control control, MotionPurpose purpose = MotionPurpose.Enter)
        => _ = AnimateAsync(control, purpose, entering: true);

    public static Task ExitAsync(Control control) => AnimateAsync(control, MotionPurpose.Exit, entering: false);

    private static Task AnimateAsync(Control control, MotionPurpose purpose, bool entering)
    {
        Stop(control);
        if (IsReduced || !control.IsEffectivelyVisible) return Task.CompletedTask;
        var animation = new Animation
        {
            Duration = Duration(purpose),
            Easing = new CubicEaseOut(),
            Children =
            {
                new KeyFrame { Cue = new Cue(0), Setters = { new Setter(Visual.OpacityProperty, entering ? .15 : 1d) } },
                new KeyFrame { Cue = new Cue(1), Setters = { new Setter(Visual.OpacityProperty, entering ? 1d : 0d) } },
            },
        };
        // A subsequent value is not held by the animation; removal/close is always immediate.
        return RunAsync(animation, control);
    }

    public static void Stop(Control control)
    {
        if (Runs.TryGetValue(control, out var previous)) previous.Cancel();
    }

    private static async Task RunAsync(Animation animation, Control control)
    {
        using var cancellation = new CancellationTokenSource();
        Runs.Remove(control);
        Runs.Add(control, cancellation);
        var finished = false;
        // An earlier Changed handler can finish this run while its event invocation is still queued.
        void Stop(object? sender, EventArgs e) { if (!finished) cancellation.Cancel(); }
        void Detached(object? sender, VisualTreeAttachmentEventArgs e) => Stop(sender, e);
        Changed += Stop;
        control.DetachedFromVisualTree += Detached;
        try { await animation.RunAsync(control, cancellation.Token); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        finally
        {
            finished = true;
            Changed -= Stop;
            control.DetachedFromVisualTree -= Detached;
            if (Runs.TryGetValue(control, out var current) && ReferenceEquals(current, cancellation)) Runs.Remove(control);
        }
    }

    private static bool? ReadSystemAnimations()
    {
        if (!OperatingSystem.IsWindows()) return true;
        if (SystemParametersInfoW(0x1042, 0, out var enabled, 0)) return enabled != 0;
        Trace.TraceWarning("Windows animation preference could not be read (Win32 {0}); reducing interface motion.", Marshal.GetLastWin32Error());
        return null;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfoW(uint action, uint parameter, out int value, uint flags);
}
