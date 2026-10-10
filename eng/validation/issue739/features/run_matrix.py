#!/usr/bin/env python3
"""Bounded extra matrix. Each sample is a fresh child process; no concurrent measurements."""
import argparse, hashlib, json, math, os, pathlib, random, statistics, subprocess
p=argparse.ArgumentParser()
p.add_argument('--dotnet', default='dotnet'); p.add_argument('--dll', required=True)
p.add_argument('--output', required=True); p.add_argument('--stage', choices=['streams','features','spotchecks','suspension'], required=True)
p.add_argument('--only-mode', choices=['oneway-stream'])
p.add_argument('--oneway-load-policy', choices=['send-completion','receive-bounded'], default='send-completion')
p.add_argument('--samples', type=int, default=5); p.add_argument('--pilot', action='store_true')
p.add_argument('--transport', choices=['tcp','shm','both'], default='both')
a=p.parse_args(); out=pathlib.Path(a.output); out.mkdir(parents=True,exist_ok=True)
if any(out.iterdir()): p.error('Output directory must be empty; preserve prior evidence and use a new directory')
if not __debug__: p.error('Optimized Python disables validation assertions; run without -O/PYTHONOPTIMIZE')
if a.samples < 1: p.error('At least one sample is required')
if a.samples < 5 and not a.pilot: p.error('Stable evidence requires at least five samples; use --pilot for smoke only')
transports=['tcp','shm'] if a.transport=='both' else [a.transport]
cells=[]
def add(t,m,c,f='none',n=1,producer='sync',control='rpc'):
    cells.append(dict(transport=t,mode=m,concurrency=c,feature=f,items=n,producer=producer,control=control,oneWayLoadPolicy=a.oneway_load_policy if m=='oneway-stream' else 'not-applicable'))
for t in transports:
    if a.stage=='streams':
        for c in [1,32]:
            for m in ['upload','download','duplex','oneway-stream']:
                for n in [1,16,256]:
                    add(t,m,c,n=n); add(t,m,c,n=n,control='fixture')
            add(t,'add',c,control='idle')
    if a.stage=='features':
        for c in [1,32]:
            for f in ['none','client-interceptor','server-interceptor','both-interceptors','allocating-plugin','metrics','tracing','authentication','common']:
                add(t,'add',c,f)
            for f in ['none','deadline','cancellation']: add(t,'cancellable',c,f)
            for f in ['none','compression']: add(t,'echo',c,f)
            add(t,'add',c,control='fixture'); add(t,'echo',c,control='fixture')
    if a.stage=='spotchecks':
        for c in [1,32]:
            for m in ['upload','download','duplex','oneway-stream']:
                for f in ['none','both-interceptors','common']: add(t,m,c,f,16)
    if a.stage=='suspension':
        # Same integer payload and count; fixture-only rows reveal producer/consumer scheduling cost.
        for m in ['upload','download','duplex','oneway-stream']:
            for producer in ['sync','yield']:
                for control in ['rpc','fixture']: add(t,m,1,n=16,producer=producer,control=control)
if a.only_mode: cells=[cell for cell in cells if cell['mode']==a.only_mode]
if not cells: p.error('Selection contains no cells')
expected_sha=os.environ.get('ISSUE739_SOURCE_SHA','UNVERIFIED')
if expected_sha=='UNVERIFIED' and not a.pilot: p.error('ISSUE739_SOURCE_SHA is required for non-pilot evidence')
def sha256(path): return hashlib.sha256(pathlib.Path(path).read_bytes()).hexdigest()
source_dir=pathlib.Path(__file__).resolve().parent
assembly_path=pathlib.Path(a.dll).resolve()
for required in [assembly_path.with_suffix('.runtimeconfig.json'),assembly_path.with_suffix('.deps.json')]:
    if not required.is_file(): p.error(f'Missing execution configuration: {required}')
