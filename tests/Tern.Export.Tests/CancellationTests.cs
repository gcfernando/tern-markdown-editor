namespace Tern.Export.Tests;

public sealed class CancellationTests
{
    [Theory]
    [InlineData("html")]
    [InlineData("pdf")]
    [InlineData("text")]
    public async Task Actual_large_export_stops_within_two_seconds_of_cancel_without_replacing_target(string format)
    {
        var directory = Directory.CreateTempSubdirectory("tern-export-cancel-");
        try
        {
            var path = Path.Combine(directory.FullName, "copy." + format);
            byte[] original = [0x00, 0x31, 0xFF, 0x09];
            await File.WriteAllBytesAsync(path, original);
            var markdown = string.Concat(Enumerable.Repeat("# Heading\n\nA paragraph with **formatting** and [a link](https://example.com).\n\n", 50000));
            using var cancellation = new CancellationTokenSource();
            var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = cancellation.Token.Register(() => canceled.TrySetResult());
            var export = Task.Run(() =>
            {
                cancellation.CancelAfter(50);
                switch (format)
                {
                    case "html": HtmlExporter.ExportToFile(markdown, path, cancellationToken: cancellation.Token); break;
                    case "pdf": PdfExporter.ExportToFile(markdown, path, cancellationToken: cancellation.Token); break;
                    default: PlainTextExporter.ExportToFile(markdown, path, cancellationToken: cancellation.Token); break;
                }
            });
            await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => export).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(original, await File.ReadAllBytesAsync(path));
            Assert.Equal([path], Directory.GetFiles(directory.FullName));
        }
        finally { directory.Delete(recursive: true); }
    }

    [Fact]
    public async Task Cancellation_during_actual_temp_writing_removes_temp_and_keeps_old_target()
    {
        var directory = Directory.CreateTempSubdirectory("tern-export-write-cancel-");
        try
        {
            var path = Path.Combine(directory.FullName, "copy.html");
            await File.WriteAllTextAsync(path, "existing output");
            using var cancellation = new CancellationTokenSource();
            using var watcher = new FileSystemWatcher(directory.FullName, "*.tmp");
            var writing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            watcher.Created += (_, _) => { cancellation.Cancel(); writing.TrySetResult(); };
            watcher.EnableRaisingEvents = true;
            var data = new byte[32 * 1024 * 1024];
            var export = Task.Run(() => AtomicFile.Write(path, data, cancellationToken: cancellation.Token));
            await writing.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => export).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal("existing output", await File.ReadAllTextAsync(path));
            Assert.Equal([path], Directory.GetFiles(directory.FullName));
        }
        finally { directory.Delete(recursive: true); }
    }
}
