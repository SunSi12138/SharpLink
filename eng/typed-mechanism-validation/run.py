#!/usr/bin/env python3
"""Frozen focused correctness and paired full-path mechanism checks; no uploads."""
from __future__ import annotations
import argparse, hashlib, importlib.util, json, math, os, shutil, statistics, subprocess, sys, tempfile
from pathlib import Path
HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent / 'static-stream-unit-validation'))
from validate import GateError, require, strict_json, read_report, discovery_uids, uid_digest, unique_uids, validate_run
spec = importlib.util.spec_from_file_location('reviewed_unit', HERE.parent / 'static-stream-unit-validation/run.py')
unit = importlib.util.module_from_spec(spec); spec.loader.exec_module(unit)
BASE_PARENT = '3d76a3a8b55a37e24c62a073fe588cff3d3867be'
BASE_TREE = '410fdb911cf7120ccabbbd10c820b0c9a73d9223'
SOURCE_TREE = 'b3e94d0c00a6e581944e98ffe7e971660f21b2e4'
BRANCH = 'refs/heads/validation/typed-window-mechanism-20261029'
REQUEST = 'eng/typed-mechanism-validation/request.json'
SOURCE_DIFF = ['A\tsrc/SharpLink.Runtime/OutgoingWindowUpdate.cs', 'M\tsrc/SharpLink.Runtime/OwnedFrame.cs',
    'M\tsrc/SharpLink.Runtime/RpcSession.SendPump.cs', 'A\tsrc/SharpLink.Runtime/RpcSession.WindowUpdatePublication.cs',
    'M\tsrc/SharpLink.Runtime/RpcSessionExtensions.cs', 'M\tsrc/SharpLink.Runtime/SharpLinkRuntimeContext.cs',
    'A\ttest/SharpLink.UnitTests/Runtime/TypedWindowUpdateTests.cs']
FOCUSED = unique_uids(strict_json((HERE/'focused-uids.json').read_text()), 'focused corpus')
TYPED = unique_uids(strict_json((HERE/'typed-uids.json').read_text()), 'typed corpus')
CLEANUP = unique_uids(strict_json((HERE/'cleanup-uids.json').read_text()), 'cleanup corpus')
CASES = ['wu1', 'wu100', 'burst513', 'cold128', 'shared4', 'normal21', 'normal1024']

def git(root, *args): return subprocess.check_output(['git','-C',str(root),*args],text=True).strip()
def sha(value): return isinstance(value,str) and len(value)==40 and all(c in '0123456789abcdef' for c in value)
def clean(root): require(not git(root,'status','--porcelain','--untracked-files=all'), 'source worktree dirty')
def hashes(root): return {str(p.relative_to(root)):hashlib.sha256(p.read_bytes()).hexdigest() for p in sorted(root.rglob('*.dll'))}

def verify_release(root, release, ref):
    require(ref==BRANCH and sha(release) and git(root,'rev-parse','HEAD')==release,'release identity mismatch');clean(root)
    request = strict_json((root/REQUEST).read_text())
    require(isinstance(request,dict) and set(request)=={'schema_version','baseline_sha','candidate_sha','tools_sha','tools_tree'},'request shape')
    require(type(request['schema_version']) is int and request['schema_version']==1,'request schema')
    require(all(sha(request[k]) for k in request if k!='schema_version'),'request SHA')
    base,candidate,tool = (request[k] for k in ('baseline_sha','candidate_sha','tools_sha'))
    require(git(root,'show','-s','--format=%P','HEAD')==tool,'request parent')
    require(git(root,'diff','--name-status','--no-renames',tool,'HEAD')=='A\t'+REQUEST,'request-only diff')
    require(git(root,'rev-parse',tool+'^{tree}')==request['tools_tree'],'tool tree')
    require(git(root,'show','-s','--format=%P',tool)==candidate,'tool parent')
    require(git(root,'show','-s','--format=%P',candidate)==base,'candidate parent')
    require(git(root,'show','-s','--format=%P',base)==BASE_PARENT,'base parent')
    require(git(root,'rev-parse',candidate+'^{tree}')==SOURCE_TREE and git(root,'rev-parse',base+'^{tree}')==BASE_TREE,'reviewed trees')
    require(git(root,'diff','--name-status','--no-renames',base,candidate).splitlines()==SOURCE_DIFF,'candidate source scope')
    files = strict_json((HERE/'tool-files.json').read_text())
    require(git(root,'diff','--name-status','--no-renames',candidate,tool).splitlines()==sorted('A\t'+f for f in files),'tool scope')
    return request

