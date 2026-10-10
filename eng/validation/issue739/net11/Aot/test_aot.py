#!/usr/bin/env python3
"""Pure Python fail-closed and copy-origin tests; no NativeAOT performance evidence."""
import copy
import json
import os
from pathlib import Path
import tempfile
import unittest
import run_aot as aot


class AotTests(unittest.TestCase):
    def test_fixed_consumer_preserves_bytes_but_refreshes_old_timestamp(self):
        with tempfile.TemporaryDirectory() as tmp:
            source, destination = Path(tmp) / "A.pdb", Path(tmp) / "B.pdb"
            source.write_bytes(b"fixed A consumer symbols")
            destination.write_bytes(b"independently built B symbols")
            os.utime(source, (1, 1))
            os.utime(destination, (2, 2))
            aot.copy_fixed_consumer(source, destination)
            self.assertEqual(aot.sha(source), aot.sha(destination))
            self.assertGreater(destination.stat().st_mtime_ns, 2_000_000_000)
            self.assertEqual(source.stat().st_mtime_ns, 1_000_000_000)

    def test_exact_plan_and_origins(self):
        self.assertEqual(len(aot.CASES) * len(aot.SEQUENCE), 48)
        self.assertEqual(aot.SEQUENCE, [("ABBA", "A"), ("ABBA", "B"), ("ABBA", "B"), ("ABBA", "A"), ("AA", "A"), ("AA", "A")])
        self.assertEqual(aot.BUDGET, 1380)
        self.assertEqual(aot.assert_copy_origins()["sourceSha"], aot.jit.SOURCE)

    def test_no_reflection_reporting_or_suppression(self):
        sources = "\n".join(path.read_text() for path in aot.HERE.glob("*.cs"))
        for forbidden in ("Assembly.Location", "JsonSerializer", "GetMethod(", "UnconditionalSuppressMessage", "RequiresUnreferencedCode"):
            self.assertNotIn(forbidden, sources)
        project = (aot.HERE / "Aot.csproj").read_text()
        self.assertIn("<PublishAot>true</PublishAot>", project)
        self.assertIn('BeforeTargets="IlcCompile"', project)
        self.assertIn('AfterTargets="IlcCompile"', project)
        self.assertIn("NativeIdentity.RequireNative();", sources)

    def test_elf_guard(self):
        with tempfile.TemporaryDirectory() as tmp:
            exe = Path(tmp) / "native"
            exe.write_bytes(b"MZ" + bytes(18))
            with self.assertRaisesRegex(ValueError, "ELF64"):
                aot.elf_identity(exe)
            exe.write_bytes(b"\x7fELF\x02\x01" + bytes(12) + b"\x3e\x00")
            self.assertEqual(aot.elf_identity(exe)["sha256"], aot.sha(exe))

    def test_ilc_response_requires_pre_and_post_identity(self):
        with tempfile.TemporaryDirectory() as tmp:
            project = Path(tmp) / "Aot.csproj"
            project.touch()
            obj = project.parent / "obj/Release/net11.0" / aot.RID
            (obj / "native").mkdir(parents=True)
            driver = obj / "SharpLink.Benchmarks.dll"
            dependency = obj / "SharpLink.Runtime.dll"
            driver.write_bytes(b"fixed consumer")
            dependency.write_bytes(b"B production")
            rsp = obj / "native/SharpLink.Benchmarks.ilc.rsp"
            rsp.write_text(f'"{driver}"\n-r:"{dependency}"\n--optimize\n')
            expected = {path.name: aot.sha(path) for path in (driver, dependency)}
            snapshot = "\n".join(str(path) + "|" + aot.sha(path).upper() for path in (driver, dependency))
            for phase in ("before", "after"):
                (obj / ("issue739-ilc-inputs-" + phase + ".txt")).write_text(snapshot)
            proof = aot.capture_ilc_inputs(project, expected)
            self.assertEqual(len(proof["inputs"]), 2)
            dependency.write_bytes(b"mutated")
            with self.assertRaisesRegex(ValueError, "proven bytes"):
                aot.capture_ilc_inputs(project, expected)
            expected[dependency.name] = aot.sha(dependency)
            with self.assertRaisesRegex(ValueError, "not proved unchanged"):
                aot.capture_ilc_inputs(project, expected)

    def test_ilc_requires_expected_project_references(self):
        with tempfile.TemporaryDirectory() as tmp:
            project = Path(tmp) / "Aot.csproj"
            obj = project.parent / "obj/Release/net11.0" / aot.RID / "native"
            obj.mkdir(parents=True)
            (obj / "one.ilc.rsp").write_text("--optimize\n")
            with self.assertRaisesRegex(ValueError, "did not consume"):
                aot.capture_ilc_inputs(project, {"SharpLink.Runtime.dll": "bad"})

    def test_copy_preserves_production_runtime_package_references(self):
        runner = (aot.HERE / "run_aot.py").read_text()
        self.assertIn("jit.make_copy", runner)
        self.assertIn("jit.verify_metadata", runner)
        self.assertIn('"--no-build", "--no-restore"', runner)
        self.assertIn("require(not jit.compiler_commands(publish)", runner)
        self.assertNotIn("NoWarn", runner)


if __name__ == "__main__":
    unittest.main(verbosity=2)
