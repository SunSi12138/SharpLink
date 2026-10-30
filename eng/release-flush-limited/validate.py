#!/usr/bin/env python3
"""Independently recompute finite flush raw rounds, never assign performance PASS."""
import argparse, hashlib, json, math, statistics
from pathlib import Path
from run import BASE,BASE_TREE,HEAD,TREE,OVERLAY,launch_plan,sha

def close(a,b):
 if isinstance(a,bool) or not isinstance(a,(int,float)) or not math.isfinite(a) or not math.isclose(a,b,rel_tol=1e-9,abs_tol=1e-7):raise ValueError(f'Metric mismatch: {a} != {b}')
def integer(x,positive=False):
 if type(x)is not int or x< (1 if positive else 0):raise ValueError('Nonnegative/positive integer expected')
 return x

def round_metrics(raw):
 n=integer(raw['operations'],True);ticks=integer(raw['elapsedTicks'],True);freq=integer(raw['stopwatchFrequency'],True);allocated=integer(raw['allocatedBytes']);elapsed_timespan=integer(raw['elapsedTimeSpanTicks'],True)
 before=integer(raw['allocatedBefore']);after=integer(raw['allocatedAfter']);cpu_before=integer(raw['cpuBeforeTimeSpanTicks']);cpu_after=integer(raw['cpuAfterTimeSpanTicks'])
 if after-before!=allocated or cpu_after<cpu_before or abs(elapsed_timespan-ticks*10000000/freq)>1.01:raise ValueError('Raw allocation/clock boundary mismatch')
 lat=raw['sortedLatencyTicks']
 if n!=4096 or not isinstance(lat,list) or len(lat)!=n or lat!=sorted(lat):raise ValueError('Raw sample count/order changed')
 for x in lat:integer(x,True)
 def pct(p):
  position=p*(n-1);lo=math.floor(position);hi=min(lo+1,n-1)
  return round(lat[lo]+((lat[hi]-lat[lo])*(position-lo)))*1_000_000/freq
 expected={'qps':n*10000000/elapsed_timespan,'cpuMicrosecondsPerOperation':(cpu_after-cpu_before)/10/n,'allocatedBytesPerOperation':allocated/n,'p50Microseconds':pct(.50),'p99Microseconds':pct(.99)}
 for k,v in expected.items():close(raw[k],v)
 return expected

def check_document(doc,state):
 if doc['state']!=state or doc['commit']!=(BASE if state=='static-baseline' else HEAD) or doc['rounds']!=5:raise ValueError('Document identity changed')
 if doc['framework']!='.NET 10.0.14' or doc['architecture']!='X64':raise ValueError('Actual runtime/architecture not pinned')
 cells=doc['measurements']
 if len(cells)!=2 or [c['payloadBytes'] for c in cells]!=[32,4096]:raise ValueError('Missing/extra/reordered cells')
 output=[]
 for c in cells:
  if (c['state'],c['profile'],c['concurrency'],c['operationsPerRound'],c['warmupOperations'],c['flushSizeThreshold'],c['maxLatencyMicroseconds'])!=(state,'Balanced',1,4096,1024,16384,1000):raise ValueError('Workload parameters changed')
  samples=c['rawRounds']
  if len(samples)!=5 or [x['round'] for x in samples]!=list(range(5)):raise ValueError('Missing/reordered rounds')
  metrics=[round_metrics(x) for x in samples]
  for k in metrics[0]:close(c[k],statistics.median(x[k] for x in metrics))
  output.append({'payload':c['payloadBytes'],'rounds':metrics,'median':{k:c[k] for k in metrics[0]}})
 return output