def verify_source(root, request, arm):
    expected=request['baseline_sha' if arm=='baseline' else 'candidate_sha']
    require(git(root,'rev-parse','HEAD')==expected,'arm SHA')
    require(git(root,'rev-parse','HEAD^{tree}')==(BASE_TREE if arm=='baseline' else SOURCE_TREE),'arm frozen tree')
    require(git(root,'show','-s','--format=%P','HEAD')==(BASE_PARENT if arm=='baseline' else request['baseline_sha']),'arm parent')
    clean(root)

def schedule():
    return [(pair,arm) for pair in range(1,7) for arm in (('baseline','candidate') if pair%2 else ('candidate','baseline'))]

def report(path):
    files=list(path.glob('*.tunit-report.json'));require(len(files)==1,'missing/ambiguous test report');return read_report(files[0])

def run_focused(root, sdk, host, env, output):
    code,timeout=unit.execute([sdk,'build',unit.PROJECT,'-c','Release','-m:1','-nr:false','--disable-build-servers','-p:UseSharedCompilation=false','-v','minimal'],root,env,900,output/'unit-build.log')
    require(code==0 and not timeout,'focused build failed')
    assembly=root/unit.ASSEMBLY;identity=hashes(assembly.parent)
    code,timeout=unit.execute([host,str(assembly),'--list-tests','json'],root,unit.private_test_environment(env,host),180,output/'discovery.json')
    require(code==0 and not timeout,'focused discovery failed')
    discovery=read_report(output/'discovery.json');raw=discovery_uids(discovery)
    original=unique_uids(strict_json((root/'eng/static-stream-unit-validation/expected-uids.json').read_text()),'original corpus')
    require(raw==sorted(original+CLEANUP+TYPED) and len(raw)==2328,'exact fresh raw UID census changed')
    require(len(FOCUSED)==138 and len(TYPED)==12 and len(CLEANUP)==6 and set(FOCUSED).issubset(raw),'focused corpus changed')
    for t in discovery['tests']:
        if t['uid'] in TYPED:
            require(t.get('location',{}).get('file','').replace('\\','/').endswith('/test/SharpLink.UnitTests/Runtime/TypedWindowUpdateTests.cs'),'typed source mapping')
    print('FOCUSED CENSUS raw=2328 selected=138 typed=12 cleanup=6 existing=120 uid_sha256='+uid_digest(FOCUSED),flush=True)
    failures=[]
    for index,uid in enumerate(FOCUSED):
        case=output/('test-'+str(index));case.mkdir()
        args=[host,str(assembly),'--filter-uid',uid,'--maximum-parallel-tests','1','--minimum-expected-tests','1','--zero-tests-policy','strict','--no-ansi','--progress','off','--show-stdout','None','--show-stderr','None','--show-test-results','failed,skipped','--report-html','--report-html-filename',str(case/'result.html'),'--results-directory',str(case/'results')]
        try:
            code,timeout=unit.execute(args,root,unit.private_test_environment(env,host),120,case/'worker.log')
            r=report(case);require(r.get('runtimeVersion')=='.NET 10.0.14','focused runtime');validate_run([uid],r,code,timeout)
            if uid in TYPED:print('TYPED PASS uid='+uid,flush=True)
        except Exception as error:
            failures.append(dict(uid=uid,error=type(error).__name__,detail=str(error)))
            print('FOCUSED FAIL uid='+uid+' class='+type(error).__name__,flush=True)
    (output/'focused-failures.json').write_text(json.dumps(failures,indent=2)+'\n')
    require(identity==hashes(assembly.parent),'focused DLLs changed')
    require(not failures,'focused failures retained; mechanism not executed')
    print('FOCUSED PASS selected=138 typed=12 failed_or_skipped=0 runtime=10.0.14',flush=True)
    return identity

