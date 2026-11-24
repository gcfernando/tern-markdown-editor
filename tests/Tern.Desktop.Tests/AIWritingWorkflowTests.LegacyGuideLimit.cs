using System.Reflection;
using Avalonia.Headless.XUnit;
using Tern.Desktop.Views;

namespace Tern.Desktop.Tests;

public sealed partial class AIWritingWorkflowTests
{
    [AvaloniaTheory]
    [InlineData(50_000)]
    [InlineData(80_000)]
    public async Task Legacy_guide_callback_uses_the_global_connections_saved_limit(int limit)
    {
        var sent = new List<string>();
        using var f = new DesktopFixture(directory => TwoLimits(directory, sent: sent));
        f.Window.AIWorkspace.Select(limit == 50_000 ? FirstId : SecondId);
        var question = "How do I export a PDF? " + new string('é', limit - 4_000);
        var callback = typeof(MainWindow).GetMethod("AnswerGuideQuestionAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;

        var answer = await (Task<string>)callback.Invoke(f.Window, [question, CancellationToken.None])!;

        Assert.Equal("Answer", answer);
        var prompt = Assert.Single(sent);
        Assert.InRange(prompt.Length, question.Length, limit);
        Assert.EndsWith(question, prompt);
    }
}
