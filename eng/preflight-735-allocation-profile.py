#!/usr/bin/env python3
"""Diagnostic-only GC allocation-type attribution on identical measured RPC windows."""
import hashlib
import json
import os
import pathlib
import statistics
import subprocess
import sys

MODE=sys.argv[1]
CONTROL=pathlib.Path(sys.argv[2]).resolve()
CANDIDATE=pathlib.Path(sys.argv[3]).resolve()
OUT=pathlib.Path(sys.argv[4]).resolve()
OUT.mkdir(parents=True,exist_ok=True)
CONTROL_SHA='e834d3c28c87ad496989af925515cf21babd308d'
REL='test/SharpLink.Benchmarks/GeneratedAbiStreamingEvidenceRunner.cs'

def once(text,old,new):
    if text.count(old)!=1: raise RuntimeError(f'Unreviewed marker location: {old!r}')
    return text.replace(old,new,1)

if MODE=='prepare':
    expected=subprocess.check_output(['git','show',f'{CONTROL_SHA}:{REL}'],cwd=CANDIDATE)
    marker=(CANDIDATE/'eng/preflight-735-allocation-window.cs').read_bytes()
    before='        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);'
    after='        var allocatedAfter = GC.GetTotalAllocatedBytes(precise: true);'
    source={}
    for label,root in [('control',CONTROL),('candidate',CANDIDATE)]:
        original=(root/REL).read_bytes()
        if original!=expected: raise RuntimeError(f'Unexpected timing harness: {label}')
        text=once(original.decode(),before,'        AllocationWindow735.Log.WindowBegin();\n'+before)
        text=once(text,after,after+'\n        AllocationWindow735.Log.WindowEnd(completed, checked((long)completed * benchmark.ItemCount), allocatedAfter - allocatedBefore);')
        (root/REL).write_text(text)
        (root/'test/SharpLink.Benchmarks/AllocationWindow735.cs').write_bytes(marker)
        source[label]={'harness_sha256':hashlib.sha256(text.encode()).hexdigest(),
            'window_sha256':hashlib.sha256(marker).hexdigest()}
    if source['control']!=source['candidate']: raise RuntimeError('Diagnostic arms differ')
    runtime=json.loads((CANDIDATE/'artifacts/first-receive-integration/provenance.json').read_text())
    provenance=dict(control_commit=CONTROL_SHA,candidate_runtime=runtime,
        original_harness_sha256=hashlib.sha256(expected).hexdigest(),diagnostic_sources=source,
        tools=dict(dotnet_trace='10.0.745401',trace_event='3.2.8'),
        boundary='WindowBegin before process allocation snapshot, WindowEnd after; no per-item instrumentation',
        meaning='sampled allocation-type attribution only, never throughput acceptance')
    (OUT/'provenance.json').write_text(json.dumps(provenance,indent=2)+'\n')
    (OUT/'diagnostic-harness.cs').write_text(text)
    (OUT/'window.cs').write_bytes(marker)
    print(json.dumps(provenance,indent=2))
    sys.exit(0)

if MODE!='measure' or len(sys.argv)!=7:
    raise SystemExit('prepare CONTROL CANDIDATE OUT, or measure CONTROL CANDIDATE OUT TRACE PARSER_DLL')
trace=str(pathlib.Path(sys.argv[5]).resolve())
parser=str(pathlib.Path(sys.argv[6]).resolve())
provenance=json.loads((OUT/'provenance.json').read_text())
plan=[(r,t,label) for r in range(3) for t in ('sharedmemory','tcp')
    for label in (('candidate','control') if r==1 else ('control','candidate'))]
provenance.update(plan=plan,scenario='Server100x16',warmups=200,seconds=10,max_operations=200000,
    pgo=1,affinity=[0,1,2,3],retries=0)
