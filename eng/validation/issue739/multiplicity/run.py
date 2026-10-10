#!/usr/bin/env python3
"""Four-cell bounded no-shim owner multiplicity experiment; preserve every sample."""
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
from project import SOURCE, HERE, ROOT, project, sha
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
    p.add_argument('--budget-seconds', type=int, default=1380)
    a = p.parse_args()
    if a.tcp_only and not a.correctness_only:
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
        'runtimePin':expected_runtime, 'sdkPin':expected_sdk, 'sourceRole':'A vanilla primary; B no-shim diagnostic',
        'operationCount':a.operations, 'warmup':a.warmup, 'budgetSeconds':a.budget_seconds,
        'platform':platform.platform(), 'processes':[], 'binaries':{}, 'rawSamples':{},
        'sanitizedTuningNames':sorted(stripped), 'toolFiles':{f.name:sha(f) for f in HERE.iterdir() if f.is_file()},
        'policy':{'noOutlierRemoval':True,'freshProcesses':True,'automaticFollowonExperiments':False,
            'productionSchedule':'four cells, each 3 ABBA blocks then AA = 56 RPC processes, plus one primitive control process',
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
        manifest.update(dotnetInfo=info, actualSdk=sdk, installedRuntimes=runtimes,
            environment={k:v for k,v in env.items() if k.startswith(('DOTNET_','COMPlus_','ISSUE739_'))})
        if not a.correctness_only and (sdk != expected_sdk or f'Microsoft.NETCore.App {expected_runtime} [' not in runtimes):
            raise ValueError('Pinned SDK/runtime not installed')
        variants = project(work,output)
        projection=json.loads((output/'projection-manifest.json').read_text())
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
        manifest['status']='running';write(output/'manifest.json',manifest)
        transports=['tcp'] if a.tcp_only else ['tcp','shm']
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
                    validate_row(row,transport,concurrency,a.operations,a.warmup,name,variant,expected_runtime,pids)
                    if row['multiplicity']['executingAssemblySha256']!=binary_hash:
                        raise ValueError('Child executing-assembly hash differs from launch-path binary')
                    manifest['rawSamples'][name]={'file':path.name,'sha256':sha(path),'processId':row['processId'],
                        'executedDllSha256':binary_hash,'variant':variant}
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
                layouts=[r['multiplicity']['stateMachineFields'] for r in cell]
                if not layouts[0] or any(x!=layouts[0] for x in layouts[1:]):
                    raise ValueError('No-shim state-machine fields/builder metadata differ across variants')
        summary=summarize(rows,a.correctness_only)
        write(output/'summary.json',summary)
        for name,digest in manifest['originalInputs'].items():
            if sha(ROOT/name)!=digest:raise ValueError('Original input changed: '+name)
        for variant in variants:
            for name,digest in projection['variants'][variant]['files'].items():
                if sha(variants[variant]/name)!=digest:raise ValueError('Projected input changed: '+name)
            bindir=pathlib.Path(exe(variant)).parent
            for name,digest in manifest['binaries'][variant].items():
                if sha(bindir/name)!=digest or sha(output/'executed-binaries'/variant/name)!=digest:
                    raise ValueError('Executed/exported binary changed: '+name)
        for record in manifest['rawSamples'].values():
            if sha(output/record['file'])!=record['sha256']:raise ValueError('Raw sample changed')
        for name,digest in manifest['toolFiles'].items():
            if sha(HERE/name)!=digest:raise ValueError('Experiment tooling changed: '+name)
        manifest['endIntegrityCheck']='original/projected sources, experiment tools, executed/exported binaries, raw sample hashes unchanged'
        manifest['status']='correctness-complete' if a.correctness_only else 'complete'
        manifest['rpcProcessesCompleted']=len(rows)
        manifest['stateMachineFieldCheck']='identical reflected fields and builder attributes across A/B; not an object-size measurement'
    except Exception as exception:
        manifest['status']='failed';manifest['error']=str(exception)
        raise
    finally:
        manifest['elapsedSeconds']=a.budget_seconds-(deadline-time.monotonic())
        write(output/'manifest.json',manifest)

def validate_row(row,transport,concurrency,operations,warmup,sample,variant,runtime,pids):
    baseline=dict(row);m=baseline.pop('multiplicity')
    validate_baseline(baseline,SOURCE,diagnostic=False)
    identity=(row['transport'],row['rpc'],row['concurrency'],row['operations'],row['warmup'],row['sample'],row['runtime'])
    if identity!=(transport,'add',concurrency,operations,warmup,sample,runtime):raise ValueError('Exact sample identity mismatch')
    if type(row['processId']) is not int or row['processId']<=0 or row['processId'] in pids:raise ValueError('Nonunique/invalid fresh process identity')
    pids.add(row['processId'])
    if row['ticksEnd']<=row['ticksStart'] or row['stopwatchFrequency']<=0:raise ValueError('Invalid timestamp range')
    if row['received']!=0 or row['receiveCreditBounded'] or row['serverGc']:raise ValueError('Wrong Add/GC shape')
    for field in ['bytes','cpuNanosecondsPerOperation','cpuMilliseconds','p50Nanoseconds','p99Nanoseconds','gen0']:
        if not math.isfinite(row[field]) or row[field]<0:raise ValueError('Nonfinite/negative metric '+field)
    if row['p99Nanoseconds']<row['p50Nanoseconds']:raise ValueError('Latency quantiles inverted')
    if not math.isclose(row['cpuNanosecondsPerOperation'],row['cpuMilliseconds']*1e6/operations):raise ValueError('CPU denominator mismatch')
    elapsed=(row['ticksEnd']-row['ticksStart'])/row['stopwatchFrequency']
    if not math.isclose(row['elapsedSeconds'],elapsed) or not math.isclose(row['qps'],operations/elapsed):raise ValueError('Elapsed/QPS denominator mismatch')
    if m['variant']!=variant or m['configurationVerified'] is not True:raise ValueError('Probe activation not verified')
    names=NAMES if variant=='B' else []
    if m['counterNames']!=names:raise ValueError('Counter schema mismatch')
    for key in ['before','after','delta']:
        if len(m[key])!=len(names) or any(type(value) is not int or (value<0 and (key!='delta' or names[index]!='operation_registration_inflight')) for index,value in enumerate(m[key])):raise ValueError('Counter data invalid: '+key)
    if m['delta']!=[end-start for start,end in zip(m['before'],m['after'])]:raise ValueError('Counter delta mismatch')
    if not (m['beforeSnapshotBegin']<=m['beforeSnapshotEnd']<=row['ticksStart']<row['ticksEnd']<=m['afterSnapshotBegin']<=m['afterSnapshotEnd']):raise ValueError('Counter/byte boundaries not ordered')
    if m['expectedMeasuredRpcInvocations']!=operations+1 or m['measuredDrainSentinels']!=1 or m['payloadOperations']!=operations:raise ValueError('Sentinel denominator mismatch')

def ledger(row,correctness):
    m=row['multiplicity'];delta=dict(zip(m['counterNames'],m['delta']))
    if m['variant']=='A':
        return {'status':'vanilla-primary-uninstrumented','grossBytes':row['bytes'],'predictedBytes':None,'signedResidualBytes':None}
    expected=row['operations']+1
    errors=[]
    def require(condition,message):
        if not condition:errors.append(message)
    require(m['operationInflightBefore']==0 and m['operationInflightAfter']==0,'nonzero operation registration boundary in-flight')
    require(delta['operation_registration_entry']==delta['operation_registration_accepted'],'registration entries/accepted differ')
    require(delta['operation_registration_failure']==0,'registration failure observed')
    require(delta['logical_decision']==expected,'logical decisions differ from N+1')
    require(delta['logical_fast']+delta['logical_slow']==expected,'logical branch balance mismatch')
    require(delta['logical_slow']==delta['logical_helper_entry'],'logical slow/helper mismatch')
    require(delta['operation_registration_accepted']<=delta['logical_helper_entry'],'unexpected underlying registration multiplicity')
    require(delta['permit_plain_new']==expected and delta['permit_decode_new']==0,'plain permit multiplicity mismatch')
    require(delta['context_cache_hit']==expected and delta['context_snapshot_new']==0,'snapshot reuse assumption failed')
    require(delta['push_null_snapshot']==expected and delta['restore_snapshot_null']==expected,'context transition pair mismatch')
    other_context=[n for n in delta if n.startswith(('push_','restore_')) and n not in ['push_null_snapshot','restore_snapshot_null']]
    require(all(delta[n]==0 for n in other_context),'uncalibrated context transition category')
    require(delta['ownership_read_calls']==delta['ownership_read_completed_successfully']+delta['ownership_read_other'],'ownership read branch balance mismatch')
    require(delta['ownership_read_other']==delta['ownership_helper_entry'],'ownership helper branch mismatch')
    require(delta['send_flush_calls']==delta['send_flush_completed_at_probe']+delta['send_flush_incomplete_at_probe'],'flush status branch balance mismatch')
    predicted=None if errors or correctness else 136*delta['operation_registration_accepted']+64*delta['permit_plain_new']+72*delta['push_null_snapshot']
    return {'status':'conditional-source-calibrated-budget' if predicted is not None else 'unpriced-correctness-or-assumption-gap',
        'assumptionErrors':errors,'grossBytes':row['bytes'],'predictedBytes':predicted,
        'signedResidualBytes':None if predicted is None else row['bytes']-predicted,
        'predictedBytesPerPayloadOperation':None if predicted is None else predicted/row['operations'],
        'signedResidualBytesPerPayloadOperation':None if predicted is None else (row['bytes']-predicted)/row['operations'],
        'formula':'136*actual-direct-logical-source-registration + 64*successful-plain-permit-new + 72*qualifying-null/snapshot/null-cycle',
        'budgetMeaning':'transferred runtime10.0.12 source-shape calibration, not exact-all-objects or profiler attribution; context term additionally assumes no other AsyncLocal map entries, which categories do not verify; gross N+1 sentinel included without subtraction',
        'counts':delta,'explicitNewSitesOnly':{k:delta[k] for k in ['operation_new','permit_plain_new','permit_decode_new','context_snapshot_new','send_capacity_tcs_new']},
        'readMeaning':'ownership branch is TCP inner IsCompletedSuccessfully observation; helper is pooling builder, not allocation count; SHM pulse counts undifferentiated; outer RPC consumer registration unknown'}

def summarize(rows,correctness):
    cells=[]
    fields=['bytesPerOperation','cpuNanosecondsPerOperation','qps','p50Nanoseconds','p99Nanoseconds']
    for transport,concurrency in sorted({(r['transport'],r['concurrency']) for r in rows}):
        cell=sorted([r for r in rows if r['transport']==transport and r['concurrency']==concurrency],key=lambda r:r['_sequence'])
        pairs=[]
        if not correctness:
            for block in range(3):
                group=cell[block*4:block*4+4]
                pairs.append({'block':block,'samples':[r['sample'] for r in group],
                    'BminusA':{f:statistics.mean(r[f] for r in group if r['_variant']=='B')-statistics.mean(r[f] for r in group if r['_variant']=='A') for f in fields}})
        matched=cell if correctness else cell[:12]
        cells.append({'transport':transport,'concurrency':concurrency,'samples':len(cell),
            'matchedABBAOnlyVariantMedians':{v:{f:statistics.median(r[f] for r in matched if r['_variant']==v) for f in fields} for v in ['A','B']},
            'ABBA':pairs,'AA':None if correctness else {'samples':[r['sample'] for r in cell[-2:]],
                'secondMinusFirst':{f:cell[-1][f]-cell[-2][f] for f in fields}},
            'diagnosticLedgers':[{'sample':r['sample'],**r['_ledger']} for r in cell if r['_variant']=='B']})
    return {'schemaVersion':1,'correctnessOnly':correctness,'rpcProcessCount':len(rows),'cells':cells,
        'observerEffect':'raw paired B-minus-A and AA variation; never subtract instrumentation overhead from primary vanilla bytes',
        'completionScope':'four paired Add cells plus bounded owner ledger; no automatic additional experiment',
        'unresolved':'hidden BCL/pool allocations, TCP inner actual registrations, outer RPC read-consumer registrations, net11 SendPump control and stack attribution remain separate gaps'}

if __name__=='__main__':
    main()
