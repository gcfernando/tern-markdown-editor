[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PublishDirectory,
    [Parameter(Mandatory)][ValidatePattern('^[0-9A-Fa-f]{40}$')][string]$CertificateThumbprint,
    [Parameter(Mandatory)][uri]$TimestampServer,
    [string]$SignToolPath = 'signtool.exe'
)
$ErrorActionPreference = 'Stop'
if ($TimestampServer.Scheme -notin @('http', 'https') -or $TimestampServer.UserInfo) { throw 'Use the signing provider timestamp URL without credentials.' }
$certificate = Get-Item -LiteralPath "Cert:\CurrentUser\My\$CertificateThumbprint"
if (-not $certificate.HasPrivateKey -or $certificate.NotAfter -le (Get-Date) -or $certificate.NotBefore -gt (Get-Date)) { throw 'An available, current signing certificate with its private key is required.' }
if (-not ($certificate.EnhancedKeyUsageList.ObjectId.Value -contains '1.3.6.1.5.5.7.3.3')) { throw 'The certificate must permit code signing.' }
$root = (Resolve-Path -LiteralPath $PublishDirectory).Path
$files = @(Get-ChildItem -LiteralPath $root -File | Where-Object { $_.Name -eq 'Tern.exe' -or $_.Name -eq 'Tern.dll' -or $_.Name -like 'Tern.*.dll' })
if (-not ($files.Name -contains 'Tern.exe') -or -not ($files.Name -contains 'Tern.dll')) { throw 'Missing Tern release binaries.' }
foreach ($file in $files) {
    & $SignToolPath sign /sha1 $CertificateThumbprint /s My /fd SHA256 /tr $TimestampServer.AbsoluteUri /td SHA256 $file.FullName
    if ($LASTEXITCODE -ne 0) { throw "Signing failed for $($file.Name). No package was produced." }
}
& (Join-Path $PSScriptRoot 'VerifyReleaseSignatures.ps1') -PublishDirectory $root
