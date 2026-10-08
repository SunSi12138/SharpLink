#!/usr/bin/env python3
"""Three-arm screen: published; consumer-only pooling; consumer plus hinted nullable receive-validation helper."""
import argparse
import hashlib
import json
import math
import os
import pathlib
import statistics
import subprocess

p=argparse.ArgumentParser()
for name in ('published','consumer','candidate','output'): p.add_argument(name,type=pathlib.Path)
p.add_argument('--runtime',choices=('jit-pgo0','jit-pgo1','native'),required=True)
a=p.parse_args()
roots={label:getattr(a,label).resolve() for label in ('published','consumer','candidate')}
out=a.output.resolve(); out.mkdir(parents=True,exist_ok=True)
ids={'published':'e834d3c28c87ad496989af925515cf21babd308d'}
for label in ('consumer','candidate'):
    ids[label]=json.loads((roots[label]/'artifacts/first-receive-integration/provenance.json').read_text())['disposable_tree']
orders=[('published','consumer','candidate'),('candidate','consumer','published'),('consumer','published','candidate')]
plan=[(r,t,s,label) for r in range(3) for t in ('tcp','sharedmemory')
      for s in ('Server1x16','Server100x16') for label in orders[r]]
relative='artifacts/consumer-native/SharpLink.Benchmarks' if a.runtime=='native' else 'test/SharpLink.Benchmarks/bin/Release/net10.0/SharpLink.Benchmarks.dll'
binaries={label:root/relative for label,root in roots.items()}
provenance=dict(identities=ids,runtime=a.runtime,plan=plan,warmups=30,seconds=5,max_operations=200000,
    affinity=[0,1,2,3],processor_count=4,socket_profile='unchanged default',attempts_per_cell=1,
    binary_sha256={label:hashlib.sha256(path.read_bytes()).hexdigest() for label,path in binaries.items()},
    roles={'published':'unchanged e834 PR','consumer':'c353 consumer-only pooling plus exactly-once wrapper fixes',
           'candidate':'consumer-only runtime plus hinted nullable receive-validation helper'},
    boundary='Incremental isolation, not all #735 acceptance cells; consumer versus candidate changes only receive validation representation, retaining all guards.')
(out/'provenance.json').write_text(json.dumps(provenance,indent=2)+'\n')
exits=[]
for repeat,transport,scenario,label in plan:
    name=f'{label}-{transport}-{scenario}-r{repeat}'
    env=dict(os.environ,DOTNET_PROCESSOR_COUNT='4',SHARPLINK_BENCHMARK_SHA=ids[label])
    for key in ('DOTNET_JitDisasm','DOTNET_JitStdOutFile','SHARPLINK_READY_TCP_RECEIVE_BUFFER'): env.pop(key,None)
    if a.runtime=='native': command=['taskset','-c','0-3',str(binaries[label])]
    else:
        env.update(DOTNET_ReadyToRun='0',DOTNET_TieredCompilation='1',DOTNET_TC_QuickJitForLoops='1',
                   DOTNET_TieredPGO='1' if a.runtime=='jit-pgo1' else '0')
        command=['taskset','-c','0-3','dotnet',str(binaries[label]),'--generated-abi-streaming-evidence']
    command += [scenario,'30','5','200000',str(out/f'{name}.json'),transport]
    print('ISOLATION_RUN',a.runtime,name,flush=True)
    with (out/f'{name}.log').open('w') as log:
        try: code=subprocess.run(command,env=env,stdout=log,stderr=subprocess.STDOUT,timeout=60).returncode
        except subprocess.TimeoutExpired: code=124
    exits.append(dict(repeat=repeat,transport=transport,scenario=scenario,label=label,exit_code=code))
    (out/'exits.json').write_text(json.dumps(exits,indent=2)+'\n')
    print('ISOLATION_EXIT',code,name,flush=True)
if len(exits)!=36 or any(row['exit_code'] for row in exits):
    raise SystemExit('Incomplete three-arm population; all failures retained without retries')
results=[]
for transport in ('tcp','sharedmemory'):
    for scenario in ('Server1x16','Server100x16'):
        groups={}
        for label in roots:
            rows=[]
            for repeat in range(3):
                name=f'{label}-{transport}-{scenario}-r{repeat}'
                row=json.loads((out/f'{name}.json').read_text())
                if not(row['commit']==ids[label] and row['transport']==transport and row['scenario']==scenario
                       and row['validationFailures']==0 and row['warmupOperations']==30
                       and row['requestedMeasurementSeconds']==5 and row['operations']>0
                       and row['throughputItemsPerSecond']>0 and math.isfinite(row['allocatedBytesPerItem'])):
                    raise RuntimeError(f'Invalid report: {row}')
                if a.runtime=='native' and 'NATIVE_RPC_EVIDENCE dynamicCodeSupported=False' not in (out/f'{name}.log').read_text():
                    raise RuntimeError(f'Missing native execution proof: {name}')
                rows.append(row)
            groups[label]=rows
        med=lambda label,key:statistics.median(row[key] for row in groups[label])
        pairs={}
        for label,baseline in (('candidate','published'),('consumer','published'),('candidate','consumer')):
            pairs[f'{label}_vs_{baseline}']=dict(
                throughput_pct=(med(label,'throughputItemsPerSecond')/med(baseline,'throughputItemsPerSecond')-1)*100,
                cpu_time_pct=(med(label,'cpuUsPerOperation')/med(baseline,'cpuUsPerOperation')-1)*100,
                allocation_delta=med(label,'allocatedBytesPerItem')-med(baseline,'allocatedBytesPerItem'))
        results.append(dict(transport=transport,scenario=scenario,pairs=pairs,
            allocation={label:[row['allocatedBytesPerItem'] for row in rows] for label,rows in groups.items()},
            throughput={label:[row['throughputItemsPerSecond'] for row in rows] for label,rows in groups.items()},
            p99_us={label:[row['p99Us'] for row in rows] for label,rows in groups.items()}))
text=json.dumps(results,indent=2)
(out/'comparison.json').write_text(text+'\n')
print(text)
print('36/36 isolation processes retained; completeness is not performance acceptance.')
