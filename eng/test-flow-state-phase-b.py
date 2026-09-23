#!/usr/bin/env python3
import copy
import importlib.util
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def load(name):
    spec = importlib.util.spec_from_file_location(name, ROOT / "eng" / (name + ".py"))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


prepare = load("prepare-flow-state-phase-b")
summary = load("summarize-flow-state-phase-b")


class PhaseBEvidenceTests(unittest.TestCase):
    def test_frozen_sources_and_gate_partition(self):
        source = prepare.read_sources(ROOT)["StreamFlowController.cs"]
        split = prepare.split_source(source)
        self.assertEqual(split.count("lock (_receiveGate)"), 9)
        self.assertIn("CreditWaiter[] waiters;\n        lock (_gate)\n        lock (_receiveGate)", split)
        first = split.index("internal ResolvedReceiveCreditLease ResolveReceiveCreditLease")
        last = split.index("public void Complete(Exception exception)")
        self.assertNotIn("lock (_gate)", split[first:last])

    def test_generation_does_not_mutate_sources(self):
        before = (ROOT / "src/SharpLink.Runtime/StreamFlowController.cs").read_bytes()
        with tempfile.TemporaryDirectory() as directory:
            prepare.generate(ROOT, Path(directory))
            self.assertEqual(len(list(Path(directory).glob("*.cs"))), 6)
        self.assertEqual(before, (ROOT / "src/SharpLink.Runtime/StreamFlowController.cs").read_bytes())

    def test_drift_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            path = root / "src/SharpLink.Runtime/StreamFlowController.cs"
            path.parent.mkdir(parents=True)
            path.write_text("not the reviewed controller")
            with self.assertRaises(ValueError): prepare.read_sources(root)

    def row(self):
        return dict(Family="send-owner-model", Variant="B1-item-queue", Shape="periodic-update-64",
                    ActiveStreams=32, ItemBytes=16, ItemsPerStream=1024, Repetition=0,
                    NsPerItem=1.0, AllocatedBytesPerItem=10.0, ConnectionGateEntriesPerItem=0,
                    OwnerHandoffsPerItem=1.015625, QueueOperationsPerItem=2.03125,
                    RuntimeAtomicRmwPerItem=None, Checksum=32 * 1024 * 16)

    def test_valid_control(self):
        self.assertEqual(summary.validate([self.row()]), 1)

    def test_empty_duplicate_invalid_and_fake_counters_rejected(self):
        row = self.row()
        for rows in ([], [row, row]):
            with self.assertRaises(ValueError): summary.validate(rows)
        for field, value in (("NsPerItem", float("nan")), ("Checksum", 0),
                             ("OwnerHandoffsPerItem", 0), ("RuntimeAtomicRmwPerItem", 0)):
            bad = copy.deepcopy(row)
            bad[field] = value
            with self.assertRaises(ValueError): summary.validate([bad])


if __name__ == "__main__": unittest.main()
