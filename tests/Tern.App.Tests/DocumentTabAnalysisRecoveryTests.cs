using System.Reflection;
using Tern.App.Documents;
using Tern.Domain.Text;
using Tern.Filesystem;
using Tern.Markdown;

namespace Tern.App.Tests;

public sealed class DocumentTabAnalysisRecoveryTests
{
    [Fact]
    public async Task A_failed_analysis_does_not_stop_later_analysis_of_the_document()
    {
        using var dir = new TempDir();
        var path = dir.File("note.md");
        await File.WriteAllTextAsync(path, "# First");
        using var tab = await DocumentTab.OpenAsync(path, new DocumentFileService(), MarkdownProfile.Extended);
        await UntilAnalyzed(tab);

        typeof(DocumentTab).GetField("_analysisWork", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(tab, Task.FromException(new InvalidOperationException("Synthetic analysis failure")));
        tab.ApplyEdits([TextEdit.Insert(tab.CurrentText.Length, "\n\n## Second")]);
        await UntilAnalyzed(tab);

        Assert.Equal(tab.Session.Revision, tab.AnalysisRevision);
        Assert.Contains(tab.Analysis!.Headings, heading => heading.Text == "Second");
    }

    private static async Task UntilAnalyzed(DocumentTab tab)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (tab.AnalysisRevision != tab.Session.Revision && DateTime.UtcNow < deadline) await Task.Delay(10);
    }
}
