#!/usr/bin/env python3
"""Four instrumented opportunity-count cells. No timings, retries or uploads."""
from __future__ import annotations
import argparse, hashlib, importlib.util, json, os, shutil, subprocess, sys, tempfile, uuid
from pathlib import Path
HERE=Path(__file__).resolve().parent
sys.path.insert(0,str(HERE.parent/'static-stream-unit-validation'))
from validate import GateError, require, strict_json, read_report
spec=importlib.util.spec_from_file_location('unit_helpers',HERE.parent/'static-stream-unit-validation/run.py')
unit=importlib.util.module_from_spec(spec);spec.loader.exec_module(unit)
BASE_PARENT='3d76a3a8b55a37e24c62a073fe588cff3d3867be'
SOURCE_TREES={'baseline':'410fdb911cf7120ccabbbd10c820b0c9a73d9223','candidate':'b3e94d0c00a6e581944e98ffe7e971660f21b2e4'}
OVERLAY_TREES={'baseline':'b09a6fb55fc5eebc2c1feb5de821390aeb281ec7','candidate':'64732f4b4721a7d5a532a8dd0b2940d5d3b67fa6'}
OVERLAY_LABELS={'baseline':'628048df6c7d23e0d1a008dff1abceff4a35ad84','candidate':'bf033991b3d43bf40b71101d73646576c4221bda'}
HOOK_BLOBS={'src/SharpLink.Runtime/RpcSession.cs':'6595d853ad7f6f6b3bbbde4876e44b9512ad74dd','src/SharpLink.Runtime/WindowUpdateCountDiagnostic.cs':'195102730db78366db98de61cebbefbd572c08c9'}
BRANCH='refs/heads/validation/typed-window-counts-20261029'
REQUEST='eng/typed-count-validation/request.json'
SCHEMA='typed-wu-count-diagnostic-v2'
SIDES=('Entered','Accepted','HandlerCompleted','EnteredCreditArgumentBytes','AcceptedCreditArgumentBytes','ReceivedCreditArgumentBytes')
OWNERS=('logicalInvocations','activeClientCalls','activeClientStreams','pendingCalls','activeServerCalls','queuedSendBytes')
SOURCE_DIFF=['A\tsrc/SharpLink.Runtime/OutgoingWindowUpdate.cs','M\tsrc/SharpLink.Runtime/OwnedFrame.cs','M\tsrc/SharpLink.Runtime/RpcSession.SendPump.cs','A\tsrc/SharpLink.Runtime/RpcSession.WindowUpdatePublication.cs','M\tsrc/SharpLink.Runtime/RpcSessionExtensions.cs','M\tsrc/SharpLink.Runtime/SharpLinkRuntimeContext.cs','A\ttest/SharpLink.UnitTests/Runtime/TypedWindowUpdateTests.cs']

def git(root,*args):return subprocess.check_output(['git','-C',str(root),*args],text=True).strip()
def sha(s):return isinstance(s,str) and len(s)==40 and all(c in '0123456789abcdef' for c in s)
def integer(s):return type(s) is int and 0<=s<=9223372036854775807
def clean(root):require(not git(root,'status','--porcelain','--untracked-files=all'),'source dirty')
def hashes(root):return {p.name:hashlib.sha256(p.read_bytes()).hexdigest() for p in sorted(root.glob('*.dll'))}
def schedule():return [('baseline',1),('candidate',1),('baseline',100),('candidate',100)]

