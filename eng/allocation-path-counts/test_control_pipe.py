import copy
import importlib.util
from pathlib import Path
import unittest

spec = importlib.util.spec_from_file_location("control", Path(__file__).with_name("control_pipe.py"))
control = importlib.util.module_from_spec(spec)
spec.loader.exec_module(control)


def fixture():
    rows = []
    for api in ("memory-valuetask", "array-task"):
        for percent in (0, 25, 50, 75, 100):
            pending = 4000 * percent // 100
            left = dict(name="ControlPipeRead", calls=0, completed=0, incomplete=0, throws=0, rootCalls=0, rootBytes=0)
            right = dict(left, calls=4000, completed=4000-pending, incomplete=pending, rootCalls=4000, rootBytes=144*pending)
            rows.append(dict(api=api, pendingPercent=percent, operations=4000, incomplete=pending,
                rawCallBytes=144*pending, observedCallBytes=144*pending, processBytes=144*pending+168,
                unattributedResidualBytes=168, stableSnapshots=True, newObserverThreadTouches=0,
                before=dict(stable=True, firstThreadTouches=1, paths=[left]),
                after=dict(stable=True, firstThreadTouches=1, paths=[right])))
    return dict(diagnosticOnly=True, rpcGateReproduction=False, passed=True, failure=None,
        runtime="10.0.12", operations=4000, warmup=512, samples=rows,
        lifecycle=[dict(api=api, cancellationPreserved=True, reuseAfterCancellation=True,
                       disposalJoinedPendingRead=True, disposalOutcome="IOException") for api in ("memory-valuetask", "array-task")])


class ControlPipeTests(unittest.TestCase):
    def test_all_ten_fixed_mixtures_are_required(self):
        self.assertEqual(len(control.validate(fixture())), 2)
        for mutation in ("missing", "duplicate", "reordered"):
            report = fixture()
            if mutation == "missing": report["samples"].pop()
            if mutation == "duplicate": report["samples"][-1] = copy.deepcopy(report["samples"][0])
            if mutation == "reordered": report["samples"].reverse()
            with self.subTest(mutation=mutation), self.assertRaises(ValueError): control.validate(report)

    def test_bad_path_accounting_fails_without_normalization(self):
        for field in ("incomplete", "rawCallBytes", "observedCallBytes", "unattributedResidualBytes"):
            report = fixture(); report["samples"][0][field] += 1
            with self.subTest(field=field), self.assertRaises(ValueError): control.validate(report)
        report = fixture(); report["samples"][0]["after"]["paths"][0]["rootBytes"] += 1
        with self.assertRaises(ValueError): control.validate(report)

    def test_valid_alternative_cost_can_refute_prediction(self):
        report = fixture()
        for row in report["samples"]:
            if row["api"] == "array-task":
                row["rawCallBytes"] = row["observedCallBytes"] = row["incomplete"] * 72
                row["processBytes"] = row["rawCallBytes"] + row["unattributedResidualBytes"]
                row["after"]["paths"][0]["rootBytes"] = row["rawCallBytes"]
        result = control.validate(report)
        self.assertEqual(result[1]["fixedMixCallSpreadBytesPerRead"], 72)
        self.assertFalse(result[1]["supportsPredictedZeroOr144ByteModel"])
        self.assertTrue(result[0]["supportsPredictedZeroOr144ByteModel"])

    def test_unstable_failed_lifecycle_or_rpc_claim_cannot_pass(self):
        for mutation in ("unstable", "failed", "lifecycle", "rpc", "runtime"):
            report = fixture()
            if mutation == "unstable": report["samples"][0]["after"]["stable"] = False
            if mutation == "failed": report["passed"] = False
            if mutation == "lifecycle": report["lifecycle"][0]["reuseAfterCancellation"] = False
            if mutation == "rpc": report["rpcGateReproduction"] = True
            if mutation == "runtime": report["runtime"] = "10.0.2"
            with self.subTest(mutation=mutation), self.assertRaises(ValueError): control.validate(report)


if __name__ == "__main__": unittest.main()
