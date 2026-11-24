using Tern.Workspaces;

namespace Tern.UnitTests;

public sealed class LinkedImageIdentityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tern-linked-image-tests", Guid.NewGuid().ToString("N"));

    public LinkedImageIdentityTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void A_different_file_created_at_the_same_path_does_not_inherit_the_allow_list()
    {
        var note = Path.Combine(_root, "note.md");
        var image = Path.Combine(_root, "a.png");
        File.WriteAllText(note, "x");
        var store = new LinkedImageStore(_root);
        Assert.True(store.Save(note, [image]));
        Assert.Equal([image], new LinkedImageStore(_root).Get(note));

        File.Delete(note);
        File.WriteAllText(note, "y");
        File.SetCreationTimeUtc(note, DateTime.UtcNow.AddHours(2));
        Assert.Empty(new LinkedImageStore(_root).Get(note));
    }

    [Fact]
    public void Remap_moves_or_copies_the_allow_list_and_restamps_it_for_the_new_file()
    {
        var oldNote = Path.Combine(_root, "old.md");
        var newNote = Path.Combine(_root, "new.md");
        var copy = Path.Combine(_root, "copy.md");
        var image = Path.Combine(_root, "a.png");
        File.WriteAllText(oldNote, "x");
        var store = new LinkedImageStore(_root);
        store.Save(oldNote, [image]);
        File.Copy(oldNote, copy);
        File.Move(oldNote, newNote);
        File.SetCreationTimeUtc(newNote, DateTime.UtcNow.AddHours(-3));

        Assert.True(store.Remap([(oldNote, newNote)], keepOriginal: false));
        Assert.True(store.Remap([(newNote, copy)], keepOriginal: true));

        var reloaded = new LinkedImageStore(_root);
        Assert.Empty(reloaded.Get(oldNote));
        Assert.Equal([image], reloaded.Get(newNote));
        Assert.Equal([image], reloaded.Get(copy));
        Assert.True(reloaded.Forget(_root, isFolder: true));
        Assert.Empty(new LinkedImageStore(_root).Get(copy));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }
}
