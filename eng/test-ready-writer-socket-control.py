#!/usr/bin/env python3
import importlib.util
import json
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]


def load(name):
    spec = importlib.util.spec_from_file_location(name, ROOT/'eng'/f'{name}.py')
    module = importlib.util.module_from_spec(spec); spec.loader.exec_module(module); return module


run = load('run-ready-writer-socket-control')


class Controls(unittest.TestCase):
    def test_direct_benchmark_knob_is_explicit_and_test_only(self):
        text=(ROOT/'test/SharpLink.Benchmarks/PhaseBTransportPair.cs').read_text()
        self.assertIn('#if SHARPLINK_READY_WRITER_EXPERIMENT',text)
        self.assertIn('SHARPLINK_READY_TCP_RECEIVE_BUFFER',text)
        self.assertEqual(text.count('ReceiveBufferBytes = 262144'),1)
        self.assertIn('receiveBufferText != "262144"',text)
        self.assertNotIn('SHARPLINK_READY_TCP_RECEIVE_BUFFER',(ROOT/'src/SharpLink.Runtime/RpcSession.SendPump.cs').read_text())

    def test_abba_all_once_even_if_default_fails(self):
        self.assertEqual(run.PLAN, [('default',0),('262144',0),('262144',1),('default',1)])
        calls = []
        def child(args, **kwargs):
            index = len(calls); calls.append((args, kwargs))
            Path(args[-1]).write_text(json.dumps(dict(metadata=dict(DiagnosticCapture=True),
                                                       status='completed', error=None, samples=[{}]*48)))
            return subprocess.CompletedProcess(args, 7 if index in (0,3) else 0)
        with tempfile.TemporaryDirectory() as directory, patch.object(run.subprocess, 'run', side_effect=child):
            failures=run.execute(ROOT,Path(directory),Path('/unused.dll'),[0,1,2,3])
            self.assertEqual(failures,[0,3]);self.assertEqual(len(calls),4)
            for args,kwargs in calls:
                self.assertEqual(kwargs['timeout'],180)
                self.assertEqual(args[args.index('--ready-writer-evidence')+1:-1], ['tcp','128','128','4096','12','524288','16','16384'])
                self.assertEqual(kwargs['env']['SHARPLINK_READY_PREPARED_BYTES'],'0');self.assertIn(kwargs['env']['SHARPLINK_READY_TCP_RECEIVE_BUFFER'],('0','262144'))
            with self.assertRaises(FileExistsError): run.execute(ROOT,Path(directory),Path('/unused.dll'),[0,1,2,3])

    def test_diagnostic_marker_and_partial_data_required(self):
        doc=dict(metadata=dict(DiagnosticCapture=True),status='completed',error=None,samples=[{}]*48)
        self.assertTrue(run.validate_report(doc)); doc['samples'].pop();self.assertFalse(run.validate_report(doc))
        doc['metadata']['DiagnosticCapture']=False
        with self.assertRaises(ValueError):run.validate_report(doc)

    def test_process_timeout_does_not_erase_other_controls(self):
        with tempfile.TemporaryDirectory() as directory, patch.object(run.subprocess,'run',side_effect=subprocess.TimeoutExpired('diagnostic',180)) as child:
            self.assertEqual(run.execute(ROOT,Path(directory),Path('/unused.dll'),[0,1,2,3]),[0,1,2,3])
            self.assertEqual(child.call_count,4)
            self.assertTrue(all(json.loads(p.read_text())['code']==124 for p in Path(directory).glob('*.exit')))


if __name__ == '__main__':unittest.main()
