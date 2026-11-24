using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Tern.App;

namespace Tern.Desktop.Services;

internal sealed record CopilotProcessResult(int ExitCode, string Output, string Error = "", bool OutputIsReply = false, bool EventError = false);

/// <summary>Opt-in native Copilot CLI text mode, using an isolated configuration and no workspace context.</summary>
public sealed class CopilotTextClient
{
    // The prompt (which may contain note text) is written to the process's standard input, never to its command line,
    // because command lines are visible to other processes and are often recorded by security logging (R-07).
    private readonly Func<ProcessStartInfo, string, CancellationToken, Task<CopilotProcessResult>> _run;
    public Func<bool>? NetworkAllowed { get; set; }
    private void EnsureNetworkAllowed()
    { if (NetworkAllowed?.Invoke() == false) throw new InvalidOperationException("Offline mode blocks Copilot. Turn Offline off to connect."); }
    /// <summary>How long one request may run before the person is told it took too long.</summary>
    internal TimeSpan ReplyLimit { get; set; } = TimeSpan.FromMinutes(5);
    public CopilotTextClient() : this((start, prompt, token) => RunProcessAsync(start, prompt, token)) { }
    internal CopilotTextClient(Func<ProcessStartInfo, CancellationToken, Task<CopilotProcessResult>> run) => _run = (start, _, token) => run(start, token);
    internal CopilotTextClient(Func<ProcessStartInfo, string, CancellationToken, Task<CopilotProcessResult>> run) => _run = run;

