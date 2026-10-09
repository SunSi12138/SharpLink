#!/usr/bin/env python3
import copy
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
import xml.etree.ElementTree as ET

SPEC = importlib.util.spec_from_file_location("transport", Path(__file__).with_name("verify-flow-state-transport.py"))
module = importlib.util.module_from_spec(SPEC); SPEC.loader.exec_module(module)


class TransportEvidenceTests(unittest.TestCase):
    def report(self):
        rows = []
        for mode in ("A", "B1", "B2", "B2-adaptive"):
            commands = 0 if mode == "A" else 2
            rows.append(dict(Mode=mode, Round=0, Source="a"*40, Transport="tcp", Streams=1,
                             ItemsPerStream=1, ItemBytes=4, ConnectionWindow=8192, StreamWindow=8192,
                             ItemsReceived=1, BytesReturned=4, UpdateFrames=1, RefillCommands=0 if mode=="A" else 1,
                             OwnerCommands=commands, OwnerCommandsPerItem=float(commands),
                             ElapsedMs=1.0, CpuMs=1.0, ItemsPerSecond=1000.0, AllocatedBytesPerItem=1.0,
                             ProducerSpreadMs=0.0, ProducerDurationMs=[1.0], PressureRevocations=0, QueuedWaiterAdmissions=0))
        return dict(status="completed", error=None, metadata=dict(Source="a"*40, transport="tcp", Pgo="1",
                         streams=1, items=1, bytes=4, rounds=1, connectionWindow=8192), samples=rows)

    def validate(self, report):
        with tempfile.TemporaryDirectory() as d:
            p=Path(d)/"sample.json"; p.write_text(json.dumps(report)); return module.validate(p)

    def test_diagnostic_report_is_not_timing_evidence(self):
        report = self.report()
        report["metadata"]["DiagnosticCapture"] = True
        with self.assertRaises(ValueError): self.validate(report)

    def test_balanced_control(self):
        self.assertEqual(len(self.validate(self.report())[1]),4)

    def test_failure_and_partial_run_rejected(self):
        for status in ("failed", "in_progress"):
            r=self.report(); r["status"]=status
            with self.assertRaises(ValueError): self.validate(r)
        r=self.report(); r["error"]="parser failed"
        with self.assertRaises(ValueError): self.validate(r)

    def test_missing_duplicate_and_renamed_controls_rejected(self):
        for action in ("missing", "duplicate", "rename"):
            r=self.report()
            if action=="missing": r["samples"].pop()
            elif action=="duplicate": r["samples"].append(copy.deepcopy(r["samples"][0]))
            else: r["samples"][3]["Mode"]="best-result"
            with self.assertRaises(ValueError): self.validate(r)

    def test_lost_data_credit_and_counters_rejected(self):
        for field,value in (("ItemsReceived",0),("BytesReturned",8),("UpdateFrames",0),
                            ("OwnerCommands",0),("OwnerCommandsPerItem",0),("RefillCommands",0)):
            r=self.report(); r["samples"][1][field]=value
            with self.assertRaises(ValueError): self.validate(r)

    def test_source_runtime_and_workload_mismatch_rejected(self):
        for field,value in (("Source","b"*40),("Streams",2),("ConnectionWindow",16384),("ItemBytes",16)):
            r=self.report(); r["samples"][0][field]=value
            with self.assertRaises(ValueError): self.validate(r)
        for field,value in (("Source","dirty"),("Pgo",None),("transport","synthetic")):
            r=self.report(); r["metadata"][field]=value
            with self.assertRaises(ValueError): self.validate(r)

    def test_nonfinite_and_false_denominators_rejected(self):
        for field,value in (("ElapsedMs",0),("CpuMs",-1),("ItemsPerSecond",1234),
                            ("AllocatedBytesPerItem",float("nan")),("ProducerDurationMs",[])):
            r=self.report(); r["samples"][0][field]=value
            with self.assertRaises(ValueError): self.validate(r)

    def test_negative_fractional_and_boolean_counts_rejected(self):
        for field, value in (("RefillCommands", -1), ("OwnerCommands", 2.5), ("UpdateFrames", True),
                             ("PressureRevocations", -1), ("RevocationSweeps", 1.5)):
            report = self.report(); report["samples"][1][field] = value
            with self.assertRaises(ValueError): self.validate(report)

    def test_mixed_trees_and_missing_exit_rejected(self):
        with tempfile.TemporaryDirectory() as d:
            root = Path(d)
            for launch in (1, 2):
                report = self.report()
                if launch == 2:
                    report["metadata"]["Source"] = "b"*40
                    for row in report["samples"]: row["Source"] = "b"*40
                path = root/f"tcp-launch{launch}.json"
                path.write_text(json.dumps(report))
                path.with_name(path.stem+".exit.json").write_text(json.dumps({"exit_code": 0}))
            with self.assertRaises(ValueError): module.summarize(root)
            (root/"tcp-launch2.json").unlink()
            (root/"tcp-launch1.exit.json").unlink()
            with self.assertRaises(ValueError): module.summarize(root)

    def test_exact_model_sources_not_executable_dependency(self):
        root=Path(__file__).resolve().parents[1]
        project=root/"test/SharpLink.Benchmarks/SharpLink.Benchmarks.csproj"
        xml=ET.parse(project)
        includes=[x.attrib['Include'] for x in xml.findall('.//Compile') if 'SharpLink.FlowStatePhaseB/' in x.attrib.get('Include','')]
        expected={f'../SharpLink.FlowStatePhaseB/{n}' for n in ('GrantAuthority.cs','GrantAuthority.Commands.cs',
                   'GrantAuthority.Publication.cs','GrantAuthority.Wire.cs','ReusableOwnerCommand.cs')}
        self.assertEqual(set(includes),expected); self.assertEqual(len(includes),len(expected))
        self.assertTrue(all((project.parent/x).is_file() for x in includes))
        self.assertFalse(any('SharpLink.FlowStatePhaseB.csproj' in x.attrib.get('Include','') for x in xml.findall('.//ProjectReference')))


if __name__ == '__main__': unittest.main()