def verify_release(root,release,ref):
    require(ref==BRANCH and sha(release) and git(root,'rev-parse','HEAD')==release,'release identity');clean(root)
    r=strict_json((root/REQUEST).read_text())
    keys={'schema_version','baseline_source_sha','candidate_source_sha','baseline_overlay_sha','candidate_overlay_sha','tools_sha','tools_tree'}
    require(isinstance(r,dict) and set(r)==keys and type(r['schema_version']) is int and r['schema_version']==1,'request shape')
    require(all(sha(r[k]) for k in keys-{'schema_version'}),'request SHA')
    base,candidate,tool=(r[k] for k in ('baseline_source_sha','candidate_source_sha','tools_sha'))
    require(git(root,'show','-s','--format=%P','HEAD')==tool,'request parent')
    require(git(root,'diff','--name-status','--no-renames',tool,'HEAD')=='A\t'+REQUEST,'request-only scope')
    require(git(root,'rev-parse',tool+'^{tree}')==r['tools_tree'],'tool tree')
    require(git(root,'show','-s','--format=%P',tool)==candidate,'tool parent')
    require(git(root,'show','-s','--format=%P',candidate)==base,'candidate source parent')
    require(git(root,'show','-s','--format=%P',base)==BASE_PARENT,'baseline source parent')
    for arm in ('baseline','candidate'):require(git(root,'rev-parse',r[arm+'_source_sha']+'^{tree}')==SOURCE_TREES[arm],'reviewed product tree')
    require(git(root,'diff','--name-status','--no-renames',base,candidate).splitlines()==SOURCE_DIFF,'typed source scope')
    files=strict_json((HERE/'tool-files.json').read_text())
    require(git(root,'diff','--name-status','--no-renames',candidate,tool).splitlines()==sorted('A\t'+p for p in files),'tool source scope')
    return r

def verify_overlay(root,request,arm):
    require(git(root,'rev-parse','HEAD')==request[arm+'_overlay_sha'],'diagnostic overlay SHA')
    require(git(root,'rev-parse','HEAD^{tree}')==OVERLAY_TREES[arm],'diagnostic overlay tree')
    parent=request[arm+'_source_sha']
    require(git(root,'show','-s','--format=%P','HEAD')==parent,'diagnostic overlay parent')
    require(git(root,'rev-parse',parent+'^{tree}')==SOURCE_TREES[arm],'diagnostic product parent tree')
    require(git(root,'diff','--name-status','--no-renames',parent,'HEAD').splitlines()==['M\tsrc/SharpLink.Runtime/RpcSession.cs','A\tsrc/SharpLink.Runtime/WindowUpdateCountDiagnostic.cs'],'diagnostic overlay scope')
    for path,blob in HOOK_BLOBS.items():require(git(root,'rev-parse','HEAD:'+path)==blob,'different/missing diagnostic hooks')
    clean(root)

def counts(value,empty=False):
    require(isinstance(value,dict) and set(value)=={'Client','Server','InvalidCredits','InvalidRoles'},'counter snapshot shape')
    require(value['InvalidCredits']==0 and type(value['InvalidCredits']) is int and value['InvalidRoles']==0 and type(value['InvalidRoles']) is int,'invalid credit/role')
    for side in ('Client','Server'):
        require(isinstance(value[side],dict) and set(value[side])==set(SIDES) and all(integer(v) for v in value[side].values()),'counter fields')
        if empty:require(all(v==0 for v in value[side].values()),'initial hooks nonzero')
    return value['Client'],value['Server']

