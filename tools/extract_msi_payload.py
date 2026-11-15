"""Verify embedded MSI cabinet bytes without starting an installation transaction."""
from pathlib import Path
import argparse
import json
import re
import shutil
import subprocess
import xml.etree.ElementTree as E
from package_release import sha256


def extract(msi, payload, evidence, wix, branding=None):
    if evidence.exists():
        raise RuntimeError('Preserve existing evidence; choose a fresh directory')
    evidence.mkdir(parents=True)
    cabinet = evidence / 'cabinet'
    source = evidence / 'decompiled.wxs'
    result = subprocess.run([str(wix.resolve() / 'dark.exe'), '-nologo', '-x', str(cabinet.resolve()), str(msi.resolve()), str(source.resolve())],
                            capture_output=True, text=True, timeout=120)
    (evidence / 'cabinet-extraction.log').write_text(result.stdout + result.stderr, encoding='utf-8')
    result.check_returncode()
    namespace = {'w': 'http://schemas.microsoft.com/wix/2006/wi'}
    directory = E.parse(source).find('.//w:Directory[@Id="INSTALLFOLDER"]', namespace)
    if directory is None:
        raise RuntimeError('MSI has no installation directory')
    actual = {}

    def walk(node, prefix):
        for file in node.findall('w:Component/w:File', namespace):
            identifier = file.get('Id', '')
            if not re.fullmatch(r'[A-Za-z_][A-Za-z0-9_.]*', identifier):
                raise RuntimeError('Unsafe MSI file identifier')
            name = file.get('Name', '').split('|')[-1]
            relative = (prefix / name).as_posix()
            if relative in actual:
                raise RuntimeError('Duplicate MSI path')
            actual[relative] = cabinet / 'File' / identifier
        for child in node.findall('w:Directory', namespace):
            walk(child, prefix / child.get('Name', '').split('|')[-1])

    walk(directory, Path())
    manifest_path = payload / 'package-manifest.json'
    manifest = json.loads(manifest_path.read_text(encoding='utf-8'))
    expected = {file['path']: file for file in manifest['files']}
    expected['package-manifest.json'] = dict(size=manifest_path.stat().st_size, sha256=sha256(manifest_path))
    if set(actual) != set(expected):
        raise RuntimeError('MSI cabinet paths disagree with the complete portable payload')
    installed = (evidence / 'MSI payload with spaces').resolve()
    installed.mkdir()
    for name, original in actual.items():
        target = (installed / name).resolve()
        if not target.is_relative_to(installed):
            raise RuntimeError('Unsafe extracted MSI path')
        record = expected[name]
        if not original.is_file() or original.stat().st_size != record['size'] or sha256(original) != record['sha256']:
            raise RuntimeError('MSI cabinet differs from portable payload: ' + name)
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(original, target)
    branding_checks = {}
    if branding is not None:
        for name, stream in [('tern-banner.bmp', 'WixUI_Bmp_Banner'), ('tern-dialog.bmp', 'WixUI_Bmp_Dialog')]:
            source_image = branding / name
            embedded = cabinet / 'Binary' / stream
            if not source_image.is_file() or not embedded.is_file() or sha256(source_image) != sha256(embedded):
                raise RuntimeError('MSI does not contain the generated Tern artwork: ' + name)
            branding_checks[name] = sha256(embedded)
        icon = cabinet / 'Icon' / 'TernIcon.ico'
        source_icon = Path(__file__).resolve().parents[1] / 'src/Tern.Desktop/Assets/tern.ico'
        if not icon.is_file() or sha256(icon) != sha256(source_icon):
            raise RuntimeError('MSI does not contain the current Tern icon')
        branding_checks['TernIcon.ico'] = sha256(icon)
    report = dict(status='passed', version=manifest['version'], verifiedFiles=len(expected), executable=str(installed / 'Tern.exe'),
                  branding=branding_checks,
                  scope='Direct MSI cabinet extraction and exact payload hashes. No user installation, default-app change or Explorer UI claim.')
    (evidence / 'result.json').write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(report, indent=2))


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ('msi', 'payload', 'evidence', 'wix'):
        parser.add_argument(name, type=Path)
    parser.add_argument('--branding', type=Path)
    args = parser.parse_args()
    extract(args.msi, args.payload, args.evidence, args.wix, args.branding)
