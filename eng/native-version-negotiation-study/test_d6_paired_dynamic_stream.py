import gzip
import json
from pathlib import Path
import tempfile
import unittest

from d6_paired_dynamic_stream import PHASE_SAMPLE, dynamic_stream_plan, read_dynamic_stream
from d6_paired_short_stream import CRITICAL, MAXIMUM_SAMPLES, PRECISION


class DynamicShortStreamEvidenceTests(unittest.TestCase):
    def test_same_static_stream_gates_for_dynamic_business(self):
        cell = dynamic_stream_plan("sharedmemory", "duplex", 256, "forward")
        self.assertEqual(cell["concurrency"], 1)
        self.assertEqual(cell["arguments"], {"duration": 91, "warmup": 11})
        self.assertEqual(cell["critical"], CRITICAL)
        self.assertEqual(cell["aa_maximum_deviation"], PRECISION)
        self.assertEqual(cell["aa"], ["PP"] * 4)
        self.assertEqual(cell["ab"], ["PN", "NP", "NP", "PN"] * 2)
        self.assertNotEqual(cell["operation"], dynamic_stream_plan("sharedmemory", "duplex", 256, "none")["operation"])

    def test_every_stream_feeds_all_three_phase_distributions(self):
        cell = dynamic_stream_plan("tcp", "duplex", 32, "none")
        n = cell["minimum_completed"]
        report = dict(Workload="SharpLink.IntegrationTests.QualifiedDynamicShortStreams", SchemaVersion=1,
                      SourceCommit="a"*40, Transport="tcp", Family="duplex", Items=32, Interceptor="none",
                      RpcTracingEnabled=False, Runtime=".NET 10.0.15", OperatingSystem="Linux local-test",
                      OsArchitecture="X64", ProcessArchitecture="X64", ProcessorCount=4, ServerGc=False,
                      GcLatencyMode="Interactive", WarmupSeconds=11, MeasureSeconds=61, SampleCapacity=MAXIMUM_SAMPLES,
                      Completed=n, ValidatedItems=n*32, ServerInputItems=n*32, ServerInputExits=n,
                      StopwatchFrequency=1_000_000, ElapsedTicks=61_001_000, CpuTimeTicks=70_000_000,
                      ManagedAllocatedBytes=n*32, Gen0=3)
        normal = PHASE_SAMPLE.pack(12, 32, 52) * n
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / "trial.json"
            trace = path.with_suffix(".streams.bin.gz")
            path.write_text(json.dumps(report))
            trace.write_bytes(gzip.compress(normal))
            sample = read_dynamic_stream(path, "P", report["SourceCommit"], cell, set())
            self.assertEqual((sample["first_progress_p99_us"], sample["business_p99_us"], sample["p99_us"]), (12, 32, 52))
            self.assertEqual(sample["items"], n * 32)
            self.assertEqual(sample["managed_b_per_op"], 32)

            trace.write_bytes(gzip.compress(normal[:-PHASE_SAMPLE.size]))
            with self.assertRaisesRegex(ValueError, "trace population"):
                read_dynamic_stream(path, "P", report["SourceCommit"], cell, set())
            trace.write_bytes(gzip.compress(PHASE_SAMPLE.pack(12, 52, 32) + normal[PHASE_SAMPLE.size:]))
            with self.assertRaisesRegex(ValueError, "phase order"):
                read_dynamic_stream(path, "P", report["SourceCommit"], cell, set())

            path.write_text(json.dumps(report | {"ServerInputExits": n - 1}))
            with self.assertRaisesRegex(ValueError, "business counts"):
                read_dynamic_stream(path, "P", report["SourceCommit"], cell, set())


if __name__ == "__main__":
    unittest.main()