def validate_result(d,arm,items):
    rpcs=1 if items==1 else 256
    require(isinstance(d,dict) and type(d.get('schemaVersion')) is int and d['schemaVersion']==1,'report schema')
    require(d.get('status')=='pass' and d.get('diagnosticSchema')==SCHEMA,'missing/failed diagnostic hooks')
    require(d.get('arm')==arm and d.get('itemCount')==items,'cell identity')
    require(d.get('diagnosticInstrumented') is True and d.get('countersMayAffectScheduling') is True,'instrumented-count scope missing')
    require(d.get('sourceTree')==SOURCE_TREES[arm] and d.get('diagnosticTree')==OVERLAY_TREES[arm] and d.get('diagnosticCommit')==OVERLAY_LABELS[arm],'frozen source/diagnostic identity')
    require(d.get('runtimeVersion')=='10.0.14','wrong count runtime')
    require(d.get('workers')==1 and d.get('mode')=='static-intercepted' and d.get('transport')=='tcp-loopback','count topology')
    require(d.get('requestedRpcs')==rpcs and d.get('completedRpcs')==rpcs and d.get('completedItems')==rpcs*items and d.get('checksum')==rpcs*items*(items-1)//2,'fixed RPC/item/checksum census')
    require(all(type(d.get(k)) is int for k in ('itemCount','workers','requestedRpcs','completedRpcs','completedItems','checksum')),'count types')
    for key,value in dict(connectionPoolMin=1,connectionPoolMax=1,requestTimeout='disabled',heartbeatIntervalSeconds=3600,heartbeatTimeoutSeconds=7200,clientInterceptors=1,serverInterceptors=0,readyConnectionsBeforeDispose=1).items():require(d.get(key)==value,'fixed connection contract')
    require(d.get('finalFenceCompleted') is True and d.get('shutdownCompleted') is True and d.get('disposeCompleted') is True,'missing final fence/disposal')
    require(d.get('channelType')=='SharpLink.Client.SharpLinkClient' and isinstance(d.get('streamType'),str) and '+LogicalInvocationAsyncEnumerable`' in d['streamType'] and d.get('interceptorObserved') is True,'static intercepted path')
    require(d.get('generatedManifest')==dict(api=5,protocol=2,generator='3.0.0',abi='sharplink-3.0-api5-owner-bound-unsafe-blit-v1'),'generated ABI identity')
    require(d.get('scope')=='instrumented_opportunity_counts_not_uninstrumented_N_or_performance','count interpretation')
    require(d.get('receiverMeaning')=='handler_completed_not_credit_granted','receiver counter meaning overclaims credit grant')
    counts(d.get('initialCounts'),True)
    client,server=counts(d.get('beforeDisposeCounts'))
    counts(d.get('afterDisposeCounts'))
    require(d['beforeDisposeCounts']==d['afterDisposeCounts'],'counts changed during disposal')
    require(client['Entered']==client['Accepted']==server['HandlerCompleted'],'entered/accepted/peer-handler-completed mismatch')
    require(client['EnteredCreditArgumentBytes']==client['AcceptedCreditArgumentBytes']==server['ReceivedCreditArgumentBytes'] and client['AcceptedCreditArgumentBytes']>0,'publication/receipt credit arguments differ')
    require(rpcs<=client['Accepted']<=rpcs*items,'missing hooks or invalid natural count range')
    require(server['Entered']==server['Accepted']==client['HandlerCompleted']==0 and server['EnteredCreditArgumentBytes']==server['AcceptedCreditArgumentBytes']==client['ReceivedCreditArgumentBytes']==0,'unexpected reverse credits')
    for key in ('beforeDisposeOwnership','afterDisposeOwnership'):
        state=d.get(key);require(isinstance(state,dict) and all(type(state.get(k)) is int and state[k]==0 for k in OWNERS) and state.get('Empty') is True,'final ownership not empty')
    for key in ('beforeDisposeFlowState','afterDisposeFlowState'):
        state=d.get(key)
        require(isinstance(state,dict) and all(type(state.get(k)) is int and state[k]==0 for k in ('receiveStates','consumedCreditUpdates','pendingConnectionConsumed')),'unflushed receive credit state')
        require(integer(state.get('connectionWindow')) and state['connectionWindow']>0 and type(state.get('receiveConnectionCredit')) is int and state['receiveConnectionCredit']==state['connectionWindow'] and state.get('Quiescent') is True,'receive credit not fully restored')
    require(d['beforeDisposeFlowState']==d['afterDisposeFlowState'],'flow state changed during disposal')
    for key in ('beforeDisposeServerFlowState','afterDisposeServerFlowState'):
        state=d.get(key)
        require(isinstance(state,dict) and all(type(state.get(k)) is int and state[k]==0 for k in ('sendStates','activeSendStreamCount','waiters','pendingSendStateWaiterCount','queuedSendBytes')),'server send owners/debt waiters not quiescent')
        require(integer(state.get('connectionWindow')) and state['connectionWindow']>0 and type(state.get('sendConnectionCredit')) is int and state['sendConnectionCredit']==state['connectionWindow'] and state.get('Quiescent') is True,'server connection credit not fully restored')
    require(d['beforeDisposeServerFlowState']==d['afterDisposeServerFlowState'],'server flow state changed during disposal')
    # Explicitly reject accidentally substituted performance reports.
    require(not set(d).intersection({'elapsedTicks','cpuTicks','operationsPerSecond','allocatedBytes','p99','p99Us'}),'timing fields forbidden')
    return dict(arm=arm,items_per_rpc=items,rpcs=rpcs,payload_items=rpcs*items,entered=client['Entered'],accepted=client['Accepted'],peer_handler_completed=server['HandlerCompleted'],accepted_credit_argument_bytes=client['AcceptedCreditArgumentBytes'],entered_credit_argument_bytes=client['EnteredCreditArgumentBytes'],received_credit_argument_bytes=server['ReceivedCreditArgumentBytes'],updates_per_rpc=client['Accepted']/rpcs,WU_frame_bytes_derived_from_count=21*client['Accepted'],instrumented_diagnostic_not_production_N=True,owners_empty=True,counters_unchanged_after_disposal=True,receive_states=0,pending_consumed_credit=0,receive_connection_credit=d['beforeDisposeFlowState']['receiveConnectionCredit'],connection_window=d['beforeDisposeFlowState']['connectionWindow'],receiver_meaning='handler_completed_not_credit_granted',server_send_states=0,server_waiters=0,server_send_connection_credit=d['beforeDisposeServerFlowState']['sendConnectionCredit'],server_connection_window=d['beforeDisposeServerFlowState']['connectionWindow'])

def validate_cross_arm(rows):
    require(len(rows)==4 and {(r['arm'],r['items_per_rpc']) for r in rows}==set(schedule()),'four-cell census')
    indexed={(r['arm'],r['items_per_rpc']):r for r in rows}
    for items in (1,100):require(indexed['baseline',items]['accepted_credit_argument_bytes']==indexed['candidate',items]['accepted_credit_argument_bytes'],'cross-arm encoded credit argument totals differ')

def identity(d,binary,expected,host):
    require(d.get('binaryHashes')==expected,'worker DLL inventory differs')
    framework=Path(host).parent/'shared'
    core=framework/'Microsoft.NETCore.App/10.0.14';asp=framework/'Microsoft.AspNetCore.App/10.0.14'
    require(d.get('frameworkLocations')==[str(core/'System.Private.CoreLib.dll'),str(asp/'Microsoft.AspNetCore.dll')],'private Core/ASP14 locations')
    rows=d.get('loadedAssemblies');require(isinstance(rows,list) and len(rows)==d.get('loadedAssemblyCount'),'loaded assembly census')
    names=[]
    for row in rows:
        require(isinstance(row,dict) and set(row)=={'name','location','mvid','sha256'},'loaded identity shape')
        path=Path(row['location']);require(path.is_absolute() and path.resolve()==path and path.is_file(),'loaded assembly canonical path')
        require(path.parent in (binary,core,asp),'loaded assembly outside own-arm/private-runtime closure')
        require(hashlib.sha256(path.read_bytes()).hexdigest()==row['sha256'],'loaded assembly disk hash')
        uuid.UUID(row['mvid']);names.append(row['name'])
        if row['name'].startswith('SharpLink.') or row['name']=='ScalarStreamModule':require(path.parent==binary and expected.get(path.name)==row['sha256'],'mixed product/module arm')
    require(len(names)==len(set(names)) and {'SharpLink.Runtime','SharpLink.Client','SharpLink.Server','SharpLink.Abstractions','ScalarStreamModule','SharpLink.Benchmarks'}.issubset(names),'missing/duplicate product identities')

def main():
    p=argparse.ArgumentParser();p.add_argument('--release-root',type=Path,required=True);p.add_argument('--baseline-root',type=Path);p.add_argument('--candidate-root',type=Path);p.add_argument('--provenance-only',action='store_true');a=p.parse_args()
    r=verify_release(a.release_root.resolve(),os.environ.get('GITHUB_SHA',''),os.environ.get('GITHUB_REF',''))
    if a.provenance_only:
        with open(os.environ['GITHUB_OUTPUT'],'a') as f:f.write('baseline_overlay_sha='+r['baseline_overlay_sha']+'\ncandidate_overlay_sha='+r['candidate_overlay_sha']+'\n')
        print('COUNT PROVENANCE PASS '+json.dumps(r,sort_keys=True));return 0
    require(a.baseline_root is not None and a.candidate_root is not None,'missing diagnostic roots')
    roots={'baseline':a.baseline_root.resolve(),'candidate':a.candidate_root.resolve()}
    for arm,root in roots.items():verify_overlay(root,r,arm)
    env={k:v for k,v in os.environ.items() if not k.startswith(('TUNIT_','TESTINGPLATFORM_'))};env.update(DOTNET_PROCESSOR_COUNT='4',DOTNET_CLI_TELEMETRY_OPTOUT='1',TESTINGPLATFORM_TELEMETRY_OPTOUT='1',PYTHONDONTWRITEBYTECODE='1')
    sdk,host=unit.verify_hosts(roots['candidate'],env);output=Path(tempfile.mkdtemp(prefix='typed-count-',dir=env.get('RUNNER_TEMP')))
    try:
        source=HERE/'fixture';sources={str(p.relative_to(source)):hashlib.sha256(p.read_bytes()).hexdigest() for p in source.rglob('*') if p.is_file()}
        binaries={};inventories={}
        for arm,root in roots.items():
            copy=output/('fixture-'+arm);shutil.copytree(source,copy)
            require({str(p.relative_to(copy)):hashlib.sha256(p.read_bytes()).hexdigest() for p in copy.rglob('*') if p.is_file()}==sources,'fixture source differs')
            binary=output/('bin-'+arm)
            code,timeout=unit.execute([sdk,'build',str(copy/'worker/Worker.csproj'),'-c','Release','-m:1','-nr:false','-p:UseSharedCompilation=false','-p:ProductRoot='+str(root),'-p:Arm='+arm,'-o',str(binary)],root,env,900,output/('build-'+arm+'.log'))
            require(not timeout and code==0,'count fixture build failed')
            analyzers=copy/'module/obj/Release/net10.0/analyzers.txt';require(analyzers.is_file(),'generator identity missing')
            generators=[Path(line) for line in analyzers.read_text().splitlines() if line.endswith('/SharpLink.Generator.dll')]
            require(len(generators)==1 and generators[0].resolve() in ((binary/'SharpLink.Generator.dll').resolve(),(root/'src/SharpLink.Generator/bin/Release/netstandard2.0/SharpLink.Generator.dll').resolve()),'module generator not own-arm')
            inventories[arm]=hashes(binary);binaries[arm]=binary
            require({'ScalarStreamModule.dll','SharpLink.Benchmarks.dll','SharpLink.Runtime.dll'}.issubset(inventories[arm]),'count binaries missing')
        (output/'identities.json').write_text(json.dumps(dict(request=r,fixture=sources,dlls=inventories),indent=2)+'\n')
        rows=[];failures=[]
        for arm,items in schedule():
            slot=arm+'-n'+str(items);path=output/(slot+'.json')
            try:
                require(hashes(binaries[arm])==inventories[arm],'DLL changed before count cell')
                code,timeout=unit.execute([host,str(binaries[arm]/'SharpLink.Benchmarks.dll'),'--arm',arm,'--items',str(items),'--output',str(path)],roots[arm],unit.private_test_environment(env,host),120,output/(slot+'.log'))
                require(not timeout and code==0,'count worker failure/timeout')
                d=read_report(path);row=validate_result(d,arm,items);identity(d,binaries[arm],inventories[arm],host)
                require(hashes(binaries[arm])==inventories[arm],'DLL changed after count cell')
                rows.append(row);print('COUNT SCALAR '+json.dumps(row,sort_keys=True,separators=(',',':'),allow_nan=False),flush=True)
            except Exception as error:
                failures.append(dict(slot=slot,type=type(error).__name__,detail=str(error)))
                print('COUNT CELL FAIL slot='+slot+' classification='+(str(error)[:150] if isinstance(error,GateError) else type(error).__name__),flush=True)
        (output/'failures.json').write_text(json.dumps(failures,indent=2)+'\n')
        require(not failures,'failed fixed cells retained; no replacement or retry');validate_cross_arm(rows)
        for arm,root in roots.items():verify_overlay(root,r,arm)
        print('COUNT GATE PASS cells=4 instrumented_opportunity_only=true timing_claim=false',flush=True);return 0
    except Exception as error:
        (output/'failure.json').write_text(json.dumps(dict(type=type(error).__name__,detail=str(error)),indent=2)+'\n')
        print('COUNT GATE FAIL classification='+(str(error)[:150] if isinstance(error,GateError) else type(error).__name__),flush=True);return 1

if __name__=='__main__':
    try:sys.exit(main())
    except Exception as error:print('COUNT PREFLIGHT FAIL class='+type(error).__name__,flush=True);sys.exit(1)
