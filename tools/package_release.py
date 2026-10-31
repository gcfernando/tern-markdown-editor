"""Package a fresh self-contained Windows publish, preserving licenses and evidence."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import shutil
import stat
import urllib.parse
import xml.etree.ElementTree as ET
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
REPOSITORY = "gcfernando/tern-markdown-editor"
REQUIRED = (
    "Tern.exe",
    "Tern.dll",
    "Tern.deps.json",
    "Tern.runtimeconfig.json",
    "coreclr.dll",
    "hostfxr.dll",
    "hostpolicy.dll",
    "System.Private.CoreLib.dll",
    "libSkiaSharp.dll",
    "libHarfBuzzSharp.dll",
    "e_sqlite3.dll",
)


class PackageError(RuntimeError):
    """The candidate is incomplete or its integrity evidence disagrees."""


def sha256(path: Path) -> str:
    value = hashlib.sha256()
    with path.open("rb") as source:
        while chunk := source.read(1024 * 1024):
            value.update(chunk)
    return value.hexdigest()


def version() -> str:
    value = ET.parse(ROOT / "Directory.Build.props").findtext(".//Version")
    if value is None or not re.fullmatch(r"\d+\.\d+\.\d+", value):
        raise PackageError("Invalid application version")
    return value


def validate_license_inventory(runtime: dict) -> None:
    """Match runtime notices and the restored, locked desktop dependency graph."""
    inventory = json.loads(
        (ROOT / "licenses/dependency-inventory.json").read_text(encoding="utf-8")
    )
    included = {
        item["name"]: item["version"] for item in runtime.get("includedFrameworks", [])
    }
    if (
        inventory.get("applicationVersion") != version()
        or inventory["runtime"]["id"] != "Microsoft.NETCore.App.Runtime.win-x64"
        or inventory["runtime"]["version"] != included.get("Microsoft.NETCore.App")
    ):
        raise PackageError(
            "Runtime/version licensing inventory differs from published output"
        )
    assets = json.loads(
        (ROOT / "src/Tern.Desktop/obj/project.assets.json").read_text(encoding="utf-8")
    )
    expected = {
        tuple(name.rsplit("/", 1))
        for name, item in assets["libraries"].items()
        if item["type"] == "package"
    }
    actual = {(item["id"], item["version"]) for item in inventory["packages"]}
    if expected != actual or len(actual) != len(inventory["packages"]):
        raise PackageError(
            "Licensing inventory does not cover the restored desktop graph"
        )
    for item in [inventory["runtime"], *inventory["packages"]]:
        for notice in item.get("notices", []):
            path = (ROOT / notice["path"]).resolve()
            if (
                ROOT.resolve() not in path.parents
                or not path.is_file()
                or sha256(path) != notice["sha256"]
            ):
                raise PackageError(
                    "Licensing notice path/hash differs from its inventory"
                )


def records(folder: Path) -> list[dict]:
    result = []
    for path in sorted(folder.rglob("*")):
        if path.is_symlink() or getattr(path, "is_junction", lambda: False)():
            raise PackageError("Release payload must not contain links")
        if path.is_file():
            result.append(
                {
                    "path": path.relative_to(folder).as_posix(),
                    "size": path.stat().st_size,
                    "sha256": sha256(path),
                }
            )
    return result


def portable_documentation(folder: Path, commit: str) -> None:
    """Keep packaged guides local and direct source-only references to GitHub."""
    for document in folder.rglob("*.md"):
        if document.relative_to(folder).parts[0] == "licenses":
            continue  # Third-party license/notice bytes must remain verbatim.

        def replace(match):
            url = match.group(2)
            parts = urllib.parse.urlsplit(url)
            if (
                parts.scheme
                or parts.netloc
                or not parts.path
                or parts.path.startswith("/")
            ):
                return match.group(0)
            target = (document.parent / urllib.parse.unquote(parts.path)).resolve()
            if target.exists():
                return match.group(0)
            relative = target.relative_to(folder.resolve()).as_posix()
            source = ROOT / relative
            if not source.exists():
                raise PackageError(f"Broken packaged documentation link: {relative}")
            if match.group(1).startswith("!"):
                base = f"https://raw.githubusercontent.com/{REPOSITORY}/{commit}/"
            else:
                kind = "tree" if source.is_dir() else "blob"
                base = f"https://github.com/{REPOSITORY}/{kind}/{commit}/"
            hosted = base + urllib.parse.quote(relative, safe="/")
            if parts.fragment:
                hosted += "#" + parts.fragment
            return match.group(1) + hosted + match.group(3)

        text = document.read_text(encoding="utf-8")
        text = re.sub(r"(!?\[[^\]\r\n]*\]\()([^\s\)]+)(\))", replace, text)
        document.write_text(text, encoding="utf-8", newline="\n")


def verify_archive(archive: Path) -> dict:
    if archive.stat().st_size > 1024 * 1024 * 1024:
        raise PackageError("Release archive exceeds the one GiB input limit")
    with zipfile.ZipFile(archive) as bundle:
        entries = bundle.infolist()
        if (
            not entries
            or len(entries) > 20000
            or sum(entry.file_size for entry in entries) > 2 * 1024 * 1024 * 1024
        ):
            raise PackageError("Release archive size/member count exceeds its limit")
        names = [entry.filename for entry in entries]
        if len(names) != len(set(name.casefold() for name in names)):
            raise PackageError("Duplicate ZIP members")
        for entry in entries:
            parts = entry.filename.split("/")
            if (
                entry.is_dir()
                or any(part in ("", ".", "..") for part in parts)
                or "\\" in entry.filename
                or ":" in entry.filename
                or stat.S_IFMT(entry.external_attr >> 16) not in (0, stat.S_IFREG)
            ):
                raise PackageError("Unsafe ZIP member")
        if bundle.getinfo("package-manifest.json").file_size > 16 * 1024 * 1024:
            raise PackageError("Package manifest exceeds its size limit")
        manifest = json.loads(bundle.read("package-manifest.json"))
        if (
            manifest.get("schemaVersion") != 1
            or manifest.get("project") != "Tern"
            or manifest.get("runtimeIdentifier") != "win-x64"
            or manifest.get("selfContained") is not True
            or manifest.get("signing") != "Unsigned preview candidate"
        ):
            raise PackageError("Unsupported package manifest")
        expected = {record["path"]: record for record in manifest["files"]}
        if (
            len(expected) != len(manifest["files"])
            or set(expected) != set(names) - {"package-manifest.json"}
            or not set(REQUIRED).issubset(expected)
            or not {"LICENSE", "THIRD-PARTY-NOTICES.md", "START-HERE.txt"}.issubset(
                expected
            )
            or not any(name.startswith("licenses/") for name in expected)
        ):
            raise PackageError("Incomplete package file coverage")
        for name, record in expected.items():
            data = bundle.read(name)  # zipfile verifies CRC as each member is read.
            if (
                len(data) != record["size"]
                or hashlib.sha256(data).hexdigest() != record["sha256"]
            ):
                raise PackageError(f"Package hash mismatch: {name}")
        return manifest


def package(publish: Path, output: Path, commit: str) -> Path:
    if output.exists():
        raise PackageError("Preserve existing output; choose a fresh release directory")
    if not re.fullmatch(r"[a-f0-9]{40}|local", commit):
        raise PackageError("Invalid source commit")
    if any(not (publish / name).is_file() for name in REQUIRED):
        raise PackageError("Self-contained Windows publish is incomplete")
    runtime = json.loads(
        (publish / "Tern.runtimeconfig.json").read_text(encoding="utf-8")
    )["runtimeOptions"]
    if (
        runtime.get("framework")
        or runtime.get("frameworks")
        or not runtime.get("includedFrameworks")
    ):
        raise PackageError("The release must bundle its .NET runtime")
    validate_license_inventory(runtime)
    records(publish)  # Refuse links before copying anything.
    staging = output.parent / (output.name + "-payload")
    if staging.exists() or staging.resolve() in publish.resolve().parents:
        raise PackageError("Preserve existing staging; use separate fresh directories")
    shutil.copytree(publish, staging)
    for name in (
        "LICENSE",
        "THIRD-PARTY-NOTICES.md",
        "licenses",
        "README.md",
        "documentation.md",
        "CONTRIBUTING.md",
        "SECURITY.md",
        "REPOSITORY_STANDARDS.md",
        "CHANGELOG.md",
        "docs",
        "Images",
    ):
        source = ROOT / name
        if not source.exists():
            raise PackageError(
                f"Required packaged documentation/license is missing: {name}"
            )
        if source.is_dir():

            def ignore_captures(directory, names):
                # Only generated per-review captures; maintained review Markdown stays.
                if Path(directory).parent == ROOT / "docs/review" and "after" in names:
                    return ["after"]
                return []

            shutil.copytree(
                source,
                staging / name,
                ignore=ignore_captures if name == "docs" else None,
            )
        else:
            shutil.copy2(source, staging / name)
    portable_documentation(staging, commit if commit != "local" else "main")
    (staging / "START-HERE.txt").write_text(
        f"Tern {version()} — Windows x64\n\nExtract the complete ZIP and run Tern.exe.\n"
        "The .NET 10 runtime and required companion files are bundled.\n"
        "Keep this entire folder together. No installer or administrator access is required.\n\n"
        "This is an unsigned preview candidate. Windows may show Unknown publisher or SmartScreen warnings.\n"
        "A checksum is an integrity check, not a trusted publisher signature.\n"
        "See README.md, documentation.md, LICENSE, THIRD-PARTY-NOTICES.md and licenses/.\n"
        "Automated tests/startup smoke do not replace manual printing, accessibility or offline acceptance.\n",
        encoding="utf-8",
        newline="\n",
    )
    manifest = {
        "schemaVersion": 1,
        "project": "Tern",
        "version": version(),
        "sourceCommit": commit,
        "runtimeIdentifier": "win-x64",
        "selfContained": True,
        "sdk": json.loads((ROOT / "global.json").read_text())["sdk"]["version"],
        "signing": "Unsigned preview candidate",
        "acceptance": {
            "requiredAutomatedScope": "Eight test projects excluding exactly eight external/interactive acceptance cases",
            "manualTestClasses": [
                "Tern.Desktop.Tests.RenderingCorpusModeTests",
                "Tern.Desktop.Tests.Question35SampleTests",
                "Tern.Desktop.Tests.NativeRecycleRestoreTests",
            ],
            "manualTestMethods": [
                "Tern.PreviewView.Tests.CorpusValidationTests.Every_inventoried_document_preserves_semantic_blocks_and_realizes_native_preview",
                "Tern.PreviewView.Tests.CorpusLocalAssetTests.Every_corpus_image_records_actual_native_load_or_explicit_policy_block",
                "Tern.PreviewView.Tests.ScrollbarDragTests.Held_left_mouse_thumb_drag_moves_down_and_back_up_without_snapback(suppliedSample: True; widths 400 and 760)",
            ],
            "testFilter": (
                Path(__file__).resolve().parent / "automated-test-filter.txt"
            )
            .read_text(encoding="utf-8")
            .strip(),
            "reason": "Original untracked rendering corpus, supplied question 35 sample and interactive Windows Shell restore are unavailable on clean hosted runners.",
            "remainingManualGates": [
                "printing",
                "accessibility",
                "native clipboard/drop combinations",
                "Windows display scaling",
                "installed offline operation",
                "performance",
                "trusted signing",
            ],
        },
        "files": records(staging),
    }
    (staging / "package-manifest.json").write_text(
        json.dumps(manifest, indent=2) + "\n", encoding="utf-8", newline="\n"
    )
    output.mkdir(parents=True)
    archive = output / "Tern-win-x64.zip"
    with zipfile.ZipFile(archive, "x", zipfile.ZIP_DEFLATED, compresslevel=9) as bundle:
        for record in records(staging):
            bundle.write(staging / record["path"], record["path"])
    verify_archive(archive)
    (output / "SHA256SUMS.txt").write_text(
        f"{sha256(archive)}  {archive.name}\n", encoding="utf-8", newline="\n"
    )
    return archive


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--publish", type=Path)
    parser.add_argument("--output", type=Path)
    parser.add_argument("--verify", type=Path)
    parser.add_argument("--commit", default=os.environ.get("GITHUB_SHA", "local"))
    args = parser.parse_args()
    if args.verify:
        print(json.dumps(verify_archive(args.verify)))
    elif args.publish and args.output:
        print(package(args.publish, args.output, args.commit))
    else:
        parser.error(
            "Use --verify ARCHIVE or both --publish FOLDER and --output FOLDER"
        )


if __name__ == "__main__":
    main()
