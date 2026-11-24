[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Executable,
    [Parameter(Mandatory)][string]$EvidenceDirectory,
    [ValidateSet(1,1000)][int]$DocumentCount = 1
)
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'Native editing verification requires Windows.' }
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$application = [IO.Path]::GetFullPath($Executable)
$evidence = [IO.Path]::GetFullPath($EvidenceDirectory)
if (Test-Path -LiteralPath $evidence) { throw 'Preserve existing evidence; choose a fresh directory.' }
[void][IO.Directory]::CreateDirectory($evidence)
$notes = [Collections.Generic.List[string]]::new()
$originalTexts = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::OrdinalIgnoreCase)
for ($index = 0; $index -lt $DocumentCount; $index++) {
    $name = if ($DocumentCount -eq 1) { 'native editing with spaces.md' } else { 'native-{0:D4}.md' -f $index }
    $path = Join-Path $evidence $name
    $text = "# Native editing verification $index`n`nSynthetic local document.`n"
    [IO.File]::WriteAllText($path, $text, [Text.UTF8Encoding]::new($false))
    $notes.Add($path)
    $originalTexts.Add($path, $text)
}
$note = $notes[$notes.Count - 1]
$original = $originalTexts[$note]
$expected = $original + "`nEdited through the packaged editor's Windows UI Automation Value pattern.`n"
$runs = [Collections.Generic.List[object]]::new()
$transientSaveReads = [Collections.Generic.List[string]]::new()

function Start-OwnedProcess([int]$run, [string[]]$documents) {
    $start = [Diagnostics.ProcessStartInfo]::new($application)
    $start.UseShellExecute = $false
    $start.WorkingDirectory = $evidence
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($document in $documents) { $start.ArgumentList.Add($document) }
    $start.Environment['TERN_DATA_ROOT'] = Join-Path $evidence 'user-data'
    $start.Environment['TERN_STARTUP_TRACE'] = Join-Path $evidence "startup-$run.json"
    $start.Environment['PATH'] = "$env:SystemRoot\System32;$env:SystemRoot"
    foreach ($name in @('DOTNET_ROOT','DOTNET_ROOT_X64','DOTNET_ROOT_X86','DOTNET_ADDITIONAL_DEPS','DOTNET_SHARED_STORE','DOTNET_STARTUP_HOOKS')) {
        [void]$start.Environment.Remove($name)
    }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    $clock = [Diagnostics.Stopwatch]::StartNew()
    if (-not $process.Start()) { $process.Dispose(); throw 'Could not start the packaged application.' }
    $entry = [pscustomobject]@{
        process=$process; run=$run
        stdout=$process.StandardOutput.ReadToEndAsync()
        stderr=$process.StandardError.ReadToEndAsync()
        clock=$clock; startupMilliseconds=$null
        samples=[Collections.Generic.List[object]]::new()
        automationReadMilliseconds=[Collections.Generic.List[double]]::new()
    }
    $runs.Add($entry)
    return $entry
}

