"""Verify the launch order without starting a workload or requiring GitHub."""

import json
from pathlib import Path
import tempfile
import unittest

from d6_paired_steady import read_sample, sample_plan
from d6_run_steady import run_phases, verify_steady_preflight, verify_stream_preflight, workload_commands
from test_d6_paired_steady import report


class ShardExecutionTests(unittest.TestCase):
    def test_native_uses_a_publish_and_not_a_jit_apphost(self):
        root, output = Path("source"), Path("results") / "P"
        workload = "SharpLink.LoadTest"
        for runner, rid, entry in (("Linux", "linux-x64", workload), ("Windows", "win-x64", workload + ".exe")):
            with self.subTest(runner=runner):
                build, binary, entrypoint = workload_commands(
                    root, output, workload, "project.csproj", "jit.dll", True, runner_os=runner)
                self.assertEqual(build[:3], ["dotnet", "publish", "project.csproj"])
                self.assertIn("/p:PublishAot=true", build)
                self.assertIn(rid, build)
                self.assertEqual(binary, output / "publish" / entry)
                self.assertEqual(entrypoint, [str(binary)])
                build, binary, entrypoint = workload_commands(
                    root, output, workload, "project.csproj", "jit.dll", False, runner_os=runner)
                self.assertEqual(build[:2], ["dotnet", "build"])
                self.assertEqual(entrypoint, ["dotnet", str(root / "jit.dll")])

    def test_preflight_reconciles_runtime_and_real_business_receipt(self):
        log = "QUALIFIED32_VERIFIED runtime=aot transport=Tcp unary=16 oneway-remote=16\n"
        verify_steady_preflight(log, "aot", "tcp", 0)
        with self.assertRaisesRegex(ValueError, "wrong runtime"):
            verify_steady_preflight(log, "jit", "tcp", 0)

    def test_short_stream_preflight_requires_all_six_receipts_from_the_expected_runtime(self):
        log = "\n".join(f"SHORT_STREAM_VERIFIED {shape} items={items} runtime=aot"
                        for shape in ("c2s", "s2c", "duplex") for items in (32, 256))
        verify_stream_preflight(log, "aot", 0)
        with self.assertRaisesRegex(ValueError, "wrong runtime"):
            verify_stream_preflight(log, "jit", 0)
        with self.assertRaisesRegex(ValueError, "short-stream workload failed"):
            verify_stream_preflight("\n".join(log.splitlines()[:-1]), "aot", 0)

    def drive(self, *, drift):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            calls, reports_read = [], []
            cells = [sample_plan("sharedmemory", 8, op) for op in ("unary32", "oneway32")]

            def launch(cell, phase, index, arm, path):
                op = cell["operation"]
                calls.append((op, phase, index, arm))
                if drift and phase == "aa" and op == "oneway32" and path.name == "aa-01-2-P.json":
                    data = report(arm, p99=26)
                else:
                    data = report(arm, p99=18 if arm == "N" else 20)
                data["Configuration"]["Operation"] = op
                stage = data["Results"][0]
                stage["Operation"] = op
                if op == "unary32":
                    stage["Evidence"] = stage.pop("OneWay32SettledEvidence")
                    stage.pop("OneWay32BusinessCompleted")
                    stage.pop("OneWay32ConfirmationSeconds")
                path.write_text(json.dumps(data))

            def read_once(path, arm, source, cell, hosts):
                reports_read.append(path)
                return read_sample(path, arm, source, cell, hosts)

            status = run_phases(root, cells, {"P": "P", "N": "N"}, launch, reader=read_once)
            self.assertEqual(len(reports_read), len(set(reports_read)))
            self.assertEqual(len(reports_read), len(calls))
            return status, calls

    def test_second_cell_aa_gate_prevents_any_candidate_launch(self):
        status, calls = self.drive(drift=True)
        self.assertEqual(status, "INCONCLUSIVE_AA")
        self.assertEqual(len(calls), 10)
        self.assertTrue(all(phase == "aa" and arm == "P" for _, phase, _, arm in calls))

    def test_both_cells_are_serially_paired_after_sixteen_aa_launches(self):
        status, calls = self.drive(drift=False)
        self.assertEqual(status, "SUPPORTS_NEXT_STAGE")
        self.assertEqual(len(calls), 48)
        self.assertEqual("".join(arm for _, _, _, arm in calls[:16]), "P" * 16)
        self.assertEqual("".join(arm for _, _, _, arm in calls[16:32]), "PNNPNPPN" * 2)
        self.assertEqual("".join(arm for _, _, _, arm in calls[32:]), "PNNPNPPN" * 2)


if __name__ == "__main__":
    unittest.main()
