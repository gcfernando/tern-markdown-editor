[CmdletBinding()]
param(
    [string]$DotNetExecutable = 'dotnet',
    [string]$ResultsDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ('..\..\tern-build-output\docs-capture-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmssfff'))))
)
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Push-Location $repository
try {
    if (Test-Path -LiteralPath $ResultsDirectory) { throw 'Choose a fresh capture results directory.' }
    $started = [DateTime]::UtcNow
    $filter = 'FullyQualifiedName~GuiWorkspaceFeaturesTests.Capture_latest_application_gui_samples|FullyQualifiedName~GuiDocumentationCaptureTests|FullyQualifiedName~DocumentSearchWorkflowTests.Search_ui_captures|FullyQualifiedName~Release105Tests'
    # This tool publishes the captures, so it asks the tests to keep them.
    $captures = Join-Path $ResultsDirectory 'captures'
    $env:TERN_TEST_OUTPUT = $captures
    try { & $DotNetExecutable test 'tests\Tern.Desktop.Tests\Tern.Desktop.Tests.csproj' -c Release --no-restore --filter $filter --logger trx --results-directory $ResultsDirectory }
    finally { Remove-Item Env:TERN_TEST_OUTPUT -ErrorAction SilentlyContinue }
    if ($LASTEXITCODE -ne 0) { throw 'Current-build documentation capture checks failed.' }
    $metadata = Get-Content -LiteralPath (Join-Path $captures 'screenshots\latest-gui\capture-metadata.json') -Raw | ConvertFrom-Json
    $version = $metadata.Version
    if (-not $version) { throw 'Capture assembly version metadata is missing.' }
    $pages = [ordered]@{}
    foreach ($page in @('docs\gui-tasks.md', 'docs\search-improvements.md', 'docs\workflows-1.0.5.md')) {
        $text = Get-Content -LiteralPath $page -Raw
        # Informational versions include the source commit. Refresh captions with the
        # captures instead of requiring a manual edit for every version or commit.
        $text = [regex]::Replace($text, '(!\[Tern v)[^\s\]]+( )', [System.Text.RegularExpressions.MatchEvaluator]{
            param($match)
            $match.Groups[1].Value + $version + $match.Groups[2].Value
        })
        foreach ($caption in [regex]::Matches($text, '!\[([^\]]*)\]\(')) {
            if (-not $caption.Groups[1].Value.StartsWith("Tern v$version ")) {
                throw "Update screenshot version captions in $page for assembly version $version."
            }
        }
        $pages[$page] = $text
    }
    $images = [ordered]@{
        'latest-gui\01-paper-workspace.png' = 'gui\split-paper.png'
        'latest-gui\01b-midnight-workspace.png' = 'gui\split-midnight.png'
        'latest-gui\05-compact-midnight.png' = 'gui\compact-midnight.png'
        'gui-pack\insert-image-Paper.png' = 'gui\insert-image-paper.png'
        'gui-pack\insert-image-Midnight.png' = 'gui\insert-image-midnight.png'
        'gui-pack\merge-review-Paper.png' = 'gui\merge-review-paper.png'
        'gui-pack\recovery-details-Midnight.png' = 'gui\recovery-details-midnight.png'
        'gui-pack\export-warnings-Paper.png' = 'gui\export-warnings-paper.png'
        'gui-pack\export-warnings-Midnight.png' = 'gui\export-warnings-midnight.png'
        'gui-pack\tutor-setup-Paper.png' = 'gui\tutor-setup-paper.png'
        'gui-pack\tutor-setup-Midnight.png' = 'gui\tutor-setup-midnight.png'
        'search-upgrade\Midnight-Split.png' = 'search\midnight-split.png'
        'search-upgrade\Paper-Reading.png' = 'search\paper-reading.png'
        'v1.0.5\help-replay.png' = 'gui\v105\help-replay.png'
        'v1.0.5\starter-templates.png' = 'gui\v105\starter-templates.png'
        'v1.0.5\metadata-tags.png' = 'gui\v105\metadata-tags.png'
        'v1.0.5\save-before-image.png' = 'gui\v105\save-before-image.png'
        'v1.0.5\insert-link-web.png' = 'gui\v105\insert-link-web.png'
        'v1.0.5\complete-print-preview.png' = 'gui\v105\complete-print-preview.png'
        'v1.0.5\ask-and-learn-offline.png' = 'gui\v105\ask-and-learn-offline.png'
    }
    $evidence = foreach ($image in $images.GetEnumerator()) {
        $source = Join-Path (Join-Path $captures 'screenshots') $image.Key
        $target = Join-Path 'docs\images' $image.Value
        $file = Get-Item -LiteralPath $source
        if ($file.Length -eq 0 -or $file.LastWriteTimeUtc -lt $started) { throw "Missing or stale screenshot: $source" }
        Copy-Item -LiteralPath $source -Destination $target -Force
        [pscustomobject]@{ Path = $target; Version = $version; Bytes = $file.Length; Sha256 = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant() }
    }
    # Do not relabel existing screenshots if a new capture is missing or stale.
    foreach ($page in $pages.GetEnumerator()) {
        [IO.File]::WriteAllText((Join-Path $repository $page.Key), $page.Value, [Text.UTF8Encoding]::new($false))
    }
    [pscustomobject]@{ Version = $version; CapturedAtUtc = $metadata.CapturedAtUtc; Evidence = $metadata.Evidence; Images = @($evidence) } |
        ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $ResultsDirectory 'documentation-screenshots.json') -Encoding utf8
    Write-Output "Updated $($images.Count) documentation screenshots from Tern v$version. Evidence: $ResultsDirectory"
}
finally { Pop-Location }
