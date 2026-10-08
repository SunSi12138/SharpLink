#!/usr/bin/env python3
"""Matched unfiltered microkernel; compiler hints alone, no lifecycle changes."""
import argparse
import hashlib
import json
import math
import os
import pathlib
import statistics
import subprocess

p=argparse.ArgumentParser()
p.add_argument('control',type=pathlib.Path)
p.add_argument('candidate',type=pathlib.Path)
p.add_argument('output',type=pathlib.Path)
p.add_argument('--runtime',choices=('jit-pgo0','jit-pgo1','native'),required=True)
a=p.parse_args()
roots={k:getattr(a,k).resolve() for k in ('control','candidate')}
out=a.output.resolve();out.mkdir(parents=True,exist_ok=True)
relative='artifacts/native-inline/SharpLink.Benchmarks' if a.runtime=='native' else 'test/SharpLink.Benchmarks/bin/Release/net10.0/SharpLink.Benchmarks.dll'
binaries={k:root/relative for k,root in roots.items()}
plan=[(r,k) for r in range(3) for k in (('control','candidate') if r%2==0 else ('candidate','control'))]
(out/'provenance.json').write_text(json.dumps(dict(runtime=a.runtime,plan=plan,
    baseline='e834d3c28c87ad496989af925515cf21babd308d',
    changes='two receive lease validation AggressiveInlining hints only',
    arguments=['--repetitions','3','--contention-items','20000'],
    binary_sha256={k:hashlib.sha256(b.read_bytes()).hexdigest() for k,b in binaries.items()},
    boundary='Micro attribution including every original cell; no production RPC claim'),indent=2)+'\n')
exits=[]
for repeat,label in plan:
    env=dict(os.environ,DOTNET_PROCESSOR_COUNT='4')
    if a.runtime=='native':command=['taskset','-c','0-3',str(binaries[label])]
    else:
        env.update(DOTNET_ReadyToRun='0',DOTNET_TieredCompilation='1',DOTNET_TC_QuickJitForLoops='1',DOTNET_TieredPGO='1' if a.runtime=='jit-pgo1' else '0')
        command=['taskset','-c','0-3','dotnet',str(binaries[label]),'--resolved-flow-state-evidence']
    name=f'{label}-r{repeat}'
    command+=['--repetitions','3','--contention-items','20000','--output',str(out/f'{name}.json')]
    print('MICRO_RUN',a.runtime,name,flush=True)
    with (out/f'{name}.log').open('w') as log:
        code=subprocess.run(command,env=env,stdout=log,stderr=subprocess.STDOUT).returncode
    exits.append(dict(repeat=repeat,label=label,code=code))
    (out/'exits.json').write_text(json.dumps(exits,indent=2)+'\n')
    if code:raise SystemExit(f'Failed original population: {name}; no retry or cell omission')
scenarios=['send-no-wait','send-periodic-window-update','send-starved-control','receive-accept','receive-consume','receive-pair','short-stream-control']
expected={(s,m,c,n) for s in scenarios for m in ('key','resolved') for c in (1,8,32,128) for n in (1,64,1000,100000)}
expected|={(s,m,c,20000) for s in ('send-contention','receive-pair-contention') for m in ('key','resolved') for c in (32,128)}
groups={}
for repeat,label in plan:
    rows=json.loads((out/f'{label}-r{repeat}.json').read_text())
    keys={(r['Scenario'],r['Mode'],r['ActiveStreams'],r['ItemsPerStream']) for r in rows}
    assert len(rows)==len(expected) and keys==expected,(label,repeat,len(rows))
    for row in rows:
        assert row['NanosecondsPerItem']>0 and math.isfinite(row['NanosecondsPerItem']),row
        assert row['AllocatedBytesPerItem']>=0 and math.isfinite(row['AllocatedBytesPerItem']),row
        assert row['Repetitions']==(6 if 'contention' in row['Scenario'] else 3),row
        key=(row['Scenario'],row['Mode'],row['ActiveStreams'],row['ItemsPerStream'])
        groups.setdefault((label,key),[]).append(row)
results=[]
for key in sorted(expected):
    assert len({r['Checksum'] for label in roots for r in groups[label,key]})==1,key
    med=lambda label,field:statistics.median(r[field] for r in groups[label,key])
    results.append(dict(scenario=key[0],mode=key[1],active_streams=key[2],items=key[3],
        time_improvement_pct=(1-med('candidate','NanosecondsPerItem')/med('control','NanosecondsPerItem'))*100,
        allocation_delta=med('candidate','AllocatedBytesPerItem')-med('control','AllocatedBytesPerItem')))
(out/'revision-comparison.json').write_text(json.dumps(results,indent=2)+'\n')
for label in roots:
    summary={}
    for scenario in scenarios+['send-contention','receive-pair-contention']:
        gains=[]
        for s,m,c,n in expected:
            if s!=scenario or m!='key':continue
            key=(s,'key',c,n);res=(s,'resolved',c,n)
            old=statistics.median(r['NanosecondsPerItem'] for r in groups[label,key])
            new=statistics.median(r['NanosecondsPerItem'] for r in groups[label,res])
            gains.append((1-new/old)*100)
        summary[scenario]=statistics.median(gains)
    print(label,json.dumps(summary),flush=True)
print(f'{len(plan)} processes; {len(expected)} original rows each; all negatives retained. Collection success is not Go.')
