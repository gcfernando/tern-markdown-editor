using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Tern.Desktop.Services;

internal sealed record CopilotProcessResult(int ExitCode, string Output);

/// <summary>Opt-in native Copilot CLI text mode, using an isolated configuration and no workspace context.</summary>
public sealed class CopilotTextClient
{
    public const int MaximumPromptCharacters = 8192;
    private const int MaximumOutputCharacters = 524_288;
    private readonly Func<ProcessStartInfo, CancellationToken, Task<CopilotProcessResult>> _run;
    public CopilotTextClient() : this(RunProcessAsync) { }
    internal CopilotTextClient(Func<ProcessStartInfo, CancellationToken, Task<CopilotProcessResult>> run) => _run = run;

    public static string DefaultExecutable => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "npm", "node_modules", "@github", "copilot", "node_modules", "@github", "copilot-win32-x64", "copilot.exe");

    public async Task<string> SendAsync(string executable, string model, string token, string reviewedPrompt, CancellationToken cancellationToken)
    {
        Validate(executable, model, token, reviewedPrompt);
        var baseDirectory = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Tern-Copilot-Text"));
        Directory.CreateDirectory(baseDirectory);
        var root = Path.Combine(baseDirectory, Guid.NewGuid().ToString("N"));
        var working = Path.Combine(root, "work"); var configuration = Path.Combine(root, "configuration");
        Directory.CreateDirectory(working); Directory.CreateDirectory(configuration);
        try
        {
            // Settings verified by native Copilot CLI 1.0.95 help config. No user/project hooks or IDE context.
            await File.WriteAllTextAsync(Path.Combine(configuration, "settings.json"),
                "{\"disableAllHooks\":true,\"ide\":{\"autoConnect\":false}}", cancellationToken).ConfigureAwait(false);
            var start = CreateStartInfo(executable, model, token, reviewedPrompt, working, configuration);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(90));
            var result = await _run(start, deadline.Token).ConfigureAwait(false);
            if (result.ExitCode != 0) throw new InvalidOperationException($"Copilot CLI exited with code {result.ExitCode}. Check the installed CLI version, Copilot entitlement, model and token's Copilot Requests permission. Raw CLI diagnostics are not displayed.");
            if (result.Output.Length > MaximumOutputCharacters) throw new InvalidOperationException("Copilot output exceeds its size limit.");
            return result.Output.Replace(token, "[redacted]", StringComparison.Ordinal).Trim();
        }
        catch (Win32Exception) { throw new InvalidOperationException("The native Copilot CLI could not start. Check its full executable path and Windows access permissions."); }
        finally { RemoveWorkingCopy(root, baseDirectory); }
    }

    private static void Validate(string executable, string model, string token, string prompt)
    {
        if (!Path.IsPathFullyQualified(executable) || executable.StartsWith("\\\\", StringComparison.Ordinal)
            || !string.Equals(Path.GetFileName(executable), "copilot.exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(executable))
            throw new ArgumentException("Choose a local installed native copilot.exe. Shell scripts, command files and network paths are not supported.", nameof(executable));
        if (string.IsNullOrWhiteSpace(model) || model.Length > 100 || model.Any(c => c < 33 || c > 126))
            throw new ArgumentException("Enter a model name, or auto, using up to 100 printable characters without spaces.", nameof(model));
        if (string.IsNullOrWhiteSpace(token) || token.Length > 4096 || token.Any(c => c < 33 || c > 126))
            throw new ArgumentException("Enter a GitHub token with Copilot Requests permission. It remains in memory and is not saved.", nameof(token));
        if (string.IsNullOrWhiteSpace(prompt) || prompt.Length > MaximumPromptCharacters || prompt.Contains('\0'))
            throw new ArgumentException("Enter text of up to 8,192 characters.", nameof(prompt));
        if (prompt.Contains('@') || prompt.TrimStart().StartsWith('/') || prompt.TrimStart().StartsWith('!') || prompt.Trim() == "$")
            throw new ArgumentException("This text-only connection does not accept @mentions, attachments or leading CLI commands. Paste the text itself and describe the task in ordinary words.", nameof(prompt));
    }

    internal static ProcessStartInfo CreateStartInfo(string executable, string model, string token, string prompt, string working, string configuration)
    {
        var start = new ProcessStartInfo(executable)
        { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = working, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        string[] flags = ["--available-tools=ask_user", "--no-ask-user", "--disable-builtin-mcps", "--no-custom-instructions", "--no-auto-update",
            "--no-remote", "--no-remote-export", "--no-experimental", "--no-bash-env", "--no-eager-powershell-resolution", "--disallow-temp-dir",
            "--deny-tool=read,write,shell,url,memory", "--silent", "--no-color", "--stream=off", "--output-format=text", "--log-level=none"];
        foreach (var flag in flags) start.ArgumentList.Add(flag);
        start.ArgumentList.Add("--model=" + model);
        // One equals-form argument prevents prompt text beginning with '-' from becoming a CLI switch.
        start.ArgumentList.Add("--prompt=" + prompt);
        // Do not inherit tokens, custom provider key commands, hooks, plugin locations or optional instrumentation.
        start.Environment.Clear();
        foreach (var variable in new[] { "SystemRoot", "WINDIR", "LOCALAPPDATA", "APPDATA", "TEMP", "TMP" })
            if (Environment.GetEnvironmentVariable(variable) is { } value) start.Environment[variable] = value;
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        start.Environment["PATH"] = Path.Combine(windows, "System32") + Path.PathSeparator + Path.GetDirectoryName(executable);
        start.Environment["COPILOT_HOME"] = configuration;
        start.Environment["COPILOT_GITHUB_TOKEN"] = token;
        start.Environment["COPILOT_AUTO_UPDATE"] = "false";
        start.Environment["COPILOT_OTEL_ENABLED"] = "false";
        start.Environment["GITHUB_COPILOT_PROMPT_MODE_WORKSPACE_MCP"] = "false";
        start.Environment["USE_TGREP"] = "false";
        return start;
    }

    private static async Task<CopilotProcessResult> RunProcessAsync(ProcessStartInfo start, CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = start };
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (!process.Start()) throw new InvalidOperationException("The Copilot CLI process did not start.");
        process.StandardInput.Close();
        var totalCharacters = 0;
        async Task<string> ReadAsync(StreamReader reader, bool retain)
        {
            var result = new StringBuilder(); var buffer = new char[4096];
            try
            {
                while (true)
                {
                    var count = await reader.ReadAsync(buffer, stop.Token).ConfigureAwait(false); if (count == 0) return result.ToString();
                    if (Interlocked.Add(ref totalCharacters, count) > MaximumOutputCharacters) throw new InvalidOperationException("Copilot output exceeded the 1 MiB text limit.");
                    if (retain) result.Append(buffer, 0, count);
                }
            }
            catch { stop.Cancel(); throw; }
        }
        var output = ReadAsync(process.StandardOutput, true); var errors = ReadAsync(process.StandardError, false);
        try
        {
            await Task.WhenAll(process.WaitForExitAsync(stop.Token), output, errors).ConfigureAwait(false);
            return new CopilotProcessResult(process.ExitCode, await output.ConfigureAwait(false));
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { }
            }
        }
    }

    private static void RemoveWorkingCopy(string path, string allowedParent)
    {
        var full = Path.GetFullPath(path); var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(allowedParent)) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("The private Copilot working path could not be verified for cleanup.");
        if (!Directory.Exists(full)) return;
        if (File.GetAttributes(full).HasFlag(FileAttributes.ReparsePoint)) { Directory.Delete(full, recursive: false); return; }
        // The directory was created exclusively for this run. Never follow links created by a child process.
        foreach (var entry in Directory.EnumerateFileSystemEntries(full))
        {
            var attributes = File.GetAttributes(entry);
            if (attributes.HasFlag(FileAttributes.Directory))
            {
                if (attributes.HasFlag(FileAttributes.ReparsePoint)) Directory.Delete(entry, recursive: false);
                else RemoveWorkingCopy(entry, allowedParent);
            }
            else File.Delete(entry);
        }
        Directory.Delete(full, recursive: false);
    }
}