def integer(value, minimum=0): return type(value) is int and minimum<=value<=9223372036854775807

def validate_measurement(data, arm, pair):
    require(isinstance(data,dict) and type(data.get('schemaVersion')) is int and data['schemaVersion']==1,'mechanism schema')
    require(data.get('arm')==arm and type(data.get('pair')) is int and data['pair']==pair,'mechanism slot identity')
    require(data.get('status')=='pass' and data.get('failure') is None,'mechanism process report failure')
    require(data.get('runtimeVersion')=='10.0.14' and data.get('runtime')=='.NET 10.0.14','mechanism runtime')
    require(data.get('sourceTree')==(BASE_TREE if arm=='baseline' else SOURCE_TREE),'mechanism reviewed source tree')
    require(data.get('reviewedSourceCommit')==('0403d6a0a4eb2b1bc9dafc9c87aa876c36f0d158' if arm=='baseline' else 'e62f7a335f16595bad37bc3a8edeb7b945c5b686'),'mechanism reviewed source commit label')
    require(data.get('architecture')=='X64' and data.get('processorCount')==4 and data.get('profile')=='Balanced_unmodified','mechanism configuration')
    require(integer(data.get('stopwatchFrequency'),1) and data.get('cpuTicksPerSecond')==10000000,'mechanism clock units')
    require(data.get('boundary')=='zero_queued_bytes_then_one_zero_byte_marker_per_batch','mechanism completion boundary')
    require(data.get('scope')=='mechanism_only_not_TCP_or_QPS_or_p99','mechanism scope')
    rows=data.get('cases');require(isinstance(rows,list) and all(isinstance(r,dict) for r in rows) and [r.get('case') for r in rows]==CASES,'seven-case census')
    for row in rows:
        name=row['case'];cold=name=='cold128'
        require(row.get('passed') is True,'mechanism case failed')
        require(all(integer(row.get(k),1 if k in ('ops','elapsedTicks') else 0) for k in ('ops','bytes','allocatedBytes','cpuTicks','elapsedTicks')),'mechanism cost counters')
        require(type(row.get('finalQueuedBytes')) is int and row['finalQueuedBytes']==0,'mechanism did not settle physical ownership')
        require(row.get('poolDisposedAndEmpty') is True,'mechanism pool disposal not verified')
        size=1024 if name=='normal1024' else 21
        require(row['bytes']==row['ops']*size and row.get('framesFromConservedFixedSize')==row['ops'],'physical frame/byte conservation')
        require(row.get('warmupSeconds')==(0 if cold else 1) and row.get('measureSeconds')==(0 if cold else 1),'fixed measurement durations')
        retained=row.get('retainedNodes')
        if arm=='baseline' or name.startswith('normal'):
            require(retained is None,'unexpected typed pool in baseline/ordinary-only case')
        else:
            upper={'wu1':1,'wu100':100,'burst513':512,'cold128':1,'shared4':4}[name]
            require(integer(retained,1) and retained<=upper,'retained node census')
            if name=='burst513':require(retained==512,'overflow cap not exercised')
        if cold:
            require(row['ops']==128 and row.get('contexts')==128 and row.get('fixedOperationCount') is True and row.get('validatedFrames')==128,'fixed cold context count')
            construction=row.get('construction')
            require(isinstance(construction,dict) and construction.get('Operations')==128 and all(integer(construction.get(k)) for k in ('AllocatedBytes','ElapsedTicks','CpuTicks')),'separate cold construction costs')
        else:
            batch=100 if name=='wu100' else 513 if name=='burst513' else 1
            sessions=4 if name=='shared4' else 1
            require(row.get('batchSize')==batch and row.get('sessions')==sessions and row.get('ordinaryBytes')==(size if name.startswith('normal') else 0),'case configuration')
            require(row.get('forcedHeldFlushStress') is (name=='burst513'),'explicit overflow-only hold')
            require(row['elapsedTicks']>=data['stopwatchFrequency'] and row['cpuTicks']>0,'warm window too short or unmeasurable CPU')
            validation=row.get('validation')
            require(isinstance(validation,dict) and validation.get('Frames')==batch*sessions and validation.get('Bytes')==batch*sessions*size and integer(validation.get('Flushes'),1),'untimed wire validation missing')
            require(integer(row.get('flushes'),1),'physical flush count missing')
    return rows