(OUT/'provenance.json').write_text(json.dumps(provenance,indent=2)+'\n')
exits=[]
for repeat,transport,label in plan:
    root=CONTROL if label=='control' else CANDIDATE
    identity=CONTROL_SHA if label=='control' else provenance['candidate_runtime']['disposable_tree']
    name=f'{label}-{transport}-r{repeat}'
    directory=OUT/name
    directory.mkdir(exist_ok=True)
    report=directory/'rpc.json'
    env=dict(os.environ,DOTNET_PROCESSOR_COUNT='4',DOTNET_ReadyToRun='0',DOTNET_TieredCompilation='1',
        DOTNET_TieredPGO='1',DOTNET_TC_QuickJitForLoops='1',SHARPLINK_BENCHMARK_SHA=identity)
    for k in ('DOTNET_JitDisasm','DOTNET_JitStdOutFile','SHARPLINK_READY_TCP_RECEIVE_BUFFER'):
        env.pop(k,None)
    command=['taskset','-c','0-3',trace,'collect','--profile','gc-verbose','--providers',
        'SharpLink-735-AllocationWindow:0xffff:4','--buffersize','128','--output',str(directory/'alloc.nettrace'),
        '--','dotnet',str(root/'test/SharpLink.Benchmarks/bin/Release/net10.0/SharpLink.Benchmarks.dll'),
        '--generated-abi-streaming-evidence','Server100x16','200','10','200000',str(report),transport]
    print('PROFILE',name,flush=True)
    with (directory/'collect.log').open('w') as log:
        try: code=subprocess.run(command,env=env,stdout=log,stderr=subprocess.STDOUT,timeout=90).returncode
        except subprocess.TimeoutExpired: code=124
    parse_code=-1
    if code==0 and report.exists():
        with (directory/'parse.log').open('w') as log:
            parse_code=subprocess.run(['dotnet',parser,str(directory/'alloc.nettrace'),str(report),str(directory/'types.json')],
                stdout=log,stderr=subprocess.STDOUT,timeout=60).returncode
    exits.append(dict(repeat=repeat,transport=transport,label=label,collect_exit=code,parse_exit=parse_code))
    (OUT/'exits.json').write_text(json.dumps(exits,indent=2)+'\n')
    if parse_code==0:
        print((directory/'parse.log').read_text(),flush=True)
    else:
        for log in ('collect.log','parse.log'):
            if (directory/log).exists(): print((directory/log).read_text()[-16000:],flush=True)
if len(exits)!=12 or any(r['collect_exit'] or r['parse_exit'] for r in exits):
    raise SystemExit('Incomplete diagnostic population; raw traces/failures retained without retries')

comparisons=[]
for transport in ('sharedmemory','tcp'):
    groups={label:[json.loads((OUT/f'{label}-{transport}-r{r}'/'types.json').read_text()) for r in range(3)]
        for label in ('control','candidate')}
    all_types=set(x['type'] for group in groups.values() for row in group for x in row['types'])
    types=[]
    for name in all_types:
        values={label:[next((t['estimatedBytesPerItem'] for t in row['types'] if t['type']==name),0)
            for row in rows] for label,rows in groups.items()}
        med={label:statistics.median(v) for label,v in values.items()}
        types.append(dict(type=name,estimated_delta_b_per_item=med['candidate']-med['control'],
            control=values['control'],candidate=values['candidate']))
    types.sort(key=lambda row:abs(row['estimated_delta_b_per_item']),reverse=True)
    comparison=dict(transport=transport,diagnostic_only=True,
        measured_b_per_item={label:[r['measuredBytesPerItem'] for r in rows] for label,rows in groups.items()},
        samples={label:[r['allocationTickSamples'] for r in rows] for label,rows in groups.items()},types=types)
    comparisons.append(comparison)
    print('TYPE_DELTA',transport,flush=True)
    print(json.dumps(dict(comparison,types=types[:20]),indent=2),flush=True)
(OUT/'type-comparison.json').write_text(json.dumps(comparisons,indent=2)+'\n')
print('12/12 profiled RPC windows; estimates diagnose types, not a Go verdict.')
