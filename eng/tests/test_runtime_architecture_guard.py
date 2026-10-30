#!/usr/bin/env python3
"""Exercise exact clock/yield exceptions without building or running managed tests."""

from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[2]
MAINTENANCE = Path("src/SharpLink.Server/SharpLinkServer.Maintenance.cs")
REPLACEMENT = Path("src/SharpLink.Server/SharpLinkServer.PreparedImplementationReplacement.cs")


class RuntimeArchitectureGuardTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        shutil.copytree(ROOT / "src", self.root / "src",
                        ignore=shutil.ignore_patterns("bin", "obj"))
        (self.root / "eng").mkdir()
        shutil.copyfile(ROOT / "eng/verify-runtime-architecture.py",
                        self.root / "eng/verify-runtime-architecture.py")

    def run_guard(self):
        return subprocess.run([sys.executable, str(self.root / "eng/verify-runtime-architecture.py")],
                              capture_output=True, text=True, check=False)

    def test_reviewed_source_passes(self):
        result = self.run_guard()
        self.assertEqual(result.returncode, 0, result.stderr)

    def test_second_maintenance_utc_site_is_rejected(self):
        path = self.root / MAINTENANCE
        path.write_text(path.read_text() + "\nvar forbiddenExtra = DateTimeOffset.UtcNow;\n")
        result = self.run_guard()
        self.assertNotEqual(result.returncode, 0)
        self.assertIn(f"Unreviewed UTC sites in {MAINTENANCE.as_posix()}: 2", result.stderr)

    def test_other_server_path_is_not_exempted(self):
        relative = "src/SharpLink.Server/UnreviewedClock.cs"
        (self.root / relative).write_text("var forbiddenClock = DateTime.UtcNow;\n")
        result = self.run_guard()
        self.assertNotEqual(result.returncode, 0)
        self.assertIn(f"Unreviewed UTC sites in {relative}: 1", result.stderr)

    def test_removed_maintenance_site_leaves_a_rejected_stale_exception(self):
        path = self.root / MAINTENANCE
        source = path.read_text()
        self.assertEqual(source.count("GetUtcNow()"), 1)
        path.write_text(source.replace("GetUtcNow()", "GetTimestamp()"))
        result = self.run_guard()
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("Stale UTC allowlist entries", result.stderr)
        self.assertIn(MAINTENANCE.as_posix(), result.stderr)

    def test_third_replacement_yield_site_is_rejected(self):
        path = self.root / REPLACEMENT
        path.write_text(path.read_text() + "\nawait Task.Yield();\n")
        result = self.run_guard()
        self.assertNotEqual(result.returncode, 0)
        self.assertIn(f"Unreviewed yield sites in {REPLACEMENT.as_posix()}: 3", result.stderr)

    def test_other_server_yield_path_is_not_exempted(self):
        relative = "src/SharpLink.Server/UnreviewedYield.cs"
        (self.root / relative).write_text("await Task.Yield();\n")
        result = self.run_guard()
        self.assertNotEqual(result.returncode, 0)
        self.assertIn(f"Unreviewed yield sites in {relative}: 1", result.stderr)

    def test_removed_replacement_yields_leave_a_rejected_stale_exception(self):
        path = self.root / REPLACEMENT
        source = path.read_text()
        self.assertEqual(source.count("await Task.Yield();"), 2)
        path.write_text(source.replace("await Task.Yield();", ""))
        result = self.run_guard()
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("Stale yield allowlist entries", result.stderr)
        self.assertIn(REPLACEMENT.as_posix(), result.stderr)


if __name__ == "__main__":
    unittest.main()