def validate_loaded_identity(data, directory, identity):
    for field,name in (('productAssembly','SharpLink.Runtime.dll'),('consoleAssembly','SharpLink.Benchmarks.dll')):
        value=data.get(field)
        require(isinstance(value,dict) and value.get('path')==str(directory/name) and value.get('sha256')==identity.get(name),'loaded own-arm assembly identity')
        mvid=value.get('mvid');require(isinstance(mvid,str) and len(mvid)==36,'missing assembly MVID')

def scalar_rows(data):
    # Emit only an allowlisted finite set of validated scalar measurements. No
    # source paths, report text, exception objects, traces or individual timings.
    rows=[]
    for case in data['cases']:
        row=dict(pair=data['pair'],arm=data['arm'],case=case['case'])
        for key in ('ops','bytes','cpuTicks','elapsedTicks','allocatedBytes','retainedNodes','finalQueuedBytes'):
            row[key]=case[key]
        row['stopwatchFrequency']=data['stopwatchFrequency']
        row['cpuTicksPerSecond']=data['cpuTicksPerSecond']
        row['retainedNodesMeaning']='maximum_per_context_across_128' if case['case']=='cold128' else 'single_shared_context_idle_nodes'
        if case['case']=='cold128':
            construction=case['construction']
            row.update(constructionOps=construction['Operations'],constructionAllocatedBytes=construction['AllocatedBytes'],
                       constructionElapsedTicks=construction['ElapsedTicks'],constructionCpuTicks=construction['CpuTicks'])
        rows.append(row)
    return rows

def summarize(results):
    require(len(results)==12 and {(r['pair'],r['arm']) for r in results}==set(schedule()),'paired result census')
    indexed={(r['pair'],r['arm']):{c['case']:c for c in r['cases']} for r in results}
    frequencies={(r['pair'],r['arm']):r['stopwatchFrequency'] for r in results}
    summaries=[]
    for name in CASES:
        cpu=[];wall=[];alloc=[];pairs=[]
        for pair in range(1,7):
            b=indexed[pair,'baseline'][name];c=indexed[pair,'candidate'][name]
            cpu_ratio=None
            if b['cpuTicks']>0 and c['cpuTicks']>0:
                cpu_ratio=(c['cpuTicks']/c['ops'])/(b['cpuTicks']/b['ops']);cpu.append(cpu_ratio)
            else:require(name=='cold128','warm CPU resolution insufficient')
            elapsed_ratio=(c['elapsedTicks']/frequencies[pair,'candidate']/c['ops'])/(b['elapsedTicks']/frequencies[pair,'baseline']/b['ops'])
            allocation_delta=c['allocatedBytes']/c['ops']-b['allocatedBytes']/b['ops']
            wall.append(elapsed_ratio);alloc.append(allocation_delta)
            pairs.append(dict(pair=pair,cpu_ratio=cpu_ratio,elapsed_ratio=elapsed_ratio,allocated_bytes_per_op_delta=allocation_delta))
        summaries.append(dict(case=name,pairs=pairs,paired_cpu_ratio_median=statistics.median(cpu) if len(cpu)==6 else None,
            paired_cpu_ratio_min=min(cpu) if len(cpu)==6 else None,paired_cpu_ratio_max=max(cpu) if len(cpu)==6 else None,
            cpu_ratio_all_six_available=len(cpu)==6,paired_elapsed_ratio_median=statistics.median(wall),
            allocated_bytes_per_op_delta_median=statistics.median(alloc)))
    return summaries

