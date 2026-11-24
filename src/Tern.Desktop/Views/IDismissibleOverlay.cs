namespace Tern.Desktop.Views;

/// <summary>
/// Optional veto for the window-level Escape handling of overlays (SB-03). Overlays that do not implement it
/// are closed by Escape. Overlays that return false handle Escape themselves (for example to cancel a request first).
/// </summary>
public interface IDismissibleOverlay
{
    bool DismissWithEscape { get; }
}
