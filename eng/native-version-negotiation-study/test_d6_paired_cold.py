"""No-timing cold-report checks: startup, connection, Prepare and comparison populations."""

import json
from pathlib import Path
import tempfile
import unittest

from d6_paired_cold import FRESH_PER_TRIAL, TOTAL_PER_ARM, cold_plan, read_cold
from d6_paired_steady import evaluate, sample_names


def report(arm, fresh, *, start_total=1000, fresh_total=100):
    rows = []
    for index in range(1, fresh + 2):
        row = dict(Index=index, FirstInProcess=index == 1, StopwatchFrequency=1_000_000,
                   ConnectTicks=30, LocalHandleTicks=5, FirstRpcTicks=10,
                   ConnectToFirstSuccessTicks=start_total if index == 1 else fresh_total)
        if arm == "N":
            row["PrepareTicks"] = 12
        rows.append(row)
    return dict(SchemaVersion=1, Workload="SharpLink.LoadTest.ColdFirstRpc", SourceCommit=arm,
                Runtime=".NET 10.0.15", OperatingSystem="Linux", OsArchitecture="X64", ProcessArchitecture="X64",
                ProcessorCount=8, ServerGc=False, GcLatencyMode="Interactive", Results=rows,
                Configuration=dict(Mode=0, Transport=0, Operation="unary32", PayloadSize=32, PayloadPattern="compressible",
                                   PerformanceProfile=0, ConcurrencyConfig=[1], MinConnections=1, MaxConnections=1,
                                   BindIp="127.0.0.1", MetricsPort=0, WarmupSeconds=0, ColdFirstRpcSamples=fresh + 1,
                                   RequestTimeoutMode="default", AdmissionMode="disabled", DetailedSharedMemoryEvidence=False,
                                   TailObserver=False, UseStaticEndpoints=False, UseDynamicResolver=False,
                                   HeartbeatIntervalSeconds=120, HeartbeatCheckIntervalSeconds=120,
                                   HeartbeatTimeoutSeconds=600))


class PairedColdTests(unittest.TestCase):
    def setUp(self):
        temp = tempfile.TemporaryDirectory()
        self.addCleanup(temp.cleanup)
        self.root = Path(temp.name)
        self.cell = cold_plan("tcp")
        self.cell["fresh_sessions_per_trial"] = 10  # Small synthetic packets; production plan remains fixed.
        self.file = self.root / "sanity.json"

    def save(self, data):
        self.file.write_text(json.dumps(data))

    def test_plan_has_one_startup_and_exact_fresh_population(self):
        plan = cold_plan("tcp")
        self.assertEqual(plan["arguments"]["cold-first-rpc-samples"], 12_501)
        self.assertEqual(plan["fresh_sessions_per_trial"], FRESH_PER_TRIAL)
        self.assertEqual(plan["fresh_sessions_per_measured_arm"], TOTAL_PER_ARM)
        self.assertEqual(plan["fresh_sessions_per_measured_arm"], 100_000)

    def test_first_in_process_excluded_from_fresh_quantiles_and_prepare_not_zero(self):
        self.save(report("P", 10, start_total=1234, fresh_total=100))
        p = read_cold(self.file, "P", "P", self.cell, set())
        self.assertEqual(p["first_in_process_us"]["connect_to_first_success_us"], 1234)
        self.assertEqual(p["p99_us"], 100)
        self.assertNotIn("prepare_us", p["fresh_session_metrics"])
        self.save(report("N", 10))
        n = read_cold(self.file, "N", "N", self.cell, set())
        self.assertEqual(n["fresh_session_metrics"]["prepare_us"]["p99_us"], 12)

    def test_baseline_cannot_claim_zero_prepare_as_a_comparable_measurement(self):
        p = report("P", 10)
        p["Results"][1]["PrepareTicks"] = 0
        self.save(p)
        with self.assertRaisesRegex(ValueError, "has no explicit Prepare"):
            read_cold(self.file, "P", "P", self.cell, set())

    def test_partial_trial_cannot_enter_a_pair(self):
        data = report("P", 10)
        data["Results"].pop()
        self.save(data)
        with self.assertRaisesRegex(ValueError, "partial cold trial"):
            read_cold(self.file, "P", "P", self.cell, set())

    def test_reliable_fresh_session_tail_regression_is_reported_without_fake_cpu_or_allocations(self):
        for phase in ("aa", "ab"):
            for pair in sample_names(phase, self.cell[phase]):
                for arm, name in pair:
                    (self.root / name).write_text(json.dumps(report(arm, 10, fresh_total=107 if arm == "N" else 100)))
        decision = evaluate(self.root, self.cell, "P", "N", reader=read_cold)
        self.assertEqual(decision["ab"]["regressions"], ["p99_us"])
        self.assertNotIn("managed_b_per_op", decision["ab"])
        self.assertNotIn("cpu_us_per_op", decision["ab"]["paired"])


if __name__ == "__main__":
    unittest.main()