def run_mechanism(roots,sdk,host,env,output,unit_identity):
    source=HERE/'console';expected={str(p.relative_to(source)):hashlib.sha256(p.read_bytes()).hexdigest() for p in source.rglob('*') if p.is_file()}
    outputs={};identities={}
    for arm in ('baseline','candidate'):
        copy=output/('console-'+arm);shutil.copytree(source,copy)
        require({str(p.relative_to(copy)):hashlib.sha256(p.read_bytes()).hexdigest() for p in copy.rglob('*') if p.is_file()}==expected,'harness source differs between arms')
        binary=output/('bin-'+arm)
        code,timeout=unit.execute([sdk,'build',str(copy/'Mechanism.csproj'),'-c','Release','-m:1','-nr:false','-p:UseSharedCompilation=false','-p:ProductRoot='+str(roots[arm]),'-p:Arm='+arm,'-o',str(binary)],roots[arm],env,900,output/('console-build-'+arm+'.log'))
        require(code==0 and not timeout,'mechanism console build failed')
        outputs[arm]=binary;identities[arm]=hashes(binary)
        require('SharpLink.Benchmarks.dll' in identities[arm] and 'SharpLink.Runtime.dll' in identities[arm],'mechanism binaries missing')
    require(identities['candidate']['SharpLink.Runtime.dll']==unit_identity['SharpLink.Runtime.dll'],'unit/mechanism production DLL mismatch')
    (output/'mechanism-identities.json').write_text(json.dumps(dict(harness=expected,dlls=identities),indent=2)+'\n')
    results=[];failures=[]
    for pair,arm in schedule():
        path=output/('pair-'+str(pair)+'-'+arm+'.json')
        try:
            require(hashes(outputs[arm])==identities[arm],'mechanism DLLs changed before slot')
            code,timeout=unit.execute([host,str(outputs[arm]/'SharpLink.Benchmarks.dll'),'--arm',arm,'--pair',str(pair),'--output',str(path)],roots[arm],unit.private_test_environment(env,host),120,output/(path.stem+'.log'))
            require(not timeout and code==0,'mechanism worker failure/timeout')
            data=read_report(path);validate_measurement(data,arm,pair);validate_loaded_identity(data,outputs[arm],identities[arm])
            require(hashes(outputs[arm])==identities[arm],'mechanism DLLs changed after slot')
            results.append(data)
            for row in scalar_rows(data):print('MECHANISM SCALAR '+json.dumps(row,sort_keys=True,separators=(',',':'),allow_nan=False),flush=True)
            print('MECHANISM SLOT PASS pair='+str(pair)+' arm='+arm,flush=True)
        except Exception as error:
            failures.append(dict(pair=pair,arm=arm,error=type(error).__name__,detail=str(error)))
            print('MECHANISM SLOT FAIL pair='+str(pair)+' arm='+arm+' class='+type(error).__name__,flush=True)
    (output/'mechanism-failures.json').write_text(json.dumps(failures,indent=2)+'\n')
    require(not failures and len(results)==12,'incomplete mechanism; every failed slot retained, no replacements')
    for summary in summarize(results):print('MECHANISM SUMMARY '+json.dumps(summary,sort_keys=True),flush=True)
    print('MECHANISM COMPLETE pairs=6 arms=2 cases=7 performance_target_claim=false',flush=True)
    return results

