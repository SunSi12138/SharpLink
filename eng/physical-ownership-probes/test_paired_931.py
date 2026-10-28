#!/usr/bin/env python3
"""Focused orchestration contract tests; no .NET build or benchmark is run."""
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location("paired_931", Path(__file__).with_name("paired_931.py"))
paired = importlib.util.module_from_spec(spec)
spec.loader.exec_module(paired)


class FrozenExperimentTests(unittest.TestCase):
    def test_exact_framework_versions_disable_roll_forward(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            config = root / "probe.runtimeconfig.json"
            config.write_text(json.dumps({"runtimeOptions": {"frameworks": [
                {"name": "Microsoft.NETCore.App", "version": "10.0.0"},
                {"name": "Microsoft.AspNetCore.App", "version": "10.0.0"}]}}))
            paired.pin_snapshot(root)
            options = json.loads(config.read_text())["runtimeOptions"]
            self.assertEqual("Disable", options["rollForward"])
            self.assertEqual(["10.0.13", "10.0.13"], [f["version"] for f in options["frameworks"]])
            options["frameworks"].pop()
            config.write_text(json.dumps({"runtimeOptions": options}))
            with self.assertRaisesRegex(ValueError, "Unexpected frameworks"):
                paired.pin_snapshot(root)

    def test_non_product_mismatch_and_missing_dependency_fail(self):
        with tempfile.TemporaryDirectory() as temp:
            b, c = Path(temp) / "b", Path(temp) / "c"
            b.mkdir(); c.mkdir()
            for root in (b, c):
                (root / "Shared.dll").write_bytes(b"same dependency")
                (root / "Probe.dll").write_bytes(b"same probe")
                (root / "SharpLink.Client.dll").write_bytes(root.name.encode())
            result = paired.verify_dependencies(b, c, ("Probe.dll",))
            self.assertNotEqual(result["baseline"]["SharpLink.Client.dll"], result["candidate"]["SharpLink.Client.dll"])
            (c / "Shared.dll").write_bytes(b"changed")
            with self.assertRaisesRegex(ValueError, "Non-product binary mismatch: Shared.dll"):
                paired.verify_dependencies(b, c, ("Probe.dll",))
            (c / "Shared.dll").unlink()
            with self.assertRaisesRegex(ValueError, "inventories differ"):
                paired.verify_dependencies(b, c, ("Probe.dll",))

    def test_friend_assembly_is_identical_even_with_product_prefix(self):
        with tempfile.TemporaryDirectory() as temp:
            b, c = Path(temp) / "b", Path(temp) / "c"
            b.mkdir(); c.mkdir()
            for root in (b, c):
                (root / "SharpLink.Benchmarks.dll").write_bytes(root.name.encode())
            with self.assertRaisesRegex(ValueError, "SharpLink.Benchmarks.dll"):
                paired.verify_dependencies(b, c, ("SharpLink.Benchmarks.dll",))

    def test_runtime_only_private_installation_rejects_patch_drift_and_sdk(self):
        root = Path("/private-runtime")
        listing = "\n".join(f"{name} 10.0.13 [{root}/shared/{name}]" for name in ("Microsoft.NETCore.App", "Microsoft.AspNetCore.App"))
        with patch.object(paired, "capture", side_effect=[listing, ""]):
            self.assertEqual(listing, paired.validate_runtime(root / "dotnet"))
        for actual, sdk in ((listing.replace("10.0.13", "10.0.14"), ""), (listing, "10.0.102")):
            with patch.object(paired, "capture", side_effect=[actual, sdk]):
                with self.assertRaisesRegex(ValueError, "runtime-only"):
                    paired.validate_runtime(root / "dotnet")
        with patch.object(paired, "capture", side_effect=[listing.replace("/private-runtime/shared", "/system/shared"), ""]):
            with self.assertRaisesRegex(ValueError, "escaped private"):
                paired.validate_runtime(root / "dotnet")

    def test_actual_process_rejects_wrong_runtime_and_framework_location(self):
        data = {"runtimeVersion": "10.0.13", "frameworkLocations": [
            "/private-runtime/shared/Microsoft.NETCore.App/10.0.13/System.Private.CoreLib.dll",
            "/private-runtime/shared/Microsoft.AspNetCore.App/10.0.13/Microsoft.AspNetCore.dll"]}
        paired.verify_process(data, Path("/private-runtime"))
        for bad in (dict(data, runtimeVersion="10.0.0"), dict(data, frameworkLocations=[]), dict(data, frameworkLocations=["/system/shared/Microsoft.NETCore.App/10.0.13/System.Private.CoreLib.dll"])):
            with self.assertRaises(ValueError):
                paired.verify_process(bad, Path("/private-runtime"))

    def test_dirty_or_wrong_source_rejected(self):
        for values in ((paired.BASELINE, " M src/file.cs"), (paired.CANDIDATE, "")):
            with patch.object(paired, "capture", side_effect=values):
                with self.assertRaisesRegex(ValueError, "Expected clean"):
                    paired.verify_source(Path("/source"), paired.BASELINE)
        with patch.object(paired, "capture", side_effect=[paired.BASELINE, ""]):
            paired.verify_source(Path("/source"), paired.BASELINE)


if __name__ == "__main__":
    unittest.main()
