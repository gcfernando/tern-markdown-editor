"""Package integrity fixtures contain dummy binaries and are never executed."""

import importlib.util
import json
import tempfile
import unittest
import zipfile
from pathlib import Path
from unittest.mock import patch

import publish_main_release as release

SPEC = importlib.util.spec_from_file_location(
    "tern_package_fixture",
    Path(__file__).resolve().parents[2] / "tools/package_release.py",
)
package = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(package)


class PackageTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.source = self.root / "source"
        self.source.mkdir()
        self.publish = self.root / "publish"
        self.publish.mkdir()
        for name in package.REQUIRED:
            (self.publish / name).write_bytes(b"dummy fixture; never executed")
        (self.publish / "Tern.runtimeconfig.json").write_text(
            json.dumps(
                {
                    "runtimeOptions": {
                        "includedFrameworks": [
                            {"name": "Microsoft.NETCore.App", "version": "10.0.13"}
                        ]
                    }
                }
            )
        )
        for name in (
            "LICENSE",
            "THIRD-PARTY-NOTICES.md",
            "documentation.md",
            "CONTRIBUTING.md",
            "SECURITY.md",
            "REPOSITORY_STANDARDS.md",
            "CHANGELOG.md",
        ):
            (self.source / name).write_text("Fixture document\n")
        (self.source / "docs").mkdir()
        (self.source / "docs/guide.md").write_text("# Guide\n")
        captures = self.source / "docs/review/oct29/after"
        captures.mkdir(parents=True)
        (captures / "machine-specific.json").write_text('{"capture":"generated"}')
        (self.source / "docs/review/oct29/maintained.md").write_text(
            "# Maintained review\n"
        )
        (self.source / "licenses").mkdir()
        (self.source / "licenses/upstream.md").write_text(
            "[Upstream local source](missing-upstream-source.md)\n"
        )
        (self.source / "licenses/dependency.txt").write_text(
            "Fixture upstream notice\n"
        )
        (self.source / "licenses/dependency-inventory.json").write_text(
            json.dumps(
                {
                    "applicationVersion": "1.0.0",
                    "runtime": {
                        "id": "Microsoft.NETCore.App.Runtime.win-x64",
                        "version": "10.0.13",
                        "notices": [],
                    },
                    "packages": [],
                }
            )
        )
        (self.source / "src/Tern.Desktop/obj").mkdir(parents=True)
        (self.source / "src/Tern.Desktop/obj/project.assets.json").write_text(
            '{"libraries":{}}'
        )
        (self.source / "Images").mkdir()
        (self.source / "Images/fixture.png").write_bytes(b"fixture image bytes")
        (self.source / "tools").mkdir()
        (self.source / "tools/BuildRelease.ps1").write_text("# Source command\n")
        (self.source / "README.md").write_text(
            "[Guide](docs/guide.md) [Build](tools/BuildRelease.ps1#source) "
            "[Browser page](https://gcfernando.github.io/tern-markdown-editor/documentation/)\n"
        )
        (self.source / "Directory.Build.props").write_text(
            "<Project><PropertyGroup><Version>1.0.0</Version></PropertyGroup></Project>"
        )
        (self.source / "global.json").write_text('{"sdk":{"version":"10.0.402"}}')
        root_patch = patch.object(package, "ROOT", self.source)
        root_patch.start()
        self.addCleanup(root_patch.stop)

    def build(self):
        return package.package(self.publish, self.root / "release", "a" * 40)

    def test_complete_package_hashes_licenses_runtime_and_portable_guides(self):
        archive = self.build()
        manifest = package.verify_archive(archive)
        self.assertEqual(manifest["version"], "1.0.0")
        self.assertTrue(manifest["selfContained"])
        self.assertEqual(manifest["sourceCommit"], "a" * 40)
        self.assertEqual(len(manifest["acceptance"]["manualTestClasses"]), 3)
        with zipfile.ZipFile(archive) as bundle:
            text = bundle.read("README.md").decode()
            self.assertIn("(docs/guide.md)", text)
            self.assertIn("/blob/" + "a" * 40 + "/tools/BuildRelease.ps1#source", text)
            self.assertIn(
                "https://gcfernando.github.io/tern-markdown-editor/documentation/", text
            )
            self.assertIn("licenses/dependency.txt", bundle.namelist())
            self.assertIn("docs/review/oct29/maintained.md", bundle.namelist())
            self.assertFalse(any("/after/" in name for name in bundle.namelist()))
            self.assertEqual(
                bundle.read("licenses/upstream.md"),
                (self.source / "licenses/upstream.md").read_bytes(),
            )
        self.assertEqual(
            (archive.parent / "SHA256SUMS.txt").read_text().strip(),
            f"{package.sha256(archive)}  {archive.name}",
        )

    def test_runtime_dependent_output_is_refused(self):
        (self.publish / "Tern.runtimeconfig.json").write_text(
            '{"runtimeOptions":{"framework":{"name":"Microsoft.NETCore.App"}}}'
        )
        with self.assertRaises(package.PackageError):
            self.build()

    def test_missing_native_dependency_is_refused(self):
        (self.publish / "coreclr.dll").unlink()
        with self.assertRaises(package.PackageError):
            self.build()

    def test_mismatched_runtime_license_inventory_is_refused(self):
        inventory = self.source / "licenses/dependency-inventory.json"
        content = json.loads(inventory.read_text())
        content["runtime"]["version"] = "10.0.99"
        inventory.write_text(json.dumps(content))
        with self.assertRaises(package.PackageError):
            self.build()

    def test_missing_license_is_refused(self):
        (self.source / "THIRD-PARTY-NOTICES.md").unlink()
        with self.assertRaises(package.PackageError):
            self.build()

    def test_existing_output_is_preserved(self):
        output = self.root / "release"
        output.mkdir()
        keep = output / "original.txt"
        keep.write_text("keep")
        with self.assertRaises(package.PackageError):
            self.build()
        self.assertEqual(keep.read_text(), "keep")

    def test_broken_documentation_link_is_refused(self):
        (self.source / "README.md").write_text("[Missing](missing.md)\n")
        with self.assertRaises(package.PackageError):
            self.build()

    def test_changed_file_bytes_are_rejected(self):
        archive = self.build()
        with zipfile.ZipFile(archive) as bundle:
            content = {info.filename: bundle.read(info) for info in bundle.infolist()}
        content["Tern.exe"] += b"changed"
        with zipfile.ZipFile(archive, "w") as bundle:
            for name, data in content.items():
                bundle.writestr(name, data)
        with self.assertRaises(package.PackageError):
            package.verify_archive(archive)

    def test_extra_unsafe_or_unrecorded_member_is_rejected(self):
        archive = self.build()
        with zipfile.ZipFile(archive, "a") as bundle:
            bundle.writestr("../escape", b"extra")
        with self.assertRaises(package.PackageError):
            package.verify_archive(archive)

    def test_publisher_rejects_other_commit_or_version(self):
        archive = self.build()
        for application_version, commit in (("2.0.0", "a" * 40), ("1.0.0", "b" * 40)):
            with self.subTest(version=application_version, commit=commit):
                with self.assertRaises(release.ReleaseError):
                    release.validate_package(archive, application_version, commit)


class PublicationBoundaryTests(unittest.TestCase):
    def test_invalid_zip_is_rejected_before_any_github_call(self):
        class NoAPI:
            def request(self, *_args, **_kwargs):
                raise AssertionError("Invalid ZIP reached GitHub")

        with tempfile.TemporaryDirectory() as temporary:
            archive = Path(temporary) / "Tern-win-x64.zip"
            archive.write_bytes(b"not a ZIP")
            environment = {
                "GITHUB_ACTIONS": "true",
                "GITHUB_EVENT_NAME": "push",
                "GITHUB_REF": "refs/heads/main",
                "GITHUB_REPOSITORY": "example/tern",
                "GITHUB_SHA": "a" * 40,
                "GITHUB_RUN_ID": "1",
            }
            with self.assertRaises(release.ReleaseError):
                release.publish(NoAPI(), environment, "Tern", "1.0.0", [archive])


if __name__ == "__main__":
    unittest.main()
