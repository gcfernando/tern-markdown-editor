[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ResultsDirectory,
    [string]$TestFilter = 'FullyQualifiedName~DocumentScaleWorkflowTests',
    [switch]$CaptureDiagnostics
)
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$results = [IO.Path]::GetFullPath($ResultsDirectory)
if (Test-Path -LiteralPath $results) { throw 'Preserve prior benchmark evidence; choose a fresh results directory.' }
New-Item -ItemType Directory -Path $results | Out-Null
$temporary = Join-Path $results 'temp'
New-Item -ItemType Directory -Path $temporary | Out-Null
$previousTemp = $env:TEMP
$previousTmp = $env:TMP
$tracked = @{}
$observations = [Collections.Generic.List[object]]::new()
$clock = [Diagnostics.Stopwatch]::StartNew()
$failure = $null
$exit = $null
$captured = $false
try {
    $env:TEMP = $temporary
    $env:TMP = $temporary
    $arguments = @(
        'test', (Join-Path $repository 'tests\Tern.Desktop.Tests\Tern.Desktop.Tests.csproj'),
        '-c', 'Release', '--no-build', '--no-restore',
        '--filter', $TestFilter, '--logger', 'trx',
        '--results-directory', $results, '--blame-hang-timeout', '5m', '--verbosity', 'quiet'
    )
    $quoted = $arguments | ForEach-Object { '"' + $_ + '"' }
    $runner = Start-Process -FilePath (Get-Command dotnet).Source -ArgumentList $quoted -PassThru -NoNewWindow `
        -RedirectStandardOutput (Join-Path $results 'stdout.log') -RedirectStandardError (Join-Path $results 'stderr.log')
    $tracked[$runner.Id] = $runner
    while (-not $runner.HasExited) {
        foreach ($parent in @($tracked.Values)) {
            if ($parent.HasExited) { continue }
            foreach ($child in @(Get-CimInstance Win32_Process -Filter "ParentProcessId = $($parent.Id)" -Property ProcessId)) {
                if ($tracked.ContainsKey([int]$child.ProcessId)) { continue }
                $process = Get-Process -Id $child.ProcessId -ErrorAction SilentlyContinue
                if ($process) { $tracked[$process.Id] = $process }
            }
        }
        foreach ($process in @($tracked.Values)) {
            if ($process.HasExited) { continue }
            $process.Refresh()
            $working = $process.WorkingSet64 / 1MB
            $private = $process.PrivateMemorySize64 / 1MB
            $observations.Add([pscustomobject]@{
                pid = $process.Id; elapsedSeconds = $clock.Elapsed.TotalSeconds
                workingMiB = $working; privateMiB = $private; cpuSeconds = $process.TotalProcessorTime.TotalSeconds
            })
            if ($CaptureDiagnostics -and -not $captured -and $working -gt 700 -and $process.ProcessName -eq 'Tern.Desktop.Tests') {
                $captured = $true
                & (Join-Path $repository '.runtime\diagnostics\dotnet-dump.exe') collect -p $process.Id --type Mini `
                    -o (Join-Path $results 'owned-busy-mini.dmp') | Set-Content -LiteralPath (Join-Path $results 'dump-collection.log')
                if ($LASTEXITCODE -ne 0) { throw 'Owned-process diagnostic collection failed.' }
            }
            if ($working -gt 2048 -or $private -gt 3072) { throw "Owned process $($process.Id) exceeded the declared resource cap." }
        }
        if ($clock.Elapsed.TotalSeconds -gt 270) { throw 'Whole benchmark command exceeded its 270-second external guard.' }
        Start-Sleep -Milliseconds 500
    }
    $runner.WaitForExit()
    $exit = $runner.ExitCode
} catch {
    $failure = $_.Exception.Message
} finally {
    foreach ($process in @($tracked.Values)) {
        if (-not $process.HasExited) { Stop-Process -Id $process.Id -ErrorAction Continue }
    }
    $env:TEMP = $previousTemp
    $env:TMP = $previousTmp
    [ordered]@{
        exitCode = $exit; guardFailure = $failure; elapsedSeconds = $clock.Elapsed.TotalSeconds
        ownedProcessIds = @($tracked.Keys); observations = $observations
        scope = 'Per-owned-process sampled caps; no unrelated process is terminated. Raw test results and synthetic temporary inputs are retained.'
    } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $results 'resource-guard.json') -Encoding utf8
}
Get-Content -LiteralPath (Join-Path $results 'stdout.log')
Get-Content -LiteralPath (Join-Path $results 'stderr.log')
if ($failure) { throw $failure }
if ($exit -ne 0) { throw "Document-scale verification failed with exit code $exit." }
