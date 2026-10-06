#!/usr/bin/env python3
"""Fixed actual NativeAOT RPC comparison, not a native correctness smoke."""
import hashlib
import json
import math
import os
import pathlib
import statistics
import subprocess
import sys

if len(sys.argv)!=5:
    raise SystemExit('usage: native-rpc.py CONTROL_BINARY CANDIDATE_BINARY CANDIDATE_TREE OUTPUT')
control_binary,candidate_binary=map(lambda s:pathlib.Path(s).resolve(),sys.argv[1:3])
candidate_tree=sys.argv[3]
output=pathlib.Path(sys.argv[4]).resolve()
output.mkdir(parents=True,exist_ok=True)
control_sha='e834d3c28c87ad496989af925515cf21babd308d'
plan=[(repeat,transport,scenario,label) for repeat in range(3)
      for transport in ('tcp','sharedmemory')
      for scenario in ('Server1x16','Server100x16')
      for label in (('candidate','control') if repeat==1 else ('control','candidate'))]
provenance=dict(control_commit=control_sha,candidate_runtime_tree=candidate_tree,
    native_binaries={label:dict(sha256=hashlib.sha256(path.read_bytes()).hexdigest(),
        size=path.stat().st_size) for label,path in [('control',control_binary),('candidate',candidate_binary)]},
    plan=plan,affinity=[0,1,2,3],processor_count=4,warmup_operations=30,
    measurement_seconds=5,max_operations=200000,attempts_per_cell=1,
    socket_profile='unchanged default',
    interpretation='actual generated NativeAOT RPC, incremental e834 versus candidate, not full pinned-dev acceptance')
(output/'provenance.json').write_text(json.dumps(provenance,indent=2)+'\n')
exits=[]
for repeat,transport,scenario,label in plan:
    binary=control_binary if label=='control' else candidate_binary
    identity=control_sha if label=='control' else candidate_tree
    name=f'{label}-{transport}-{scenario}-r{repeat}'
    env=dict(os.environ,SHARPLINK_BENCHMARK_SHA=identity,DOTNET_PROCESSOR_COUNT='4')
    for key in ('DOTNET_JitDisasm','DOTNET_JitStdOutFile','SHARPLINK_READY_TCP_RECEIVE_BUFFER'):
        env.pop(key,None)
    command=['taskset','-c','0-3',str(binary),scenario,'30','5','200000',str(output/f'{name}.json'),transport]
    print('RUN_NATIVE',name,flush=True)
    with (output/f'{name}.log').open('w') as log:
        try:
            code=subprocess.run(command,env=env,stdout=log,stderr=subprocess.STDOUT,timeout=60).returncode
        except subprocess.TimeoutExpired:
            code=124
    exits.append(dict(repeat=repeat,transport=transport,scenario=scenario,label=label,exit_code=code))
    (output/'exits.json').write_text(json.dumps(exits,indent=2)+'\n')
    print('EXIT_NATIVE',code,name,flush=True)
if len(exits)!=24 or any(x['exit_code'] for x in exits):
    raise SystemExit('Incomplete native population; failures retained, no retries')
results=[]
for transport in ('tcp','sharedmemory'):
    for scenario in ('Server1x16','Server100x16'):
        groups={}
        for label in ('control','candidate'):
            rows=[]
            identity=control_sha if label=='control' else candidate_tree
            for repeat in range(3):
                name=f'{label}-{transport}-{scenario}-r{repeat}'
                if 'NATIVE_RPC_EVIDENCE dynamicCodeSupported=False' not in (output/f'{name}.log').read_text():
                    raise RuntimeError(f'Missing actual NativeAOT proof: {name}')
                row=json.loads((output/f'{name}.json').read_text())
                if not(row['commit']==identity and row['transport']==transport and row['scenario']==scenario
                    and row['validationFailures']==0 and row['warmupOperations']==30
                    and row['requestedMeasurementSeconds']==5 and row['operations']>0
                    and row['throughputItemsPerSecond']>0 and math.isfinite(row['allocatedBytesPerItem'])
                    and row['allocatedBytesPerItem']>=0):
                    raise RuntimeError(f'Invalid NativeAOT report: {row}')
                rows.append(row)
            groups[label]=rows
        med=lambda label,key:statistics.median(r[key] for r in groups[label])
        results.append(dict(transport=transport,scenario=scenario,
            throughput_pct=(med('candidate','throughputItemsPerSecond')/med('control','throughputItemsPerSecond')-1)*100,
            cpu_time_pct=(med('candidate','cpuUsPerOperation')/med('control','cpuUsPerOperation')-1)*100,
            allocation_delta=med('candidate','allocatedBytesPerItem')-med('control','allocatedBytesPerItem'),
            p99_us={label:[r['p99Us'] for r in rows] for label,rows in groups.items()},
            throughput={label:[r['throughputItemsPerSecond'] for r in rows] for label,rows in groups.items()},
            allocations={label:[r['allocatedBytesPerItem'] for r in rows] for label,rows in groups.items()}))
text=json.dumps(results,indent=2)
(output/'comparison.json').write_text(text+'\n')
print(text)
print('24/24 actual native processes complete; all negative deltas retained, integrity is not Go acceptance.')
