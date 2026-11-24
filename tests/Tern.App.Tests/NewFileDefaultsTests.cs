using System.Text;
using Tern.App.Help;
using Tern.App.Settings;
using Tern.Domain.Text;
using Tern.Filesystem;
using Tern.Recovery;
using Tern.Workspaces;

namespace Tern.App.Tests;

public sealed class NewFileDefaultsTests
{
    [Theory]
    [InlineData(TextEncodingKind.Utf8, false, LineEnding.Lf)]
    [InlineData(TextEncodingKind.Utf8, true, LineEnding.CrLf)]
    [InlineData(TextEncodingKind.Utf16LittleEndian, false, LineEnding.Cr)]
    [InlineData(TextEncodingKind.Windows1252, true, LineEnding.Lf)]
    public async Task New_drafts_and_templates_save_exact_selected_bytes_without_changing_existing_tabs(TextEncodingKind encoding, bool bom, LineEnding ending)
    {
        using var f = new ShellFixture();
        var existing = f.Shell.NewDocument("Keep\r\nthese\nexact endings");
        var existingFormat = existing.Session.Format;
        f.Shell.Settings.DefaultEncoding = encoding;
        f.Shell.Settings.DefaultEncodingHasBom = bom;
        f.Shell.Settings.DefaultLineEnding = ending;
        var format = f.Shell.Settings.NewFileFormat;
        var blank = f.Shell.NewDocument();
        Assert.Equal(format.Encoding, blank.Session.Format.Encoding);
        Assert.Equal(format.HasBom, blank.Session.Format.HasBom);
        Assert.Equal(ending, blank.Session.Format.DominantLineEnding);
        var draft = f.Shell.NewDocumentFromTemplate(new StarterTemplate("Defaults", "", "# Café\nSecond line\r\n"));
        Assert.Equal("# Café" + format.NewLineText + "Second line" + format.NewLineText, draft.CurrentText);
        var destination = Path.Combine(f.Docs, "new-defaults.md");
        Assert.IsType<SaveSucceeded>(await draft.SaveAsAsync(destination, false));
        Assert.Equal(TextFileCodec.Encode(draft.CurrentText, format), await File.ReadAllBytesAsync(destination));
        Assert.False(draft.IsDirty);
        Assert.Equal("Keep\r\nthese\nexact endings", existing.CurrentText);
        Assert.Equal(existingFormat, existing.Session.Format);
    }

    [Fact]
    public async Task Settings_round_trip_and_invalid_default_values_keep_other_preferences()
    {
        using var f = new ShellFixture();
        var store = new SettingsStore(f.Root.File("defaults-settings"));
        store.Save(new AppSettings { DefaultEncoding = TextEncodingKind.Utf32BigEndian, DefaultLineEnding = LineEnding.Lf,
            Theme = ThemeChoice.Paper, WordWrap = false });
        var loaded = store.Load();
        Assert.Equal(TextEncodingKind.Utf32BigEndian, loaded.DefaultEncoding);
        Assert.True(loaded.DefaultEncodingHasBom);
        Assert.Equal(LineEnding.Lf, loaded.DefaultLineEnding);
        Assert.Equal(ThemeChoice.Paper, loaded.Theme);
        await File.WriteAllTextAsync(store.FilePath, "{\"DefaultEncoding\":\"not-an-encoding\",\"DefaultLineEnding\":\"none\",\"WordWrap\":false,\"Theme\":\"Paper\"}");
        loaded = store.Load();
        Assert.Equal(TextEncodingKind.Utf8, loaded.DefaultEncoding);
        Assert.Equal(LineEnding.CrLf, loaded.DefaultLineEnding);
        Assert.False(loaded.WordWrap);
        Assert.Equal(ThemeChoice.Paper, loaded.Theme);
        Assert.Contains(nameof(AppSettings.DefaultEncoding), store.LastLoadProblem);
        Assert.Contains(nameof(AppSettings.DefaultLineEnding), store.LastLoadProblem);
        await File.WriteAllTextAsync(store.FilePath, "{\"DefaultEncoding\":100932,\"WordWrap\":false}");
        loaded = store.Load();
        Assert.Equal(TextEncodingIds.FromCodePage(932), loaded.DefaultEncoding);
        Assert.Null(store.LastLoadProblem);
        Assert.False(loaded.WordWrap);
    }

