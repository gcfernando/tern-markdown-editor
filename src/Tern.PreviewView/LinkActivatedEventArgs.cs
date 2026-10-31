using Tern.Rendering.Resources;

namespace Tern.PreviewView;

/// <summary>Raised when the user activates a link. The preview never opens anything itself; the host decides.</summary>
public sealed class LinkActivatedEventArgs : EventArgs
{
    /// <summary>Creates the event data.</summary>
    public LinkActivatedEventArgs(ResourceDecision resolution, string destination)
    {
        Resolution = resolution;
        Destination = destination;
    }

    /// <summary>What the resource resolver decided about the destination (a local file, an external link, a blocked target, ...).</summary>
    public ResourceDecision Resolution { get; }

    /// <summary>The destination as written in the document.</summary>
    public string Destination { get; }
}

/// <summary>Raised when a link points inside the document. Set <see cref="Handled"/> to stop the preview scrolling to it.</summary>
public sealed class AnchorRequestedEventArgs : EventArgs
{
    /// <summary>Creates the event data.</summary>
    public AnchorRequestedEventArgs(string fragment) => Fragment = fragment;

    /// <summary>The fragment without the leading <c>#</c>.</summary>
    public string Fragment { get; }

    /// <summary>Set to true to take over navigation.</summary>
    public bool Handled { get; set; }
}

/// <summary>A 1-based source line.</summary>
public sealed class SourceLineEventArgs : EventArgs
{
    /// <summary>Creates the event data.</summary>
    public SourceLineEventArgs(int line) => Line = line;

    /// <summary>The line, 1-based.</summary>
    public int Line { get; }
}
