[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Archive,
    [Parameter(Mandatory)][string]$Checksum,
    [Parameter(Mandatory)][string]$EvidenceDirectory
)
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'The extracted application smoke check requires Windows.' }
$archivePath = [IO.Path]::GetFullPath($Archive)
$evidence = [IO.Path]::GetFullPath($EvidenceDirectory)
if (Test-Path -LiteralPath $evidence) { throw 'Preserve existing smoke evidence; choose a fresh directory.' }
$hash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
if ([IO.File]::ReadAllText([IO.Path]::GetFullPath($Checksum)).Trim() -cne "$hash  $([IO.Path]::GetFileName($archivePath))") {
    throw 'Release archive checksum mismatch.'
}
$manifestText = python (Join-Path $PSScriptRoot 'package_release.py') --verify $archivePath
if ($LASTEXITCODE -ne 0) { throw 'Release file coverage, CRC or SHA-256 verification failed.' }
$manifest = $manifestText | ConvertFrom-Json
[void][IO.Directory]::CreateDirectory($evidence)
$extract = Join-Path $evidence 'extracted'
[IO.Compression.ZipFile]::ExtractToDirectory($archivePath, $extract)
$trace = Join-Path $evidence 'startup.json'
$note = Join-Path $evidence 'release-smoke.md'
[IO.File]::WriteAllText($note, "# Release smoke`n`nLocal **Markdown** startup fixture.`n", [Text.UTF8Encoding]::new($false))
$before = (Get-FileHash -LiteralPath $note -Algorithm SHA256).Hash
$start = [Diagnostics.ProcessStartInfo]::new((Join-Path $extract 'Tern.exe'))
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
$start.WorkingDirectory = $extract
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
$start.ArgumentList.Add($note)
$start.Environment['TERN_DATA_ROOT'] = Join-Path $evidence 'user-data'
$start.Environment['TERN_STARTUP_TRACE'] = $trace
$start.Environment['PATH'] = "$env:SystemRoot\System32;$env:SystemRoot"
foreach ($name in @('DOTNET_ROOT','DOTNET_ROOT_X64','DOTNET_ROOT_X86','DOTNET_ADDITIONAL_DEPS','DOTNET_SHARED_STORE','DOTNET_STARTUP_HOOKS')) {
    [void]$start.Environment.Remove($name)
}
$process = [Diagnostics.Process]::new()
$process.StartInfo = $start
$finished = $false
$started = $false
try {
    if (-not $process.Start()) { throw 'Could not start the extracted Tern.exe.' }
    $started = $true
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    $deadline = [DateTime]::UtcNow.AddSeconds(90)
    $startup = $null
    while ([DateTime]::UtcNow -lt $deadline) {
        if ($process.HasExited) { throw "Extracted application exited early: $($process.ExitCode)" }
        if (Test-Path -LiteralPath $trace) {
            $startup = [IO.File]::ReadAllText($trace) | ConvertFrom-Json
            if ('startup-complete-dispatch' -in @($startup.phases.phase)) { $finished = $true; break }
        }
        Start-Sleep -Milliseconds 200
    }
    if (-not $finished) { throw 'Extracted application did not complete startup in 90 seconds.' }
    $process.Refresh()
    $caption = $process.MainWindowTitle
    if ($caption -notmatch 'Tern' -or $caption -notmatch 'release-smoke') { throw 'The extracted application did not expose the opened fixture in its native window.' }
    if ((Get-FileHash -LiteralPath $note -Algorithm SHA256).Hash -cne $before) { throw 'Opening the fixture changed its bytes.' }
    $closed = $process.CloseMainWindow()
    if (-not $closed -or -not $process.WaitForExit(15000)) { throw 'The extracted application did not close normally.' }
    if ($process.ExitCode -ne 0) { throw "Extracted application exit code: $($process.ExitCode)" }
    [IO.File]::WriteAllText((Join-Path $evidence 'stdout.log'), $stdout.GetAwaiter().GetResult())
    [IO.File]::WriteAllText((Join-Path $evidence 'stderr.log'), $stderr.GetAwaiter().GetResult())
    $result = [ordered]@{ schemaVersion=1; archiveSha256=$hash; version=$manifest.version; selfContained=$manifest.selfContained;
        sourceCommit=$manifest.sourceCommit; status='passed'; startupCompleted=$true; nativeWindowTitle=$caption;
        fixtureUnchanged=$true; normalExit=$true; scope='Extracted native startup, isolated state, unchanged local Markdown fixture and normal close. No manual printing, accessibility, or network-isolation acceptance claim.' }
    [IO.File]::WriteAllText((Join-Path $evidence 'result.json'), ($result | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
    Write-Output 'PASS: extracted self-contained Tern startup and normal close.'
} finally {
    if ($started -and -not $process.HasExited) { $process.Kill($true); $process.WaitForExit() }
    if ($started) {
        [IO.File]::WriteAllText((Join-Path $evidence 'stdout.log'), $stdout.GetAwaiter().GetResult())
        [IO.File]::WriteAllText((Join-Path $evidence 'stderr.log'), $stderr.GetAwaiter().GetResult())
    }
    $process.Dispose()
}
