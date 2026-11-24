[CmdletBinding()]
param(
    [string]$BuildRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\tern-build-output\release-build')),
    [string]$OutputDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\tern-build-output\release')),
    [string]$SigningCertificateThumbprint,
    [uri]$TimestampServer,
    [string]$SignToolPath = 'signtool.exe',
    [switch]$RequireSigned,
    [string]$WixDirectory
)
$ErrorActionPreference = 'Stop'
# The optional build collector can fail in restricted build accounts; it is not part of Tern.
$env:AVALONIA_TELEMETRY_OPTOUT = '1'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Push-Location $repository
try {
    $build = [IO.Path]::GetFullPath($BuildRoot)
    $output = [IO.Path]::GetFullPath($OutputDirectory)
    if ((Test-Path -LiteralPath $build) -or (Test-Path -LiteralPath $output)) {
        throw 'Preserve existing release outputs. Choose fresh build and output directories.'
    }
    if ($RequireSigned -and -not $SigningCertificateThumbprint) { throw 'Signing credentials are required to sign the freshly built MSI and standalone EXE.' }
    & (Join-Path $PSScriptRoot 'UpdateDocumentationScreenshots.ps1')
    $publish = Join-Path $build 'publish'
    # Separate from scripts/publish.ps1: a fresh release directory cannot contain stale files.
    dotnet publish src/Tern.Desktop/Tern.Desktop.csproj -c Release -r win-x64 --self-contained true --no-restore -p:RestoreLockedMode=true -p:UseSharedCompilation=false --output $publish -m:1
    if ($LASTEXITCODE -ne 0) { throw 'Self-contained Windows publish failed.' }
    $packageArguments = @('tools/package_release.py', '--publish', $publish, '--output', $output)
    if ($SigningCertificateThumbprint) {
        if (-not $TimestampServer) { throw 'A timestamp server is required for signed releases.' }
        & (Join-Path $PSScriptRoot 'SignRelease.ps1') -PublishDirectory $publish -CertificateThumbprint $SigningCertificateThumbprint -TimestampServer $TimestampServer -SignToolPath $SignToolPath
        $RequireSigned = $true
    }
    if ($RequireSigned) { $packageArguments += '--require-signed' }
    python @packageArguments
    if ($LASTEXITCODE -ne 0) { throw 'Release ZIP integrity verification failed.' }
    & (Join-Path $PSScriptRoot 'TestRelease.ps1') -Archive (Join-Path $output 'Tern-win-x64.zip') -Checksum (Join-Path $output 'SHA256SUMS.txt') -EvidenceDirectory (Join-Path $build 'smoke')
    & (Join-Path $PSScriptRoot 'TestNativeEditing.ps1') -Executable (Join-Path $publish 'Tern.exe') -EvidenceDirectory (Join-Path $build 'native-editing')
    if (-not $WixDirectory) { $WixDirectory = & (Join-Path $PSScriptRoot 'GetWix.ps1') }
    $payload = Join-Path (Split-Path $output -Parent) ((Split-Path $output -Leaf) + '-payload')
    $content = Join-Path $build 'bundled-content.zip'
    python tools/prepare_bundled_content.py $payload $content
    if ($LASTEXITCODE -ne 0) { throw 'Bundled documentation preparation failed.' }
    $single = Join-Path $build 'standalone'
    dotnet publish src/Tern.Desktop/Tern.Desktop.csproj -c Release -r win-x64 --self-contained true --no-restore -p:RestoreLockedMode=true -p:UseSharedCompilation=false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishTrimmed=false "-p:TernBundledContent=$([IO.Path]::GetFullPath($content))" --output $single -m:1
    if ($LASTEXITCODE -ne 0) { throw 'Standalone publish failed.' }
    Copy-Item -LiteralPath (Join-Path $single 'Tern.exe') -Destination (Join-Path $output 'Tern.exe')
    $version = ([xml](Get-Content -LiteralPath 'Directory.Build.props' -Raw)).Project.PropertyGroup.Version
    $msi = Join-Path $build "Tern-$version.msi"
    python tools/BuildMsi.py --payload $payload --output $msi --wix $WixDirectory
    if ($LASTEXITCODE -ne 0) { throw 'MSI build or validation failed.' }
    Copy-Item -LiteralPath $msi -Destination $output
    if ($SigningCertificateThumbprint) {
        foreach ($file in @((Join-Path $output 'Tern.exe'), (Join-Path $output "Tern-$version.msi"))) {
            & $SignToolPath sign /sha1 $SigningCertificateThumbprint /fd SHA256 /tr $TimestampServer.AbsoluteUri /td SHA256 $file
            if ($LASTEXITCODE -ne 0) { throw 'Distribution signature failed.' }
            & $SignToolPath verify /pa /all $file
            if ($LASTEXITCODE -ne 0) { throw 'Distribution signature verification failed.' }
        }
    }
    $files = @('Tern-win-x64.zip', 'Tern.exe', "Tern-$version.msi")
    $files | ForEach-Object { $file = Join-Path $output $_; "$( (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant() )  $_" } | Set-Content -LiteralPath (Join-Path $output 'SHA256SUMS.txt') -Encoding ascii
    python tools/verify_msi.py (Join-Path $output "Tern-$version.msi") $payload | Set-Content -LiteralPath (Join-Path $build 'msi-verification.json') -Encoding utf8
    if ($LASTEXITCODE -ne 0) { throw 'MSI registry or payload validation failed.' }
    python tools/TestMsiWizard.py (Join-Path $output "Tern-$version.msi") | Set-Content -LiteralPath (Join-Path $build 'msi-wizard-checks.json') -Encoding utf8
    if ($LASTEXITCODE -ne 0) { throw 'Restricted MSI wizard selection checks failed.' }
    & (Join-Path $PSScriptRoot 'TestMsiPayload.ps1') -Msi (Join-Path $output "Tern-$version.msi") -Payload $payload -EvidenceDirectory (Join-Path $build 'msi-extraction') -WixDirectory $WixDirectory -BrandingDirectory (Join-Path $build 'installer-branding')
    & (Join-Path $PSScriptRoot 'TestRelease.ps1') -Archive (Join-Path $output 'Tern-win-x64.zip') -Checksum (Join-Path $output 'SHA256SUMS.txt') -EvidenceDirectory (Join-Path $build 'standalone-smoke') -Executable (Join-Path $output 'Tern.exe')
    & (Join-Path $PSScriptRoot 'TestNativeEditing.ps1') -Executable (Join-Path $output 'Tern.exe') -EvidenceDirectory (Join-Path $build 'standalone-native-editing')
    Write-Output "Verified release candidate: $(Join-Path $output 'Tern-win-x64.zip')"
} finally { Pop-Location }
