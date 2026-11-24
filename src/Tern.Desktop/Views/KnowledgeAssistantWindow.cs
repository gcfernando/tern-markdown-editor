using Avalonia.Controls;
using Tern.App.Knowledge;
using Tern.App.Shell;
using Tern.Desktop.Services;

namespace Tern.Desktop.Views;

internal sealed class KnowledgeAssistantWindow : Window
{
    internal KnowledgeAssistantPanel Assistant { get; }

    internal KnowledgeAssistantWindow(ShellViewModel shell, AIWorkspaceService ai, Func<string, int, Task> open,
        Action<KnowledgeAnswer, string?, Func<bool>, Func<CancellationToken, Task>> review, Func<Task> configure, Func<Task>? openFolder = null, Func<Task>? openSettings = null)
    {
        DialogParts.Setup(this, "Explore your notes", 1120, 1040, DialogGuidance.Connections,
            stableViewport: true, guidance: false);
        CanMaximize = false;
        CanMinimize = true;
        Assistant = new KnowledgeAssistantPanel(shell, ai, open, review, configure, openFolder, openSettings);
        Content = Assistant;
        Closed += (_, _) => Assistant.Dispose();
    }
}
