[CmdletBinding()]
param(
    [string]$BuildRoot = 'artifacts/release-build',
    [string]$OutputDirectory = 'artifacts/release'
)
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Push-Location $repository
try {
    $build = [IO.Path]::GetFullPath($BuildRoot)
    $output = [IO.Path]::GetFullPath($OutputDirectory)
    if ((Test-Path -LiteralPath $build) -or (Test-Path -LiteralPath $output)) {
        throw 'Preserve existing release outputs. Choose fresh build and output directories.'
    }
    $publish = Join-Path $build 'publish'
    # Separate from scripts/publish.ps1: a fresh release directory cannot contain stale files.
    dotnet publish src/Tern.Desktop/Tern.Desktop.csproj -c Release -r win-x64 --self-contained true --no-restore -p:RestoreLockedMode=true -p:UseSharedCompilation=false --output $publish -m:1
    if ($LASTEXITCODE -ne 0) { throw 'Self-contained Windows publish failed.' }
    python tools/package_release.py --publish $publish --output $output
    if ($LASTEXITCODE -ne 0) { throw 'Release ZIP integrity verification failed.' }
    & (Join-Path $PSScriptRoot 'TestRelease.ps1') -Archive (Join-Path $output 'Tern-win-x64.zip') -Checksum (Join-Path $output 'SHA256SUMS.txt') -EvidenceDirectory (Join-Path $build 'smoke')
    Write-Output "Verified release candidate: $(Join-Path $output 'Tern-win-x64.zip')"
} finally { Pop-Location }
