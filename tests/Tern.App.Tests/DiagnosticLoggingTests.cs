using Tern.App.Shell;

namespace Tern.App.Tests;

[CollectionDefinition("Diagnostic logging", DisableParallelization = true)]
public sealed class DiagnosticLoggingCollection { }

[Collection("Diagnostic logging")]
public sealed class DiagnosticLoggingTests
{
    [Fact]
    public void Opt_in_records_boundary_types_and_status_severity_without_content_actions_paths_or_messages()
    {
        var previous = Environment.GetEnvironmentVariable("TERN_DIAGNOSTIC_LOG");
        try
        {
            Environment.SetEnvironmentVariable("TERN_DIAGNOSTIC_LOG", null);
            using var f = new ShellFixture();
            var file = Path.Combine(f.Shell.Paths.StateDirectory, "diagnostics.log");
            const string privateText = "synthetic-private-draft-and-token-7afcb5";
            f.Shell.ReportUnexpectedError(privateText, new InvalidOperationException(privateText));
            Assert.False(File.Exists(file));

            Environment.SetEnvironmentVariable("TERN_DIAGNOSTIC_LOG", "1");
            f.Shell.SetStatus(privateText, StatusSeverity.Info);
            try { throw new InvalidOperationException(privateText, new IOException(f.Docs)); }
            catch (InvalidOperationException exception) { f.Shell.ReportUnexpectedError(privateText, exception); }
            var report = File.ReadAllText(file);
            Assert.Contains("Status: Info", report);
            Assert.Contains("Status: Error", report);
            Assert.Contains("InvalidOperationException", report);
            Assert.Contains("IOException", report);
            Assert.Contains(nameof(Opt_in_records_boundary_types_and_status_severity_without_content_actions_paths_or_messages), report);
            Assert.DoesNotContain(privateText, report);
            Assert.DoesNotContain(f.Docs, report);
            Assert.DoesNotContain(" in ", report);
            Assert.DoesNotContain("Message:", report);

            var length = new FileInfo(file).Length;
            using (var locked = File.Open(file, FileMode.Open, FileAccess.Read, FileShare.None))
                f.Shell.SetStatus("A diagnostic write cannot interrupt normal feedback.", StatusSeverity.Warning);
            Assert.Equal(StatusSeverity.Warning, f.Shell.StatusSeverity);
            Assert.Equal(length, new FileInfo(file).Length);

            Environment.SetEnvironmentVariable("TERN_DIAGNOSTIC_LOG", null);
            f.Shell.SetStatus("Logging is off again.", StatusSeverity.Success);
            Assert.Equal(length, new FileInfo(file).Length);
        }
        finally { Environment.SetEnvironmentVariable("TERN_DIAGNOSTIC_LOG", previous); }
    }
}
