#!/usr/bin/env python3
"""Read raw fixed B2 sized-DTO reports; independently recompute adjacent pairs.

Run the frozen strict source/sample validator separately. This file neither
imports it nor treats its metric summaries as inputs. No failed/missing pair is
imputed, and no runtime or measurement-source population is pooled.
"""
import argparse
import csv
import hashlib
import json
import math
from pathlib import Path
import re
import statistics

SHA = '5cabd4d7cc5ce9aa0a87424e36954e414e0912da'
BASE = 'c7ba1768f31ce7219dc438dc6009347de6806ed5'
METRICS = ['throughputRpcPerSecond','cpuMicrosecondsPerRpc','allocationBytesPerRpc',
 'settledThroughputRpcPerSecond','settledCpuMicrosecondsPerRpc','settledAllocationBytesPerRpc',
 'p50Microseconds','p95Microseconds','p99Microseconds','p999Microseconds',
 'threadPoolCompletedWorkItemsPerRpc','gen0Collections','gen1Collections','gen2Collections',
 'gcPauseTotalMicroseconds','completedToSettledMicroseconds']
COUNTERS = ['b2ActivatedStreams','b2DisposedStreams','b2FirstProbeFallbacks','b2LocalItems',
 'b2OwnerRefills','b2ExactSlowAttempts','b2RecalledGrantBytes','b2RefundedItemBytes']

def h(path): return hashlib.sha256(path.read_bytes()).hexdigest()
def obj(path): return json.loads(path.read_text())
def report(path):
 values={}
 for line in path.read_text(encoding='utf-8-sig').splitlines():
  if '=' not in line:continue
  key,value=line.split('=',1)
  assert key not in values, (path,key)
  values[key]=value
 return values
def summary(values):
 return {'n':len(values),'median':statistics.median(values) if values else None,
  'min':min(values) if values else None,'max':max(values) if values else None,'raw':values}
def close(a,b): assert math.isclose(a,b,rel_tol=1e-9,abs_tol=1e-6),(a,b)

