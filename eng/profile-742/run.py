#!/usr/bin/env python3
"""Matched three-arm diagnostic profiles plus same-configuration untraced controls."""
import argparse,hashlib,json,os,pathlib,statistics,subprocess
p=argparse.ArgumentParser()
for x in ('dev','published','candidate','output','trace','parser'):p.add_argument(x,type=pathlib.Path)
p.add_argument('--workload',choices=('short-shm','long-tcp','long-shm'),required=True)
a=p.parse_args();roots={k:getattr(a,k).resolve() for k in ('dev','published','candidate')}
out=a.output.resolve();out.mkdir(parents=True,exist_ok=True)
ids={'dev':'0fe26024b114bb6e78411a9b86276086c045d03d','published':'e834d3c28c87ad496989af925515cf21babd308d','candidate':json.loads((roots['candidate']/'artifacts/first-receive-integration/provenance.json').read_text())['disposable_tree']}
short=a.workload=='short-shm';transport='tcp' if a.workload=='long-tcp' else 'sharedmemory';workload='rpc-Server1x16' if short else 'c8-s2c-10000'
project='SharpLink.Benchmarks' if short else 'SharpLink.StreamLoadTest'
relative=f'test/{project}/bin/Release/net10.0/{project}.dll'
orders=[('dev','published','candidate'),('published','candidate','dev'),('candidate','dev','published')]
plan=[(repeat,label,mode) for repeat in range(3) for label in orders[repeat] for mode in (('untraced','traced') if repeat%2==0 else ('traced','untraced'))]
provider='Microsoft-DotNETCore-SampleProfiler:0:4,Microsoft-Windows-DotNETRuntime:0x40034019:5,SharpLink-742-ProfileWindow:0xffff:4'
source_proof={}
for label,root in roots.items():
    directory=root/relative;runtime=directory.parent/'SharpLink.Runtime.dll';client=directory.parent/'SharpLink.Client.dll'
    source_proof[label]={'source_identity':ids[label],'host_sha256':hashlib.sha256(directory.read_bytes()).hexdigest(),'runtime_dll_sha256':hashlib.sha256(runtime.read_bytes()).hexdigest(),'client_dll_sha256':hashlib.sha256(client.read_bytes()).hexdigest()}
