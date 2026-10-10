#!/usr/bin/env python3
"""Bounded SHM synchronous-initiation scopes: two cells, 28 fresh RPC processes."""
import argparse
import json
import math
import os
import pathlib
import platform
import statistics
import shutil
import subprocess
import sys
import time
from scope_project import SOURCE, HERE, ROOT, project, sha, MULTIPLICITY
import importlib.util
spec = importlib.util.spec_from_file_location("multiplicity_runner", MULTIPLICITY/"run.py")
base = importlib.util.module_from_spec(spec)
spec.loader.exec_module(base)
from counters import NAMES
sys.path.insert(0,str(HERE.parent))
from validate import validate as validate_baseline

def write(path, value):
    path.write_text(json.dumps(value, indent=2)+'\n')

def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--dotnet', default='dotnet')
    p.add_argument('--output', required=True)
    p.add_argument('--work-root', required=True)
    p.add_argument('--operations', type=int, default=131072)
    p.add_argument('--warmup', type=int, default=32768)
    p.add_argument('--correctness-only', action='store_true')
    p.add_argument('--tcp-only', action='store_true')
    p.add_argument('--controls-only', action='store_true')
    p.add_argument('--budget-seconds', type=int, default=1380)
    a = p.parse_args()
    if (a.tcp_only or a.controls_only) and not a.correctness_only:
        p.error('--tcp-only is only for non-performance local correctness')
    if not a.correctness_only and (a.operations != 131072 or a.warmup != 32768):
        p.error('Published bounded matrix fixes N=131072 and warmup=32768')
    if a.operations < 32 or a.operations % 32 or a.warmup % 32:
        p.error('Counts must be divisible by 32')
    dotnet = str(pathlib.Path(a.dotnet).resolve()) if '/' in a.dotnet else a.dotnet
    output = pathlib.Path(a.output).resolve()
    if output.exists():
        p.error('Output directory must be new; preserve previous runs unchanged')
    output.mkdir(parents=True)
    work = pathlib.Path(a.work_root).resolve()
    deadline = time.monotonic()+a.budget_seconds
    expected_runtime = os.environ.get('ISSUE739_RUNTIME_VERSION','10.0.2') if a.correctness_only else '10.0.12'
    retained_dotnet={'DOTNET_ROOT','DOTNET_ROOT_X64','DOTNET_ROOT_ARM64','DOTNET_CLI_HOME'}
    stripped={k:v for k,v in os.environ.items() if k.startswith(('DOTNET_','COMPlus_','ISSUE739_')) and k not in retained_dotnet}
    env={k:v for k,v in os.environ.items() if k not in stripped}
    env.update(DOTNET_TieredPGO='1',DOTNET_TieredCompilation='1',DOTNET_ReadyToRun='1',DOTNET_gcServer='0',COMPlus_gcServer='0',
        DOTNET_ROLL_FORWARD='Disable',DOTNET_CLI_TELEMETRY_OPTOUT='1',DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1',
        ISSUE739_SOURCE_SHA=SOURCE,ISSUE739_DIAGNOSTIC='0',ISSUE739_RUNTIME_VERSION=expected_runtime)
    expected_sdk = '10.0.112'
    manifest = {'schemaVersion': 1, 'sourceSha': SOURCE, 'status':'initializing', 'correctnessOnly':a.correctness_only,
        'runtimePin':expected_runtime, 'sdkPin':expected_sdk, 'sourceRole':'A vanilla primary; B owner counters plus original synchronous initiation inclusive scopes',
        'operationCount':a.operations, 'warmup':a.warmup, 'budgetSeconds':a.budget_seconds,
        'platform':platform.platform(), 'processes':[], 'binaries':{}, 'rawSamples':{},
        'sanitizedTuningNames':sorted(stripped), 'toolFiles':{f.name:sha(f) for f in HERE.iterdir() if f.is_file()},
        'policy':{'noOutlierRemoval':True,'freshProcesses':True,'automaticFollowonExperiments':False,
            'productionSchedule':'SHM Add c1/c32, each 3 ABBA blocks then AA = 28 RPC processes, plus two standalone primitive controls',
            'budgetExhaustion':'abort and preserve partial evidence; never relabel a truncated matrix complete'}}
    write(output/'manifest.json',manifest)
    def command(args, cwd, log, timeout=180, overrides=None):
        remaining = deadline-time.monotonic()
        if remaining <= 0:
            raise TimeoutError('Whole experiment deadline exhausted')
        runenv = dict(env, **(overrides or {}))
        started = time.monotonic()
        try:
            with log.open('w') as stream:
                result = subprocess.run(args, cwd=cwd, env=runenv, stdout=stream, stderr=subprocess.STDOUT,
                    timeout=min(timeout,remaining), text=True)
            code = result.returncode
        except subprocess.TimeoutExpired:
            code = 'timeout'
        manifest['processes'].append({'command':args,'log':log.name,'exitCode':code,
            'elapsedSeconds':time.monotonic()-started, 'variant':runenv.get('ISSUE739_MULTIPLICITY_VARIANT')})
        write(output/'manifest.json',manifest)
        if code != 0:
            raise RuntimeError(f'{log.name}: exit {code}')
    rows = []
    pids=set()
    try:
        info = subprocess.check_output([dotnet,'--info'],cwd=ROOT,text=True,env=env)
        sdk = subprocess.check_output([dotnet,'--version'],cwd=ROOT,text=True,env=env).strip()
        runtimes = subprocess.check_output([dotnet,'--list-runtimes'],cwd=ROOT,text=True,env=env)
        dotnet_path=pathlib.Path(shutil.which(dotnet) or dotnet).resolve()
        toolchain=[dotnet_path, dotnet_path.parent/'sdk'/sdk/'Roslyn/bincore/csc.dll',
            dotnet_path.parent/'shared/Microsoft.NETCore.App'/expected_runtime/'System.Private.CoreLib.dll']
        manifest['toolchainFiles']={str(path):sha(path) for path in toolchain}
        manifest.update(dotnetInfo=info, actualSdk=sdk, installedRuntimes=runtimes,
            environment={k:v for k,v in env.items() if k.startswith(('DOTNET_','COMPlus_','ISSUE739_'))})
        if not a.correctness_only and (sdk != expected_sdk or f'Microsoft.NETCore.App {expected_runtime} [' not in runtimes):
            raise ValueError('Pinned SDK/runtime not installed')
        variants = project(work,output)
        projection=json.loads((output/'projection-manifest.json').read_text())
        manifest['projectionArtifacts']={name:sha(output/name) for name in ['projection-manifest.json','A-source.patch','B-source.patch','B-scope-only.patch']}
        original_names=subprocess.check_output(['git','ls-files','-z','src'],cwd=ROOT,text=True).split('\0')
        original_names=[x for x in original_names if x]+['Directory.Build.props','Directory.Packages.props','global.json','.editorconfig']
        manifest['originalInputs']={name:sha(ROOT/name) for name in original_names if (ROOT/name).is_file()}
        manifest['status']='building';write(output/'manifest.json',manifest)
        for variant,dest in variants.items():
            command([dotnet,'build','eng/validation/issue739/Issue739.csproj','-c','Release','-m:1',
                '-p:UseSharedCompilation=false','--nologo'],dest,output/f'build-{variant}.log',timeout=300)
            bindir=dest/'eng/validation/issue739/bin/Release/net10.0'
            manifest['binaries'][variant]={f.name:sha(f) for f in bindir.iterdir() if f.suffix in ('.dll','.json')}
            export=output/'executed-binaries'/variant;export.mkdir(parents=True)
            for file in bindir.iterdir():
                if file.suffix in ('.dll','.json','.pdb'):
                    shutil.copy2(file,export/file.name)
        write(output/'manifest.json',manifest)
        exe=lambda v:str(variants[v]/'eng/validation/issue739/bin/Release/net10.0/SharpLink.Benchmarks.dll')
        execute=lambda v:[dotnet,'exec','--fx-version',expected_runtime,exe(v)]
        command(execute('B')+['counter-control',str(output/'counter-control.json')],variants['B'],output/'counter-control.log',
            overrides={'ISSUE739_MULTIPLICITY_VARIANT':'B'})
        control=json.loads((output/'counter-control.json').read_text())
        if control['currentThreadBytes'] != 0:
            raise ValueError('Counter primitive allocated')
        if not a.correctness_only and control['runtime'] != expected_runtime:
            raise ValueError('Primitive control runtime mismatch')
        command(execute('B')+['empty-scope-control',str(output/'empty-scope-control.json')],variants['B'],output/'empty-scope-control.log',
            overrides={'ISSUE739_MULTIPLICITY_VARIANT':'B'})
        empty=json.loads((output/'empty-scope-control.json').read_text())
        if empty['currentThreadBytes'] != 0 or empty['runtime'] != expected_runtime or empty['corelibSha256'] != manifest['toolchainFiles'][str(toolchain[-1])]:
            raise ValueError('Empty scope control failed')
        if not all(empty['fixtures'][name] is True for name in ['originalValueTaskIdentity','fourReturnStatuses','synchronousThrowsPreserved','nonShmBypass','sourceNotConsumedOrRegistered','nestedOverlapDetected','pricedOwnerOverlapDetected']):
            raise ValueError('Scope fixtures failed')
        for name in ['counter-control','empty-scope-control']:
            data=json.loads((output/(name+'.json')).read_text())
            if data['processId'] in pids: raise ValueError('Duplicate control PID')
            pids.add(data['processId'])
            manifest.setdefault('controls',{})[name]={'file':name+'.json','sha256':sha(output/(name+'.json')),
                'processId':data['processId'],'processStartUtc':data['processStartUtc']}
        manifest['status']='running';write(output/'manifest.json',manifest)
        transports=[] if a.controls_only else ['tcp'] if a.tcp_only else ['shm']
        schedule=['A','B'] if a.correctness_only else list('ABBA'*3+'AA')
        for transport in transports:
            for concurrency in [1,32]:
                for index,variant in enumerate(schedule):
                    name=f'{transport}-add-c{concurrency}-{index:02}-{variant}'
                    path=output/(name+'.json')
                    binary_hash=sha(pathlib.Path(exe(variant)))
                    if binary_hash != manifest['binaries'][variant]['SharpLink.Benchmarks.dll']:
                        raise ValueError('Executing DLL drift before launch')
                    command(execute(variant)+[transport,'add',str(concurrency),str(a.operations),str(a.warmup),name,str(path)],
                        variants[variant],output/(name+'.log'),overrides={'ISSUE739_MULTIPLICITY_VARIANT':variant})
                    row=json.loads(path.read_text())
                    try:
                        validate_row(row,transport,concurrency,a.operations,a.warmup,name,variant,expected_runtime,pids)
                    except Exception:
                        # Preserve labeled standalone scope totals even when the additive ledger is invalid.
                        write(output/(name+'-rejected-ledger.json'),ledger(row,a.correctness_only))
                        raise
                    expected_corelib=manifest['toolchainFiles'][str(toolchain[-1])]
                    if row['scopes']['corelibSha256']!=expected_corelib:raise ValueError('Executing runtime CoreLib hash mismatch')
                    if row['multiplicity']['executingAssemblySha256']!=binary_hash:
                        raise ValueError('Child executing-assembly hash differs from launch-path binary')
                    manifest['rawSamples'][name]={'file':path.name,'sha256':sha(path),'processId':row['processId'],
                        'executedDllSha256':binary_hash,'variant':variant,'processStartUtc':row['scopes']['processStartUtc']}
                    write(output/'manifest.json',manifest)
                    if row['sourceSha'] != SOURCE or row['runtime'] != expected_runtime and not a.correctness_only:
                        raise ValueError('Source/runtime mismatch')
                    if row['multiplicity']['variant'] != variant or not row['multiplicity']['configurationVerified']:
                        raise ValueError('Diagnostic/configuration mismatch')
                    row['_sequence']=index;row['_variant']=variant
                    row['_ledger']=ledger(row,a.correctness_only)
                    rows.append(row)
                    write(output/'ledger-partial.json',[{'sample':r['sample'],**r['_ledger']} for r in rows])
                    print(name,f"{row['bytesPerOperation']:.3f} B/op",flush=True)
                cell=[r for r in rows if r['transport']==transport and r['concurrency']==concurrency]
                layouts=[(r['multiplicity']['stateMachineFields'],r['scopes']['stateMachineFields']) for r in cell]
                if not layouts[0] or any(x!=layouts[0] for x in layouts[1:]):
                    raise ValueError('No-shim state-machine fields/builder metadata differ across variants')
        summary=summarize(rows,a.correctness_only)
        write(output/'summary.json',summary)
        for name,digest in manifest['toolchainFiles'].items():
            if sha(pathlib.Path(name))!=digest:raise ValueError('Toolchain file changed: '+name)
        for name,digest in manifest['projectionArtifacts'].items():
            if sha(output/name)!=digest:raise ValueError('Projection artifact changed: '+name)
        for name,digest in manifest['originalInputs'].items():
            if sha(ROOT/name)!=digest:raise ValueError('Original input changed: '+name)
        for variant in variants:
            for name,digest in projection['variants'][variant]['files'].items():
                if sha(variants[variant]/name)!=digest:raise ValueError('Projected input changed: '+name)
            bindir=pathlib.Path(exe(variant)).parent
            for name,digest in manifest['binaries'][variant].items():
                if sha(bindir/name)!=digest or sha(output/'executed-binaries'/variant/name)!=digest:
                    raise ValueError('Executed/exported binary changed: '+name)
        for record in list(manifest['rawSamples'].values())+list(manifest['controls'].values()):
            if sha(output/record['file'])!=record['sha256']:raise ValueError('Raw sample changed')
        for name,digest in projection['scopeExpectedInputHashes'].items():
            if sha(ROOT/name)!=digest:raise ValueError('Frozen dependency changed: '+name)
        for name,digest in manifest['toolFiles'].items():
            if sha(HERE/name)!=digest:raise ValueError('Experiment tooling changed: '+name)
        manifest['endIntegrityCheck']='original/projected sources, experiment tools, executed/exported binaries, raw sample hashes unchanged'
        manifest['status']='correctness-controls-complete' if a.controls_only else 'correctness-complete' if a.correctness_only else 'complete'
        manifest['rpcProcessesCompleted']=len(rows)
        manifest['stateMachineFieldCheck']='not compared (controls-only)' if a.controls_only else 'identical full reflected async fields and builder attributes across A/B; not an object-size measurement'
    except Exception as exception:
        manifest['status']='failed';manifest['error']=str(exception)
        raise
    finally:
        manifest['elapsedSeconds']=a.budget_seconds-(deadline-time.monotonic())
        write(output/'manifest.json',manifest)