def main():
 p=argparse.ArgumentParser();p.add_argument('--input',type=Path,required=True);p.add_argument('--output',type=Path,required=True)
 args=p.parse_args();root=args.input
 plan=obj(root/'screen-plan.json');exits=obj(root/'exits.json')
 assert plan['payloadKind']=='sized-dto' and plan['candidateSha']==SHA and plan['baselineSha']==BASE
 assert plan['abOrders']==[['A','B','B','A'],['B','A','A','B']]
 planned={}
 for ri,order in enumerate(plan['abOrders']):
  for transport in ['tcp','sharedmemory']:
   for shape,items,size,c,ops in plan['cases']:
    cell=f'{transport}-{shape}-{items}x{size}-c{c}'
    for position,arm in enumerate(order):planned[f'ab-r{ri}-{cell}-p{position}-{arm}']=(cell,ri,position,arm,shape,items,size,c,ops)
 for transport in ['tcp','sharedmemory']:
  for shape,items,size,c,ops in plan['aaCases']:
   cell=f'{transport}-{shape}-{items}x{size}-c{c}'
   for ri in range(2):
    for side in ['left','right']:planned[f'aa-r{ri}-{cell}-{side}']=(cell,ri,side,'A',shape,items,size,c,ops)
 assert len(planned)==144
 executed={r['name']:r for r in exits if r['name'].startswith(('ab-','aa-'))}
 assert len(executed)==sum(r['name'].startswith(('ab-','aa-')) for r in exits)
 assert set(executed)<=set(planned)
 reports={};processes=[];identities=set();payloads={};rpc_count=0
 for label,work in planned.items():
  path=root/'samples'/(label+'.txt');entry={'label':label,'status':'NotRun'}
  if label not in executed:
   assert not path.exists();processes.append(entry);continue
  entry.update(exitCode=executed[label]['exitCode'],status='Failed' if executed[label]['exitCode'] else 'Passed')
  if executed[label]['exitCode']:
   if path.exists():entry['reportSha256']=h(path)
   processes.append(entry);continue
  r=report(path);cell,ri,pos,arm,shape,items,size,c,ops=work
  assert r['status']=='passed' and r['payloadKind']=='sized-dto'
  assert r['baselineSha']==BASE and r['candidateSha']==(SHA if arm=='B' else BASE)
  assert r['actualItemCanExactSize']=='true' and int(r['actualEncodedItemBytes'])==size
  length=(size-10)//2;text=''.join(chr(97+(i*17+11)%26) for i in range(length))
  wire=b'\x01\x0e'+(length*2).to_bytes(4,'little')+text.encode('utf-16-le')+b'\x12\x5b\x73\x00'
  assert len(wire)==size and hashlib.sha256(wire).hexdigest().upper()==r['actualEncodedItemSha256']
  assert int(r['actualDtoTextCharacters'])==length and int(r['actualDtoTag'])==0x735B
  assert int(r['actualUncompressedStreamDataFrameBytes'])==size+17
  payload=(r['actualItemCodec'],r['actualEncodedItemSha256'])
  if size in payloads:assert payloads[size]==payload
  payloads[size]=payload
  identities.add(r['measurementSourceSha'])
  expected=c*ops;assert int(r['successfulRpcs'])==expected
  close(float(r['throughputRpcPerSecond']),expected/float(r['elapsedSeconds']))
  close(float(r['cpuMicrosecondsPerRpc']),float(r['cpuTotalMicroseconds'])/expected)
  close(float(r['allocationBytesPerRpc']),float(r['allocationBytesTotal'])/expected)
  frequency=int(r['stopwatchFrequency']);latencies=[];counts=[0]*c;last=[0]*c
  sample_path=path.with_name(path.name+'.samples.csv')
  with sample_path.open(encoding='utf-8-sig') as f:
   for row in csv.DictReader(f):
    worker,op=int(row['worker']),int(row['operation']);assert 0<=worker<c and op==counts[worker]
    start,end,ticks=map(int,[row['start_timestamp'],row['end_timestamp'],row['latency_ticks']])
    assert start>=last[worker] and ticks==end-start and ticks>=0
    close(float(row['latency_us']),ticks*1e6/frequency)
    latencies.append(ticks*1e6/frequency);counts[worker]+=1;last[worker]=end
  assert len(latencies)==expected and counts==[ops]*c
  latencies.sort()
  for key,fraction in [('p50Microseconds',.5),('p95Microseconds',.95),('p99Microseconds',.99),('p999Microseconds',.999)]:close(float(r[key]),latencies[math.ceil(expected*fraction)-1])
  counters={key:int(r[key]) for key in COUNTERS};assert all(v>=0 for v in counters.values())
  if arm=='B' and shape in ('Server','Duplex'):
   assert counters['b2ActivatedStreams']>0
   assert counters['b2ActivatedStreams']==counters['b2DisposedStreams']
   assert counters['b2ActivatedStreams']+counters['b2FirstProbeFallbacks']==expected
   assert counters['b2RefundedItemBytes']==0
  else:assert not any(counters.values())
  entry.update(reportSha256=h(path),rawSampleSha256=h(sample_path),successfulRpcs=expected,
    verifiedRawQuantiles=True,counters=counters,metrics={k:float(r[k]) for k in METRICS})
  processes.append(entry);reports[label]=r;rpc_count+=expected
 assert len(identities)<=1
 cells={};aa={}
 for transport in ['tcp','sharedmemory']:
  for shape,items,size,c,ops in plan['cases']:
   cell=f'{transport}-{shape}-{items}x{size}-c{c}';pairs=[]
   for ri,order in enumerate(plan['abOrders']):
    for position in [0,2]:
     labels={order[pos]:f'ab-r{ri}-{cell}-p{pos}-{order[pos]}' for pos in [position,position+1]}
     pair={'round':ri,'firstPosition':position,**labels,'complete':all(l in reports for l in labels.values())}
     if pair['complete']:
      a,b=(reports[labels[k]] for k in ['A','B'])
      pair['changesPercent']={k:(float(b[k])/float(a[k])-1)*100 if float(a[k]) else None for k in METRICS}
      pair['allocationDeltaBPerRpc']=float(b['allocationBytesPerRpc'])-float(a['allocationBytesPerRpc'])
      pair['countersPerRpc']={k:int(b[k])/int(b['successfulRpcs']) for k in COUNTERS}
     pairs.append(pair)
   cells[cell]={'pairs':pairs,'completedPairs':sum(p['complete'] for p in pairs),
    'changesPercent':{k:summary([p['changesPercent'][k] for p in pairs if p['complete'] and p['changesPercent'][k] is not None]) for k in METRICS},
    'allocationDeltaBPerRpc':summary([p['allocationDeltaBPerRpc'] for p in pairs if p['complete']])}
  for shape,items,size,c,ops in plan['aaCases']:
   cell=f'{transport}-{shape}-{items}x{size}-c{c}';pairs=[]
   for ri in range(2):
    left,right=f'aa-r{ri}-{cell}-left',f'aa-r{ri}-{cell}-right'
    pair={'round':ri,'left':left,'right':right,'complete':left in reports and right in reports}
    if pair['complete']:pair['changesPercent']={k:(float(reports[right][k])/float(reports[left][k])-1)*100 if float(reports[left][k]) else None for k in METRICS}
    pairs.append(pair)
   aa[cell]={'pairs':pairs,'changesPercent':{k:summary([p['changesPercent'][k] for p in pairs if p['complete'] and p['changesPercent'][k] is not None]) for k in METRICS}}
 output={'classification':'independent-raw-adjacent-pair-recalculation-stage1-not-production-Go',
  'candidateSha':SHA,'baselineSha':BASE,'runtime':plan['runtimePopulation'],'payloadKind':'sized-dto',
  'measurementHashes':sorted(identities),'plannedProcesses':144,'successfulProcesses':len(reports),
  'failedProcesses':sum(x['status']=='Failed' for x in processes),'notRunProcesses':sum(x['status']=='NotRun' for x in processes),
  'individualRpcSamplesRechecked':rpc_count,'processes':processes,'cells':cells,'aa':aa,
  'analysisScriptSha256':h(Path(__file__)),'planSha256':h(root/'screen-plan.json'),'exitsSha256':h(root/'exits.json')}
 args.output.write_text(json.dumps(output,indent=2,allow_nan=False)+'\n')
 print(json.dumps({k:v for k,v in output.items() if k not in ('processes','cells','aa')},indent=2))

if __name__=='__main__':main()