def validate(out):
 execution=json.loads((out/'execution.json').read_text());plan=launch_plan()
 if not execution.get('completed') or execution['plannedLaunches']!=plan or len(execution['launches'])!=18:raise ValueError('Incomplete/changed campaign')
 if (execution['baseline'],execution['candidate'],execution['candidateTree'])!=(BASE,HEAD,TREE):raise ValueError('Source identity differs')
 if execution.get('sourceTrees')!={'baseline':BASE_TREE,'candidate':TREE}:raise ValueError('Source tree map differs')
 if set(execution.get('binaries',{}))!={'baseline','candidate'} or any(not x for x in execution['binaries'].values()):raise ValueError('Both nonempty binary closures required')
 root=Path(__file__).resolve().parents[2]
 overlays={rel:sha(root/'eng/release-flush-limited/RpcSessionFlushPolicyEvidenceRunner.cs' if rel.endswith('RpcSessionFlushPolicyEvidenceRunner.cs') else root/rel) for rel in OVERLAY}
 if execution.get('overlays')!=overlays:raise ValueError('Harness overlay identity differs')
 if set(execution.get('sourceRoots',{}))!={'baseline','candidate'}:raise ValueError('Source roots missing')
 expected_files={f"{r['index']:02d}-{r['state']}.json" for r in plan}
 actual_files={x.name for x in out.glob('[0-9][0-9]-*.json') if not x.name.endswith('.receipt.json')}
 if expected_files!=actual_files:raise ValueError('Missing/extra measurement file')
 for name, hashes in execution['binaries'].items():
  if not {'SharpLink.Benchmarks.dll','SharpLink.Runtime.dll','SharpLink.Client.dll','SharpLink.Server.dll','SharpLink.Abstractions.dll'}.issubset(hashes):raise ValueError('Required executed assemblies missing from closure')
  folder=out/'binaries'/name
  actual={str(x.relative_to(folder)):sha(x) for x in sorted(folder.rglob('*')) if x.is_file()}
  if actual!=hashes:raise ValueError('Archived execution closure changed')
 reports=[];runtime_files=None
 for p,actual in zip(plan,execution['launches']):
  if any(actual.get(k)!=v for k,v in p.items()) or actual['status']!='completed':raise ValueError('Launch order/failure')
  f=out/f"{p['index']:02d}-{p['state']}.json"
  receipt=json.loads(f.with_suffix('.receipt.json').read_text())
  source=Path(execution['sourceRoots'][p['arm']]);original_output=Path(execution['outputRoot'])/f.name
  expected_argv=['dotnet','exec','--fx-version','10.0.14',str(source/'test/SharpLink.Benchmarks/bin/Release/net10.0/SharpLink.Benchmarks.dll'),'--rpc-session-flush-policy-evidence',p['state'],str(original_output)]
  if receipt['argv']!=expected_argv or receipt['cwd']!=str(source) or receipt['timeoutSeconds']!=180:raise ValueError('Wrong command/arm/state/output/timeout')
  if receipt['exitCode']!=0 or receipt.get('processGroupQuiescent') is not True or any(receipt.get(k) for k in ('timedOut','cleanupTimedOut','descendantLeak','error','cleanupError')):raise ValueError('Process failed or cleanup uncertain')
  doc=json.loads(f.read_text());files=doc['runtimeFiles']
  if {Path(x).name for x in files}!={'dotnet','System.Private.CoreLib.dll','libcoreclr.so','libclrjit.so','libhostpolicy.so'} or len(files)!=5 or not all(isinstance(v,str) and len(v)==64 and all(x in '0123456789ABCDEF' for x in v) for v in files.values()):raise ValueError('Runtime binary hashes missing')
  if runtime_files is not None and files!=runtime_files:raise ValueError('Runtime binary files changed between states')
  runtime_files=files
  cells=check_document(doc,p['state'])
  reports.append(dict(p,cells=cells))
 rows=[]
 for block in range(6):
  group={r['state']:r for r in reports if r['block']==block}
  for i,payload in enumerate((32,4096)):
   base=group['static-baseline']['cells'][i]['median']
   for state in ('runtime-no-update','post-update'):
    head=group[state]['cells'][i]['median'];rows.append({'block':block,'payload':payload,'state':state,'qpsRatio':head['qps']/base['qps'],'p99Ratio':head['p99Microseconds']/base['p99Microseconds'],'allocationRatio':head['allocatedBytesPerOperation']/base['allocatedBytesPerOperation']})
 return {'scope':'limited-descriptive','performanceDecision':'not-assessed','sourceBaseline':BASE,'sourceCandidate':HEAD,'rawRoundCount':180,'launches':reports,'pairedBlockRatios':rows}

def main():
 p=argparse.ArgumentParser();p.add_argument('--output',type=Path,required=True);a=p.parse_args()
 try:
  result=validate(a.output);dest=a.output/'validated-summary.json'
  with dest.open('x')as f:json.dump(result,f,indent=2)
  for row in result['pairedBlockRatios']:print('FLUSH_BLOCK '+json.dumps(row,sort_keys=True))
  print('Raw 180 rounds retained; operation/five-round/block metrics kept separate; no performance acceptance or causal attribution.')
 except Exception as e:
  (a.output/'validation-failure.txt').write_text(type(e).__name__+': '+str(e)+'\n');raise
if __name__=='__main__':main()
