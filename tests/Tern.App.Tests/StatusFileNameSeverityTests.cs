using Tern.App.Shell;
using Tern.Domain.Text;
using Tern.Recovery;

namespace Tern.App.Tests;

/// <summary>A file or folder name must never decide whether a finished action looks like a warning or an error.</summary>
public sealed class StatusFileNameSeverityTests
{
    [Theory]
    [InlineData("Code Review")]
    [InlineData("error-log")]
    public async Task Opening_a_folder_is_information_whatever_it_is_called(string folderName)
    {
        using var fixture = new ShellFixture();
        var note = await fixture.WriteAsync(Path.Combine(folderName, "note.md"), "# Note");

        Assert.True(await fixture.Shell.OpenDirectoryAsync(Path.GetDirectoryName(note)!));

        Assert.Contains(folderName, fixture.Shell.StatusMessage);
        Assert.Equal(StatusSeverity.Info, fixture.Shell.StatusSeverity);
    }

    [Theory]
    [InlineData("error-log.md")]
    [InlineData("review-plan.md")]
    public async Task Renaming_is_success_whatever_the_new_name_is(string name)
    {
        using var fixture = new ShellFixture();
        var tab = (await fixture.Shell.OpenFileAsync(await fixture.WriteAsync("note.md", "# Note")))!;

        Assert.True(await fixture.Shell.RenameDocumentAsync(tab, name));

        Assert.Contains(name, fixture.Shell.StatusMessage);
        Assert.Equal(StatusSeverity.Success, fixture.Shell.StatusSeverity);
    }

    [Fact]
    public async Task Recovering_a_document_named_like_a_failure_is_success()
    {
        using var fixture = new ShellFixture();
        var record = new RecoveryRecord(Guid.NewGuid(), Path.Combine(fixture.Docs, "error-log.md"), "error-log.md", 4,
            DateTime.UtcNow, null, TextFileFormat.NewFileDefault, "Recovered text");

        await fixture.Shell.RestoreRecoveryAsync(record);

        Assert.Contains("error-log.md", fixture.Shell.StatusMessage);
        Assert.Equal(StatusSeverity.Success, fixture.Shell.StatusSeverity);
    }
}
