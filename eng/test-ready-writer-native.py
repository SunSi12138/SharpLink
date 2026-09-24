#!/usr/bin/env python3
"""Guard native source sharing and prevent JIT or incomplete data being accepted as AOT."""
import copy
import importlib.util
import json
from pathlib import Path
import tempfile
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
                    "ReadyWriterChecks.cs", "ReadyWriterEvidence.cs", "ReadyWriterJson.cs", "ReadyWriterStallDiagnostics.cs",
                    "GrantAuthority.cs", "GrantAuthority.Commands.cs", "GrantAuthority.Publication.cs",
                    "GrantAuthority.Wire.cs", "ReusableOwnerCommand.cs"}
        self.assertEqual(linked, expected)


if __name__ == "__main__":
    unittest.main()
