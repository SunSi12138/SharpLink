#!/usr/bin/env python3
"""Predeclared three-arm real-RPC screen: published, first-receive, pooled consumer wait."""
import argparse
import hashlib
import json
import math
import os
import pathlib
import statistics
import subprocess

parser=argparse.ArgumentParser()
parser.add_argument('published',type=pathlib.Path)
parser.add_argument('prior',type=pathlib.Path)
parser.add_argument('candidate',type=pathlib.Path)
parser.add_argument('output',type=pathlib.Path)
parser.add_argument('--runtime',choices=('jit-pgo0','jit-pgo1','native'),required=True)
args=parser.parse_args()
roots={label:getattr(args,label).resolve() for label in ('published','prior','candidate')}
out=args.output.resolve();out.mkdir(parents=True,exist_ok=True)
ids={'published':'e834d3c28c87ad496989af925515cf21babd308d'}
for label in ('prior','candidate'):
    ids[label]=json.loads((roots[label]/'artifacts/first-receive-integration/provenance.json').read_text())['disposable_tree']
orders=[('published','prior','candidate'),('candidate','prior','published'),('prior','published','candidate')]
plan=[(r,t,s,label) for r in range(3) for t in ('tcp','sharedmemory')
      for s in ('Server1x16','Server100x16') for label in orders[r]]
if args.runtime=='native':
    binaries={label:root/'artifacts/consumer-native/SharpLink.Benchmarks' for label,root in roots.items()}
else:
    binaries={label:root/'test/SharpLink.Benchmarks/bin/Release/net10.0/SharpLink.Benchmarks.dll' for label,root in roots.items()}
provenance=dict(identities=ids,runtime=args.runtime,plan=plan,warmups=30,seconds=5,max_operations=200000,
    affinity=[0,1,2,3],processor_count=4,socket_profile='unchanged default',attempts_per_cell=1,
    binary_sha256={label:hashlib.sha256(p.read_bytes()).hexdigest() for label,p in binaries.items()},
    boundary='Uninstrumented incremental screening, not all #735 acceptance cells. Prior isolates the consumer-builder change.')
(out/'provenance.json').write_text(json.dumps(provenance,indent=2)+'\n')
exits=[]
for repeat,transport,scenario,label in plan:
    name=f'{label}-{transport}-{scenario}-r{repeat}'
    env=dict(os.environ,DOTNET_PROCESSOR_COUNT='4',SHARPLINK_BENCHMARK_SHA=ids[label])
    for key in ('DOTNET_JitDisasm','DOTNET_JitStdOutFile','SHARPLINK_READY_TCP_RECEIVE_BUFFER'):
        env.pop(key,None)
    if args.runtime=='native':
        command=['taskset','-c','0-3',str(binaries[label])]
    else:
        env.update(DOTNET_ReadyToRun='0',DOTNET_TieredCompilation='1',
                   DOTNET_TieredPGO='1' if args.runtime=='jit-pgo1' else '0',DOTNET_TC_QuickJitForLoops='1')
        command=['taskset','-c','0-3','dotnet',str(binaries[label]),'--generated-abi-streaming-evidence']
    command += [scenario,'30','5','200000',str(out/f'{name}.json'),transport]
    print('RPC_SCREEN',args.runtime,name,flush=True)
    with (out/f'{name}.log').open('w') as log:
        try: code=subprocess.run(command,env=env,stdout=log,stderr=subprocess.STDOUT,timeout=60).returncode
        except subprocess.TimeoutExpired: code=124
    exits.append(dict(repeat=repeat,transport=transport,scenario=scenario,label=label,exit_code=code))
    (out/'exits.json').write_text(json.dumps(exits,indent=2)+'\n')
    print('RPC_EXIT',code,name,flush=True)
if len(exits)!=36 or any(x['exit_code'] for x in exits):
    raise SystemExit('Incomplete three-arm population; failed logs retained without retries')
results=[]
for transport in ('tcp','sharedmemory'):
    for scenario in ('Server1x16','Server100x16'):
        groups={}
        for label in roots:
            rows=[]
            for r in range(3):
                name=f'{label}-{transport}-{scenario}-r{r}'
                row=json.loads((out/f'{name}.json').read_text())
                if not(row['commit']==ids[label] and row['transport']==transport and row['scenario']==scenario
                    and row['validationFailures']==0 and row['warmupOperations']==30
                    and row['requestedMeasurementSeconds']==5 and row['operations']>0
                    and row['throughputItemsPerSecond']>0 and math.isfinite(row['allocatedBytesPerItem'])):
                    raise RuntimeError(f'Invalid three-arm report: {row}')
                if args.runtime=='native' and 'NATIVE_RPC_EVIDENCE dynamicCodeSupported=False' not in (out/f'{name}.log').read_text():
                    raise RuntimeError(f'The native evidence is not an actual native process: {name}')
                rows.append(row)
            groups[label]=rows
        med=lambda label,key:statistics.median(r[key] for r in groups[label])
        comparison={}
        for baseline in ('published','prior'):
            comparison[baseline]=dict(
                throughput_pct=(med('candidate','throughputItemsPerSecond')/med(baseline,'throughputItemsPerSecond')-1)*100,
                cpu_time_pct=(med('candidate','cpuUsPerOperation')/med(baseline,'cpuUsPerOperation')-1)*100,
                allocation_delta=med('candidate','allocatedBytesPerItem')-med(baseline,'allocatedBytesPerItem'))
        results.append(dict(transport=transport,scenario=scenario,vs=comparison,
            allocation={label:[r['allocatedBytesPerItem'] for r in rows] for label,rows in groups.items()},
            throughput={label:[r['throughputItemsPerSecond'] for r in rows] for label,rows in groups.items()},
            p99_us={label:[r['p99Us'] for r in rows] for label,rows in groups.items()}))
text=json.dumps(results,indent=2)
(out/'comparison.json').write_text(text+'\n')
print(text)
print('36/36 three-arm RPC processes retained; integrity success is not performance acceptance.')