    [Fact]
    public async Task Today_uses_current_defaults_but_existing_notes_keep_detected_bytes_and_format()
    {
        using var f = new ShellFixture();
        Assert.True(await f.Shell.OpenWorkspaceAsync(f.Docs));
        f.Shell.Settings.DefaultEncoding = TextEncodingKind.Windows1252;
        f.Shell.Settings.DefaultLineEnding = LineEnding.Lf;
        var date = new DateTimeOffset(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);
        var tab = Assert.IsType<Tern.App.Documents.DocumentTab>(await f.Shell.OpenTodaysNoteAsync(date));
        Assert.Equal(TextEncodingKind.Windows1252, tab.Session.Format.Encoding);
        Assert.Equal(LineEnding.Lf, tab.Session.Format.DominantLineEnding);
        Assert.DoesNotContain('\r', tab.CurrentText);
        var bytes = await File.ReadAllBytesAsync(tab.FilePath!);
        Assert.Equal(TextFileCodec.Encode(tab.CurrentText, f.Shell.Settings.NewFileFormat), bytes);
        f.Shell.Settings.DefaultEncoding = TextEncodingKind.Utf16BigEndian;
        f.Shell.Settings.DefaultLineEnding = LineEnding.Cr;
        Assert.Same(tab, await f.Shell.OpenTodaysNoteAsync(date));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(tab.FilePath!));
        var existingPath = await f.WriteAsync(Path.Combine("Journal", "2026-11-22.md"), "# Existing π\r\nKeep\n");
        var existingBytes = await File.ReadAllBytesAsync(existingPath);
        var existing = Assert.IsType<Tern.App.Documents.DocumentTab>(await f.Shell.OpenTodaysNoteAsync(date.AddDays(1)));
        Assert.Equal(TextEncodingKind.Utf8, existing.Session.Format.Encoding);
        Assert.True(existing.Session.Format.HasMixedLineEndings);
        Assert.Equal(existingBytes, await File.ReadAllBytesAsync(existingPath));
        Assert.False(existing.IsDirty);
    }

    [Fact]
    public void Discarded_recovery_keeps_exact_text_and_original_format_under_different_defaults()
    {
        using var f = new ShellFixture();
        f.Shell.Settings.DefaultEncoding = TextEncodingKind.Utf16BigEndian;
        f.Shell.Settings.DefaultLineEnding = LineEnding.Cr;
        var format = new TextFileFormat(TextEncodingKind.Windows1252, false, LineEnding.Lf, true, true);
        var record = new RecoveryRecord(Guid.NewGuid(), f.Root.File("original.md"), "Recovered", 7,
            DateTime.UtcNow, null, format, "Keep €\r\nmixed\nexact\r");
        var recovered = f.Shell.ReopenDiscarded(record);
        Assert.Equal(record.Text, recovered.CurrentText);
        Assert.Equal(format, recovered.Session.Format);
        Assert.True(recovered.IsDirty);
        Assert.Null(recovered.FilePath);
    }

    [Fact]
    public async Task Creation_receipt_applies_only_to_exact_created_bytes_and_never_creates_a_lossy_file()
    {
        using var f = new ShellFixture();
        var operations = new WorkspaceFileOperations(f.Docs);
        var format = new AppSettings { DefaultEncoding = TextEncodingKind.Windows1252, DefaultLineEnding = LineEnding.Cr }.NewFileFormat;
        var created = operations.CreateFile(f.Docs, "empty.md", format: format);
        Assert.True(created.Succeeded);
        Assert.NotNull(created.CreatedFile);
        var files = new DocumentFileService();
        var opened = await files.LoadAsync(created.Path!, createdFile: created.CreatedFile);
        Assert.Equal(TextEncodingKind.Windows1252, opened.Format.Encoding);
        Assert.Equal(LineEnding.Cr, opened.Format.DominantLineEnding);
        Assert.False(opened.ShouldOpenReadOnly);
        var replacement = Encoding.UTF8.GetBytes("Replacement π\n");
        await File.WriteAllBytesAsync(created.Path!, replacement);
        opened = await files.LoadAsync(created.Path!, createdFile: created.CreatedFile);
        Assert.Equal("Replacement π\n", opened.Text);
        Assert.Equal(TextEncodingKind.Utf8, opened.Format.Encoding);
        Assert.Equal(LineEnding.Lf, opened.Format.DominantLineEnding);
        Assert.Equal(replacement, await File.ReadAllBytesAsync(created.Path!));
        var failed = operations.CreateFile(f.Docs, "must-not-exist.md", "漢字", format: format);
        Assert.False(failed.Succeeded);
        Assert.Equal(FileOperationError.Failed, failed.Error);
        Assert.False(File.Exists(Path.Combine(f.Docs, "must-not-exist.md")));
        var collision = operations.CreateFile(f.Docs, "empty.md", "Would replace", format: format);
        Assert.Equal(FileOperationError.AlreadyExists, collision.Error);
        Assert.Null(collision.CreatedFile);
        Assert.Equal(replacement, await File.ReadAllBytesAsync(created.Path!));
    }
}