function Start-OwnedApplication([int]$run) {
    $documents = if ($DocumentCount -eq 1000) {
        if ($run -eq 1) { @($notes | ForEach-Object { [IO.Path]::GetFileName($_) }) } else { @() }
    } else { @($note) }
    $entry = Start-OwnedProcess $run $documents
    $process = $entry.process
    $limit = if ($DocumentCount -eq 1000) { 120 } else { 90 }
    $deadline = [DateTime]::UtcNow.AddSeconds($limit)
    $root = $null
    while ([DateTime]::UtcNow -lt $deadline) {
        if ($process.HasExited) { throw "Packaged application exited early: $($process.ExitCode)" }
        $process.Refresh()
        $working = $process.WorkingSet64 / 1MB
        $private = $process.PrivateMemorySize64 / 1MB
        $entry.samples.Add([pscustomobject]@{
            elapsedMilliseconds=$entry.clock.Elapsed.TotalMilliseconds
            workingMiB=$working; privateMiB=$private; cpuSeconds=$process.TotalProcessorTime.TotalSeconds
        })
        if ($working -gt 2048 -or $private -gt 3072) { throw 'Task-owned native process exceeded the predeclared resource cap.' }
        $trace = Join-Path $evidence "startup-$run.json"
        if ($DocumentCount -eq 1000 -and $process.MainWindowHandle -ne [IntPtr]::Zero) {
            if ($null -eq $root) {
                $root = [System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
                if ($root.Current.ProcessId -ne $process.Id) { throw 'Automation window does not belong to the task-owned process.' }
            }
            $probe = [Diagnostics.Stopwatch]::StartNew()
            [void]$root.Current.BoundingRectangle
            $entry.automationReadMilliseconds.Add($probe.Elapsed.TotalMilliseconds)
        }
        if ($process.MainWindowHandle -ne [IntPtr]::Zero -and (Test-Path -LiteralPath $trace)) {
            $startup = [IO.File]::ReadAllText($trace) | ConvertFrom-Json
            if ('startup-complete-dispatch' -in @($startup.phases.phase)) {
                $root = [System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
                if ($root.Current.ProcessId -ne $process.Id) { throw 'Automation window does not belong to the task-owned process.' }
                $entry.startupMilliseconds = $entry.clock.Elapsed.TotalMilliseconds
                if ($entry.startupMilliseconds -gt $limit * 1000) { throw 'Native startup exceeded the predeclared time limit.' }
                if ($DocumentCount -eq 1000 -and ($entry.automationReadMilliseconds.Count -eq 0 -or
                    ($entry.automationReadMilliseconds | Measure-Object -Maximum).Maximum -gt 1500)) {
                    throw 'Native startup lacked responsive UI Automation read probes within the predeclared limit.'
                }
                return $root
            }
        }
        Start-Sleep -Milliseconds 200
    }
    throw "Packaged application did not complete native startup within $limit seconds."
}

function Confirm-OpenDocumentCount {
    $sessionPath = Join-Path $evidence 'user-data\state\session.json'
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    while ([DateTime]::UtcNow -lt $deadline) {
        if (Test-Path -LiteralPath $sessionPath) {
            $session = [IO.File]::ReadAllText($sessionPath) | ConvertFrom-Json
            if (@($session.tabs).Count -eq $DocumentCount) {
                $paths = @($session.tabs | ForEach-Object path)
                if (@($paths | Sort-Object -Unique).Count -ne $DocumentCount) { throw 'Native live session contains duplicate tab paths.' }
                foreach ($path in $paths) {
                    if (-not $originalTexts.ContainsKey($path)) { throw 'Native live session contains an unexpected document.' }
                }
                return
            }
        }
        Start-Sleep -Milliseconds 100
    }
    throw 'Native live session did not persist the exact simultaneously open document count.'
}

function Get-EditorValue($root) {
    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ClassNameProperty, 'AccessibleMarkdownEditor')
    $editor = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $condition)
    if ($null -eq $editor) { $editor = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition) }
    if ($null -eq $editor) { throw 'The native automation tree does not expose the Markdown editor.' }
    $value = $editor.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    if ($value.Current.IsReadOnly) { throw 'The synthetic Markdown editor is unexpectedly read-only.' }
    return $value
}

function Close-OwnedApplication($entry) {
    if (-not $entry.process.CloseMainWindow() -or -not $entry.process.WaitForExit(15000)) {
        throw 'The packaged application did not close normally.'
    }
    if ($entry.process.ExitCode -ne 0) { throw "Packaged application exit code: $($entry.process.ExitCode)" }
}

function Confirm-SavedText($value) {
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    while ([DateTime]::UtcNow -lt $deadline) {
        if ($value.Current.Value -cne $expected) {
            throw 'The owned native editor buffer changed while verifying Save; exact expected text was not preserved.'
        }
        try {
            if ([IO.File]::ReadAllText($note) -ceq $expected) { return }
        } catch [IO.FileNotFoundException] {
            $transientSaveReads.Add($_.Exception.Message)
        } catch [IO.IOException] {
            if (($_.Exception.HResult -band 0xffff) -notin 32,33) { throw }
            $transientSaveReads.Add($_.Exception.Message)
        }
        Start-Sleep -Milliseconds 100
    }
    throw "Native Save did not persist the exact edited text within 30 seconds; transient missing-path reads: $($transientSaveReads.Count)."
}

