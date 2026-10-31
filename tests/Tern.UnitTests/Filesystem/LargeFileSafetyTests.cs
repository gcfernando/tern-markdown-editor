using System.Text;
using Tern.Filesystem;

namespace Tern.UnitTests.Filesystem;

public sealed class LargeFileSafetyTests
{
    [Theory]
    [InlineData(67_108_865L)]
    [InlineData(1_000_000_000L)]
    [InlineData(1_073_741_824L)]
    public async Task Oversized_files_are_refused_before_decoding_and_left_unchanged(long length)
    {
        using var dir = new TempDirectory();
        var path = dir.File("large.md");
        await using (var stream = File.Create(path))
        {
            stream.Write("# original"u8);
            stream.SetLength(length);
        }

        var error = await Assert.ThrowsAsync<IOException>(() => new DocumentFileService().LoadAsync(path));
        Assert.Contains("64 MiB", error.Message, StringComparison.Ordinal);
        Assert.Equal(length, new FileInfo(path).Length);
        using var check = File.OpenRead(path);
        var prefix = new byte[10];
        check.ReadExactly(prefix);
        Assert.Equal("# original", Encoding.UTF8.GetString(prefix));
    }

    [Fact]
    public async Task Cancelled_open_does_not_allocate_or_change_the_file()
    {
        using var dir = new TempDirectory();
        var path = dir.File("cancel.md");
        await File.WriteAllTextAsync(path, "unchanged");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new DocumentFileService().LoadAsync(path, cancellationToken: cancellation.Token));
        Assert.Equal("unchanged", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Streaming_baseline_matches_loaded_bytes()
    {
        using var dir = new TempDirectory();
        var path = dir.File("unicode.md");
        await File.WriteAllTextAsync(path, "# å世界\r\n```malformed\n");
        var files = new DocumentFileService();
        var loaded = await files.LoadAsync(path);
        var baseline = await files.ReadBaselineAsync(path);
        Assert.NotNull(baseline);
        Assert.True(loaded.Baseline.SameContentAs(baseline));
    }
}