    public static string DefaultExecutable => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "npm", "node_modules", "@github", "copilot", "node_modules", "@github", "copilot-win32-x64", "copilot.exe");

    public Task<string> SendAsync(string executable, string model, string token, string reviewedPrompt, CancellationToken cancellationToken) =>
        SendAsync(executable, model, token, AIRequest.ConnectionTest(reviewedPrompt), cancellationToken);

    public async Task<string> SendAsync(string executable, string model, string token, AIRequest request, CancellationToken cancellationToken)
    {
        EnsureNetworkAllowed();
        if (!request.HasUserContent) throw new ArgumentException("Enter a message to send.", nameof(request));
        var reviewedPrompt = request.Text;
        ValidateInputs(executable, model, token, request);
        var baseDirectory = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Tern-Copilot-Text"));
        Directory.CreateDirectory(baseDirectory);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(ReplyLimit);
        async Task<CopilotProcessResult> RunFreshAttempt()
        {
            var root = Path.Combine(baseDirectory, Guid.NewGuid().ToString("N"));
            var working = Path.Combine(root, "work"); var configuration = Path.Combine(root, "configuration");
            try
            {
                Directory.CreateDirectory(working); Directory.CreateDirectory(configuration);
                // No CLI session files, hooks or workspace context can carry into this attempt.
                await File.WriteAllTextAsync(Path.Combine(configuration, "settings.json"),
                    "{\"disableAllHooks\":true,\"ide\":{\"autoConnect\":false}}", deadline.Token).ConfigureAwait(false);
                var start = CreateStartInfo(executable, model, token, working, configuration);
                EnsureNetworkAllowed();
                return await _run(start, reviewedPrompt, deadline.Token).ConfigureAwait(false);
            }
            finally { RemoveWorkingCopy(root, baseDirectory); }
        }
        try
        {
            var result = await RunFreshAttempt().ConfigureAwait(false);
            // Retry only a recognized transport failure, once, with another fresh scope.
            if (result.ExitCode != 0 && IsTransient(Diagnostics(result)))
            {
                await Task.Delay(300, deadline.Token).ConfigureAwait(false);
                EnsureNetworkAllowed();
                result = await RunFreshAttempt().ConfigureAwait(false);
            }
            var reply = result.OutputIsReply ? result.Output : ExtractReply(result.Output);
            // A run can end normally yet report its failure only as an event in the output.
            if (result.ExitCode != 0 || result.EventError || string.IsNullOrWhiteSpace(reply) && ErrorEvents(result.Output).Length > 0)
            {
                if (IsTooMuchText(Diagnostics(result)))
                    throw new AIServiceException(AIServiceException.TooMuchTextMessage, "Copilot CLI reported that the request was too large.", 413, sizeRejected: true);
                throw new InvalidOperationException(FailureMessage(result.ExitCode, Diagnostics(result)));
            }
            return reply.Replace(token, "[redacted]", StringComparison.Ordinal).Trim();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new InvalidOperationException("Copilot took too long to reply. Check your internet connection, then try again. Your note has not changed."); }
        catch (Win32Exception) { throw new InvalidOperationException("The native Copilot CLI could not start. Check its full executable path and Windows access permissions."); }
    }

    /// <summary>
    /// Reads the final assistant message from the CLI's JSON event stream. Its text output is hard-wrapped to a terminal
    /// width, which corrupts generated Markdown and structured answers. Plain output from an older CLI is returned as is.
    /// </summary>
    internal static string ExtractReply(string output)
    {
        string? reply = null;
        var isEventStream = false;
        foreach (var line in output.Split((char)10, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!line.StartsWith('{')) continue;
            try
            {
                using var document = System.Text.Json.JsonDocument.Parse(line);
                var root = document.RootElement;
                if (EventType(root) is { } name && (name == "result" || name.Contains('.', StringComparison.Ordinal))) isEventStream = true;
                if (root.ValueKind == System.Text.Json.JsonValueKind.Object
                    && root.TryGetProperty("type", out var type) && type.ValueKind == System.Text.Json.JsonValueKind.String && type.GetString() == "assistant.message"
                    && root.TryGetProperty("data", out var data) && data.ValueKind == System.Text.Json.JsonValueKind.Object
                    && data.TryGetProperty("content", out var content) && content.ValueKind == System.Text.Json.JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(content.GetString()))
                    reply = content.GetString();
            }
            catch (System.Text.Json.JsonException) { }
        }
        // Internal events are never an answer. Without a message the caller reports an empty reply.
        return reply ?? (isEventStream ? "" : output);
    }

    private static string? EventType(System.Text.Json.JsonElement root) =>
        root.ValueKind == System.Text.Json.JsonValueKind.Object && root.TryGetProperty("type", out var type)
        && type.ValueKind == System.Text.Json.JsonValueKind.String ? type.GetString() : null;

    /// <summary>Error events only, so words in the person's note can never be mistaken for a failure reason.</summary>
    private static string ErrorEvents(string output)
    {
        var errors = new StringBuilder();
        foreach (var line in output.Split((char)10, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!line.StartsWith('{')) continue;
            try
            {
                using var document = System.Text.Json.JsonDocument.Parse(line);
                if (EventType(document.RootElement)?.Contains("error", StringComparison.OrdinalIgnoreCase) == true) errors.AppendLine(line);
            }
            catch (System.Text.Json.JsonException) { }
        }
        return errors.ToString();
    }

    private static string Diagnostics(CopilotProcessResult result) => result.Error + "\n" + (result.OutputIsReply ? "" : ErrorEvents(result.Output));

    /// <summary>Plain reason and next step. Raw CLI diagnostics are classified here and never displayed.</summary>
    internal static string FailureMessage(int exitCode, string diagnostics)
    {
        const System.Text.RegularExpressions.RegexOptions Options = System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant;
        bool Has(string pattern) => System.Text.RegularExpressions.Regex.IsMatch(diagnostics, pattern, Options, TimeSpan.FromMilliseconds(200));
        if (IsAccountLimit(diagnostics))
            return "Copilot reported that this account has no remaining credits or requests. Check its plan and billing on GitHub. Retrying will not restore access.";
        if (Has(@"\b401\b|unauthori[sz]ed|bad credentials|authentication (?:failed|required|error)|not (?:logged|signed) in|(?:token|credentials?|sign-?in|session) (?:has |is |was )?(?:expired|invalid|revoked)|invalid (?:token|credentials?)"))
            return "Your Copilot sign-in has expired or is no longer valid. Update it in Settings, under AI connections, then try again. Your note has not changed.";
        if (Has(@"\b429\b|rate[ -]?limit|too many requests"))
            return "Copilot has had too many requests for now. Wait a minute, then try again. Your note has not changed.";
        if (Has(@"ENOTFOUND|ECONNREFUSED|ECONNRESET|ETIMEDOUT|EAI_AGAIN|ENETUNREACH|socket hang up|getaddrinfo|fetch failed|network (?:error|is unreachable)"))
            return "Tern could not reach Copilot. Check your internet connection, then try again. Your note has not changed.";
        if (Has(@"HTTP 50[234]"))
            return "Copilot is having a problem right now. Try again in a few minutes. Your note has not changed.";
        return $"Copilot CLI exited with code {exitCode}. Check the installed CLI version, Copilot entitlement, model and token's Copilot Requests permission. Raw CLI diagnostics are not displayed.";
    }

    /// <summary>The CLI said the request holds too much text. Raw diagnostics are never displayed.</summary>
    internal static bool IsTooMuchText(string diagnostics) => System.Text.RegularExpressions.Regex.IsMatch(diagnostics,
        @"\b(?:HTTP|status)(?:\s*code)?[ :=/]*413\b|context[_ ]length|maximum context|(?:prompt|input|request|message) (?:is )?too (?:long|large)|too many tokens",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));

    /// <summary>Classifies CLI diagnostics without ever displaying their raw text.</summary>
    internal static bool IsAccountLimit(string diagnostics) => System.Text.RegularExpressions.Regex.IsMatch(diagnostics,
        @"\b402\b|payment required|quota|premium request|out of credits?|no credits?|insufficient (?:balance|credits?)|billing|usage limit",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    internal static void ValidateInputs(string executable, string model, string token, string prompt)
    {
        if (!Path.IsPathFullyQualified(executable) || executable.StartsWith("\\\\", StringComparison.Ordinal)
            || !string.Equals(Path.GetFileName(executable), "copilot.exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(executable))
            throw new ArgumentException("Choose a local installed native copilot.exe. Shell scripts, command files and network paths are not supported.", nameof(executable));
        if (string.IsNullOrWhiteSpace(model) || model.Length > 100 || model.Any(c => c < 33 || c > 126))
            throw new ArgumentException("Enter a model name, or auto, using up to 100 printable characters without spaces.", nameof(model));
        if (string.IsNullOrWhiteSpace(token) || token.Length > 4096 || token.Any(c => c < 33 || c > 126))
            throw new ArgumentException("Enter a GitHub token with Copilot Requests permission. Choose Save connection to store it securely in Windows Credential Manager.", nameof(token));
        if (string.IsNullOrWhiteSpace(prompt) || prompt.Length > AIConnectionRules.MaximumSendCharacters || prompt.Contains('\0'))
            throw new ArgumentException("Enter text of up to 1,000,000 characters.", nameof(prompt));
        ValidateUserText(prompt);
    }

    internal static void ValidateInputs(string executable, string model, string token, AIRequest request)
    {
        ValidateInputs(executable, model, token, request.Text);
        ValidateUserText(request.CurrentInput);
    }

    private static void ValidateUserText(string prompt)
    {
        if (prompt.Contains('@') || prompt.TrimStart().StartsWith('/') || prompt.TrimStart().StartsWith('!') || prompt.Trim() == "$")
            throw new ArgumentException("This text-only connection does not accept @mentions, attachments or leading CLI commands. Paste the text itself and describe the task in ordinary words.", nameof(prompt));
    }

    internal static ProcessStartInfo CreateStartInfo(string executable, string model, string token, string working, string configuration)
    {
        var start = new ProcessStartInfo(executable)
        { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = working, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, StandardInputEncoding = new UTF8Encoding(false) };
        string[] flags = ["--available-tools=ask_user", "--no-ask-user", "--disable-builtin-mcps", "--no-custom-instructions", "--no-auto-update",
            "--disable-client-telemetry", "--no-experimental", "--no-bash-env", "--no-eager-powershell-resolution", "--disallow-temp-dir",
            "--deny-tool=read,write,shell,url,memory", "--silent", "--no-color", "--stream=off", "--output-format=json", "--log-level=none"];
        foreach (var flag in flags) start.ArgumentList.Add(flag);
        start.ArgumentList.Add("--model=" + model);
        // No --prompt argument: with piped standard input the CLI runs the piped text as one non-interactive prompt
        // (verified with Copilot CLI 1.0.90: stdin alone starts prompt mode). Prompt text can never become a CLI switch.
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

    internal static async Task<CopilotProcessResult> RunProcessAsync(ProcessStartInfo start, string prompt, CancellationToken cancellationToken, Action<Process>? started = null)
    {
        using var process = new Process { StartInfo = start };
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        start.Environment.TryGetValue("COPILOT_GITHUB_TOKEN", out var credential);
        await using var capture = new CopilotOutputCapture(credential);
        if (!process.Start()) throw new InvalidOperationException("The Copilot CLI process did not start.");
        started?.Invoke(process);
        var answered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<string> ReadAsync(StreamReader reader, bool captureOutput)
        {
            var result = new StringBuilder(); var buffer = new char[4096];
            try
            {
                while (true)
                {
                    var count = await reader.ReadAsync(buffer, stop.Token).ConfigureAwait(false); if (count == 0) return result.ToString();
                    if (captureOutput)
                    {
                        await capture.AppendAsync(buffer.AsMemory(0, count), stop.Token).ConfigureAwait(false);
                        if (capture.CompletionObserved) answered.TrySetResult();
                    }
                    else
                    {
                        result.Append(buffer, 0, count);
                        if (result.Length > 65_536) result.Remove(0, result.Length - 65_536);
                    }
                }
            }
            catch { stop.Cancel(); throw; }
        }
        // Read before writing. A long prompt could otherwise deadlock if the CLI fills an output pipe first.
        var output = ReadAsync(process.StandardOutput, captureOutput: true);
        var errors = ReadAsync(process.StandardError, captureOutput: false);
        try
        {
            // Inside the cleanup block: cancelling while the prompt is written must also stop the process.
            try
            {
                await process.StandardInput.WriteAsync(prompt.AsMemory(), stop.Token).ConfigureAwait(false);
                await process.StandardInput.FlushAsync(stop.Token).ConfigureAwait(false);
            }
            catch (IOException) { /* The CLI exited before reading its input; its exit code and diagnostics are reported below. */ }
            finally { try { process.StandardInput.Close(); } catch (IOException) { } }
            var running = Task.WhenAll(process.WaitForExitAsync(stop.Token), output, errors);
            await Task.WhenAny(running, answered.Task).ConfigureAwait(false);
            // The answer is complete. Do not make the user wait for the CLI to shut down.
            var complete = answered.Task.IsCompleted;
            if (complete && !process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { }
            }
            await running.ConfigureAwait(false);
            var collected = await capture.CompleteAsync(stop.Token).ConfigureAwait(false);
            return new CopilotProcessResult(complete ? 0 : process.ExitCode, collected.Output,
                await errors.ConfigureAwait(false) + "\n" + collected.Error, collected.OutputIsReply, collected.EventError);
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { }
            }
            // The readers use this process's pipes, so they must finish before it is disposed.
            try { await Task.WhenAll(output, errors).ConfigureAwait(false); }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidOperationException or ObjectDisposedException) { }
        }
    }

    private static bool IsTransient(string error) => new[] { "ECONNRESET", "ETIMEDOUT", "EAI_AGAIN", "socket hang up", "HTTP 502", "HTTP 503", "HTTP 504" }
        .Any(value => error.Contains(value, StringComparison.OrdinalIgnoreCase));

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
