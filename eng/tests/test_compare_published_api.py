"""Fail-closed input validation and review-only diff behavior for published API evidence."""

import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import warnings
from zipfile import ZipFile

SPEC = importlib.util.spec_from_file_location("compare_published_api", Path(__file__).resolve().parents[1] / "compare-published-api.py")
COMPARE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(COMPARE)
PACKAGE = "SharpLink.Client"
VERSION = "3.0.0"
COMMIT = "a" * 40


class PublishedApiEvidenceTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.assemblies = self.root / "assemblies"
        self.assemblies.mkdir()
        self.package = self.root / f"{PACKAGE}.{VERSION}.nupkg"

    def write_package(self, package=PACKAGE, version=VERSION, commit=COMMIT, dlls=None):
        expected = f"lib/net10.0/{PACKAGE}.dll"
        with warnings.catch_warnings(), ZipFile(self.package, "w") as archive:
            warnings.simplefilter("ignore", UserWarning)  # Deliberately duplicate ZIP entries.
            archive.writestr(f"{PACKAGE}.nuspec", '<package xmlns="urn:nuget"><metadata>'
                             f'<id>{package}</id><version>{version}</version><repository commit="{commit}" />'
                             '</metadata></package>')
            for name in ([expected] if dlls is None else dlls):
                archive.writestr(name, b"assembly bytes")

    def extract(self):
        return COMPARE.extract_package(self.package, PACKAGE, VERSION, self.assemblies,
                                       self.package.as_uri(), COMMIT)

    def test_actual_bytes_and_source_are_recorded(self):
        self.write_package()
        result = self.extract()
        self.assertEqual(result["package_sha256"], COMPARE.sha256(self.package.read_bytes()))
        self.assertEqual(result["assembly_sha256"], COMPARE.sha256(b"assembly bytes"))
        self.assertEqual(result["source"], self.package.as_uri())
        self.assertEqual((result["id"], result["version"], result["repository_commit"]), (PACKAGE, VERSION, COMMIT))
        self.assertEqual((self.assemblies / f"{PACKAGE}.dll").read_bytes(), b"assembly bytes")

    def test_missing_package_fails(self):
        with self.assertRaises(FileNotFoundError):
            self.extract()
        self.assertEqual(list(self.assemblies.iterdir()), [])

    def test_wrong_nuspec_identity_fails_without_extracting(self):
        for package, version in (("Unrelated", VERSION), (PACKAGE, "2.0.3")):
            with self.subTest(package=package, version=version):
                self.write_package(package=package, version=version)
                with self.assertRaisesRegex(ValueError, "nuspec identity"):
                    self.extract()
                self.assertEqual(list(self.assemblies.iterdir()), [])

    def test_wrong_candidate_commit_fails(self):
        self.write_package(commit="b" * 40)
        with self.assertRaisesRegex(ValueError, "repository commit"):
            self.extract()

    def test_missing_duplicate_or_ambiguous_shipping_dll_fails(self):
        dll = f"lib/net10.0/{PACKAGE}.dll"
        for entries in ([], [dll, dll], [dll, dll.upper()], [dll, f"ref/net10.0/{PACKAGE}.dll"],
                        [dll, "lib/net10.0/Other.dll"], [f"lib/net9.0/{PACKAGE}.dll"]):
            with self.subTest(entries=entries):
                self.write_package(dlls=entries)
                with self.assertRaisesRegex(ValueError, "exactly one shipping DLL"):
                    self.extract()
                self.assertEqual(list(self.assemblies.iterdir()), [])

    def test_missing_or_duplicate_nuspec_fails(self):
        for duplicate in (False, True):
            self.write_package()
            if duplicate:
                with warnings.catch_warnings(), ZipFile(self.package, "a") as archive:
                    warnings.simplefilter("ignore", UserWarning)
                    archive.writestr(f"{PACKAGE}.nuspec", "<package />")
            else:
                with ZipFile(self.package, "w") as archive:
                    archive.writestr(f"lib/net10.0/{PACKAGE}.dll", b"dll")
            with self.assertRaisesRegex(ValueError, "exactly one root"):
                self.extract()

    def inventory(self, output, version):
        output.mkdir()
        for package in COMPARE.PACKAGES:
            for suffix in ("api.txt", "forwarders.txt"):
                (output / f"{package}.{suffix}").write_text("", encoding="utf-8")
            (output / f"{package}.assembly.json").write_text(json.dumps({"Name": package, "Version": version}), encoding="utf-8")

    def test_wrong_assembly_version_fails(self):
        output = self.root / "inventory"
        self.inventory(output, "2.0.3.0")
        with patch.object(COMPARE.subprocess, "run"), self.assertRaisesRegex(ValueError, "unexpected assembly identity"):
            COMPARE.generate_inventory(self.assemblies, output, [{"id": PACKAGE}], VERSION, self.root / "tool.dll")

    def test_incomplete_extractor_output_fails(self):
        output = self.root / "inventory"
        self.inventory(output, "3.0.0.0")
        (output / "SharpLink.Sdk.forwarders.txt").unlink()
        with patch.object(COMPARE.subprocess, "run"), self.assertRaisesRegex(ValueError, "inventory mismatch"):
            COMPARE.generate_inventory(self.assemblies, output, [{"id": PACKAGE}], VERSION, self.root / "tool.dll")

    def test_stale_candidate_dll_build_identity_fails(self):
        output = self.root / "inventory"
        self.inventory(output, "3.0.0.0")
        identity_file = output / f"{PACKAGE}.assembly.json"
        identity_file.write_text(json.dumps({"Name": PACKAGE, "Version": "3.0.0.0",
                                            "InformationalVersion": "3.0.0+" + "b" * 40}), encoding="utf-8")
        with patch.object(COMPARE.subprocess, "run"), self.assertRaisesRegex(ValueError, "assembly build identity"):
            COMPARE.generate_inventory(self.assemblies, output, [{"id": PACKAGE}], VERSION, self.root / "tool.dll", COMMIT)

    def test_breaking_api_and_forwarder_changes_are_reported_without_failure(self):
        for label in ("published", "candidate"):
            self.inventory(self.root / label, "3.0.0.0")
        (self.root / "published" / f"{PACKAGE}.api.txt").write_text("public class Removed {}\n", encoding="utf-8")
        (self.root / "candidate" / f"{PACKAGE}.api.txt").write_text("public sealed class Added {}\n", encoding="utf-8")
        (self.root / "published/SharpLink.Sdk.forwarders.txt").write_text("Contract -> Old\n", encoding="utf-8")
        (self.root / "candidate/SharpLink.Sdk.forwarders.txt").write_text("Contract -> New\n", encoding="utf-8")
        COMPARE.write_differences(self.root, VERSION)
        self.assertEqual(len(list((self.root / "diffs").iterdir())), 16)
        api_diff = (self.root / f"diffs/{PACKAGE}.api.txt.diff").read_text(encoding="utf-8")
        self.assertIn("-public class Removed {}", api_diff)
        self.assertIn("+public sealed class Added {}", api_diff)
        forwarder_diff = (self.root / "diffs/SharpLink.Sdk.forwarders.txt.diff").read_text(encoding="utf-8")
        self.assertIn("-Contract -> Old", forwarder_diff)
        self.assertIn("+Contract -> New", forwarder_diff)
        self.assertIn("[1 / 1]", (self.root / "README.md").read_text(encoding="utf-8"))

    def test_existing_output_is_never_overwritten(self):
        marker = self.root / "reviewed.api.txt"
        marker.write_bytes(b"reviewed")
        with patch.object(COMPARE.urllib.request, "urlopen") as download:
            with self.assertRaisesRegex(ValueError, "Output already exists"):
                COMPARE.compare(self.root, self.root, VERSION, COMMIT, self.root / "tool.dll")
        download.assert_not_called()
        self.assertEqual(marker.read_bytes(), b"reviewed")

    def test_download_failure_does_not_publish_partial_report(self):
        tool = self.root / "tool.dll"
        tool.write_bytes(b"tool")
        output = self.root / "report"
        with patch.object(COMPARE, "extract_package", return_value={}), \
                patch.object(COMPARE, "generate_inventory"), \
                patch.object(COMPARE.urllib.request, "urlopen", side_effect=OSError("offline")):
            with self.assertRaisesRegex(OSError, "offline"):
                COMPARE.compare(self.root, output, VERSION, COMMIT, tool)
        self.assertFalse(output.exists())
        self.assertEqual(list(self.root.glob("sharplink-published-api-*")), [])


if __name__ == "__main__":
    unittest.main()
