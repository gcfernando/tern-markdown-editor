[CmdletBinding()]
param([string]$Destination = '.runtime/wix3141')
$ErrorActionPreference = 'Stop'
$archive = Join-Path $Destination 'wix314-binaries.zip'
New-Item -ItemType Directory -Force -Path $Destination | Out-Null
if (-not (Test-Path -LiteralPath $archive)) {
    Invoke-WebRequest -Uri 'https://github.com/wixtoolset/wix3/releases/download/wix3141rtm/wix314-binaries.zip' -OutFile $archive
}
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne '6AC824E1642D6F7277D0ED7EA09411A508F6116BA6FAE0AA5F2C7DAA2FF43D31') { throw 'WiX download hash differs from the reviewed 3.14.1 build tool.' }
$bin = Join-Path $Destination 'bin'
if (-not (Test-Path -LiteralPath (Join-Path $bin 'candle.exe'))) { Expand-Archive -LiteralPath $archive -DestinationPath $bin }
Write-Output ([IO.Path]::GetFullPath($bin))
