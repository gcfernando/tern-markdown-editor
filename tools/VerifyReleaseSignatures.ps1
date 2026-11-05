[CmdletBinding()]
param([Parameter(Mandatory)][string]$PublishDirectory)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath $PublishDirectory).Path
$files = @(Get-ChildItem -LiteralPath $root -File | Where-Object { $_.Name -eq 'Tern.exe' -or $_.Name -eq 'Tern.dll' -or $_.Name -like 'Tern.*.dll' })
if (-not ($files.Name -contains 'Tern.exe') -or -not ($files.Name -contains 'Tern.dll')) { throw 'Missing Tern release binaries.' }
$publisher = $null
$evidence = foreach ($file in $files) {
    $signature = Get-AuthenticodeSignature -LiteralPath $file.FullName
    if ($signature.Status -ne 'Valid' -or $null -eq $signature.SignerCertificate -or $null -eq $signature.TimeStamperCertificate) {
        throw "A trusted, timestamped signature is required for $($file.Name). Status: $($signature.Status)"
    }
    if ($null -eq $publisher) { $publisher = $signature.SignerCertificate.Thumbprint }
    if ($signature.SignerCertificate.Thumbprint -ne $publisher) { throw 'First-party binaries have different signing identities.' }
    [ordered]@{ file = $file.Name; sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant(); publisher = $signature.SignerCertificate.Subject; thumbprint = $publisher }
}
ConvertTo-Json -InputObject @($evidence) -Compress
