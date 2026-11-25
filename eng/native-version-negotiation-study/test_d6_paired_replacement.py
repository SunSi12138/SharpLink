import gzip
import json
from pathlib import Path
import tempfile
import unittest

from d6_paired_replacement import CALL, MIN_CALLS, invocation, read_replacement, replace_plan, verify_dynamic_preflight, verify_no_tracing


class ReplacementReportTests(unittest.TestCase):
    def test_fixed_launch_selects_exactly_one_simple_method(self):
        tcp = invocation("tcp", measured=False)
        shm = invocation("sharedmemory", measured=True)
        self.assertTrue(tcp[1].endswith("SessionTcp"))
        self.assertTrue(shm[1].endswith("SessionSharedMemory"))
        self.assertEqual(tcp[tcp.index("--timeout") + 1], "45s")
        self.assertEqual(shm[shm.index("--timeout") + 1], "110s")

    def test_dynamic_preflight_reconciles_every_ordinary_shape(self):
        cmd = invocation("sharedmemory", measured=False, dynamic_streams=True)
        self.assertTrue(cmd[1].endswith("QualifiedDynamicShortStreamsUseThePreparedRouteSharedMemory"))
        rows = [f"\x1b[32mDYNAMIC_STREAM_VERIFIED transport=shared-memory family={shape} "
                f"items={size} interceptor=forward\x1b[m" for size in (32, 256) for shape in ("c2s", "s2c", "duplex")]
        verify_dynamic_preflight("\n".join(rows), "sharedmemory", "forward", 0)
        with self.assertRaisesRegex(ValueError, "incomplete real dynamic"):
            verify_dynamic_preflight("\n".join(rows[:-1]), "sharedmemory", "forward", 0)
        timed = invocation("tcp", measured=True, dynamic_streams=True)
        self.assertTrue(timed[1].endswith("QualifiedDynamicShortStreamsUseThePreparedRouteTcp"))
        self.assertEqual(timed[timed.index("--timeout") + 1], "130s")

    def test_workload_must_prove_no_rpc_tracing(self):
        verify_no_tracing("REPLACE_NO_RPC_TRACING\n")
        with self.assertRaisesRegex(ValueError, "tracing disabled"):
            verify_no_tracing("REPLACE_WORKLOAD_VERIFIED\n")

    def test_business_and_overlap_populations_are_both_measured(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "report.json"
            cadence = 10_000
            commit = "a" * 40
            report = dict(Workload="SharpLink.IntegrationTests.QualifiedReplacement", SchemaVersion=2, RpcTracingEnabled=False,
                          SourceCommit=commit, Transport="tcp", Cadence=cadence, Interceptor="none",
                          WarmupSeconds=11, MeasureSeconds=21,
                          Runtime=".NET 10.0.15", OperatingSystem="Linux local-test", OsArchitecture="X64",
                          ProcessArchitecture="X64", ProcessorCount=4, ServerGc=False, GcLatencyMode="Interactive",
                          StopwatchFrequency=50_000, ElapsedTicks=20 * MIN_CALLS + 10, CpuTimeTicks=875_000,
                          ManagedAllocatedBytes=20_000, Gen0=3, CompletedA=MIN_CALLS, CompletedB=MIN_CALLS,
                          Replacements=[dict(CompletedBefore=i, UpdateStartedTicks=i * 20 - 3,
                                             LoadCompleteTicks=i * 20 - 2, PublicCompleteTicks=i * 20 - 1,
                                             FirstNewCallStartedTicks=i * 20, FirstNewCallCompleteTicks=i * 20 + 4)
                                        for i in range(cadence, MIN_CALLS, cadence)])
            path.write_text(json.dumps(report))
            for service, start, end in (("a", 0, 4), ("b", 1, 6)):
                with gzip.open(path.with_suffix(f".{service}.bin.gz"), "wb") as evidence:
                    evidence.write(b"".join(CALL.pack(i * 20 + start, i * 20 + end) for i in range(MIN_CALLS)))

            cell = replace_plan("tcp", cadence)
            result = read_replacement(path, "P", commit, cell, set())
            self.assertEqual(result["a_completed"], MIN_CALLS)
            self.assertEqual(result["a_p99_us"], 80)
            self.assertEqual(result["b_p99_us"], 100)
            self.assertEqual(result["replacements"], 9)
            self.assertEqual(result["b_update_overlap"], 9)
            self.assertNotIn("b_update_overlap_latency", result)
            self.assertEqual(result["update_to_first_r2_p50_us"], 120)
            self.assertFalse(cell["compare_allocations"])
            self.assertTrue(replace_plan("tcp", 0)["compare_allocations"])

            report["RpcTracingEnabled"] = True
            path.write_text(json.dumps(report))
            with self.assertRaisesRegex(ValueError, "included RPC tracing"):
                read_replacement(path, "P", commit, cell, set())
            report["RpcTracingEnabled"] = False
            report["Interceptor"] = "forward"
            path.write_text(json.dumps(report))
            with self.assertRaisesRegex(ValueError, "interceptor mismatch"):
                read_replacement(path, "P", commit, cell, set())
            report["Interceptor"] = "none"
            report["Replacements"] = report["Replacements"][:-1]
            path.write_text(json.dumps(report))
            with self.assertRaisesRegex(ValueError, "frequency"):
                read_replacement(path, "P", commit, cell, set())

    def test_forwarding_is_a_separate_steady_cell(self):
        normal = replace_plan("tcp", 0)
        forwarded = replace_plan("tcp", 0, "forward")
        self.assertNotEqual(normal["operation"], forwarded["operation"])
        self.assertEqual(forwarded["aa_maximum_deviation"], normal["aa_maximum_deviation"])
        self.assertEqual(forwarded["critical"], normal["critical"])
        self.assertTrue(forwarded["compare_allocations"])
        with self.assertRaisesRegex(ValueError, "separate steady"):
            replace_plan("tcp", 100, "forward")


if __name__ == "__main__":
    unittest.main()