try {
    $root = Start-OwnedApplication 1
    $value = Get-EditorValue $root
    if ($value.Current.Value -cne $original) { throw 'Native editor did not load the exact fixture text.' }
    Confirm-OpenDocumentCount
    $value.SetValue($expected)
    if ($value.Current.Value -cne $expected) { throw 'Native editor did not retain the automation edit.' }
    $saveCondition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::NameProperty, 'Save document')
    $save = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $saveCondition)
    if ($null -eq $save) { throw 'The native automation tree does not expose Save document.' }
    $save.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Confirm-SavedText $value
    if ($DocumentCount -eq 1) {
        $forwardedNote = Join-Path $evidence 'forwarded file with spaces.md'
        $forwardedText = "# Native forwarding`n`nA second synthetic local document.`n"
        [IO.File]::WriteAllText($forwardedNote, $forwardedText, [Text.UTF8Encoding]::new($false))
        $forwarder = Start-OwnedProcess 3 $forwardedNote
        if (-not $forwarder.process.WaitForExit(15000) -or $forwarder.process.ExitCode -ne 0) {
            throw 'The second native process did not forward arguments and exit successfully.'
        }
        $deadline = [DateTime]::UtcNow.AddSeconds(30)
        while ($value.Current.Value -cne $forwardedText -and [DateTime]::UtcNow -lt $deadline) {
            Start-Sleep -Milliseconds 100
        }
        if ($value.Current.Value -cne $forwardedText) { throw 'The task-owned original window did not open the forwarded document.' }
        if ([IO.File]::ReadAllText($note) -cne $expected -or [IO.File]::ReadAllText($forwardedNote) -cne $forwardedText) {
            throw 'Opening forwarded arguments changed saved fixture text.'
        }
    }
    Close-OwnedApplication $runs[0]
    $root = Start-OwnedApplication 2
    if ((Get-EditorValue $root).Current.Value -cne $expected) { throw 'Native restart did not reopen the saved edit.' }
    if ($DocumentCount -eq 1000) { Confirm-OpenDocumentCount }
    Close-OwnedApplication $runs[$runs.Count - 1]
    foreach ($path in $notes) {
        $text = if ($path -eq $note) { $expected } else { $originalTexts[$path] }
        if ([IO.File]::ReadAllText($path) -cne $text) { throw 'Native verification changed an unexpected fixture.' }
    }
    $result = [ordered]@{
        schemaVersion=1; status='passed'; executable=$application
        executableSha256=(Get-FileHash -LiteralPath $application -Algorithm SHA256).Hash.ToLowerInvariant()
        fixture=$note; fixtureSha256=(Get-FileHash -LiteralPath $note -Algorithm SHA256).Hash.ToLowerInvariant()
        nativeValueEdit=$true; nativeSaveInvoke=$true; exactSavedText=$true
        nativeRestartReopened=$true; nativeSingleInstanceForwarding=($DocumentCount -eq 1); normalWindowExits=2
        simultaneousOpenDocuments=$DocumentCount; restoreWithoutArguments=($DocumentCount -eq 1000)
        transientSaveReads=$transientSaveReads
        logicalProcessors=[Environment]::ProcessorCount; operatingSystem=[Environment]::OSVersion.VersionString
        runs=@($runs | ForEach-Object { [pscustomobject]@{
            run=$_.run; pid=$_.process.Id; startupMilliseconds=$_.startupMilliseconds
            samples=$_.samples; automationReadMilliseconds=$_.automationReadMilliseconds
        } })
        scope='Task-owned packaged windows and exact live-session tab counts, synthetic local data, native Value/Invoke edit-save-restart and single-document forwarding. Startup probes measure UI Automation reads/dispatcher roundtrips, not physical input or FPS. No Narrator/TextPattern, MSI installation, or host-setting changes.'
    }
    [IO.File]::WriteAllText((Join-Path $evidence 'result.json'), ($result | ConvertTo-Json -Depth 4), [Text.UTF8Encoding]::new($false))
    if ($DocumentCount -eq 1000) {
        Write-Output 'PASS: packaged native 1,000-tab edit/save and no-argument session restoration.'
    } else {
        Write-Output 'PASS: packaged native edit, save, single-instance forwarding and restart/reopen.'
    }
} catch {
    [IO.File]::WriteAllText((Join-Path $evidence 'failure.json'), ([ordered]@{
        status='failed'; message=$_.Exception.Message; hresult=$_.Exception.HResult
        scriptStackTrace=$_.ScriptStackTrace; position=$_.InvocationInfo.PositionMessage
        transientSaveReads=$transientSaveReads
    } | ConvertTo-Json -Depth 4), [Text.UTF8Encoding]::new($false))
    throw
} finally {
    [IO.File]::WriteAllText((Join-Path $evidence 'run-observations.json'), (@($runs | ForEach-Object {
        [pscustomobject]@{
            run=$_.run; pid=$_.process.Id; startupMilliseconds=$_.startupMilliseconds
            samples=$_.samples; automationReadMilliseconds=$_.automationReadMilliseconds
            probe='UIA window bounding rectangle; no whole-tree traversal during mutable startup'
        }
    }) | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
    foreach ($entry in $runs) {
        if (-not $entry.process.HasExited) { $entry.process.Kill($true); $entry.process.WaitForExit() }
        [IO.File]::WriteAllText((Join-Path $evidence "stdout-$($entry.run).log"), $entry.stdout.GetAwaiter().GetResult())
        [IO.File]::WriteAllText((Join-Path $evidence "stderr-$($entry.run).log"), $entry.stderr.GetAwaiter().GetResult())
        $entry.process.Dispose()
    }
}
