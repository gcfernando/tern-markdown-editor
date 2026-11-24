namespace Tern.App;

/// <summary>A transport-confirmed incomplete response, distinct from invalid or unsupported model content.</summary>
public sealed class AIReplyIncompleteException() : InvalidOperationException("AI stopped before completing its reply. Your content is kept.") { }