def tcp(root,sdk,host,env,output,unit_identity):
    target=root/'eng/static-stream-cleanup-regression'
    for name in ('module','probe'):
        code,timeout=unit.execute([sdk,'build',str(target/name/(name+'.csproj')),'-c','Release','-m:1','-nr:false','-p:UseSharedCompilation=false'],root,env,900,output/('tcp-build-'+name+'.log'))
        require(code==0 and not timeout,'TCP build failed')
    directory=target/'probe/bin/Release/net10.0';shutil.copy2(target/'module/bin/Release/net10.0/StaticStreamCleanupModule.dll',directory/'StaticStreamCleanupModule.dll')
    identity=hashes(directory)
    for name in ('SharpLink.Runtime.dll','SharpLink.Client.dll','SharpLink.Server.dll'):require(identity.get(name)==unit_identity.get(name),'unit/TCP production DLL mismatch')
    code,timeout=unit.execute([sys.executable,str(target/'run.py'),'--extended','--no-build','--dotnet',sdk,'--runtime-dotnet',host,'--runtime-version','10.0.14'],root,env,1500,output/'tcp.log')
    require(not timeout and code==0 and hashes(directory)==identity,'TCP failed/timeout/DLL changed')
    lines=(output/'tcp.log').read_text().splitlines()
    expected=[(c,t) for c in ('static-none','static-cancellable','dynamic-none','static-deadline') for t in ('early-break','explicit-dispose','consumer-throw')]+[('static-enumerator-only','enumerator-cancel'),('static-none-held','explicit-dispose')]
    rows=[strict_json(l) for l in lines if l.startswith('{')];rows=[r for r in rows if r.get('suite')=='new-source-serverstream-cleanup']
    require([(r.get('condition'),r.get('termination')) for r in rows]==expected,'TCP case census')
    require(all(r.get('passed') is True and r.get('failure') is None and r.get('teardownFailures')==[] and r.get('runtimeVersion')=='10.0.14' and r.get('expectedRuntimeVersion')=='10.0.14' and r.get('frameworkDescription')=='.NET 10.0.14' and r.get('historicalReplay') is False for r in rows),'TCP case/runtime/teardown')
    require('NEW_CORRECTNESS_RESULT cases=14 failures=[]' in lines,'TCP final marker')
    print('TCP14 PASS candidate_only runtime=10.0.14',flush=True)

def main():
    p=argparse.ArgumentParser();p.add_argument('--release-root',type=Path,required=True);p.add_argument('--baseline-root',type=Path);p.add_argument('--candidate-root',type=Path);p.add_argument('--provenance-only',action='store_true');a=p.parse_args()
    request=verify_release(a.release_root.resolve(),os.environ.get('GITHUB_SHA',''),os.environ.get('GITHUB_REF',''))
    if a.provenance_only:
        with open(os.environ['GITHUB_OUTPUT'],'a') as f:f.write('baseline_sha='+request['baseline_sha']+'\ncandidate_sha='+request['candidate_sha']+'\n')
        print('TYPED PROVENANCE PASS '+json.dumps(request,sort_keys=True));return 0
    require(a.baseline_root is not None and a.candidate_root is not None,'missing roots')
    roots={'baseline':a.baseline_root.resolve(),'candidate':a.candidate_root.resolve()}
    for arm,root in roots.items():verify_source(root,request,arm)
    env={k:v for k,v in os.environ.items() if not k.startswith(('TUNIT_','TESTINGPLATFORM_'))};env.update(DOTNET_PROCESSOR_COUNT='4',DOTNET_CLI_TELEMETRY_OPTOUT='1',TESTINGPLATFORM_TELEMETRY_OPTOUT='1',PYTHONDONTWRITEBYTECODE='1')
    sdk,host=unit.verify_hosts(roots['candidate'],env)
    output=Path(tempfile.mkdtemp(prefix='typed-mechanism-',dir=env.get('RUNNER_TEMP')))
    try:
        unit_identity=run_focused(roots['candidate'],sdk,host,env,output)
        run_mechanism(roots,sdk,host,env,output,unit_identity)
        tcp(roots['candidate'],sdk,host,env,output,unit_identity)
        for arm,root in roots.items():verify_source(root,request,arm)
        print('TYPED GATE PASS focused=138 paired_mechanism=12x7 TCP=14 no_release_or_performance_target_claim=true',flush=True);return 0
    except Exception as error:
        (output/'failure.json').write_text(json.dumps(dict(type=type(error).__name__,detail=str(error)),indent=2)+'\n')
        print('TYPED GATE FAIL class='+type(error).__name__+' classification='+(str(error)[:180] if isinstance(error,GateError) else 'unclassified'),flush=True);return 1

if __name__=='__main__':
    try:sys.exit(main())
    except Exception as error:print('TYPED PREFLIGHT FAIL class='+type(error).__name__,flush=True);sys.exit(1)
