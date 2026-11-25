"""Synthetic report checks for the offline tool; no performance process is started."""

import json
from pathlib import Path
import tempfile
import unittest

import d6_paired_steady as gate


def report(arm, p99=20, remote=100_000):
    result = dict(Operation="oneway32", Concurrency=8, WorkerCount=8, RecorderMode="formal", FormalComparable=True,
                  RecorderVersion=gate.RECORDER, Failure=0, Success=100_000, SampleCount=100_000,
                  OperationsCompleted=100_000, OperationsStartedDuringMeasurement=100_000,
                  MaximumSampleCapacity=30_000_000, StopwatchFrequency=10_000_000,
                  WarmupDurationSeconds=11, MeasurementDurationSeconds=21, DrainDurationSeconds=0.1,
                  P50Us=10, P95Us=15, P99Us=p99, P999Us=40, Qps=4_760, SendQueueBackpressureRetries=0,
                  OneWay32BusinessCompleted=remote, OneWay32ConfirmationSeconds=0.1,
                  OneWay32SettledEvidence=dict(CpuMilliseconds=840 if arm == "N" else 920,
                                               AllocatedBytes=10_000, Gen0Collections=1))
    return dict(Workload="SharpLink.LoadTest", SchemaVersion=1, SourceCommit=arm,
                OperatingSystem="Linux", OsArchitecture="X64", ProcessArchitecture="X64", Runtime=".NET 10.0.15",
                ProcessorCount=8, ServerGc=False, GcLatencyMode="Interactive", Results=[result],
                Configuration=dict(Mode=0, Transport=4, Operation="oneway32", ConcurrencyConfig=[8], PayloadSize=32,
                                   PerformanceProfile=0, RecordingMode=1, MinConnections=1, MaxConnections=1,
                                   WarmupSeconds=11, DurationSeconds=21, DrainTimeoutSeconds=5, MetricsPort=0,
                                   DetailedSharedMemoryEvidence=False, TailObserver=False, UseStaticEndpoints=False,
                                   UseDynamicResolver=False, PayloadPattern="compressible", RequestTimeoutMode="default",
                                   AdmissionMode="disabled", MaximumRecordedOperations=30_000_000,
                                   HeartbeatIntervalSeconds=120, HeartbeatCheckIntervalSeconds=120, HeartbeatTimeoutSeconds=600))


class PairedQualificationTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.path = Path(temporary.name)
        self.cell = gate.sample_plan("sharedmemory", 8, "oneway32")

    def populate(self, phase, candidate_p99=18):
        for pair in gate.sample_names(phase, self.cell[phase]):
            for arm, name in pair:
                (self.path / name).write_text(json.dumps(report(arm, candidate_p99 if arm == "N" else 20)))

    def test_plan_has_eight_baseline_samples_then_sixteen_symmetric_samples(self):
        self.assertEqual(self.cell["aa"], ["PP"] * 4)
        self.assertEqual("".join(self.cell["ab"]), "PNNPNPPN" * 2)

    def test_paced_oneway_checks_effective_load_without_changing_default(self):
        plan = gate.sample_plan("sharedmemory", 8, "oneway32", oneway_rate=80_000)
        self.assertNotIn("oneway32-rate", self.cell["arguments"])
        self.assertEqual(plan["arguments"]["oneway32-rate"], 80_000)
        self.assertEqual(plan["aa_maximum_deviation"]["ops_per_s"], .03)
        item = report("P")
        item["Configuration"]["OneWay32Rate"] = 80_000
        path = self.path / "controlled.json"
        path.write_text(json.dumps(item))
        with self.assertRaisesRegex(ValueError, "outside the frozen range"):
            gate.read_sample(path, "P", "P", plan, set())
        item["Results"][0]["Qps"] = 78_000
        path.write_text(json.dumps(item))
        sample = gate.read_sample(path, "P", "P", plan, set())
        self.assertEqual(sample["ops_per_s"], 78_000)
        aa = gate.aa_decision(plan, [(sample, sample)] * 4)
        with self.assertRaisesRegex(ValueError, "did not offer the same rate"):
            gate.ab_decision(aa, [(sample, dict(sample, arm="N", ops_per_s=73_000))] * 8)

    def test_unstable_aa_stops_before_opening_any_ab_result(self):
        self.populate("aa")
        (self.path / "aa-01-2-P.json").write_text(json.dumps(report("P", 22)))
        result = gate.evaluate(self.path, self.cell, "P", "N")
        self.assertEqual(result["aa"]["status"], "INCONCLUSIVE")
        self.assertNotIn("ab", result)

    def test_partial_aa_is_valid_only_when_it_proves_the_stop(self):
        left = self.path / "aa-01-1-P.json"
        right = self.path / "aa-01-2-P.json"
        left.write_text(json.dumps(report("P")))
        right.write_text(json.dumps(report("P")))
        with self.assertRaisesRegex(ValueError, "without a first proven threshold breach"):
            gate.evaluate(self.path, self.cell, "P", "N")

        right.write_text(json.dumps(report("P", 22)))
        result = gate.evaluate(self.path, self.cell, "P", "N")
        self.assertEqual(result["aa"]["status"], "INCONCLUSIVE")
        self.assertEqual((result["aa_pairs_completed"], result["aa_pairs_required"]), (1, 4))
        self.assertNotIn("ab", result)

        # A failed A/A prefix cannot bless samples taken after the stop.
        self.populate("ab")
        with self.assertRaisesRegex(ValueError, "ab: missing or extra raw sample"):
            gate.evaluate(self.path, self.cell, "P", "N")

    def test_lost_one_way_completion_is_not_a_valid_sample(self):
        self.populate("aa")
        (self.path / "aa-01-2-P.json").write_text(json.dumps(report("P", remote=99_999)))
        with self.assertRaisesRegex(ValueError, "remote OneWay work did not match"):
            gate.evaluate(self.path, self.cell, "P")

    def test_ab_uses_the_same_ratio_direction_even_in_np_order(self):
        self.populate("aa")
        self.populate("ab", candidate_p99=23)
        result = gate.evaluate(self.path, self.cell, "P", "N")
        self.assertEqual(result["ab"]["status"], "REGRESSION")
        self.assertAlmostEqual(result["ab"]["paired"]["p99_us"]["ratio"], 1.15)

    def test_stronger_candidate_qualifies_only_the_measured_cell(self):
        self.populate("aa")
        self.populate("ab")
        result = gate.evaluate(self.path, self.cell, "P", "N")
        self.assertEqual(result["ab"]["status"], "SUPPORTS_NEXT_STAGE")
        self.assertIn("same-machine execution must be established", result["scope"])


if __name__ == "__main__":
    unittest.main()
