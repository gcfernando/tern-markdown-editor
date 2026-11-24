using System.Diagnostics;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Tern.Desktop.Services;

namespace Tern.Desktop.Views;

/// <summary>A bounded, noninteractive chrome snapshot lets real hide/focus/layout changes finish immediately.</summary>
internal sealed class SurfaceMotion(Control surface, Canvas layer, Action returnFocus) : IDisposable
{
    private Image? _snapshot;
    private RenderTargetBitmap? _bitmap;
    private int _generation;
    private bool _disposed;

    public void SetVisible(bool visible)
    {
        if (surface.IsVisible == visible) return;
        Cancel();
        var returnKeyboardFocus = !visible && surface.IsKeyboardFocusWithin;
        if (!visible)
        {
            CaptureExit();
        }
        surface.IsVisible = visible;
        if (returnKeyboardFocus) returnFocus();
        if (visible) MotionPolicy.Enter(surface, MotionPurpose.Panel);
    }

    public void CaptureExit()
    {
        Cancel();
        var bounds = surface.Bounds;
        if (_disposed || MotionPolicy.IsReduced || !surface.IsEffectivelyVisible ||
            bounds.Width <= 0 || bounds.Height <= 0 || !double.IsFinite(bounds.Width * bounds.Height) ||
            surface.TranslatePoint(default, layer) is not { } position || TopLevel.GetTopLevel(surface) is not { } top)
            return;
        RenderTargetBitmap? bitmap = null;
        try
        {
            // At most about 6 MiB per chrome surface, never a document/editor screenshot.
            var scale = Math.Min(top.RenderScaling, Math.Sqrt(1_500_000 / (bounds.Width * bounds.Height)));
            bitmap = new RenderTargetBitmap(new PixelSize(Math.Max(1, (int)Math.Ceiling(bounds.Width * scale)),
                Math.Max(1, (int)Math.Ceiling(bounds.Height * scale))), new Vector(96 * scale, 96 * scale));
            bitmap.Render(surface);
            var snapshot = new Image
            {
                Source = bitmap, Width = bounds.Width, Height = bounds.Height, Stretch = Stretch.Fill,
                IsHitTestVisible = false, Focusable = false, Name = "ChromeMotionSnapshot",
            };
            AutomationProperties.SetAccessibilityView(snapshot, AccessibilityView.Raw);
            Canvas.SetLeft(snapshot, position.X);
            Canvas.SetTop(snapshot, position.Y);
            _bitmap = bitmap;
            bitmap = null;
            _snapshot = snapshot;
            layer.Children.Add(snapshot);
            _ = FinishExitAsync(snapshot, _generation);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            Cancel();
            Trace.TraceWarning("Chrome transition capture was unavailable; visibility changed immediately ({0}).", ex.GetType().Name);
        }
        finally { bitmap?.Dispose(); }
    }

    private async Task FinishExitAsync(Image snapshot, int generation)
    {
        await MotionPolicy.ExitAsync(snapshot);
        if (_generation == generation) Cancel();
    }

    public void Cancel()
    {
        _generation++;
        var snapshot = _snapshot;
        var bitmap = _bitmap;
        _snapshot = null;
        _bitmap = null;
        if (snapshot is not null)
        {
            MotionPolicy.Stop(snapshot);
            layer.Children.Remove(snapshot);
            snapshot.Source = null;
        }
        bitmap?.Dispose();
    }

    public void Dispose()
    {
        _disposed = true;
        Cancel();
    }
}