(out/'plan.json').write_text(json.dumps(dict(workload=workload,transport=transport,plan=plan,identities=source_proof,providers=provider,warmup=200 if short else 2,duration=10,max_operations=1000000 if short else None,c8_windows=[8192,65536],affinity=[0,1,2,3],pgo=1,retries=0,meaning='Longer diagnostic windows and observer controls, not replacement acceptance samples'),indent=2)+'\n')
exits=[]
for repeat,label,mode in plan:
    name=f'{label}-{mode}-r{repeat}';d=out/name;d.mkdir();report=d/'report.json'
    command=['dotnet',str(roots[label]/relative)]
    if short:command+=['--generated-abi-streaming-evidence','Server1x16','200','10','1000000',str(report),transport]
    else:command+=['--mode','local','--transport',transport,'--operation','s2c','--stream-size','10000','--concurrency','8','--stream-receive-window-bytes','8192','--connection-receive-window-bytes','65536','--warmup','2','--duration','10','--recording','off','--json-output',str(report)]
    if mode=='traced':command=[str(a.trace.resolve()),'collect','--providers',provider,'--buffersize','256','--output',str(d/'profile.nettrace'),'--']+command
    command=['taskset','-c','0-3']+command
    env=dict(os.environ,DOTNET_PROCESSOR_COUNT='4',DOTNET_ReadyToRun='0',DOTNET_TieredCompilation='1',DOTNET_TieredPGO='1',DOTNET_TC_QuickJitForLoops='1',SHARPLINK_BENCHMARK_SHA=ids[label],SHARPLINK_COMMIT=ids[label])
    print('PROFILE_START',a.workload,name,flush=True)
    with (d/'collect.log').open('w') as log:
        try:code=subprocess.run(command,env=env,stdout=log,stderr=subprocess.STDOUT,timeout=180).returncode
        except subprocess.TimeoutExpired:code=124
    parsed=-1
    if code==0 and report.exists():
        r=json.loads(report.read_text())
        if short:
            assert r['commit']==ids[label] and r['scenario']=='Server1x16' and r['transport']==transport and r['itemCount']==1 and r['itemBytes']==16 and r['warmupOperations']==200 and r['requestedMeasurementSeconds']==10 and r['validationFailures']==0 and not r['hitOperationLimit'],r
            n=dict(workload=workload,transport=transport,operations=r['operations'],items=r['operations'],commit=r['commit'],validationFailures=0,failure=0,cancelled=0,operationsStarted=r['operations'],processCpuMs=r['cpuUsPerOperation']*r['operations']/1000,profileWindowSeconds=r['actualMeasurementSeconds'],allocatedBytes=int(round(r['allocatedBytesPerOperation']*r['operations'])),runtime=r['runtimeVersion'])
        else:
            assert r['SourceCommit']==ids[label] and len(r['Results'])==1,r
            c=r['Configuration'];assert c['Transport']==(0 if transport=='tcp' else 4),c
            assert c['StreamSize']==10000 and c['StreamReceiveWindowBytes']==8192 and c['ConnectionReceiveWindowBytes']==65536 and c['DurationSeconds']==10 and c['WarmupSeconds']==2 and c['ConcurrencyConfig']==[8],c
            x=r['Results'][0];assert x['Operation']=='s2c' and x['Concurrency']==8 and x['Failure']==x['ValidationFailure']==x['Cancelled']==0 and x['Success']==x['OperationsCompleted']==x['OperationsStartedDuringMeasurement'],x
            n=dict(workload=workload,transport=transport,operations=x['Success'],items=x['Success']*10000,commit=r['SourceCommit'],validationFailures=x['ValidationFailure'],failure=x['Failure'],cancelled=x['Cancelled'],operationsStarted=x['OperationsStartedDuringMeasurement'],processCpuMs=x['Evidence']['CpuMilliseconds'],profileWindowSeconds=x['MeasurementDurationSeconds']+x['DrainDurationSeconds'],allocatedBytes=x['Evidence']['AllocatedBytes'],runtime=r['Runtime'])
        (d/'normalized.json').write_text(json.dumps(n,indent=2)+'\n')
        if mode=='traced':
            with (d/'parse.log').open('w') as log:parsed=subprocess.run(['dotnet',str(a.parser.resolve()),str(d/'profile.nettrace'),str(d/'normalized.json'),str(d/'profile.json')],stdout=log,stderr=subprocess.STDOUT,timeout=180).returncode
        else:parsed=0
    exits.append(dict(repeat=repeat,label=label,mode=mode,exit=code,parse_exit=parsed));(out/'exits.json').write_text(json.dumps(exits,indent=2)+'\n')
    print('PROFILE_EXIT',name,code,parsed,flush=True)
    if code or parsed:
        for path in (d/'collect.log',d/'parse.log'):
            if path.exists():print(path.read_text()[-18000:],flush=True)
if len(exits)!=18 or any(x['exit'] or x['parse_exit'] for x in exits):raise SystemExit('Incomplete diagnostic population; no retry or omitted cell')
rows=[]
for label in roots:
    for mode in ('untraced','traced'):
        data=[json.loads((out/f'{label}-{mode}-r{r}'/'normalized.json').read_text()) for r in range(3)]
        rows.append(dict(label=label,mode=mode,cpu_us_per_operation=[r['processCpuMs']*1000/r['operations'] for r in data],allocation_b_per_item=[r['allocatedBytes']/r['items'] for r in data],operations=data[0]['operations']))
(out/'observer-controls.json').write_text(json.dumps(rows,indent=2)+'\n');print(json.dumps(rows,indent=2))
print('All 18 diagnostic processes retained. Profile samples are managed thread time, not hardware CPU percentages.')
