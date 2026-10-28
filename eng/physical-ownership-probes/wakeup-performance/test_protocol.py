import copy
import importlib.util
import json
from pathlib import Path
import sys
import unittest
sys.path.insert(0,str(Path(__file__).resolve().parent))
from protocol import *
from verify_request import validate_request,BRANCH,MESSAGE,REQUEST

class ProtocolTests(unittest.TestCase):
    def test_exact_percentile_boundaries(self):
        for count in [1,2,10,100,1000,2000,10000,1000000,2**31-1]:
            for num,den in [(1,2),(19,20),(99,100),(999,1000),(1,1)]:
                index=exact_rank(count,num,den)
                self.assertLessEqual(index*den,count*num-1)
                self.assertGreaterEqual((index+1)*den,count*num)
                self.assertTrue(0<=index<count)
        self.assertEqual(exact_rank(1000,999,1000),998)
        self.assertEqual(exact_rank(2000,999,1000),1997)
        self.assertEqual(exact_rank(1000000,999,1000),998999)
    def test_invalid_ranks(self):
        for args in [(0,1,2),(1,0,2),(1,3,2)]:
            with self.assertRaises(ValueError):exact_rank(*args)
    def fixture(self):
        return {0:dict(socket=0,core=0,siblings=[0,1]),1:dict(socket=0,core=0,siblings=[0,1]),2:dict(socket=0,core=1,siblings=[2,3]),3:dict(socket=0,core=1,siblings=[2,3])}
    def test_topology_no_false_crosscore(self):
        selected=select_topologies(self.fixture())
        self.assertEqual(selected,dict(zip(TOPOLOGIES,([0,2],[0,1]))))
        self.assertIsNone(select_topologies({k:v for k,v in self.fixture().items() if k<2})[TOPOLOGIES[0]])
    def test_no_substitution(self):
        selected=select_topologies({k:v for k,v in self.fixture().items() if k in (0,2)})
        self.assertIsNone(selected[TOPOLOGIES[1]])
        self.assertEqual(len(schedule(selected)),24)
        self.assertEqual(schedule(dict.fromkeys(TOPOLOGIES)),[])
    def test_balanced_rotating_schedule(self):
        seq=schedule(select_topologies(self.fixture()));self.assertEqual(len(seq),48)
        self.assertEqual(len({(c['topology'],c['sample']) for c in seq}),48)
        for kind in TOPOLOGIES:
            for workers in (1,8):
                pairs=[c for c in seq if c['topology']==kind and c['workers']==workers]
                self.assertEqual([c['arm'] for c in pairs[::2]],['baseline','candidate']*3)
        self.assertEqual([(seq[i]['topology'],seq[i]['workers']) for i in (0,8,16,24)],[(TOPOLOGIES[0],1),(TOPOLOGIES[0],8),(TOPOLOGIES[1],1),(TOPOLOGIES[1],8)])
    def test_cpus(self):self.assertEqual(cpu_set('0-2,4'),{0,1,2,4})
    def request_args(self):
        head='1'*40;parent='2'*40;tree='3'*40
        return [dict(state='authorized',campaign='fenced-wakeup-48tcp-v1',reviewedToolCommit=parent,reviewedToolTree=tree,baseline=BASELINE,candidate=CANDIDATE,candidateTree=CANDIDATE_TREE),dict(GITHUB_EVENT_NAME='push',GITHUB_REF=BRANCH,GITHUB_RUN_ATTEMPT='1',GITHUB_REPOSITORY='SunSi12138/SharpLink',GITHUB_SHA=head),head,parent,tree,[REQUEST],MESSAGE,True]
    def test_request_positive(self):validate_request(*self.request_args())
    def test_request_failclosed(self):
        for index,value in [(5,[REQUEST,'src/change.cs']),(6,'rerun'),(7,False)]:
            args=self.request_args();args[index]=value
            with self.assertRaises(ValueError):validate_request(*args)
        for key,value in [('GITHUB_RUN_ATTEMPT','2'),('GITHUB_REF','refs/heads/dev')]:
            args=self.request_args();args[1][key]=value
            with self.assertRaises(ValueError):validate_request(*args)
        for key,value in [('state','review-required'),('baseline','0'*40),('reviewedToolTree','0'*40)]:
            args=self.request_args();args[0][key]=value
            with self.assertRaises(ValueError):validate_request(*args)
    def test_ticks_multiworker_boundary(self):
        values=list(range(1,1001));sample=dict(workers=8,operations=1000,elapsedSeconds=2,totalAllocatedBytes=2000,totalCpuMicroseconds=3000)
        raw=dict(complete=True,workers=8,operations=1000,stopwatchFrequency=1000000,samples=[dict(worker=w,durationTicks=values[w::8]) for w in range(8)])
        for key,num,den in [('p50Us',1,2),('p95Us',19,20),('p99Us',99,100),('p999Us',999,1000),('maxUs',1,1)]:sample[key]=values[exact_rank(1000,num,den)]
        sample.update(meanUs=500.5,operationsPerSecond=500,allocatedBytesPerOperation=2,cpuUsPerOperation=3)
        validate_ticks(raw,sample)
        bad=copy.deepcopy(raw);bad['samples'][0]['durationTicks'].pop()
        with self.assertRaises(ValueError):validate_ticks(bad,sample)
        bad=copy.deepcopy(sample);bad['p999Us']=1000
        with self.assertRaises(ValueError):validate_ticks(raw,bad)

if __name__=='__main__':unittest.main()
