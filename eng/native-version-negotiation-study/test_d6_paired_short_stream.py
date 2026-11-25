"""Check phase grading and stop before any measured N trial on bad A/A evidence."""

import json
from pathlib import Path
import tempfile
import unittest

from d6_paired_short_stream import MAXIMUM_SAMPLES, read_short_stream, stream_plan
from d6_paired_steady import RECORDER, evaluate, sample_names
from d6_run_steady import run_phases


def report(arm, first_p99=12, n=100_000, *, phase_count=None):
    owners = dict(Count=n, P50Us=17, P99Us=24, P999Us=40)
    result = dict(Operation="duplex", Concurrency=8, WorkerCount=8, RecorderMode="formal", FormalComparable=True,
                  RecorderVersion=RECORDER, Failure=0, ValidationFailure=0, Cancelled=0, Success=n, SampleCount=n,
                  OperationsCompleted=n, OperationsStartedDuringMeasurement=n,
                  MaximumSampleCapacity=MAXIMUM_SAMPLES, StopwatchFrequency=10_000_000, ValidatedMessages=n * 32,
                  WarmupDurationSeconds=11, MeasurementDurationSeconds=61, DrainDurationSeconds=0.1,
                  P50Us=17, P95Us=20, P99Us=24, P999Us=40, Qps=1800,
                  ShortStreamPhases=dict(FirstProgressMeaning="first response item",
                                         FirstProgress=dict(Count=n if phase_count is None else phase_count,
                                                            P50Us=8, P99Us=first_p99, P999Us=20),
                                         BusinessComplete=dict(Count=n, P50Us=15, P99Us=22, P999Us=35),
                                         ApplicationOwnersExited=owners),
                  Evidence=dict(CpuMilliseconds=6200, AllocatedBytes=200_000_000, Gen0Collections=10))
    return dict(Workload="SharpLink.StreamLoadTest", SchemaVersion=1, SourceCommit=arm,
                OperatingSystem="Linux", OsArchitecture="X64", ProcessArchitecture="X64", Runtime=".NET 10.0.15",
                ProcessorCount=8, ServerGc=False, GcLatencyMode="Interactive", Results=[result],
                Configuration=dict(Mode=0, Transport=4, Operation="duplex", ConcurrencyConfig=[8], StreamSize=32,
                                   ShortStreamPhases=True, VerifyShortStreams=False, PerformanceProfile=0,
                                   RecordingMode=1, MinConnections=1, MaxConnections=1, WarmupSeconds=11,
                                   DurationSeconds=61, DrainTimeoutSeconds=5, MaximumRecordedOperations=MAXIMUM_SAMPLES,
                                   BindIp="127.0.0.1", DetailedSharedMemoryEvidence=False, ConsumerDelayMilliseconds=0,
                                   EarlyBreakAfter=0, PauseAfter=0, PauseMilliseconds=0, HeartbeatIntervalSeconds=120,
                                   HeartbeatCheckIntervalSeconds=120, HeartbeatTimeoutSeconds=600))


class PairedShortStreamTests(unittest.TestCase):
    def setUp(self):
        base = tempfile.TemporaryDirectory()
        self.addCleanup(base.cleanup)
        self.root = Path(base.name)
        self.cell = stream_plan("sharedmemory", 8, "duplex", 32)

    def test_first_progress_drift_prevents_even_first_n_trial(self):
        seen = []

        def launch(cell, phase, index, arm, path):
            seen.append((phase, arm))
            path.write_text(json.dumps(report(arm, first_p99=14 if path.name.startswith("aa-01-2") else 12)))

        rc = run_phases(self.root, [self.cell], {"P": "P", "N": "N"}, launch, reader=read_short_stream)
        self.assertEqual(rc, "INCONCLUSIVE_AA")
        self.assertEqual(seen, [("aa", "P")] * 2)
        result = evaluate(self.root / self.cell["operation"], self.cell, "P", "N", reader=read_short_stream)
        self.assertEqual((result["aa_pairs_completed"], result["aa_pairs_required"]), (1, 4))

    def test_reliable_early_regression_cannot_be_hidden_in_total_latency(self):
        for phase in ("aa", "ab"):
            for pair in sample_names(phase, self.cell[phase]):
                for arm, name in pair:
                    (self.root / name).write_text(json.dumps(report(arm, first_p99=15 if arm == "N" else 12)))
        out = evaluate(self.root, self.cell, "P", "N", reader=read_short_stream)
        self.assertEqual(out["ab"]["status"], "REGRESSION")
        self.assertEqual(out["ab"]["regressions"], ["first_progress_p99_us"])

    def test_below_completion_floor_fails_even_with_full_phase_population(self):
        path = self.root / "short.json"
        path.write_text(json.dumps(report("P", n=99_999)))
        with self.assertRaisesRegex(ValueError, "insufficient completions"):
            read_short_stream(path, "P", "P", self.cell, set())

    def test_phase_cannot_hide_missing_successes(self):
        path = self.root / "short.json"
        path.write_text(json.dumps(report("P", phase_count=99_999)))
        with self.assertRaisesRegex(ValueError, "phase population"):
            read_short_stream(path, "P", "P", self.cell, set())


if __name__ == "__main__":
    unittest.main()
