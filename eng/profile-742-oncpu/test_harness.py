#!/usr/bin/env python3
"""Deterministic parser/header negatives; fixtures are not successful perf captures."""
import json
import pathlib
import struct
import tempfile
import unittest
from common import Blocked, inspect_perf_data
from parse import calibration, parse_script, slice_samples

class Tests(unittest.TestCase):
    def setUp(self):
        self.temp=tempfile.TemporaryDirectory();self.root=pathlib.Path(self.temp.name)
    def tearDown(self):self.temp.cleanup()
    def data(self, record_types=(9,), *, clock=1, event_type=1, flags=None):
        flags=flags if flags is not None else (1<<1)|(1<<5)|(1<<10)|(1<<25)
        attr=bytearray(144)
        struct.pack_into('<IIQQQ',attr,0,event_type,128,0,99,(1<<1)|(1<<2)|(1<<8)|(1<<12)|(1<<13))
        struct.pack_into('<Q',attr,40,flags);struct.pack_into('<i',attr,92,clock)
        data=b''
        for kind in record_types:
            payload=struct.pack('<QQ',1,2) if kind==2 else struct.pack('<Q',2)
            data+=struct.pack('<IHH',kind,0,8+len(payload))+payload
        header=bytearray(104);header[:8]=b'PERFILE2'
        struct.pack_into('<6Q',header,8,104,144,104,144,248,len(data))
        path=self.root/'fixture.perf.data';path.write_bytes(header+attr+data);return path
    def test_valid_header(self):
        r=inspect_perf_data(self.data());self.assertEqual(r['raw_sample_records'],1);self.assertEqual(r['events_lost'],0)
    def test_lost_and_throttle_and_compressed_rejected(self):
        for kind in (2,13,5,6,81):
            with self.subTest(kind=kind),self.assertRaises(Blocked):inspect_perf_data(self.data((9,kind)))
    def test_wrong_clock_and_event_rejected(self):
        for kwargs in ({'clock':4},{'event_type':0},{'flags':0}):
            with self.subTest(kwargs=kwargs),self.assertRaises(Blocked):inspect_perf_data(self.data(**kwargs))
    def test_truncated_rejected(self):
        path=self.data();path.write_bytes(path.read_bytes()[:-1])
        with self.assertRaises(Blocked):inspect_perf_data(path)
    def fixture(self):
        lines=[]
        def add(t,tid,symbol):
            lines.append(f'  123/{tid} {t:.9f}: 10000000 cpu-clock:u:\n\t0000000000400100 {symbol} (/tmp/perf-calibration)\n\t0000000000400200 main (/tmp/perf-calibration)\n')
        for i in range(20):add(0.1+i*.01,123,'PerfBusyBefore')
        for i in range(80):
            add(1.1+i*.01,123,'PerfBusyInside');add(1.105+i*.01,124,'PerfWorkerInside')
        for i in range(20):add(4.1+i*.01,123,'PerfBusyAfter')
        path=self.root/'script';path.write_text('\n'.join(lines))
        w=dict(clock='CLOCK_MONOTONIC',clockId=1,pid=123,workerTid=124,beginNs=1_000_000_000,sleepBeginNs=3_000_000_000,endNs=4_000_000_000,userCpuNs=1_600_000_000)
        return parse_script(path),w,dict(raw_sample_records=200)
    def test_monotonic_phase_and_inheritance_fixture(self):
        samples,w,audit=self.fixture();result=calibration(samples,w,audit);self.assertTrue(all(result['checks'].values()))
    def test_misaligned_window_rejected(self):
        samples,w,audit=self.fixture();w['beginNs']=0
        with self.assertRaises(Blocked):calibration(samples,w,audit)
    def test_incomplete_decoder_rejected(self):
        samples,w,audit=self.fixture();audit['raw_sample_records']+=1
        with self.assertRaises(Blocked):slice_samples(samples,w,audit)
    def test_wrong_pid_rejected(self):
        samples,w,audit=self.fixture();samples[0]['pid']=999
        with self.assertRaises(Blocked):slice_samples(samples,w,audit)
    def test_header_leaf_duplicate(self):
        p=self.root/'header';p.write_text(' 123 124 2.000000000: 10000 cpu-clock:u: 40100 Leaf (/app)\n 40100 Leaf (/app)\n 40200 Caller (/app)\n')
        self.assertEqual(len(parse_script(p)[0]['frames']),2)
    def test_lost_or_unknown_text_rejected(self):
        for text in ('PERF_RECORD_LOST lost=2','unexpected payload'):
            p=self.root/'bad';p.write_text(text)
            with self.assertRaises(Blocked):parse_script(p)

if __name__=='__main__':unittest.main(verbosity=2)