execution_files=sorted(f for f in assembly_path.parent.iterdir() if f.is_file() and (f.suffix=='.dll' or f.name.endswith('.runtimeconfig.json') or f.name.endswith('.deps.json')))
expected_runtime=os.environ.get('ISSUE739_RUNTIME_VERSION')
manifest={'requiredRuntimeVersion':expected_runtime,'executionFilesSha256':{f.name:sha256(f) for f in execution_files},'sourceSha':expected_sha,'assemblySha256':sha256(a.dll),'sourceHashes':{f.name:sha256(f) for f in source_dir.iterdir() if f.suffix in ['.cs','.csproj','.py','.md']},'environment':{k:os.environ.get(k,'runtime-default') for k in ['DOTNET_TieredPGO','DOTNET_TieredCompilation','DOTNET_gcServer','DOTNET_GCServer','DOTNET_ReadyToRun']},'schemaVersion':1,'stage':a.stage,'pilot':a.pilot,'samples':a.samples,'cells':cells,'failures':[]}
(out/'manifest.json').write_text(json.dumps(manifest,indent=2))
order=[(cell,sample) for cell in cells for sample in range(a.samples)]
random.Random(739).shuffle(order) # avoid deterministic scenario-order drift while retaining reproducibility
results=[]
processes=set()
for cell,sample in order:
    n=cell['items']; c=cell['concurrency']; mode=cell['mode']
    calls=32 if a.pilot else (max(1024,16384//n) if mode in ['upload','download','duplex','oneway-stream'] else 16384)
    calls=((calls+c-1)//c)*c
    warmup=32 if a.pilot else max(256,min(4096,calls//4)); warmup=((warmup+c-1)//c)*c
    name='-'.join(str(cell[k]) for k in ['transport','mode','concurrency','feature','items','producer','control'])
    if cell['oneWayLoadPolicy']=='receive-bounded': name+='-receive-bounded'
    name+=f'-s{sample}'
    cmd=[a.dotnet,a.dll,cell['transport'],mode,str(c),str(calls),str(warmup),name,str(out/(name+'.json')),cell['feature'],str(n),cell['producer'],cell['control'],cell['oneWayLoadPolicy']]
    print(name,flush=True)
    with (out/(name+'.log')).open('w') as log:
        try:
            run=subprocess.run(cmd,stdout=log,stderr=subprocess.STDOUT,timeout=300)
            exit_code=run.returncode
        except subprocess.TimeoutExpired:
            exit_code=124
    if exit_code:
        manifest['failures'].append({'cell':cell,'sample':sample,'exitCode':exit_code,'log':name+'.log'})
        (out/'manifest.json').write_text(json.dumps(manifest,indent=2))
        raise SystemExit(f'Cell failed: {name}; missing results are never zero')
    try:
        result=json.loads((out/(name+'.json')).read_text())
        assert result['schemaVersion']==1 and result['sourceSha']==expected_sha and result['sample']==name
        if expected_runtime is not None: assert result['runtime']==expected_runtime, f"Runtime mismatch: expected {expected_runtime}, got {result['runtime']}"
        assert all(result[k]==v for k,v in cell.items())
        assert result['calls']==calls and result['warmup']==warmup and result['clientCompletedCalls']==calls
        idle=cell['control']=='idle'
        assert result['completedCalls']==(0 if idle else calls)
        expected_input=calls*n if not idle and mode in ['upload','duplex','oneway-stream'] else 0
        expected_output=calls*n if not idle and mode in ['download','duplex'] else 0
        assert result['serverReceivedInputItems']==expected_input
        assert result['serverProducedOutputItems']==expected_output and result['clientReceivedOutputItems']==expected_output
        for key in ['bytes','bytesPerCall','elapsedSeconds','workloadSeconds','drainAndTailSeconds','p50Nanoseconds','p99Nanoseconds']:
            assert math.isfinite(result[key]) and result[key]>=0
        assert math.isclose(result['bytesPerCall'], result['bytes']/calls)
        assert result['p99Nanoseconds']>=result['p50Nanoseconds']
        if cell['oneWayLoadPolicy']=='receive-bounded' and not idle:
            gate=result['receiveCompletionGate']
            assert result['fixtureRevision']=='receive-completion-credits-v2'
            assert gate['capacity']==c and gate['acquired']==calls and gate['released']==calls
            assert gate['finalCredits']==c and gate['finalOutstanding']==0 and 0<gate['peakOutstanding']<=c
            assert 0<=gate['pendingWaitObservations']<=calls and math.isfinite(gate['acquireWaitNanoseconds']) and gate['acquireWaitNanoseconds']>=0
        identity=(result['processId'],result['processStartUtc'])
        assert identity not in processes, 'A sample reused a process'
        processes.add(identity)
        results.append(result)
    except Exception as error:
        manifest['failures'].append({'cell':cell,'sample':sample,'validationError':str(error) or type(error).__name__})
        (out/'manifest.json').write_text(json.dumps(manifest,indent=2))
        raise SystemExit(f'Invalid result: {name}: {error}')
summary=[]
for cell in cells:
    selected=[r for r in results if all(r[k]==v for k,v in cell.items())]
    assert len(selected)==a.samples
    row={'cell':cell,'validUniqueProcessSamples':len(selected)}
    for key in ['bytesPerCall','p50Nanoseconds','p99Nanoseconds']:
        values=[r[key] for r in selected]; mean=statistics.mean(values)
        row[key]={'median':statistics.median(values),'min':min(values),'max':max(values),'cv':statistics.stdev(values)/mean if len(values)>1 and mean else None}
    row['minimumSampleCountSatisfied']=not a.pilot and len(selected)>=5
    row['stabilityRequiresReview']='Inspect CV/spread and host comparability; sample count alone does not establish stability'
    summary.append(row)
(out/'summary.json').write_text(json.dumps(summary,indent=2))
manifest['completed']=True
(out/'manifest.json').write_text(json.dumps(manifest,indent=2))
