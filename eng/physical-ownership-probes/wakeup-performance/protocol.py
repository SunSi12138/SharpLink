"""Frozen W1 topology/schedule and exact statistics; no timing on import."""
import json
import math
from pathlib import Path

BASELINE='f724c7aaabdeb5e02bec23f32f6b3c709860e97c'
CANDIDATE='e5a35b9bf63d162c3077aec389d402e28bd2fb53'
CANDIDATE_ORIGINAL='c17f434e8ec8d019625e64c7f51a4c8eb8e55981'
CANDIDATE_TREE='ea401a81c4b5a52966f4281acc348f838b9fc935'
TOPOLOGIES=('different-reported-cores','same-reported-core-smt')
ROUNDS=6
WARMUP=3
SECONDS=6

def cpu_set(text):
    result=set()
    for part in text.split(','):
        bounds=list(map(int,part.split('-')))
        if len(bounds)==1: result.add(bounds[0])
        elif len(bounds)==2 and bounds[0]<=bounds[1]: result.update(range(bounds[0],bounds[1]+1))
        else: raise ValueError('Invalid CPU set')
    return result

def select_topologies(rows):
    """Lexicographic selection using guest socket/core IDs AND sibling sets, never outcome."""
    selected={kind:None for kind in TOPOLOGIES}
    for a in sorted(rows):
        for b in sorted(rows):
            if a>=b: continue
            x,y=rows[a],rows[b]
            if x['socket']==y['socket'] and x['core']!=y['core'] and b not in x['siblings'] and a not in y['siblings']:
                selected[TOPOLOGIES[0]]=selected[TOPOLOGIES[0]] or [a,b]
            if (x['socket'],x['core'])==(y['socket'],y['core']) and b in x['siblings'] and a in y['siblings']:
                selected[TOPOLOGIES[1]]=selected[TOPOLOGIES[1]] or [a,b]
    return selected

def topology(allowed):
    rows={}
    for cpu in sorted(allowed):
        root=Path(f'/sys/devices/system/cpu/cpu{cpu}/topology')
        rows[cpu]={'socket':int((root/'physical_package_id').read_text()),'core':int((root/'core_id').read_text()),
                   'siblings':sorted(cpu_set((root/'thread_siblings_list').read_text().strip()))}
    if any(row['socket']<0 or row['core']<0 for row in rows.values()): raise ValueError('Unknown guest topology IDs')
    return {'allowedCpus':sorted(allowed),'reportedTopology':rows,'selected':select_topologies(rows),
            'boundary':'Guest-reported topology only; no host physical isolation, producer/consumer thread placement or branch-hit proof.'}

def schedule(selected):
    cells=[(kind,workers) for kind in TOPOLOGIES if selected[kind] for workers in (1,8)]
    result=[]
    for round_number in range(1,ROUNDS+1):
        offset=(round_number-1)%len(cells) if cells else 0
        for kind,workers in cells[offset:]+cells[:offset]:
            for arm in (('baseline','candidate') if round_number%2 else ('candidate','baseline')):
                result.append({'topology':kind,'workers':workers,'round':round_number,'arm':arm,
                    'sample':f'static-intercepted-n100-c{workers}-r{round_number}-{arm}',
                    'cpus':selected[kind]})
    return result

def exact_rank(count,num,den):
    if type(count) is not int or count<=0 or not 0<num<=den: raise ValueError('Invalid nearest-rank arguments')
    return (count*num+den-1)//den-1

def validate_ticks(data,sample):
    if data.get('complete') is not True:raise ValueError('Partial/failed tick evidence')
    n=sample['operations']; workers=sample['workers']; frequency=data['stopwatchFrequency']
    if type(frequency) is not int or frequency<=0 or (data['workers'],data['operations'])!=(workers,n):raise ValueError('Tick metadata mismatch')
    rows=data['samples']
    if len(rows)!=workers or {r['worker'] for r in rows}!=set(range(workers)):raise ValueError('Worker tick inventory')
    ticks=[value for row in rows for value in row['durationTicks']]
    if len(ticks)!=n or any(type(v) is not int or v<0 for v in ticks):raise ValueError('Tick count/value mismatch')
    if any(not row['durationTicks'] for row in rows):raise ValueError('Worker completed no requests')
    ticks.sort()
    for metric,num,den in [('p50Us',1,2),('p95Us',19,20),('p99Us',99,100),('p999Us',999,1000),('maxUs',1,1)]:
        expected=ticks[exact_rank(n,num,den)]*1e6/frequency
        if not math.isclose(sample[metric],expected,rel_tol=1e-12,abs_tol=1e-9):raise ValueError(f'Exact tick percentile mismatch: {metric}')
    for metric,expected in [('meanUs',sum(ticks)*1e6/frequency/n),('operationsPerSecond',n/sample['elapsedSeconds']),
                            ('allocatedBytesPerOperation',sample['totalAllocatedBytes']/n),('cpuUsPerOperation',sample['totalCpuMicroseconds']/n)]:
        if not math.isclose(sample[metric],expected,rel_tol=1e-10,abs_tol=1e-8):raise ValueError(f'Counter mismatch: {metric}')
