using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using System.Text;
using Tern.Domain.Text;
using Tern.Recovery;

namespace Tern.UnitTests.Recovery;

public sealed class RecoveryFormatTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "tern-format-recovery-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Sixty_MiB_ascii_document_uses_utf8_and_recovers_exactly()
    {
        var store = new RecoveryStore(_directory);
        var record = Record(Guid.NewGuid(), new string('a', 60 * 1024 * 1024), TextFileFormat.NewFileDefault);
        await store.WriteCheckpointAsync(record);
        var file = Assert.Single(Directory.GetFiles(_directory, "*.tern-recovery"));
        Assert.InRange(new FileInfo(file).Length, record.Text.Length, record.Text.Length + 2048L);
        using (var stream = File.OpenRead(file))
        {
            var magic = new byte[8];
            stream.ReadExactly(magic);
            Assert.Equal("TERNREC2", Encoding.ASCII.GetString(magic));
        }
        var scan = store.Scan();
        Assert.Empty(scan.Corrupt);
        Assert.Equal(record, Assert.Single(scan.Entries).Record);
    }

    [Fact]
    public async Task Legacy_utf16_checkpoint_is_read_and_next_generation_uses_utf8()
    {
        var store = new RecoveryStore(_directory);
        var record = Record(Guid.NewGuid(), "legacy 日本語\r\n\U0001F600", TextFileFormat.NewFileDefault);
        await store.WriteCheckpointAsync(record);
        var file = Assert.Single(Directory.GetFiles(_directory, "*.tern-recovery"));
        await ReplacePayload(file, "TERNREC1", Encoding.Unicode.GetBytes(record.Text));
        Assert.Equal(record, Assert.Single(store.Scan().Entries).Record);
        await store.WriteCheckpointAsync(record with { Text = "next odd length", Revision = 2 });
        Assert.Equal("next odd length", Assert.Single(store.Scan().Entries).Record.Text);
        Assert.Equal(2, Directory.GetFiles(_directory, "*.tern-recovery").Length);
    }

    [Fact]
    public async Task Invalid_utf8_with_a_matching_checksum_is_rejected_and_previous_generation_survives()
    {
        var store = new RecoveryStore(_directory);
        var older = Record(Guid.NewGuid(), "safe", TextFileFormat.NewFileDefault);
        await store.WriteCheckpointAsync(older);
        await store.WriteCheckpointAsync(older with { Text = "new", Revision = 2 });
        var newest = Directory.GetFiles(_directory, "*.tern-recovery").Order(StringComparer.Ordinal).Last();
        await ReplacePayload(newest, "TERNREC2", [0xc3, 0x28]);
        var scan = store.Scan();
        Assert.Single(scan.Corrupt);
        Assert.Equal(older, Assert.Single(scan.Entries).Record);
    }

    [Fact]
    public void Large_notes_get_a_longer_idle_delay_but_it_is_bounded()
    {
        var store = new RecoveryStore(_directory);
        using var scheduler = new RecoveryScheduler(store);
        Assert.Equal(TimeSpan.FromMilliseconds(1200), scheduler.IdleDelayFor(1024));
        Assert.Equal(TimeSpan.FromMilliseconds(4800), scheduler.IdleDelayFor(16 * 1024 * 1024));
        Assert.Equal(TimeSpan.FromSeconds(10), scheduler.IdleDelayFor(60 * 1024 * 1024));
    }

    private static async Task ReplacePayload(string path, string magic, byte[] payload)
    {
        var bytes = await File.ReadAllBytesAsync(path);
        var header = JsonNode.Parse(bytes.AsSpan(12, BitConverter.ToInt32(bytes, 8)))!.AsObject();
        header["PayloadLength"] = payload.Length;
        header["PayloadSha256"] = Convert.ToHexString(SHA256.HashData(payload));
        var serialized = JsonSerializer.SerializeToUtf8Bytes(header);
        await File.WriteAllBytesAsync(path, [.. Encoding.ASCII.GetBytes(magic), .. BitConverter.GetBytes(serialized.Length), .. serialized, .. payload]);
    }

    [Theory]
    [InlineData(932, "日本語\n")]
    [InlineData(1251, "Привет\r\n")]
    public async Task Extended_encoding_and_source_identity_survive_checkpoint_roundtrip(int codePage, string text)
    {
        var store = new RecoveryStore(_directory);
        var format = LineEndingAnalyzer.Describe(TextEncodingIds.FromCodePage(codePage), false, text);
        var record = Record(Guid.NewGuid(), text, format) with { IsExternalDraft = true, SourceUrl = "https://example.com/original.md?version=1" };
        await store.WriteCheckpointAsync(record);

        var scan = store.Scan();

        Assert.Empty(scan.Corrupt);
        var restored = Assert.Single(scan.Entries).Record;
        Assert.Equal(record, restored);
        Assert.Contains(codePage.ToString(System.Globalization.CultureInfo.InvariantCulture), restored.Format.EncodingDisplayName, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(999)]
    [InlineData(165000)] // The extended-ID representation of unsafe UTF-7.
    [InlineData(165535)] // Unavailable code page.
    [InlineData(int.MaxValue)]
    public async Task Invalid_encoding_metadata_reports_corrupt_and_offers_older_generation(int encoding)
    {
        var store = new RecoveryStore(_directory);
        var id = Guid.NewGuid();
        var older = Record(id, "recover this exact text\n", TextFileFormat.NewFileDefault);
        await store.WriteCheckpointAsync(older);
        await store.WriteCheckpointAsync(older with { Text = "newest text", Revision = 2 });
        var newest = Directory.GetFiles(_directory, "*.tern-recovery").Order(StringComparer.Ordinal).Last();
        await RewriteHeader(newest, header => header["Encoding"] = encoding);

        var scan = store.Scan();

        Assert.Equal(older, Assert.Single(scan.Entries).Record);
        Assert.Contains("format metadata", Assert.Single(scan.Corrupt).Reason, StringComparison.Ordinal);
        Assert.True(File.Exists(newest));
        Assert.Equal(2, Directory.GetFiles(_directory, "*.tern-recovery").Length);
    }

    [Fact]
    public async Task Long_valid_source_url_is_metadata_only_and_roundtrips_without_an_arbitrary_length_limit()
    {
        var store = new RecoveryStore(_directory);
        var record = Record(Guid.NewGuid(), "unchanged document", TextFileFormat.NewFileDefault)
            with { SourceUrl = "https://example.com/" + new string('a', 9000) + ".md" };
        await store.WriteCheckpointAsync(record);
        var scan = store.Scan();
        Assert.Empty(scan.Corrupt);
        Assert.Equal(record, Assert.Single(scan.Entries).Record);
    }

    [Theory]
    [InlineData("not a URI")]
    [InlineData("/relative.md")]
    [InlineData("file:///C:/private.md")]
    [InlineData("https://user:password@example.com/file.md")]
    public async Task Invalid_source_metadata_reports_corrupt_and_keeps_older_text(string sourceUrl)
    {
        var store = new RecoveryStore(_directory);
        var older = Record(Guid.NewGuid(), "safe previous document", TextFileFormat.NewFileDefault);
        await store.WriteCheckpointAsync(older);
        await store.WriteCheckpointAsync(older with { Text = "newer document", Revision = 2 });
        var newest = Directory.GetFiles(_directory, "*.tern-recovery").Order(StringComparer.Ordinal).Last();
        await RewriteHeader(newest, header => header["SourceUrl"] = sourceUrl);
        var scan = store.Scan();
        Assert.Equal(older, Assert.Single(scan.Entries).Record);
        Assert.Contains("source URL metadata", Assert.Single(scan.Corrupt).Reason, StringComparison.Ordinal);
        Assert.True(File.Exists(newest));
    }

    [Fact]
    public async Task Legacy_checkpoint_without_source_url_remains_readable()
    {
        var store = new RecoveryStore(_directory);
        var record = Record(Guid.NewGuid(), "legacy\n", TextFileFormat.NewFileDefault);
        await store.WriteCheckpointAsync(record);
        await RewriteHeader(Assert.Single(Directory.GetFiles(_directory, "*.tern-recovery")), header => header.Remove("SourceUrl"));

        var scan = store.Scan();

        Assert.Empty(scan.Corrupt);
        Assert.Equal(record, Assert.Single(scan.Entries).Record);
        Assert.Null(Assert.Single(scan.Entries).Record.SourceUrl);
    }

    [Theory]
    [InlineData(TextEncodingKind.Utf16LittleEndian)]
    [InlineData(TextEncodingKind.Utf16BigEndian)]
    [InlineData(TextEncodingKind.Utf32LittleEndian)]
    [InlineData(TextEncodingKind.Utf32BigEndian)]
    public void Unicode_labels_distinguish_actual_bom_presence(TextEncodingKind encoding)
    {
        var format = TextFileFormat.NewFileDefault with { Encoding = encoding };
        Assert.EndsWith("(no BOM)", format.EncodingDisplayName, StringComparison.Ordinal);
        Assert.EndsWith(" BOM", (format with { HasBom = true }).EncodingDisplayName, StringComparison.Ordinal);
    }

    private static RecoveryRecord Record(Guid id, string text, TextFileFormat format) =>
        new(id, null, "Recovered.md", 1, DateTime.UtcNow, null, format, text);

    private static async Task RewriteHeader(string path, Action<JsonObject> mutate)
    {
        var bytes = await File.ReadAllBytesAsync(path);
        var length = BitConverter.ToInt32(bytes, 8);
        var header = JsonNode.Parse(bytes.AsSpan(12, length))!.AsObject();
        mutate(header);
        var updated = JsonSerializer.SerializeToUtf8Bytes(header);
        await File.WriteAllBytesAsync(path, [.. bytes.AsSpan(0, 8).ToArray(), .. BitConverter.GetBytes(updated.Length), .. updated, .. bytes.AsSpan(12 + length).ToArray()]);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
