"""Exercise the documentation refresh script with synthetic capture output."""

import json
from pathlib import Path
import re
import shutil
import subprocess
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[2]
SCRIPT = ROOT / 'tools/UpdateDocumentationScreenshots.ps1'
PAGES = ('gui-tasks.md', 'search-improvements.md', 'workflows-1.0.5.md')


@unittest.skipUnless(shutil.which('pwsh'), 'PowerShell 7 is required')
class DocumentationScreenshotsTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        (self.root / 'tools').mkdir()
        (self.root / 'docs').mkdir()
        shutil.copyfile(SCRIPT, self.root / 'tools' / SCRIPT.name)
        self.original = '# Guide\n\n![Tern v1.0.5 Paper view](images/gui/split-paper.png)\n\nKeep café and `v1.0.5` unchanged.\n'
        for page in PAGES:
            (self.root / 'docs' / page).write_text(self.original, encoding='utf-8')
        mappings = re.findall(r"^        '([^']+\.png)' = '([^']+\.png)'", SCRIPT.read_text(), re.M)
        self.sources = [source for source, _ in mappings]
        for _, target in mappings:
            (self.root / 'docs/images' / target.replace('\\', '/')).parent.mkdir(parents=True, exist_ok=True)
        (self.root / 'sources.json').write_text(json.dumps(self.sources), encoding='utf-8')
        self.fake = self.root / 'capture.ps1'
        self.fake.write_text('''
$ErrorActionPreference = 'Stop'
$screenshots = Join-Path $env:TERN_TEST_OUTPUT 'screenshots'
foreach ($source in (Get-Content (Join-Path $PSScriptRoot 'sources.json') -Raw | ConvertFrom-Json)) {
    $path = Join-Path $screenshots $source
    New-Item -ItemType Directory -Path (Split-Path $path) -Force | Out-Null
    [IO.File]::WriteAllBytes($path, [byte[]](1, 2, 3))
}
@{ Version = '1.0.6+8b8ed29520c2669ef1dad60dc43a0e95b77a3a87'; CapturedAtUtc = [DateTime]::UtcNow; Evidence = 'Synthetic test' } |
    ConvertTo-Json | Set-Content (Join-Path $screenshots 'latest-gui/capture-metadata.json')
$global:LASTEXITCODE = 0
''', encoding='utf-8')

    def run_refresh(self, name='results'):
        return subprocess.run([
            shutil.which('pwsh'), '-NoProfile', '-File',
            str(self.root / 'tools' / SCRIPT.name),
            '-DotNetExecutable', str(self.fake),
            '-ResultsDirectory', str(self.root / name),
        ], capture_output=True, text=True, encoding='utf-8', timeout=60)

    def test_refreshes_captions_and_preserves_prose(self):
        result = self.run_refresh()
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        evidence = json.loads((self.root / 'results/documentation-screenshots.json').read_text(encoding='utf-8-sig'))
        self.assertEqual(len(self.sources), len(evidence['Images']))
        expected = self.original.replace('![Tern v1.0.5 ', f"![Tern v{evidence['Version']} ")
        for page in PAGES:
            self.assertEqual(expected, (self.root / 'docs' / page).read_text(encoding='utf-8'))
        # A second refresh also accepts captions that already include commit metadata.
        result = self.run_refresh('second-results')
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        for page in PAGES:
            self.assertEqual(expected, (self.root / 'docs' / page).read_text(encoding='utf-8'))

    def test_missing_capture_does_not_relabel_pages(self):
        (self.root / 'sources.json').write_text(json.dumps(self.sources[:-1]), encoding='utf-8')
        result = self.run_refresh()
        self.assertNotEqual(0, result.returncode)
        for page in PAGES:
            self.assertEqual(self.original, (self.root / 'docs' / page).read_text(encoding='utf-8'))
        self.assertFalse((self.root / 'results/documentation-screenshots.json').exists())

    def test_malformed_caption_still_fails_validation(self):
        (self.root / 'docs' / PAGES[0]).write_text('![Unversioned screenshot](images/gui/split-paper.png)\n', encoding='utf-8')
        result = self.run_refresh()
        self.assertNotEqual(0, result.returncode)
        self.assertIn('Update screenshot version captions', result.stderr)
        self.assertEqual(self.original, (self.root / 'docs' / PAGES[1]).read_text(encoding='utf-8'))


if __name__ == '__main__':
    unittest.main()
