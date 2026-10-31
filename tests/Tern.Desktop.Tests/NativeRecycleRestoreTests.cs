using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Tern.Desktop.Services;

namespace Tern.Desktop.Tests;

public sealed class NativeRecycleRestoreTests
{
    [Fact]
    public async Task Native_recycle_retains_a_test_owned_file_and_windows_can_restore_its_exact_bytes()
    {
        Assert.SkipWhen(!OperatingSystem.IsWindows(), "Windows Shell integration requires Windows.");
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new Thread(() =>
        {
            try
            {
                if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
                VerifyNativeRecycleAndRestore();
                completion.SetResult();
            }
            catch (Exception error) { completion.SetException(error); }
        }) { IsBackground = true };
        if (OperatingSystem.IsWindows()) worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(45), TestContext.Current.CancellationToken);
    }

    [SupportedOSPlatform("windows")]
    private static void VerifyNativeRecycleAndRestore()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tern-native-recycle-probe", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var name = "tern-recycle-probe-" + Guid.NewGuid().ToString("N") + ".md";
        var path = Path.Combine(directory, name);
        byte[] bytes = [35, 32, 84, 101, 114, 110, 13, 10, 0xC3, 0xA9, 13, 10];
        File.WriteAllBytes(path, bytes);
        object? application = null, recycle = null, items = null, matched = null;
        try
        {
            new WindowsRecycleBinService().MoveToRecycleBinAsync(path).GetAwaiter().GetResult();
            Assert.False(File.Exists(path));
            application = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application", throwOnError: true)!);
            recycle = ((dynamic)application!).NameSpace(10);
            items = ((dynamic)recycle!).Items();
            var count = (int)((dynamic)items!).Count;
            for (var index = 0; index < count; index++)
            {
                object candidate = ((dynamic)items).Item(index);
                var displayName = (string)((dynamic)candidate).Name;
                if (string.Equals(displayName, name, StringComparison.Ordinal) || string.Equals(displayName, Path.GetFileNameWithoutExtension(name), StringComparison.Ordinal))
                { matched = candidate; break; }
                Marshal.FinalReleaseComObject(candidate);
            }
            Assert.NotNull(matched);
            // Canonical Windows Shell restore verb; target is only our GUID-named test item.
            ((dynamic)matched).InvokeVerb("undelete");
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (!File.Exists(path) && DateTime.UtcNow < deadline) Thread.Sleep(50);
            Assert.True(File.Exists(path), "Windows did not restore the test-owned recycled file.");
            Assert.Equal(bytes, File.ReadAllBytes(path));
        }
        finally
        {
            foreach (var value in new[] { matched, items, recycle, application })
                if (value is not null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value);
            // Only remove this test's own restored file. Never purge the Recycle Bin.
            if (File.Exists(path)) File.Delete(path);
            if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
        }
    }
}
