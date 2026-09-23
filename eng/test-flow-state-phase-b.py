#!/usr/bin/env python3
import copy
import itertools
import json
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
comparison = load("compare-flow-state-completions")


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

    def test_batched_counts_include_updates_and_warm_grant_remainder(self):
        row = self.row()
        row.update(Variant="B2-grant-4096", OwnerHandoffsPerItem=0.01953125,
                   QueueOperationsPerItem=0.0390625, ReusableCommandsAllocated=0, QueueBackpressureWaits=0)
        self.assertEqual(summary.validate([row]), 1)
        for count in (0, 0.00390625, 1.015625):
            bad = copy.deepcopy(row)
            bad["OwnerHandoffsPerItem"] = count
            bad["QueueOperationsPerItem"] = 2 * count
            with self.assertRaises(ValueError): summary.validate([bad])
        row.update(ItemsPerStream=1, Checksum=32 * 16, OwnerHandoffsPerItem=1, QueueOperationsPerItem=2)
        self.assertEqual(summary.validate([row]), 1) # no refill; one final peer update

    def test_command_allocation_or_invalid_backpressure_cannot_be_hidden(self):
        for field, value in (("ReusableCommandsAllocated", 1), ("ReusableCommandsAllocated", -1),
                             ("QueueBackpressureWaits", -1), ("QueueBackpressureWaits", 0.5)):
            row = self.row()
            row[field] = value
            with self.assertRaises(ValueError): summary.validate([row])

    def make_comparison(self, root):
        (root / "provenance.json").write_text(json.dumps({"baseline": comparison.BASELINE, "candidate": "a" * 40}))
        for runtime, size, launch, label in itertools.product(("pgo0", "pgo1", "aot"), (1024, 16384), (1, 2), ("before", "after")):
            rows = []
            for variant, repetition in itertools.product(comparison.VARIANTS, (0, 1)):
                chunk = {"B1-item-queue": 1, "B2-grant-256": 16, "B2-grant-1024": 64, "B2-grant-4096": 256}[variant]
                owner_calls = 1 / chunk + 1 / 64
                row = self.row()
                row.update(Variant=variant, Repetition=repetition, ActiveStreams=128, ItemsPerStream=size,
                           Checksum=128*size*16, OwnerHandoffsPerItem=owner_calls, QueueOperationsPerItem=2*owner_calls,
                           ReusableCommandsAllocated=0 if label == "after" else None,
                           QueueBackpressureWaits=0 if label == "after" else None)
                rows.append(row)
            (root / f"{runtime}-{size}-r{launch}-{label}.json").write_text(json.dumps(rows))

    def test_complete_comparison_and_missing_file(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            self.make_comparison(root)
            self.assertIn("not", comparison.compare(root))
            (root / "aot-16384-r2-after.json").unlink()
            with self.assertRaises(FileNotFoundError): comparison.compare(root)

    def test_comparison_rejects_missing_case_and_extra_file(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            self.make_comparison(root)
            path = root / "pgo1-1024-r1-after.json"
            rows = json.loads(path.read_text())
            path.write_text(json.dumps(rows[:-1]))
            with self.assertRaises(ValueError): comparison.compare(root)
            path.write_text(json.dumps(rows))
            (root / "unrelated.json").write_text("[]")
            with self.assertRaises(ValueError): comparison.compare(root)

    def test_comparison_rejects_fake_reuse_and_unmatched_workload(self):
        for field, value in (("ReusableCommandsAllocated", 1), ("QueueBackpressureWaits", 1), ("ActiveStreams", 32)):
            with tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                self.make_comparison(root)
                path = root / "pgo0-1024-r1-after.json"
                rows = json.loads(path.read_text())
                rows[0][field] = value
                path.write_text(json.dumps(rows))
                with self.assertRaises(ValueError): comparison.compare(root)

    def test_comparison_rejects_wrong_revision(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            self.make_comparison(root)
            (root / "provenance.json").write_text(json.dumps({"baseline": "b"*40, "candidate": "a"*40}))
            with self.assertRaises(ValueError): comparison.compare(root)

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
