[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Msi,
    [Parameter(Mandatory)][string]$Payload,
    [Parameter(Mandatory)][string]$EvidenceDirectory,
    [Parameter(Mandatory)][string]$WixDirectory,
    [string]$BrandingDirectory
)
$ErrorActionPreference = 'Stop'
$arguments = @($Msi, $Payload, $EvidenceDirectory, $WixDirectory)
if ($BrandingDirectory) { $arguments += @('--branding', $BrandingDirectory) }
python (Join-Path $PSScriptRoot 'extract_msi_payload.py') @arguments
if ($LASTEXITCODE -ne 0) { throw 'MSI cabinet extraction or payload verification failed.' }
