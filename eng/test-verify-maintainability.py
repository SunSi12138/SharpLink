#!/usr/bin/env python3
import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path


REPO_ROOT = Path(__file__).resolve().parents[1]
VERIFIER = REPO_ROOT / "eng" / "verify-maintainability.py"


def make_baseline(*allowances):
    return {
        "schemaVersion": 1,
        "sourceRef": "test-fixture",
        "rules": {
            "source": {"maxFileLoc": 800},
            "test": {"maxFileLoc": 1000},
        },
        "allowances": list(allowances),
    }


def allowance(domain, path, max_loc):
    return {
        "domain": domain,
        "path": path,
        "maxLoc": max_loc,
        "reason": "Test fixture allowance.",
    }


def make_report(*files):
    return {"files": list(files)}


def report_file(domain, path, loc):
    return {"domain": domain, "path": path, "loc": loc}


class MaintainabilityVerifierTests(unittest.TestCase):
    def run_verifier(self, report, baseline):
        with tempfile.TemporaryDirectory() as temp_dir:
            temp_path = Path(temp_dir)
            report_path = temp_path / "report.json"
            baseline_path = temp_path / "baseline.json"
            report_path.write_text(json.dumps(report), encoding="utf-8")
            baseline_path.write_text(json.dumps(baseline), encoding="utf-8")

            return subprocess.run(
                [
                    sys.executable,
                    str(VERIFIER),
                    "--report",
                    str(report_path),
                    "--baseline",
                    str(baseline_path),
                ],
                check=False,
                capture_output=True,
                text=True,
            )

    def assert_result(self, result, returncode, message, stream="stderr"):
        self.assertEqual(returncode, result.returncode, result.stdout + result.stderr)
        output = result.stderr if stream == "stderr" else result.stdout
        self.assertIn(message, output)

    def test_current_baseline_passes(self):
        result = self.run_verifier(
            make_report(
                report_file("source", "src/Normal.cs", 800),
                report_file("source", "src/Legacy.cs", 900),
                report_file("test", "test/ScenarioTests.cs", 1000),
            ),
            make_baseline(allowance("source", "src/Legacy.cs", 900)),
        )

        self.assert_result(result, 0, "Maintainability debt gate passed", stream="stdout")

    def test_new_oversized_source_file_fails(self):
        result = self.run_verifier(
            make_report(report_file("source", "src/NewDebt.cs", 801)),
            make_baseline(),
        )

        self.assert_result(result, 1, "threshold is 800 LOC and no baseline allowance exists")

    def test_existing_allowance_plus_one_fails(self):
        result = self.run_verifier(
            make_report(report_file("source", "src/Legacy.cs", 901)),
            make_baseline(allowance("source", "src/Legacy.cs", 900)),
        )

        self.assert_result(result, 1, "baseline allowance is 900 LOC")

    def test_stale_allowance_fails(self):
        result = self.run_verifier(
            make_report(),
            make_baseline(allowance("source", "src/Removed.cs", 900)),
        )

        self.assert_result(result, 1, "stale baseline allowance")

    def test_obsolete_allowance_fails(self):
        result = self.run_verifier(
            make_report(report_file("source", "src/Refactored.cs", 800)),
            make_baseline(allowance("source", "src/Refactored.cs", 900)),
        )

        self.assert_result(result, 1, "obsolete baseline allowance")

    def test_malformed_baseline_returns_configuration_error(self):
        baseline = make_baseline()
        baseline["rules"]["source"]["maxFileLoc"] = "800"

        result = self.run_verifier(make_report(), baseline)

        self.assert_result(result, 2, "Maintainability baseline configuration error")
        self.assertIn("rules.source.maxFileLoc must be an integer", result.stderr)


if __name__ == "__main__":
    unittest.main()