SCOPE_FIELDS = ['calls','inclusiveBytes','elapsedTicks','completedSuccess','pending','faulted','canceled','synchronousThrow',
    'completedSuccessBytes','pendingBytes','faultedBytes','canceledBytes','synchronousThrowBytes','firstBeginSinceProcessStart','lastEnd','sameThread']
SCOPE_KEYS = {'schemaVersion','variant','processStartUtc','corelibSha256','before','after','delta','beforeSnapshotBegin',
    'beforeSnapshotEnd','afterSnapshotBegin','afterSnapshotEnd','stateMachineFields','sites','fieldOrder','trailingFields','meaning','boundary'}

def validate_row(row,transport,concurrency,operations,warmup,sample,variant,runtime,pids):
    import datetime
    import re
    original=dict(row);s=original.pop('scopes')
    base.validate_row(original,transport,concurrency,operations,warmup,sample,variant,runtime,pids)
    if set(s)!=SCOPE_KEYS or s['schemaVersion']!=1 or s['variant']!=variant:raise ValueError('Scope schema/variant mismatch')
    if not re.fullmatch('[0-9a-f]{64}',s['corelibSha256']):raise ValueError('Invalid corelib hash')
    start=datetime.datetime.fromisoformat(s['processStartUtc'].replace('Z','+00:00'))
    if start.tzinfo is None:raise ValueError('Missing process start timezone')
    if s['fieldOrder']!=SCOPE_FIELDS or s['trailingFields']!=['overlap','coldThreads','threadMismatch','invalidObservation','pricedOwnerOverlap']:raise ValueError('Scope field order mismatch')
    if len(s['sites'])!=2 or not s['stateMachineFields']:raise ValueError('Missing scope sites/metadata')
    methods=[x['method'] for x in s['stateMachineFields']]
    for required in ['SharpLink.Runtime.SharedMemoryPipeReader.ReadAsync/0','SharpLink.Runtime.SharedMemoryControlChannel.RunReaderAsync/0','SharpLink.Runtime.SharedMemoryControlChannel.WaitWithCancellationAsync/0','SharpLink.Client.SharpLinkClient.ProcessRequestLoop/0','SharpLink.Server.SharpLinkServer.ProcessRequestLoop/0']:
        if methods.count(required)!=1:raise ValueError('Missing/duplicate required state machine '+required)
    if not (s['beforeSnapshotBegin']<=s['beforeSnapshotEnd']<=row['ticksStart']<row['ticksEnd']<=s['afterSnapshotBegin']<=s['afterSnapshotEnd']):raise ValueError('Scope/gross boundaries not ordered')
    expected=37 if variant=='B' else 0
    for key in ['before','after','delta']:
        if len(s[key])!=expected or any(type(x)is not int or x<0 for x in s[key]):raise ValueError('Invalid scope '+key)
    if s['delta']!=[end-begin for begin,end in zip(s['before'],s['after'])]:raise ValueError('Scope delta mismatch')
    if variant=='A':return
    if any(s['delta'][index]!=0 for index in [32,33,34,35,36]):raise ValueError('Scope overlap/cold ThreadStatic/thread mismatch/invalid observation/priced owner overlap; reject attribution')
    # Also reject any earlier overlap or thread mismatch; warmup cannot hide invalid instrumentation.
    if any(s['after'][index]!=0 for index in [32,34,35,36]):raise ValueError('Prior scope invariant failure')
    if s['before'][33]<1:raise ValueError('No warmed ThreadStatic paths')
    for site in [0,1]:
        o=site*16;delta=s['delta'][o:o+16]
        if delta[0]!=sum(delta[3:8]) or delta[1]!=sum(delta[8:13]) or delta[0]!=delta[15]:raise ValueError('Scope count/byte/thread reconciliation failed')
        if transport=='shm' and delta[0]<=0:raise ValueError('SHM scope did not execute')
        if transport=='tcp' and any(delta[:13]):raise ValueError('TCP negative control unexpectedly scoped')
        if delta[0] and not (0<s['after'][o+13]<=s['beforeSnapshotBegin'] and s['beforeSnapshotEnd']<=s['after'][o+14]<=s['afterSnapshotEnd']):raise ValueError('Scope timestamp bounds inconsistent')
    # Scientific owner counts are outcomes, not rejection gates. The reused owner
    # ledger checks branch balance and entry=accepted<=helper, prices observed
    # registrations, and leaves changed calibration assumptions unpriced.

