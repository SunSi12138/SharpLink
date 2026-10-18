"""Run the real performance matrix shell with a labelled no-load dotnet shim."""
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest

SCRIPT = Path(__file__).resolve().parents[1] / "run-performance-matrix.sh"
DOTNET = """#!/usr/bin/env python3
import json, os, pathlib, sys
argv = sys.argv[1:]
command = argv[0]
with open(os.environ["MATRIX_EVENTS"], "a") as f:
    f.write(command + "\\n")
if command == "run":
    path = pathlib.Path(argv[argv.index("--json-output")+1])
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps({"mock": "only", "status": "completed"}))
elif command != "build":
    raise SystemExit("unexpected dotnet call " + repr(argv))
"""


class MatrixRepetitionValidation(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        root = Path(self.temp.name)
        (root / "test/SharpLink.LoadTest").mkdir(parents=True)
        (root / "test/SharpLink.StreamLoadTest").mkdir(parents=True)
        bindir = root / "bin"
        bindir.mkdir()
        dotnet = bindir / "dotnet"
        dotnet.write_text(DOTNET)
        dotnet.chmod(0o755)
        self.events = root / "events.txt"
        self.output = root / "out"
        self.output.mkdir()
        for name in ("dotnet-info.txt", "uname.txt", "cpu.txt"):
            (self.output / name).write_text("previous environment metadata")
        self.env = dict(os.environ, PATH=str(bindir) + ":" + os.environ["PATH"],
            MATRIX_EVENTS=str(self.events), SHARPLINK_MATRIX_ROOT=str(root),
            SHARPLINK_MATRIX_OUTPUT=str(self.output),
            SHARPLINK_MATRIX_RUNTIMES="jit", SHARPLINK_MATRIX_TRANSPORTS="tcp",
            SHARPLINK_MATRIX_PROFILES="balanced", SHARPLINK_MATRIX_PAYLOADS="0",
            SHARPLINK_MATRIX_CONCURRENCY="1", SHARPLINK_MATRIX_WORKLOADS="unary")

    def run_matrix(self, repetitions=None):
        env = dict(self.env)
        if repetitions is not None:
            env["SHARPLINK_MATRIX_REPETITIONS"] = repetitions
        else:
            env.pop("SHARPLINK_MATRIX_REPETITIONS", None)
        result = subprocess.run(["bash", str(SCRIPT)], env=env, text=True, capture_output=True)
        events = self.events.read_text().splitlines() if self.events.exists() else []
        return result, events

    def test_invalid_repetitions_fail_before_build_even_with_metadata_files(self):
        for value in ("0", "-1", "abc", "01"):
            with self.subTest(value=value):
                self.events.unlink(missing_ok=True)
                result, events = self.run_matrix(value)
                self.assertEqual(result.returncode, 2, result.stderr)
                self.assertEqual(events, [], "invalid input must not build or run")
                self.assertNotIn("matrix complete:", result.stdout)
                self.assertFalse(list(self.output.glob("*.json")))

    def test_valid_runs_create_exact_report_count(self):
        for value, repetitions in ((None, 1), ("", 1), ("1", 1), ("2", 2)):
            with self.subTest(value=value):
                self.events.unlink(missing_ok=True)
                for item in self.output.glob("*.json"):
                    item.unlink()
                result, events = self.run_matrix(value)
                self.assertEqual(result.returncode, 0, result.stderr)
                self.assertEqual(events.count("build"), 2)
                self.assertEqual(events.count("run"), 2 * repetitions)
                self.assertEqual(len(list(self.output.glob("*.json"))), 2 * repetitions)
                self.assertIn(f"reports={2 * repetitions}", result.stdout)


if __name__ == "__main__":
    unittest.main()
