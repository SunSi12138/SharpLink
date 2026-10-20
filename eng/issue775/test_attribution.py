"""Fail-closed tests for the diagnostic evidence reader; no .NET required."""
import math
import unittest
from collections import Counter

import attribution as runner
import attribution_report as audit


def rpc(arm="B", shape="unary"):
    n = 1000
    samples = list(range(n))
    quantiles = [samples[math.ceil(q*n)-1] / 1000 for q in (0.5, 0.99, 0.999)]
    return {"Schema": 1, "Passed": True, "Transport": "tcp", "Shape": shape,
            "Concurrency": 8, "Pool": 4, "Operations": n, "Warmup": 10000,
            "Dialect": "static-binding" if arm == "B" else "original-dev",
            "ProcessorCount": 2, "RoutePrefixBytes": 16, "BoundRoutes": 4,
            "PendingBinds": 0, "BindCount": 4 if arm == "B" else 0,
            "StopwatchFrequency": 1000000000,
            "Measurement": {"SenderCompleted": n, "ReceiverCompleted": n,
                "SenderLatencyTicks": samples, "ReceiverLatencyTicks": samples.copy(),
                "SenderP50P99P999Microseconds": quantiles, "ReceiverP50P99P999Microseconds": quantiles.copy(),
                "SenderSeconds": 1.0, "ReceiverSeconds": 1.0, "SenderOperationsPerSecond": n,
                "ReceiverOperationsPerSecond": n, "CpuMilliseconds": 600.0,
                "ProcessBytesPerCompletedOperation": 700.0, "DrainSeconds": 0.0}}


class AttributionTests(unittest.TestCase):
    def test_plan_agrees_and_is_balanced(self):
        self.assertEqual(runner.COMMITS, audit.COMMITS)
        self.assertEqual(runner.BLOCKS, audit.BLOCKS)
        self.assertEqual(tuple(map(runner.case_name, runner.CASES)), audit.CASES)
        self.assertEqual(tuple(map(runner.case_name, runner.FOCUS)), audit.FOCUS)
        self.assertEqual(runner.AA_ORDER, audit.AA_ORDER)
        for position in range(3):
            self.assertEqual(Counter(block[position] for block in runner.BLOCKS), Counter(A=2, B=2, C=2))
        plan = audit.expected_executions()
        self.assertEqual(len(plan), 26)
        self.assertEqual(sum(len(p[5]) for p in plan), 232)
        self.assertEqual(len({p[0] for p in plan}), 26)
        self.assertTrue(all(p[1] == "A" for p in plan if p[3] == "aa"))

    def test_valid_all_dialects(self):
        for arm in "ABC":
            audit.validate_rpc(rpc(arm), arm, "tcp-unary-c8-p4", 1000)

    def reject(self, change):
        value = rpc()
        change(value)
        with self.assertRaises(ValueError):
            audit.validate_rpc(value, "B", "tcp-unary-c8-p4", 1000)

    def test_reject_dialect(self):
        self.reject(lambda x: x.update(Dialect="original-dev"))

    def test_reject_rebinding(self):
        self.reject(lambda x: x.update(BindCount=5))

    def test_reject_pending_bind(self):
        self.reject(lambda x: x.update(PendingBinds=1))

    def test_reject_cpu_change(self):
        self.reject(lambda x: x.update(ProcessorCount=4))

    def test_reject_pool_change(self):
        self.reject(lambda x: x.update(Pool=1))

    def test_reject_prefix_change(self):
        self.reject(lambda x: x.update(RoutePrefixBytes=32))

    def test_reject_missing_receiver(self):
        self.reject(lambda x: x["Measurement"].update(ReceiverCompleted=999))

    def test_reject_missing_raw(self):
        self.reject(lambda x: x["Measurement"]["SenderLatencyTicks"].pop())

    def test_reject_quantile_rewrite(self):
        self.reject(lambda x: x["Measurement"]["ReceiverP50P99P999Microseconds"].__setitem__(1, 10))

    def test_reject_denominator(self):
        self.reject(lambda x: x["Measurement"].update(ReceiverOperationsPerSecond=1200))

    def test_reject_nan(self):
        self.reject(lambda x: x["Measurement"].update(CpuMilliseconds=float("nan")))

    def test_reject_drain_rewrite(self):
        self.reject(lambda x: x["Measurement"].update(DrainSeconds=1))

    def test_pairing_known_ratio(self):
        for n in (4, 6):
            result = audit.compare([1, 2, 3, 4, 5, 6][:n], [2, 4, 6, 8, 10, 12][:n])
            self.assertEqual(result["medianRatio"], 2)
            self.assertAlmostEqual(result["pairedGeometricRatio"], 2)
            for bound in result["paired95Ci"]:
                self.assertAlmostEqual(bound, 2)

    def test_reject_missing_pair(self):
        with self.assertRaises(ValueError):
            audit.compare([1, 2, 3], [1, 2, 3])

    def test_zero_drain_is_not_divided(self):
        result = audit.compare([0]*4, [0]*4)
        self.assertIsNone(result["medianRatio"])
        self.assertNotIn("paired95Ci", result)

    def test_budget_matches_original_policy(self):
        from pathlib import Path
        import json
        original = json.loads((Path(__file__).resolve().parents[1] / "perf/allocation-budgets.json").read_text())
        self.assertEqual(audit.BUDGETS, {k: (v["maxBytesPerOperation"], v["maxSpreadBytesPerOperation"])
                                       for k, v in original["cases"].items()})


if __name__ == "__main__":
    unittest.main()
