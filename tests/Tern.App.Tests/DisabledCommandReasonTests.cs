using Tern.App.Commands;
using Tern.Domain.Documents;
using Tern.Domain.Text;

namespace Tern.App.Tests;

public sealed class DisabledCommandReasonTests
{
    [Fact]
    public async Task Every_gated_command_has_a_reason_and_available_commands_have_none()
    {
        using var f = new ShellFixture();
        var saved = (await f.Shell.OpenFileAsync(await f.WriteAsync("saved.md", "# Saved")))!;
        var draft = new DocumentSession(null, "", TextFileFormat.NewFileDefault, null);
        var readOnly = new DocumentSession(null, "Online content", TextFileFormat.NewFileDefault, null, isReadOnly: true);
        DocumentCommandContext[] contexts =
        [
            new(null), new(draft), new(readOnly), new(draft, HasWorkspace: true),
            new(saved.Session, OpenDocumentCount: 1),
            new(saved.Session, HasWorkspace: true, OpenDocumentCount: 2, CanUndo: true, CanRedo: true,
                HasDirtyDocuments: true, HasFolder: true, HasHistorySource: true),
            new(draft, HasFolder: true, HasBrowsedFolder: true),
        ];
        foreach (var context in contexts.SelectMany(context => new[] { context, context with { IsBusy = true } }))
        foreach (var command in f.Shell.Registry.Commands)
        {
            var reason = DocumentCommandPolicy.DisabledReason(command.Id, context);
            if (DocumentCommandPolicy.CanExecute(command.Id, context)) Assert.Null(reason);
            else Assert.False(string.IsNullOrWhiteSpace(reason), $"{command.Id} has no unavailable-command explanation for {context}.");
        }
    }

    [Fact]
    public void Missing_workspace_history_and_undo_reasons_explain_the_action_needed()
    {
        var draft = new DocumentSession(null, "A note", TextFileFormat.NewFileDefault, null);
        Assert.Contains("Make this folder a workspace", DocumentCommandPolicy.DisabledReason(CommandIds.FindInWorkspace,
            new(draft, HasFolder: true, HasBrowsedFolder: true)));
        Assert.Contains("Save", DocumentCommandPolicy.DisabledReason(CommandIds.ShowHistory, new(draft)));
        Assert.Contains("undo", DocumentCommandPolicy.DisabledReason(CommandIds.Undo, new(draft)));
    }
}