def ledger(row,correctness):
    owner=base.ledger(row,correctness)
    if row['multiplicity']['variant']=='A':return owner
    s=row['scopes'];details=[]
    for index,name in enumerate(['shmPipeReadInitiation','controlPipeReadInitiation']):
        delta=s['delta'][index*16:(index+1)*16]
        details.append({'site':name,**{key:delta[i] for i,key in enumerate(SCOPE_FIELDS) if i not in [13,14]},
            'firstBeginSinceProcessStart':s['after'][index*16+13], 'lastEnd':s['after'][index*16+14],
            'inclusiveBytesPerPayloadOperation':delta[1]/row['operations'],
            'inclusiveBytesPerInitiation':delta[1]/delta[0] if delta[0] else None,
            'elapsedNanosecondsPerInitiation':delta[2]*1e9/row['stopwatchFrequency']/delta[0] if delta[0] else None})
    scope_bytes=sum(site['inclusiveBytes'] for site in details)
    violations=[name for i,name in enumerate(['overlap','coldThreads','threadMismatch','invalidObservation','pricedOwnerOverlap']) if s['delta'][32+i] != 0 or (i != 1 and s['after'][32+i] != 0)]
    predicted=None if violations else owner['predictedBytes']
    reconciled=None if predicted is None else predicted+scope_bytes
    return {'status':'inclusive-scope-plus-conditional-owner-budget' if reconciled is not None else 'unpriced-correctness-or-assumption-gap',
        'grossBytes':row['bytes'],'ownerBudget':owner,'observedUnpricedOwnerActivity':{name:owner.get('counts',{}).get(name) for name in ['operation_new','context_snapshot_new','send_capacity_tcs_new','send_capacity_wait_uses']},'scopeSites':details,'additiveReconciliationRejected':bool(violations),'scopeInvariantViolations':violations,'measuredInclusiveScopeBytes':scope_bytes,
        'measuredInclusiveScopeBytesPerPayloadOperation':scope_bytes/row['operations'],'conditionalReconciledBytes':reconciled,
        'signedResidualBytes':None if reconciled is None else row['bytes']-reconciled,
        'signedResidualBytesPerPayloadOperation':None if reconciled is None else (row['bytes']-reconciled)/row['operations'],
        'meaning':'two observed nonnested synchronous managed-allocation calltrees plus transferred conditional owner calibration; no exact objects/box count, no separate nested wait pricing',
        'residual':'signed, not clamped; includes allocations after original synchronous calls return, unmeasured paths and measurement-boundary difference; gross diagnostic B only, never replace or correct primary A',
        'returnStatus':'observed after allocation endpoint; pending is not proven actual suspension or source registration'}

def summarize(rows,correctness):
    result=base.summarize(rows,correctness)
    result['completionScope']='SHM Add c1/c32, 3 ABBA blocks plus AA per cell; two standalone zero-managed-allocation controls; no automatic extension'
    result['unresolved']='exact object identities/counts, completion-path allocations after initiation returns, outer read-consumer registrations, and any signed residual remain unknown'
    result['scopeBudget']='inclusive measured calltree bytes, not per-type pricing; nested WaitWithCancellation included once inside SHM ReadAsync initiation'
    return result

if __name__=='__main__':main()
