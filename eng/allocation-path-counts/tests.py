import contextlib
import copy
import importlib.util
import io
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location("paths", Path(__file__).with_name("run.py"))
paths = importlib.util.module_from_spec(spec)
spec.loader.exec_module(paths)
fixture_spec = importlib.util.spec_from_file_location("gate_fixtures", Path(__file__).parents[1] / "allocation-spread-diagnostics/tests.py")
fixtures = importlib.util.module_from_spec(fixture_spec)
fixture_spec.loader.exec_module(fixtures)


def fixture(injected=0):
    report = fixtures.fixture(injected)
    report["observationCompiled"] = True
    for case in report["cases"]:
        for sample in case["samples"]:
            zero = [{"name": name, "calls": 0, "completed": 0, "incomplete": 0, "throws": 0,
                     "rootCalls": 0, "rootBytes": 0} for name in sorted(paths.KINDS)]
            after = copy.deepcopy(zero)
            for row in after:
                if row["name"] in ("RpcStart", "ServerRead"):
                    row.update(calls=sample["completedOperations"], incomplete=sample["completedOperations"],
                               rootCalls=sample["completedOperations"], rootBytes=sample["allocatedBytes"] // 4)
            sample["pathsBefore"] = {"stable": True, "firstThreadTouches": 1, "paths": zero}
            sample["pathsAfter"] = {"stable": True, "firstThreadTouches": 1, "paths": after}
    return report


class AttributionTests(unittest.TestCase):
    def test_all_samples_and_exact_residual_are_retained(self):
        report = fixture()
        result = paths.measured_paths(report)
        self.assertEqual(sum(len(case["samples"]) for case in result), 10)
        for case in result:
            for row in case["samples"]:
                self.assertEqual(row["callBoundaryBytes"] + row["unattributedResidualBytes"], row["processBytes"])
                self.assertTrue(row["stableSnapshots"])

    def test_negative_residual_and_unstable_snapshot_are_not_hidden(self):
        report = fixture()
        sample = report["cases"][0]["samples"][0]
        sample["pathsBefore"]["stable"] = False
        row = next(row for row in sample["pathsAfter"]["paths"] if row["name"] == "ServerRead")
        row["rootBytes"] = sample["allocatedBytes"] + 100
        result = paths.measured_paths(report)[0]["samples"][0]
        self.assertFalse(result["stableSnapshots"])
        self.assertLess(result["unattributedResidualBytes"], 0)
        self.assertFalse(result["nonnegativeResidual"])

    def test_incomplete_and_malformed_counts_fail_closed(self):
        for change in ("missing", "root", "sum", "denominator", "regression"):
            report = fixture()
            sample = report["cases"][0]["samples"][0]
            row = next(row for row in sample["pathsAfter"]["paths"] if row["name"] == "RpcStart")
            if change == "missing": sample["pathsAfter"]["paths"].pop()
            if change == "root": row["rootCalls"] = 0
            if change == "sum": row["completed"] += 1
            if change == "denominator": sample["completedOperations"] -= 1
            if change == "regression": row["rootBytes"] = -1
            with self.subTest(change=change), self.assertRaises(ValueError):
                paths.measured_paths(report)

    def test_plain_report_cannot_claim_path_attribution(self):
        with self.assertRaises(ValueError):
            paths.measured_paths(fixtures.fixture())


class OrchestrationTests(unittest.TestCase):
    def execute(self, calibration_fails=False, plain_fails=False):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        output = Path(temporary.name) / "evidence"
        labels = []
        baseline = Path(temporary.name) / "baseline"

        def fake_git(root, *args):
            if args[0] == "rev-parse": return paths.BASE
            if args[:2] == ("diff", "--name-only"): return "\n".join(sorted(paths.ALLOWED_OVERLAY))
            return ""

        def fake_run(command, directory, label, executions, timeout=300):
            labels.append(label)
            (directory / (label + ".log")).write_text("Synthetic control-flow fixture only")
            if label in ("counter-self-test", "forced-paths"):
                paths.gate.write(directory / (label + ".json"), {"passed": not calibration_fails})
                return 1 if calibration_fails else 0
            if label in ("plain", "observed", "observed-negative"):
                report = fixture(512 if label == "observed-negative" else 0)
                if label == "plain":
                    report.pop("observationCompiled")
                    if plain_fails:
                        case = report["cases"][0]
                        sample = case["samples"][-1]
                        sample["bytesPerOperation"] += 51
                        sample["allocatedBytes"] = sample["bytesPerOperation"] * sample["completedOperations"]
                        case.update(spreadBytesPerOperation=51, maxBytesPerOperationObserved=sample["bytesPerOperation"], passed=False)
                        report["passed"] = False
                paths.gate.write(directory / (label + ".json"), report)
                return 0 if report["passed"] else 1
            return 0

        with patch.object(paths, "git", side_effect=fake_git), patch.object(paths.gate, "run", side_effect=fake_run), \
                patch.object(paths.sys, "argv", ["run.py", "--baseline", str(baseline), "--output", str(output)]), \
                contextlib.redirect_stdout(io.StringIO()):
            status = paths.main()
        return status, labels, json.loads((output / "summary.json").read_text()), output

    def test_fixed_plan_and_failing_baseline_are_preserved(self):
        status, labels, summary, output = self.execute(plain_fails=True)
        self.assertEqual(status, 1)
        self.assertTrue(summary["complete"], summary)
        self.assertTrue(summary["observed"]["passed"])
        self.assertFalse(summary["plain"]["passed"])
        self.assertEqual(labels, ["dotnet-info", "cpu", "counter-self-test", "forced-paths", "plain", "observed", "observed-negative"])
        self.assertEqual(len(json.loads((output / "observed-paths.json").read_text())[0]["samples"]), 5)

    def test_calibration_failure_stops_before_contrast(self):
        status, labels, summary, _ = self.execute(calibration_fails=True)
        self.assertEqual(status, 1)
        self.assertFalse(summary["complete"])
        self.assertNotIn("plain", labels)


if __name__ == "__main__":
    unittest.main()
