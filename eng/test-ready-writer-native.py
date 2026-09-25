#!/usr/bin/env python3
"""Guard native source sharing and prevent JIT or incomplete data being accepted as AOT."""
import copy
import importlib.util
import json
from pathlib import Path
import tempfile
import subprocess
from types import SimpleNamespace
from unittest.mock import patch
import unittest
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]


def load(name):
    spec = importlib.util.spec_from_file_location(name, ROOT / "eng" / (name + ".py"))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


verify = load("verify-ready-writer-native")
runner = load("run-ready-writer-native")
fixtures = load("test-ready-writer-budget")


class NativeEvidenceTests(unittest.TestCase):
    def fixture(self, case):
        document, _ = fixtures.Guards().fixture()
        # Use the independent JIT fixture's counter schema, not measured results.
        transport, _, streams, items, size, window, flush, launch, budget = case
        total = streams * items
        document["metadata"].update(Pgo="nativeaot", DynamicCodeSupported=False, transport=transport,
                                    streams=streams, items=items, bytes=size, connection=window,
                                    flush=flush, orderOffset=launch, preparedByteBudget=budget)
        for sample in document["samples"]:
            owner = sample["Mode"].startswith("B3")
            sample.update(Transport=transport, Streams=streams, ItemsPerStream=items,
                          ItemBytes=size, ConnectionWindow=window, ItemsReceived=total,
                          BytesReturned=total * size, ItemsPerSecond=total * 10,
                          ProducerDurationMs=[10.0] * streams)
            metrics = sample["ReadyWriterMetrics"]
            metrics.update(FramesReleased=total, CreditBytesApplied=total * size,
                           ReadyNotifications=streams, EventChannelReadCalls=streams + sample["UpdateFrames"],
                           ExistingPumpBudgetRmwLowerBound=2 * total,
                           PumpOwnedCreditDebits=total if owner else 0,
                           ProducerSideCreditGateOperations=0 if owner else total,
                           PreparedByteBudgetPerStream=budget, MaximumObservedPacketBytes=size + 16,
                           MaximumQueuedBytesPerStream=size + 16, SumOfStreamQueuedBytePeaks=streams * (size + 16))
        return document

    def populate(self, root):
        (root / "provenance.json").write_text(json.dumps(dict(
            source_tree="a" * 40, host_sha256="b" * 64, cpu_affinity=[0, 1, 2, 3],
            runtime="nativeaot", plan=verify.expected_plan(), rounds=4, slots=16,
            quanta=[1, 16], allocation_diagnostic=False)))
        for index, case in enumerate(verify.expected_plan()):
            path = root / runner.name(index, case)
            path.write_text(json.dumps(self.fixture(case)))
            path.with_suffix(".exit").write_text('{"code":0}')

    def test_diagnostics_require_the_separate_build_flag(self):
        path = ROOT / "test/SharpLink.Benchmarks/ReadyWriterStallDiagnostics.cs"
        text = path.read_text().strip()
        self.assertTrue(text.startswith("#if SHARPLINK_READY_WRITER_DIAGNOSTIC"))
        self.assertTrue(text.endswith("#endif"))
        project = (ROOT / "test/SharpLink.Benchmarks/SharpLink.Benchmarks.csproj").read_text()
        self.assertIn("'$(ReadyWriterExperiment)' == 'true' And '$(ReadyWriterDiagnostic)' == 'true'", project)
        native = (ROOT / "test/SharpLink.ReadyWriterAotEvidence/SharpLink.ReadyWriterAotEvidence.csproj").read_text()
        self.assertNotIn("SHARPLINK_READY_WRITER_DIAGNOSTIC", native)

    def test_population_and_complete_evidence(self):
        self.assertEqual(runner.plan(), verify.expected_plan())
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            self.populate(root)
            self.assertEqual(len(verify.summarize(root)[1]), 128)

    def test_jit_relabeling_is_rejected(self):
        case = verify.expected_plan()[0]
        for field, value in (("DynamicCodeSupported", True), ("Pgo", "1")):
            document = self.fixture(case)
            document["metadata"][field] = value
            with self.assertRaises(ValueError):
                verify.validate_document(document, "a" * 40, case)

    def test_missing_report_or_failed_process_is_rejected(self):
        for missing in (False, True):
            with tempfile.TemporaryDirectory() as temp:
                root = Path(temp)
                self.populate(root)
                path = root / runner.name(0, verify.expected_plan()[0])
                if missing:
                    path.unlink()
                else:
                    path.with_suffix(".exit").write_text('{"code":1}')
                with self.assertRaises((ValueError, FileNotFoundError)):
                    verify.summarize(root)

    def test_failure_artifacts_are_retained_per_attempt(self):
        for name in ("flow-state-ready-writer.yml", "flow-state-phase-b-transport.yml"):
            text = (ROOT / ".github/workflows" / name).read_text()
            names = [line for line in text.splitlines() if "name:" in line and "github.event.pull_request.head.sha" in line]
            self.assertTrue(names)
            self.assertTrue(all("github.run_attempt" in line for line in names))
            self.assertNotIn("overwrite: true", text)

    def test_native_collection_continues_after_failure_without_retry(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            binary = root / "native-host"
            binary.write_bytes(b"test-only fixture, never executed")
            output = root / "out"
            launches = []

            def invoke(command, **kwargs):
                if command[:2] == ["git", "diff"]:
                    return SimpleNamespace(returncode=0)
                self.assertEqual(kwargs["timeout"], 180)
                launches.append(command[-1])
                index = len(launches) - 1
                if index == 0:
                    raise subprocess.TimeoutExpired(command, 180)
                if index == 6:
                    raise OSError("controlled launch failure")
                return SimpleNamespace(returncode=0)

            with patch.object(runner.subprocess, "check_output", return_value="a" * 40), \
                 patch.object(runner.subprocess, "run", side_effect=invoke), \
                 patch.object(runner.os, "sched_getaffinity", return_value={0, 1, 2, 3}), \
                 patch("sys.argv", ["run-native", str(output), "--binary", str(binary), "--source", "a" * 40]):
                with self.assertRaises(SystemExit):
                    runner.main()
            self.assertEqual(len(launches), 8, "the first failure must not hide later predeclared controls")
            self.assertEqual(len(set(launches)), 8, "no case is retried")
            exits = [json.loads((output / runner.name(i, case)).with_suffix(".exit").read_text())["code"]
                     for i, case in enumerate(runner.plan())]
            self.assertEqual(exits, [124, 0, 0, 0, 0, 0, 127, 0])

    def test_native_preflight_preserves_existing_logs_and_exits(self):
        for suffix in (".json", ".log", ".exit"):
            with tempfile.TemporaryDirectory() as temp:
                root = Path(temp)
                binary = root / "native-host"
                binary.write_bytes(b"fixture")
                output = root / "out"
                output.mkdir()
                old = (output / runner.name(7, runner.plan()[7])).with_suffix(suffix)
                old.write_text("retained failure")
                with patch.object(runner.subprocess, "check_output", return_value="a" * 40), \
                     patch.object(runner.subprocess, "run", return_value=SimpleNamespace(returncode=0)) as invoke, \
                     patch.object(runner.os, "sched_getaffinity", return_value={0, 1, 2, 3}), \
                     patch("sys.argv", ["run-native", str(output), "--binary", str(binary), "--source", "a" * 40]):
                    with self.assertRaises(FileExistsError):
                        runner.main()
                self.assertEqual(invoke.call_count, 1, "only git verification is permitted before preflight")
                self.assertEqual(old.read_text(), "retained failure")

    def test_boolean_exit_is_not_a_successful_process(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            self.populate(root)
            path = root / runner.name(0, runner.plan()[0])
            path.with_suffix(".exit").write_text('{"code":false}')
            with self.assertRaises(ValueError):
                verify.summarize(root)

    def test_partial_coverage_is_not_performance_acceptance(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            self.populate(root)
            failed = root / runner.name(0, runner.plan()[0])
            failed.with_suffix(".exit").write_text('{"code":124}')
            invalid = root / runner.name(1, runner.plan()[1])
            invalid.with_suffix(".exit").write_text('{"code":false}')
            missing = root / runner.name(7, runner.plan()[7])
            missing.with_suffix(".exit").unlink()
            result = verify.coverage(root)
            self.assertFalse(result["accepted"])
            self.assertEqual(result["complete"], 5)
            self.assertEqual([case["status"] for case in result["cases"]],
                             ["failed", "invalid", "complete", "complete", "complete", "complete", "complete", "missing"])
            self.assertEqual(result["cases"][0]["validated_rows"], 0)
            with self.assertRaises(ValueError):
                verify.summarize(root)
            with patch("sys.argv", ["verify-native", str(root), "--coverage-only"]):
                with self.assertRaises(SystemExit) as failure:
                    verify.main()
            self.assertEqual(failure.exception.code, 1)
            self.assertTrue((root / "coverage.json").exists())
            self.assertTrue((root / "coverage.md").exists())
            self.assertFalse((root / "summary.md").exists())

    def test_coverage_excludes_diagnostics_and_extra_reports(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            self.populate(root)
            result = verify.coverage(root)
            self.assertTrue(result["accepted"])
            self.assertEqual(sum(case["validated_rows"] for case in result["cases"]), 128)
            path = root / runner.name(0, runner.plan()[0])
            document = json.loads(path.read_text())
            document["metadata"]["DiagnosticCapture"] = True
            path.write_text(json.dumps(document))
            self.assertEqual(verify.coverage(root)["cases"][0]["status"], "invalid")
            self.populate(root)
            (root / "99-unexpected.json").write_text("{}")
            result = verify.coverage(root)
            self.assertFalse(result["accepted"])
            self.assertEqual(result["unexpected_reports"], ["99-unexpected.json"])
            with self.assertRaises(ValueError):
                verify.summarize(root)

    def test_invalid_exit_types_are_rejected(self):
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / "test.json"
            for code in (False, True, 0.0, "0", None):
                path.with_suffix(".exit").write_text(json.dumps({"code": code}))
                with self.assertRaises(ValueError):
                    verify.read_exit(path)

    def test_native_finalizers_run_even_after_failure(self):
        text = (ROOT / ".github/workflows/flow-state-ready-writer.yml").read_text()
        native = text.split("  native-evidence:", 1)[1].split("  stalled-jit-diagnostic:", 1)[0]
        blocks = native.split("      - name:")
        coverage = [block for block in blocks if "--coverage-only" in block]
        verification = [block for block in blocks if "verify-ready-writer-native.py" in block
                        and "--coverage-only" not in block]
        self.assertEqual(len(coverage), 1)
        self.assertEqual(len(verification), 1)
        for block in coverage + verification:
            self.assertIn("        if: always()\n", block)
            self.assertNotIn("continue-on-error", block)
            self.assertNotIn("|| true", block)
        self.assertIn("    timeout-minutes: 20\n", native)

    def test_native_host_links_reviewed_sources_only(self):
        project = ROOT / "test/SharpLink.ReadyWriterAotEvidence/SharpLink.ReadyWriterAotEvidence.csproj"
        xml = ET.parse(project).getroot()
        self.assertEqual(xml.findtext(".//JsonSerializerIsReflectionEnabledByDefault"), "false")
        self.assertEqual(xml.findtext(".//IsPackable"), "false")
        self.assertEqual([x.attrib["Include"] for x in xml.findall(".//ProjectReference")],
                         ["../../src/SharpLink.Runtime/SharpLink.Runtime.csproj"])
        self.assertFalse(xml.findall(".//PackageReference"))
        linked = set()
        for item in xml.findall(".//Compile"):
            pattern = item.attrib["Include"]
            paths = list(project.parent.glob(pattern))
            self.assertTrue(paths, pattern)
            linked.update(path.name for path in paths)
        expected = {"PhaseBTransportSample.cs", "PhaseBTransportCase.cs", "PhaseBTransportPair.cs",
                    "PhaseBTransportFailure.cs", "PhaseBTransportFailureChecks.cs",
                    "ReadyWriterCoordinator.cs", "ReadyWriterCoordinator.Checks.cs",
                    "ReadyWriterCoordinator.PreparedChecks.cs", "ReadyWriterCoordinator.StopChecks.cs",
                    "ReadyWriterCoordinator.Cancellation.cs", "ReadyWriterCancellationChecks.cs",
                    "ReadyWriterCoordinator.Wire.cs", "ReadyWriterWirePermissionChecks.cs",
                    "ReadyWriterQueueBudgetChecks.cs", "ReadyWriterProgressChecks.cs", "ReadyWriterChecks.cs", "ReadyWriterEvidence.cs", "ReadyWriterJson.cs", "ReadyWriterStallDiagnostics.cs",
                    "GrantAuthority.cs", "GrantAuthority.Commands.cs", "GrantAuthority.Publication.cs",
                    "GrantAuthority.Wire.cs", "ReusableOwnerCommand.cs"}
        self.assertEqual(linked, expected)


if __name__ == "__main__":
    unittest.main()
