using Tern.Desktop.Services;

namespace Tern.Desktop.Tests;

public sealed class WindowsRecycleBinServiceTests
{
    [Fact]
    public void Operation_requires_recycling_and_stops_on_errors_without_elevation_and_avoids_duplicate_confirmation()
    {
        var flags = WindowsRecycleBinService.OperationFlags;
        Assert.Equal(0x00080000u, flags & 0x00080000u); // RECYCLEONDELETE
        Assert.Equal(0x00100400u, flags & 0x00100400u); // EARLYFAILURE + NOERRORUI
        Assert.Equal(0x20000000u, flags & 0x20000000u); // ADDUNDORECORD
        Assert.Equal(0u, flags & (0x00040000u | 0x10000000u)); // No elevation.
        Assert.Equal(0x10u, flags & 0x10u); // The application already confirmed the selected file.
    }

    [Theory]
    [InlineData(unchecked((int)0x80004005), false)]
    [InlineData(unchecked((int)0x80070005), false)]
    [InlineData(0, true)]
    public void Native_failure_and_aborted_success_are_not_reported_as_completed(int result, bool aborted) =>
        Assert.Throws<IOException>(() => WindowsRecycleBinService.CheckResult(result, aborted));

    [Fact]
    public void Completed_native_operation_accepts_success() => WindowsRecycleBinService.CheckResult(0);
}
