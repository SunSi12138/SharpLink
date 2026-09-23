#!/usr/bin/env python3
"""Negative guards for the immutable-reference direct publication comparison."""
import importlib.util
import itertools
import json
import tempfile
import unittest
import xml.etree.ElementTree as ET
from pathlib import Path

SPEC = importlib.util.spec_from_file_location("fused", Path(__file__).with_name("compare-flow-state-fused.py"))
fused = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(fused)


class FusedEvidenceTests(unittest.TestCase):
    def make_reports(self, root):
        (root / "provenance.json").write_text(json.dumps({"baseline": fused.BASELINE, "candidate": "a" * 40}))
        for runtime, length, launch, label in itertools.product(fused.RUNTIMES, fused.SIZES, (1, 2), ("before", "after")):
            rows = []
            for streams, size, repetition in itertools.product((1, 8, 32, 128), (16, 4096), range(4)):
                handoffs = (1 if size == 16 else 2) if length == 1 else size / 4096 + 1 / 64
                rows.append(dict(Family="send-fused-admission-model", Variant="B2-grant-4096",
                    Shape="split-writer-admission" if label == "before" else "direct-writer-admission",
                    ActiveStreams=streams, Workers=streams, ItemBytes=size, ItemsPerStream=length,
                    Repetition=repetition, NsPerItem=2.0 if label == "after" else 1.0,
                    AllocatedBytesPerItem=0.1, ConnectionGateEntriesPerItem=0, OwnerHandoffsPerItem=handoffs,
                    QueueOperationsPerItem=2 * handoffs, RuntimeAtomicRmwPerItem=None, Instrumented=False,
                    Checksum=streams * length * size, ReusableCommandsAllocated=0, QueueBackpressureWaits=0))
            (root / f"{runtime}-{length}-r{launch}-{label}.json").write_text(json.dumps(rows))

    def test_real_writer_boundary_uses_the_same_model_sources(self):
        root = Path(__file__).resolve().parents[1]
        project = root / "test/SharpLink.UnitTests/SharpLink.UnitTests.csproj"
        xml = ET.parse(project)
        includes = [item.attrib["Include"] for item in xml.findall(".//Compile")
                    if "SharpLink.FlowStatePhaseB/" in item.attrib.get("Include", "")]
        expected = ["GrantAuthority.cs", "GrantAuthority.Commands.cs", "GrantAuthority.Publication.cs", "ReusableOwnerCommand.cs"]
        self.assertEqual(set(includes), {"../SharpLink.FlowStatePhaseB/" + name for name in expected})
        self.assertEqual(len(includes), len(expected))
        for source in includes:
            self.assertTrue((project.parent / source).is_file())
        references = [item.attrib["Include"].replace("\\", "/") for item in xml.findall(".//ProjectReference")]
        self.assertIn("../../src/SharpLink.Runtime/SharpLink.Runtime.csproj", references)
        self.assertFalse(any("SharpLink.FlowStatePhaseB.csproj" in path for path in references))

    def test_complete_matrix_retains_regression(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            self.make_reports(root)
            text = fused.compare(root)
            self.assertIn("24 reports / 768 rows", text)
            self.assertIn("+100.00%", text)
            self.assertIn("NOT a cold", text)

    def test_missing_native_report_and_extra_report_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            self.make_reports(root)
            path = root / "aot-4096-r2-after.json"
            saved = path.read_bytes()
            path.unlink()
            with self.assertRaises(ValueError): fused.compare(root)
            path.write_bytes(saved)
            (root / "stale.json").write_text("[]")
            with self.assertRaises(ValueError): fused.compare(root)

    def test_missing_and_duplicate_rows_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            self.make_reports(root)
            path = root / "pgo1-4096-r1-after.json"
            rows = json.loads(path.read_text())
            for altered in (rows[:-1], rows + [rows[0]]):
                path.write_text(json.dumps(altered))
                with self.assertRaises(ValueError): fused.compare(root)

    def test_fake_ownership_allocation_or_changed_workload_rejected(self):
        for field, value in (("Shape", "legacy-commit"), ("Family", "send-owner-model"),
                             ("Workers", 0), ("Instrumented", True), ("NsPerItem", 0),
                             ("OwnerHandoffsPerItem", 0), ("ConnectionGateEntriesPerItem", 1),
                             ("ReusableCommandsAllocated", 1), ("QueueBackpressureWaits", 1),
                             ("RuntimeAtomicRmwPerItem", 0), ("ItemsPerStream", 4096),
                             ("AllocatedBytesPerItem", float("nan"))):
            with self.subTest(field=field), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                self.make_reports(root)
                path = root / "pgo0-1-r1-after.json"
                rows = json.loads(path.read_text())
                rows[0][field] = value
                path.write_text(json.dumps(rows))
                with self.assertRaises(ValueError): fused.compare(root)

    def test_wrong_or_missing_candidate_and_baseline_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            self.make_reports(root)
            for provenance in ({}, {"baseline": fused.BASELINE, "candidate": fused.BASELINE},
                               {"baseline": "b" * 40, "candidate": "a" * 40},
                               {"baseline": fused.BASELINE, "candidate": "uncommitted"}):
                (root / "provenance.json").write_text(json.dumps(provenance))
                with self.assertRaises(ValueError): fused.compare(root)


if __name__ == "__main__": unittest.main()
