import copy,unittest
from run import launch_plan
from validate import round_metrics
class Controls(unittest.TestCase):
 def fixture(self):
  return dict(operations=4096,elapsedTicks=409600,elapsedTimeSpanTicks=4096000,allocatedBefore=1000,allocatedAfter=9192,cpuBeforeTimeSpanTicks=0,cpuAfterTimeSpanTicks=409600,cpuMicrosecondsPerOperation=10.,stopwatchFrequency=1000000,allocatedBytes=8192,sortedLatencyTicks=[100]*4096,qps=10000.,allocatedBytesPerOperation=2.,p50Microseconds=100.,p99Microseconds=100.)
 def test_order(self):
  plan=launch_plan();self.assertEqual([r['index'] for r in plan],list(range(18)))
  self.assertEqual(len(set(tuple(r['state'] for r in plan[b*3:b*3+3]) for b in range(6))),6)
 def test_metrics(self):self.assertEqual(round_metrics(self.fixture())['qps'],10000)
 def test_mutations(self):
  for k,v in dict(operations=4095,elapsedTicks=0,stopwatchFrequency=0,allocatedBytes=-1,sortedLatencyTicks=[100],qps=float('nan'),p99Microseconds=101.,allocatedBytesPerOperation=3.).items():
   r=self.fixture();r[k]=v
   with self.subTest(key=k),self.assertRaises(ValueError):round_metrics(r)
if __name__=='__main__':unittest.main()

class DocumentControls(unittest.TestCase):
 def fixture(self):
  from run import HEAD
  from validate import check_document
  cells=[]
  for payload in (32,4096):
   rounds=[dict(Controls().fixture(),round=i) for i in range(5)]
   cells.append(dict(state='runtime-no-update',profile='Balanced',payloadBytes=payload,concurrency=1,operationsPerRound=4096,warmupOperations=1024,flushSizeThreshold=16384,maxLatencyMicroseconds=1000,rawRounds=rounds,qps=10000.,allocatedBytesPerOperation=2.,p50Microseconds=100.,p99Microseconds=100.,cpuMicrosecondsPerOperation=10.))
  return dict(state='runtime-no-update',commit=HEAD,rounds=5,framework='.NET 10.0.14',architecture='X64',measurements=cells)
 def test_document(self):
  from validate import check_document
  self.assertEqual(len(check_document(self.fixture(),'runtime-no-update')),2)
 def test_document_negative(self):
  from validate import check_document
  for change in [lambda d:d.update(commit='wrong'),lambda d:d.update(framework='.NET 10.0.13'),lambda d:d['measurements'].pop(),lambda d:d['measurements'].reverse(),lambda d:d['measurements'][0].update(concurrency=8),lambda d:d['measurements'][0]['rawRounds'].pop(),lambda d:d['measurements'][0]['rawRounds'][0].update(round=4),lambda d:d['measurements'][0].update(qps=9999.)]:
   d=self.fixture();change(d)
   with self.assertRaises(ValueError):check_document(d,'runtime-no-update')
