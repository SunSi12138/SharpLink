import base64
import importlib.util
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch
from zipfile import ZipFile

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("packaged_trim_smoke", ROOT / "eng/run-packaged-trim-smoke.py")
smoke = importlib.util.module_from_spec(spec)
spec.loader.exec_module(smoke)
VERSION = "3.0.0"
COMMIT = "a" * 40


class PackagedTrimSmokeTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.directory = Path(self.temporary.name)
        self.feed = self.directory / "feed"
        self.feed.mkdir()
        for name in smoke.PACKAGES:
            self.package(name)

    def tearDown(self):
        self.temporary.cleanup()

    def package(self, name, commit=COMMIT, version=VERSION, dependency="", symbol_version=None):
        for extension in ("nupkg", "snupkg"):
            actual_version = symbol_version if extension == "snupkg" and symbol_version else version
            nuspec = (f'<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd"><metadata>'
                      f'<id>{name}</id><version>{actual_version}</version><repository commit="{commit}" />'
                      f'<dependencies><group targetFramework="net10.0">{dependency}</group></dependencies>'
                      '</metadata></package>')
            with ZipFile(self.feed / f"{name}.{version}.{extension}", "w") as archive:
                archive.writestr(f"{name}.nuspec", nuspec)

    def manifest(self):
        return smoke.package_manifest(self.feed, VERSION, COMMIT)

    def assets(self):
        libraries = {}
        cache = self.directory / "cache"
        for short in ("Abstractions", "GenerationControl", "Runtime", "Sdk"):
            name = "SharpLink." + short
            source = self.feed / f"{name}.{VERSION}.nupkg"
            relative = f"{name.lower()}/{VERSION}"
            destination = cache / relative / source.name.lower()
            destination.parent.mkdir(parents=True)
            shutil.copyfile(source, destination)
            libraries[f"{name}/{VERSION}"] = {
                "type": "package", "path": relative,
                "sha512": base64.b64encode(smoke.digest(source, "sha512")).decode()}
        return {"libraries": libraries}, cache

    def properties(self, aot=False, mode="full"):
        return {"Properties": {"PublishTrimmed": "true", "PublishAot": str(aot).lower(), "TrimMode": mode,
                               "TreatWarningsAsErrors": "true", "ILLinkTreatWarningsAsErrors": "true",
                               "IlcTreatWarningsAsErrors": "true"}, "Items": {"ProjectReference": []}}

    def test_complete_nine_package_closure_and_exact_bytes(self):
        self.assertEqual(18, len(self.manifest()))
        assets, cache = self.assets()
        self.assertEqual(4, len(smoke.verify_assets(assets, cache, self.manifest(), VERSION)))

    def test_missing_package_rejected(self):
        (self.feed / f"SharpLink.Hosting.{VERSION}.nupkg").unlink()
        with self.assertRaisesRegex(ValueError, "closure mismatch"):
            self.manifest()

    def test_extra_or_second_version_package_rejected(self):
        self.package("SharpLink.Hosting", version="2.0.3")
        with self.assertRaisesRegex(ValueError, "closure mismatch"):
            self.manifest()

    def test_wrong_candidate_commit_rejected(self):
        self.package("SharpLink.Runtime", commit="b" * 40)
        with self.assertRaisesRegex(ValueError, "commit does not match"):
            self.manifest()

    def test_symbol_identity_mismatch_rejected(self):
        self.package("SharpLink.Runtime", symbol_version="2.0.3")
        with self.assertRaisesRegex(ValueError, "nuspec identity"):
            self.manifest()

    def test_dependency_outside_candidate_closure_rejected(self):
        for name, version in (("SharpLink.Runtime", "2.0.3"), ("SharpLink.Generator", VERSION)):
            with self.subTest(name=name):
                self.package("SharpLink.Client", dependency=f'<dependency id="{name}" version="{version}" />')
                with self.assertRaisesRegex(ValueError, "outside candidate closure"):
                    self.manifest()

    def test_candidate_internal_dependency_accepted(self):
        self.package("SharpLink.Client", dependency=f'<dependency id="SharpLink.Runtime" version="{VERSION}" />')
        self.assertEqual(18, len(self.manifest()))

    def test_stale_cache_bytes_rejected(self):
        assets, cache = self.assets()
        (cache / f"sharplink.runtime/{VERSION}/sharplink.runtime.{VERSION}.nupkg").write_bytes(b"old package")
        with self.assertRaisesRegex(ValueError, "bytes differ"):
            smoke.verify_assets(assets, cache, self.manifest(), VERSION)

    def test_assets_hash_mismatch_rejected(self):
        assets, cache = self.assets()
        assets["libraries"][f"SharpLink.Runtime/{VERSION}"]["sha512"] = "wrong"
        with self.assertRaisesRegex(ValueError, "bytes differ"):
            smoke.verify_assets(assets, cache, self.manifest(), VERSION)

    def test_non_candidate_resolved_version_rejected(self):
        assets, cache = self.assets()
        assets["libraries"]["SharpLink.Runtime/2.0.3"] = assets["libraries"].pop(f"SharpLink.Runtime/{VERSION}")
        with self.assertRaisesRegex(ValueError, "non-candidate"):
            smoke.verify_assets(assets, cache, self.manifest(), VERSION)

    def test_project_dependency_rejected(self):
        assets, cache = self.assets()
        assets["libraries"][f"SharpLink.Runtime/{VERSION}"]["type"] = "project"
        with self.assertRaisesRegex(ValueError, "Non-package"):
            smoke.verify_assets(assets, cache, self.manifest(), VERSION)

    def test_missing_required_consumed_package_rejected(self):
        assets, cache = self.assets()
        del assets["libraries"][f"SharpLink.GenerationControl/{VERSION}"]
        with self.assertRaisesRegex(ValueError, "Missing required"):
            smoke.verify_assets(assets, cache, self.manifest(), VERSION)

    def test_expected_publish_modes(self):
        for _, mode, aot in smoke.CONSUMERS:
            smoke.verify_properties(self.properties(aot, mode), mode, aot)

    def test_aot_disabled_or_wrong_trim_mode_rejected(self):
        with self.assertRaisesRegex(ValueError, "PublishAot"):
            smoke.verify_properties(self.properties(), "full", True)
        with self.assertRaisesRegex(ValueError, "TrimMode"):
            smoke.verify_properties(self.properties(), "partial", False)

    def test_warning_error_gates_cannot_be_disabled(self):
        for key in ("PublishTrimmed", "TreatWarningsAsErrors", "ILLinkTreatWarningsAsErrors", "IlcTreatWarningsAsErrors"):
            with self.subTest(property=key):
                evaluation = self.properties()
                evaluation["Properties"][key] = "false"
                with self.assertRaisesRegex(ValueError, key):
                    smoke.verify_properties(evaluation, "full", False)

    def test_evaluated_project_reference_rejected(self):
        evaluation = self.properties()
        evaluation["Items"]["ProjectReference"] = [{"Identity": "../../src/SharpLink.Runtime.csproj"}]
        with self.assertRaisesRegex(ValueError, "ProjectReference"):
            smoke.verify_properties(evaluation, "full", False)

    def test_native_rids_only(self):
        for host, machine, rid in (("Linux", "x86_64", "linux-x64"), ("Windows", "AMD64", "win-x64"),
                                   ("Darwin", "arm64", "osx-arm64")):
            self.assertEqual(rid, smoke.native_rid(host, machine))
        with self.assertRaisesRegex(ValueError, "Unsupported native"):
            smoke.native_rid("Darwin", "x86_64")

    def test_failed_command_preserves_output_and_exit_code(self):
        evidence = {"commands": []}
        log = self.directory / "negative.log"
        with self.assertRaisesRegex(RuntimeError, "Command failed \\(17\\)"):
            smoke.run([sys.executable, "-c", "print('preserved failure'); raise SystemExit(17)"],
                      log, os.environ.copy(), evidence)
        self.assertIn("preserved failure", log.read_text())
        self.assertEqual(17, evidence["commands"][0]["exitCode"])

    def test_cross_host_cli_rejected_before_any_restore(self):
        output = self.directory / "evidence"
        with patch.object(sys, "argv", ["smoke", "--rid", "win-x64", "--output", str(output)]), \
                patch.object(smoke, "native_rid", return_value="linux-x64"), \
                patch.object(smoke, "run") as run:
            self.assertEqual(1, smoke.main())
        run.assert_not_called()
        evidence = json.loads(next(output.rglob("evidence.json")).read_text())
        self.assertEqual("failed", evidence["status"])
        self.assertIn("Refusing cross-compiled execution", evidence["error"])

    def test_release_and_reusable_workflow_wiring(self):
        release = (ROOT / ".github/workflows/release-gate.yml").read_text()
        matrix = (ROOT / ".github/workflows/packaged-trim-smoke.yml").read_text()
        action = (ROOT / ".github/actions/package-smoke/action.yml").read_text()
        self.assertIn("packaged-trim-aot:\n    needs: pack\n    uses: ./.github/workflows/packaged-trim-smoke.yml", release)
        self.assertIn("package-artifact: nuget-packages", release)
        summary = release.split("  release-summary:", 1)[1].split("  publish-nuget:", 1)[0]
        self.assertIn("packaged-trim-aot", summary)
        self.assertIn("job['result'] != 'success'", summary)
        for rid in ("linux-x64", "win-x64", "osx-arm64"):
            self.assertEqual(1, matrix.count("rid: " + rid))
        consumers = matrix.split("  packaged-consumers:", 1)[1]
        self.assertNotIn("dotnet pack", consumers)
        self.assertIn("inputs.package-artifact || 'packaged-trim-candidate'", consumers)
        self.assertLess(consumers.index("git config --global core.autocrlf false"), consumers.index("- name: Checkout"))
        self.assertIn("packaged-only: 'true'", consumers)
        self.assertIn("VerifySharpLinkLateTrimConfiguration", action)
        self.assertIn("run-quickstart-package-smoke.sh", action)
        self.assertIn("run-packaged-trim-smoke.py", action)
        self.assertIn("run-project-reference-trim-smoke.sh", (ROOT / ".github/workflows/pr-package-smoke.yml").read_text())


if __name__ == "__main__":
    unittest.main()
