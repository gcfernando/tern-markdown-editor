using Tern.Rendering.Preview;

namespace Tern.PreviewView;

public sealed class TaskActivatedEventArgs(PreviewDocument document, int line, bool wasChecked) : EventArgs
{
    public PreviewDocument Document { get; } = document;
    public int Line { get; } = line;
    public bool WasChecked { get; } = wasChecked;
}
