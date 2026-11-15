using Tern.Rendering.Preview;
using Tern.Rendering.Resources;

namespace Tern.PreviewView;

public enum ImageAction { Locate, Retry, ReviewAccess }

public sealed class ImageActionEventArgs(PreviewDocument document, int line, string alt,
    ResourceDecision resolution, ImageAction action, Action retry) : EventArgs
{
    public PreviewDocument Document { get; } = document;
    public int Line { get; } = line;
    public string Alt { get; } = alt;
    public ResourceDecision Resolution { get; } = resolution;
    public ImageAction Action { get; } = action;
    internal Action Retry { get; } = retry;
    public void RetryApprovedLoad() => Retry();
}
