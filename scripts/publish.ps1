[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$projectPath = Join-Path $repositoryRoot 'src/Tern.Desktop/Tern.Desktop.csproj'
$publishDirectory = Join-Path $repositoryRoot 'artifacts/publish/win-x64'

# Keep the complete output directory together; Tern.exe depends on its adjacent files.
& dotnet publish $projectPath --configuration $Configuration --runtime win-x64 --self-contained true --output $publishDirectory -m:1
if ($LASTEXITCODE -ne 0) { throw "Publishing failed with exit code $LASTEXITCODE." }
if (-not (Test-Path -LiteralPath (Join-Path $publishDirectory 'Tern.exe') -PathType Leaf)) {
    throw 'Publishing did not produce the expected Tern.exe.'
}
Write-Output "Published self-contained Windows x64 application to $publishDirectory"
