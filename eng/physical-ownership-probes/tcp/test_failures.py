#!/usr/bin/env python3
"""Regression checks for failed TCP process evidence; no benchmark runs."""
import importlib.util
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

SOURCE = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location("run_matrix", SOURCE / "run_matrix.py")
matrix = importlib.util.module_from_spec(spec)
spec.loader.exec_module(matrix)


class FailedProcessEvidenceTests(unittest.TestCase):
    def test_timeout_preserves_host_raw_and_log(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            raw, log, host = (root / name for name in ("sample.json", "sample.log", "sample.host.json"))
            raw.write_text('{"writtenBeforeShutdown":true}')
            command = ["dotnet", "probe.dll"]
            def timeout(*args, **kwargs):
                kwargs["stdout"].write("shutdown still pending\n")
                raise subprocess.TimeoutExpired(command, 99)
            with patch.object(matrix.subprocess, "run", side_effect=timeout):
                with self.assertRaises(subprocess.TimeoutExpired):
                    matrix.execute_sample(command, log, host, "candidate", 3, {}, 99)
            data = json.loads(host.read_text())
            self.assertEqual("timed-out", data["status"])
            self.assertIsNone(data["exitCode"])
            self.assertEqual(command, data["command"])
            self.assertEqual(("candidate", 3, 99), (data["variant"], data["round"], data["timeoutSeconds"]))
            self.assertIn("timed out", data["reason"])
            self.assertIn("timestampUtc", data["before"])
            self.assertIn("timestampUtc", data["after"])
            self.assertGreaterEqual(data["wallSeconds"], 0)
            self.assertEqual('{"writtenBeforeShutdown":true}', raw.read_text())
            self.assertEqual("shutdown still pending\n", log.read_text())

    def test_nonzero_exit_retains_failure_reason(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            with patch.object(matrix.subprocess, "run", return_value=subprocess.CompletedProcess(["probe"], 1)):
                code = matrix.execute_sample(["probe"], root / "sample.log", root / "sample.host.json", "baseline", 1, {}, 99)
            host = json.loads((root / "sample.host.json").read_text())
            self.assertEqual((1, "failed", 1), (code, host["status"], host["exitCode"]))
            self.assertEqual("Process exited with code 1", host["reason"])

    def sample(self, root, repetition, variant, exit_code=0, status="success", raw=True, host=True):
        name = f"static-intercepted-n1-c1-r{repetition}-{variant}"
        if raw:
            value = 1 if variant == "baseline" else 2
            data = dict(mode="static-intercepted", itemCount=1, workers=1,
                        operationsPerSecond=value, p50Us=value, p99Us=value, p999Us=value,
                        allocatedBytesPerOperation=value, cpuUsPerOperation=value)
            (root / (name + ".json")).write_text(json.dumps(data))
        if host:
            (root / (name + ".host.json")).write_text(json.dumps({"exitCode": exit_code, "status": status, "reason": status}))

    def summarize(self, root):
        return subprocess.run([sys.executable, str(SOURCE / "summarize.py"), "--input", str(root)],
                              capture_output=True, text=True)

    def test_failed_after_sample_timeout_and_missing_host_are_not_pairs(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            runs = root / "runs"
            runs.mkdir()
            for repetition in range(1, 5):
                self.sample(runs, repetition, "baseline")
            self.sample(runs, 1, "candidate")
            self.sample(runs, 2, "candidate", exit_code=1, status="failed")
            self.sample(runs, 3, "candidate", exit_code=None, status="timed-out", raw=False)
            self.sample(runs, 4, "candidate", host=False)
            originals = {p.name: p.read_text() for p in runs.iterdir()}
            result = self.summarize(root)
            self.assertEqual(0, result.returncode, result.stderr)
            summary = json.loads((root / "summary.json").read_text())
            integrity = json.loads((root / "summary-integrity.json").read_text())
            self.assertEqual(1, summary[0]["pairs"])
            self.assertEqual([2.0], summary[0]["metrics"]["operationsPerSecond"]["pairedRatios"])
            self.assertEqual(7, integrity["rawSamples"])
            self.assertEqual(5, integrity["completedRawSamples"])
            self.assertEqual([2, 3, 4], [r["round"] for r in integrity["incompletePairs"]])
            self.assertEqual(["failed", "timed-out", "missing-host"], [r["status"] for r in integrity["failedSamples"]])
            self.assertEqual([1, None, None], [r["exitCode"] for r in integrity["failedSamples"]])
            self.assertEqual(originals, {p.name: p.read_text() for p in runs.iterdir()})

    def test_zero_successful_pairs_still_writes_failure_inventory(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            runs = root / "runs"
            runs.mkdir()
            self.sample(runs, 1, "baseline", exit_code=None, status="timed-out", raw=False)
            result = self.summarize(root)
            self.assertNotEqual(0, result.returncode)
            self.assertEqual([], json.loads((root / "summary.json").read_text()))
            integrity = json.loads((root / "summary-integrity.json").read_text())
            self.assertEqual(0, integrity["completedRawSamples"])
            self.assertEqual("timed-out", integrity["failedSamples"][0]["status"])
            self.assertEqual([{"mode": "static-intercepted", "items": 1, "workers": 1, "round": 1}], integrity["incompletePairs"])


if __name__ == "__main__":
    unittest.main()
