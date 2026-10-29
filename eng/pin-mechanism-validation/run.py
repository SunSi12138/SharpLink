#!/usr/bin/env python3
"""Frozen pin-specific correctness and 6x2x8 bridge mechanism observations. No uploads/retries."""
from __future__ import annotations
import argparse, hashlib, importlib.util, json, math, os, re, shutil, subprocess, sys, tempfile, uuid
import xml.etree.ElementTree as ET
from pathlib import Path
HERE=Path(__file__).resolve().parent
sys.path.insert(0,str(HERE.parent/'static-stream-unit-validation'))
from validate import GateError, require, strict_json, read_report, discovery_uids, uid_digest, unique_uids, validate_run, check_summary
spec=importlib.util.spec_from_file_location('reviewed_unit',HERE.parent/'static-stream-unit-validation/run.py')
unit=importlib.util.module_from_spec(spec);spec.loader.exec_module(unit)
BASE_TREE='410fdb911cf7120ccabbbd10c820b0c9a73d9223'
SOURCE_TREE='dfd3618edb53d8664262bdfceca0c15bfb528e58'
BASE_PARENTS={'dd0dcacd8edf9552e9face66f4a6473b258428b2':'3d76a3a8b55a37e24c62a073fe588cff3d3867be'}
LABELS={'baseline':'dd0dcacd8edf9552e9face66f4a6473b258428b2','candidate':'f76cc19133a94b35614756517ef33dddb68d2200'}
BRANCH='refs/heads/validation/server-call-pin-unified-mechanism-20261029'
REQUEST='eng/pin-mechanism-validation/request.json'
SOURCE_DIFF=['M\tsrc/SharpLink.Server/ServerGeneratedBridge.cs','A\ttest/SharpLink.UnitTests/Server/ServerGeneratedOutboundCallPinTests.cs']
CASES=['static-n0','static-n1','static-n100','module-configured-n0','module-configured-n1','module-configured-n100','legacy-n0','legacy-n100']
ROUTES={'static':'tracked-static-generated-stub','module-configured':'module-configured-generated-stub-not-dynamic-channel','legacy':'legacy-untracked-generated-stub'}
SCOPE='server_call_pin_mechanism_not_TCP_QPS_p99'
BOUNDARY='per_invocation_state_rent_generated_guard_pump_release_retire_and_final_drain'
PIN_CLASS='SharpLink.UnitTests.Server.ServerGeneratedOutboundCallPinTests'
PIN_METHODS=[('PinnedTerminalPreventsEnumeratorFactoryEntry','System.Int32',4),('RemovedCallCannotRecycleUntilPhysicalMoveAndDisposalBothFinish','System.Boolean',2),('PinnedCallRejectsNextMoveAfterUnlinkAtTerminalOrExactDeadline','System.Boolean',2),('UnpinnedPumpStillFindsStateCreatedAfterEnumeratorEntry','System.Boolean',2),('MappingAndPublicationFaultsRetainPinThroughPhysicalWorkAndReleaseIt','System.Boolean',2)]
PIN_ORIGINAL=sorted(f'{PIN_CLASS}.1.1.{method}({kind}).{i}.1.0' for method,kind,count in PIN_METHODS for i in range(1,count+1))
PIN_UNIFIED=[f'{PIN_CLASS}.1.1.UnifiedWrappersKeepOneReceiverTypeAndBaselineFieldShape.1.1.0']+[f'{PIN_CLASS}.1.1.EscapedInboundWrapperKeepsLookupOwnershipAfterCallRetirement(System.Boolean).{i}.1.0' for i in (1,2)]
PIN=sorted(PIN_ORIGINAL+PIN_UNIFIED)
CLEANUP=['SharpLink.UnitTests.Runtime.SendPumpCleanupProgressTests.1.1.'+s for s in ['CaptureMustNotMistakeWatchdogTimeoutForCompletedOperationFailure.1.1.0','StopMustJoinBlockedThrowingReturnAndStillCompleteMarkersAndCapacityWaiters.1.1.0','TerminalCleanupMustDrainBothQueuesAndPreserveTransportCause(System.String).1.1.0','TerminalCleanupMustDrainBothQueuesAndPreserveTransportCause(System.String).2.1.0','TerminalCleanupMustDrainBothQueuesAndPreserveTransportCause(System.String).3.1.0','ThrowingBatchReturnMustNotReleaseRerentedOwnerOrAbandonEitherQueue.1.1.0']]
FOCUSED_CLASSES=['SharpLink.UnitTests.Runtime.AbandonedGeneratedStreamOwnershipTests','SharpLink.UnitTests.Server.AbandonedGeneratedMapperOwnershipTests','SharpLink.UnitTests.Server.ServerCallCancellationStateTests','SharpLink.UnitTests.Server.ServerCallCancellationDeadlinePriorityTests','SharpLink.UnitTests.Runtime.GeneratedServerPublicationDeadlineTests','SharpLink.UnitTests.Runtime.TransportOutputFaultTests','SharpLink.UnitTests.Server.SharpLinkServerRequestScopeReviewTests']
DYNAMIC_UID='SharpLink.IntegrationTests.RuntimeAssemblyIntegrationTests.1.1.DynamicStreamTerminalMustRetainPhysicalOwnersThroughSequentialDisposal(System.String).3.1.0'

def git(root,*args):return subprocess.check_output(['git','-C',str(root),*args],text=True).strip()
def sha(value):return isinstance(value,str) and len(value)==40 and all(c in '0123456789abcdef' for c in value)
def integer(value,minimum=0):return type(value) is int and minimum<=value<=9223372036854775807
def clean(root):require(not git(root,'status','--porcelain','--untracked-files=all'),'source worktree dirty')
def hashes(root):return {str(p.relative_to(root)):hashlib.sha256(p.read_bytes()).hexdigest() for p in sorted(root.rglob('*.dll'))}
def schedule():return [(pair,arm) for pair in range(1,7) for arm in (('baseline','candidate') if pair%2 else ('candidate','baseline'))]
def focused(original):return sorted(PIN+[u for u in original if any(u.startswith(c+'.') for c in FOCUSED_CLASSES)])

CLOSURE_PRODUCTS=('SharpLink.Abstractions','SharpLink.Runtime','SharpLink.Server','SharpLink.Sdk')
CLOSURE_PACKAGES=('Microsoft.Extensions.DependencyInjection','Microsoft.Extensions.DependencyInjection.Abstractions','Microsoft.Extensions.Logging.Abstractions','System.Threading.RateLimiting')
CLOSURE_NAMES={name+'.dll' for name in CLOSURE_PRODUCTS+CLOSURE_PACKAGES}

def file_sha(path):return hashlib.sha256(path.read_bytes()).hexdigest()
def runtime_closure(deps):
    require(isinstance(deps,dict) and isinstance(deps.get('runtimeTarget'),dict),'closure deps shape')
    target=deps['runtimeTarget'].get('name');require(target=='.NETCoreApp,Version=v10.0','closure target')
    targets=deps.get('targets');require(isinstance(targets,dict) and target in targets,'closure target graph')
    graph=targets[target];libraries=deps.get('libraries');require(isinstance(graph,dict) and isinstance(libraries,dict),'closure graph/libraries')
    pending=[name+'/3.0.0' for name in CLOSURE_PRODUCTS];visited=set();result={}
    while pending:
        key=pending.pop()
        if key in visited:continue
        visited.add(key);require(key in graph and key in libraries,'missing transitive closure library')
        name,version=key.rsplit('/',1)
        require(name in CLOSURE_PRODUCTS+CLOSURE_PACKAGES and version==('3.0.0' if name in CLOSURE_PRODUCTS else '10.0.12'),'unexpected closure library/version')
        require(libraries[key].get('type')==('project' if name in CLOSURE_PRODUCTS else 'package'),'closure library type')
        node=graph[key];require(isinstance(node,dict) and not any(node.get(k) for k in ('native','runtimeTargets','resources')),'unsupported closure native/RID/resource assets')
        runtime=node.get('runtime');require(isinstance(runtime,dict) and len(runtime)==1,'closure runtime asset census')
        asset=next(iter(runtime));basename=Path(asset).name
        require(not Path(asset).is_absolute() and '..' not in Path(asset).parts and basename==name+'.dll' and basename not in result,'closure asset identity')
        result[basename]=key
        dependencies=node.get('dependencies',{});require(isinstance(dependencies,dict),'closure dependencies shape')
        for dep,version in dependencies.items():
            require(isinstance(dep,str) and isinstance(version,str),'closure dependency type');pending.append(dep+'/'+version)
    require(set(result)==CLOSURE_NAMES,'complete eight-DLL closure required')
    return result

def assert_closure(binary,expected,label):
    require(set(expected)==CLOSURE_NAMES,'expected closure census')
    for name in sorted(CLOSURE_NAMES):
        path=binary/name
        actual=file_sha(path) if path.is_file() and not path.is_symlink() else 'missing'
        if actual!=expected[name]:
            # Deliberately bounded identity diagnostics, never paths or report payloads.
            print('PIN CLOSURE MISMATCH '+json.dumps(dict(stage=label,assembly=name,expected_sha256=expected[name],actual_sha256=actual),sort_keys=True),flush=True)
            raise GateError('immutable closure mismatch: '+name)

def freeze_closure(root,identity,destination):
    binary=root/Path(unit.ASSEMBLY).parent;deps=binary/'SharpLink.UnitTests.deps.json'
    require(file_sha(deps)==identity['__deps_sha256'],'tested dependency graph changed')
    libraries=runtime_closure(read_report(deps));expected={name:identity[name] for name in CLOSURE_NAMES}
    assert_closure(binary,expected,'tested-source')
    destination.mkdir();product=destination/'product';product.mkdir()
    for name in sorted(CLOSURE_NAMES):shutil.copy2(binary/name,product/name)
    generator_source=root/'src/SharpLink.Generator/bin/Release/netstandard2.0/SharpLink.Generator.dll'
    require(generator_source.is_file() and not generator_source.is_symlink() and file_sha(generator_source)==identity['__generator_sha256'],'tested generator bytes changed')
    analyzer=destination/'generator';analyzer.mkdir();generator=analyzer/'SharpLink.Generator.dll';shutil.copy2(generator_source,generator)
    assert_closure(product,expected,'frozen-copy')
    return dict(directory=product,generator=generator,generator_sha256=identity['__generator_sha256'],hashes=expected,libraries=libraries)

def snapshot_build_identity(root):
    binary=root/Path(unit.ASSEMBLY).parent;identity=hashes(binary)
    identity['__deps_sha256']=file_sha(binary/'SharpLink.UnitTests.deps.json')
    identity['__generator_sha256']=file_sha(root/'src/SharpLink.Generator/bin/Release/netstandard2.0/SharpLink.Generator.dll')
    runtime_closure(read_report(binary/'SharpLink.UnitTests.deps.json'))
    return identity

def verify_fixture_references(copy,closure):
    for project in ('module','worker'):
        records=list((copy/project).rglob('references.txt'));require(len(records)==1,'resolved compiler references absent/ambiguous')
        references=[Path(line) for line in records[0].read_text().splitlines() if line]
        for name in CLOSURE_NAMES:
            matches=[p for p in references if p.name==name]
            require(len(matches)==1 and matches[0].resolve()==(closure['directory']/name).resolve(),'compiler did not resolve exact frozen '+name)
            require(file_sha(matches[0])==closure['hashes'][name],'compiler reference changed '+name)
    records=list((copy/'module').rglob('analyzers.txt'));require(len(records)==1,'generator inventory absent/ambiguous')
    generators=[Path(line) for line in records[0].read_text().splitlines() if line.endswith('/SharpLink.Generator.dll')]
    require(len(generators)==1 and generators[0].resolve()==closure['generator'].resolve() and file_sha(generators[0])==closure['generator_sha256'],'fixture generator not frozen own-arm bytes')

def verify_fixture_runtimeconfig(path):
    d=read_report(path);options=d.get('runtimeOptions')
    require(isinstance(options,dict) and options.get('tfm')=='net10.0','fixture runtimeconfig shape')
    require(options.get('framework')=={'name':'Microsoft.NETCore.App','version':'10.0.0'} and not any(k in options for k in ('frameworks','includedFrameworks','additionalProbingPaths')),'fixture must use only tested Core framework')

def verify_fixture_deps(path):
    d=read_report(path);target=d.get('runtimeTarget',{}).get('name');require(target=='.NETCoreApp,Version=v10.0','fixture deps target')
    graph=d.get('targets',{}).get(target);require(isinstance(graph,dict),'fixture deps graph')
    found={name:[] for name in CLOSURE_NAMES}
    for key,node in graph.items():
        for asset in node.get('runtime',{}):
            name=Path(asset).name
            if name in found:
                require(asset==name,'fixture runtime dependency is not own-bin asset');found[name].append(key)
        require(not node.get('native') and not node.get('runtimeTargets'),'fixture unexpected native/RID asset')
    require(all(len(keys)==1 for keys in found.values()),'fixture pruned/duplicated frozen runtime dependency')
    return found


def fixture_identity(arm,commit,tree):
    require(arm in ('baseline','candidate') and sha(commit),'fixture identity arm/commit')
    require(tree==(BASE_TREE if arm=='baseline' else SOURCE_TREE),'fixture identity source tree')
    return {'Arm':arm,'SourceTree':tree,'ExpectedProductCommit':commit}

def fixture_build_properties(identity):
    require(identity==fixture_identity(identity.get('Arm'),identity.get('ExpectedProductCommit'),identity.get('SourceTree')),'fixture identity properties')
    return ['-p:Arm='+identity['Arm'],'-p:ProductCommit='+identity['ExpectedProductCommit'],'-p:SourceTree='+identity['SourceTree']]

def verify_fixture_identity_source(source):
    root=ET.fromstring((source/'worker/Worker.csproj').read_text())
    require(not list(root.iter('SourceTree')),'fixture must not duplicate source tree constants')
    rows=list(root.iter('AssemblyMetadata'))
    require(len(rows)==3 and {e.get('Include'):e.get('Value') for e in rows}=={'Arm':'$(Arm)','SourceTree':'$(SourceTree)','ExpectedProductCommit':'$(ProductCommit)'},'fixture metadata source binding')
    errors=list(root.iter('Error'))
    require(any("'$(SourceTree)' == ''" in e.get('Condition','') for e in errors),'fixture missing-tree build guard')

def verify_worker_build_properties(copy,sdk,env,output,identity):
    report=output/('worker-properties-'+identity['Arm']+'.json')
    code,timeout=unit.execute([sdk,'msbuild',str(copy/'worker/Worker.csproj'),*fixture_build_properties(identity),'-getProperty:Arm,SourceTree,ProductCommit','-nologo'],copy,env,30,report)
    expected={'Arm':identity['Arm'],'SourceTree':identity['SourceTree'],'ProductCommit':identity['ExpectedProductCommit']}
    require(code==0 and not timeout and read_report(report)=={'Properties':expected},'worker evaluated build identity')

def build_identity_reader(sdk,env,output):
    source=HERE/'identity';copy=output/'identity-reader';copy.mkdir()
    for name in ('Identity.csproj','Program.cs'):shutil.copy2(source/name,copy/name)
    binary=copy/'bin'
    code,timeout=unit.execute([sdk,'build',str(copy/'Identity.csproj'),'-c','Release','-m:1','-nr:false','--disable-build-servers','-p:UseSharedCompilation=false','-o',str(binary)],copy,env,180,output/'identity-reader-build.log')
    require(code==0 and not timeout,'metadata-only reader build failed')
    return binary/'Identity.dll'

def verify_worker_identity(copy,binary,identity,reader,host,env,output):
    fixture_identity(identity['Arm'],identity['ExpectedProductCommit'],identity['SourceTree'])
    files=list((copy/'worker/obj').rglob('*.AssemblyInfo.cs'));require(len(files)==1,'worker generated AssemblyInfo census')
    rows=re.findall(r'\[assembly:\s*System\.Reflection\.AssemblyMetadata(?:Attribute)?\("([^"\\]*)",\s*"([^"\\]*)"\)\]',files[0].read_text())
    require(len(rows)==3 and dict(rows)==identity,'worker generated metadata identity')
    report=output/('worker-identity-'+identity['Arm']+'.json')
    code,timeout=unit.execute([host,str(reader),str(binary/'SharpLink.Benchmarks.dll')],copy,unit.private_test_environment(env,host),30,report)
    require(code==0 and not timeout and read_report(report)==identity,'worker embedded metadata identity')
    print('PIN WORKER IDENTITY PASS '+json.dumps(identity,sort_keys=True),flush=True)


def verify_release(root,release,ref):
    require(ref==BRANCH and sha(release) and git(root,'rev-parse','HEAD')==release,'release identity');clean(root)
    r=strict_json((root/REQUEST).read_text())
    require(isinstance(r,dict) and set(r)=={'schema_version','baseline_sha','candidate_sha','tools_sha','tools_tree'},'request shape')
    require(type(r['schema_version']) is int and r['schema_version']==1 and all(sha(r[k]) for k in r if k!='schema_version'),'request SHA/schema')
    base,candidate,tool=(r[k] for k in ('baseline_sha','candidate_sha','tools_sha'))
    require(base in BASE_PARENTS and git(root,'show','-s','--format=%P',base)==BASE_PARENTS[base],'baseline lineage')
    require(git(root,'show','-s','--format=%P','HEAD')==tool,'request parent')
    require(git(root,'diff','--name-status','--no-renames',tool,'HEAD')=='A\t'+REQUEST,'request-only scope')
    require(git(root,'rev-parse',tool+'^{tree}')==r['tools_tree'],'tool tree')
    require(git(root,'show','-s','--format=%P',tool)==candidate and git(root,'show','-s','--format=%P',candidate)==base,'candidate/tool parent')
    require(git(root,'rev-parse',base+'^{tree}')==BASE_TREE and git(root,'rev-parse',candidate+'^{tree}')==SOURCE_TREE,'reviewed source trees')
    require(git(root,'diff','--name-status','--no-renames',base,candidate).splitlines()==SOURCE_DIFF,'pin-only source diff')
    files=strict_json((HERE/'tool-files.json').read_text())
    require(isinstance(files,list) and files and len(set(files))==len(files) and all(isinstance(p,str) and not p.startswith('/') and '..' not in Path(p).parts for p in files),'tool manifest')
    require(git(root,'diff','--name-status','--no-renames',candidate,tool).splitlines()==sorted('A\t'+p for p in files),'tool-only scope')
    return r

def verify_source(root,r,arm):
    expected=r[arm+'_sha'];require(git(root,'rev-parse','HEAD')==expected,'arm SHA')
    require(git(root,'rev-parse','HEAD^{tree}')==(BASE_TREE if arm=='baseline' else SOURCE_TREE),'arm frozen tree')
    require(git(root,'show','-s','--format=%P','HEAD')==(BASE_PARENTS[r['baseline_sha']] if arm=='baseline' else r['baseline_sha']),'arm parent');clean(root)

def report(path):
    files=list(path.glob('*.tunit-report.json'));require(len(files)==1,'missing/ambiguous test report');return read_report(files[0])

def test_args(host,assembly,uid,case):
    return [host,str(assembly),'--filter-uid',uid,'--maximum-parallel-tests','1','--minimum-expected-tests','1','--zero-tests-policy','strict','--no-ansi','--progress','off','--show-stdout','None','--show-stderr','None','--show-test-results','failed,skipped','--report-html','--report-html-filename',str(case/'result.html'),'--results-directory',str(case/'results')]

def run_focused(root,sdk,host,env,output):
    code,timeout=unit.execute([sdk,'build',unit.PROJECT,'-c','Release','-m:1','-nr:false','--disable-build-servers','-p:UseSharedCompilation=false','-v','minimal'],root,env,900,output/'unit-build.log')
    require(code==0 and not timeout,'focused build failed')
    assembly=root/unit.ASSEMBLY;identity=snapshot_build_identity(root)
    code,timeout=unit.execute([host,str(assembly),'--list-tests','json'],root,unit.private_test_environment(env,host),180,output/'discovery.json')
    require(code==0 and not timeout,'focused discovery failed')
    discovery=read_report(output/'discovery.json');raw=discovery_uids(discovery)
    original=unique_uids(strict_json((root/'eng/static-stream-unit-validation/expected-uids.json').read_text()),'original corpus')
    require(len(original)==2310 and raw==sorted(original+CLEANUP+PIN),'exact fresh raw UID census changed')
    selected=focused(original);require(len(PIN)==15 and len(selected)==109 and uid_digest(selected)=='a1e95f6efb0cdb1ecc6bd7477ad6e8f47584e9b0f0fed51d13cb4b2ace78c8e2' and set(selected).issubset(raw),'focused corpus')
    for t in discovery['tests']:
        if t['uid'] in PIN:require(t.get('location',{}).get('file','').replace('\\','/').endswith('/test/SharpLink.UnitTests/Server/ServerGeneratedOutboundCallPinTests.cs'),'pin source mapping')
    print('PIN FOCUSED CENSUS '+json.dumps(dict(raw=len(raw),selected=len(selected),pin=15,uid_sha256=uid_digest(selected)),sort_keys=True),flush=True)
    failures=[]
    for index,uid in enumerate(selected):
        case=output/('test-'+str(index));case.mkdir()
        try:
            code,timeout=unit.execute(test_args(host,assembly,uid,case),root,unit.private_test_environment(env,host),120,case/'worker.log')
            r=report(case);require(r.get('runtimeVersion')=='.NET 10.0.14','focused runtime');validate_run([uid],r,code,timeout)
            if uid in PIN:print('PIN TEST PASS uid='+uid,flush=True)
        except Exception as error:
            failures.append(dict(uid=uid,type=type(error).__name__,detail=str(error)));print('PIN FOCUSED FAIL uid='+uid+' class='+type(error).__name__,flush=True)
    (output/'focused-failures.json').write_text(json.dumps(failures,indent=2)+'\n')
    require(snapshot_build_identity(root)==identity,'focused DLLs/deps/generator changed');require(not failures,'focused failures retained; mechanism not executed')
    print('PIN FOCUSED PASS selected='+str(len(selected))+' pin=15 failed_or_skipped=0',flush=True)
    return identity

def validate_integration(r,code,timeout):
    require(not timeout and code==0,'dynamic correctness process failed')
    require(type(r.get('schemaVersion')) is int and r['schemaVersion']==1 and r.get('assemblyName')=='SharpLink.IntegrationTests' and r.get('tunitVersion')=='1.73.19.0' and r.get('runtimeVersion')=='.NET 10.0.14','dynamic report identity')
    generation=r.get('publicationGeneration');require(isinstance(generation,str) and len(generation)==32 and all(c in '0123456789abcdef' for c in generation),'dynamic generation')
    groups=r.get('groups');require(isinstance(groups,list) and groups,'dynamic groups')
    tests=[]
    for group in groups:
        require(isinstance(group,dict) and isinstance(group.get('tests'),list) and all(isinstance(t,dict) for t in group['tests']),'dynamic group shape')
        check_summary(group.get('summary'),len(group['tests']),'dynamic group');tests.extend(group['tests'])
    require(len(tests)==1 and tests[0].get('id')==DYNAMIC_UID and tests[0].get('status')=='passed','exact dynamic module-retirement execution')
    check_summary(r.get('summary'),1,'dynamic summary')

def run_dynamic(root,sdk,host,env,output,unit_identity):
    project='test/SharpLink.IntegrationTests/SharpLink.IntegrationTests.csproj'
    code,timeout=unit.execute([sdk,'build',project,'-c','Release','-m:1','-nr:false','--disable-build-servers','-p:UseSharedCompilation=false','-v','minimal'],root,env,900,output/'integration-build.log')
    require(code==0 and not timeout,'dynamic integration build failed')
    assembly=root/'test/SharpLink.IntegrationTests/bin/Release/net10.0/SharpLink.IntegrationTests.dll';identity=hashes(assembly.parent)
    assert_closure(assembly.parent,{name:unit_identity[name] for name in CLOSURE_NAMES},'unit-integration')
    code,timeout=unit.execute([host,str(assembly),'--list-tests','json'],root,unit.private_test_environment(env,host),180,output/'integration-discovery.json')
    require(code==0 and not timeout,'dynamic discovery failed')
    discovery=read_report(output/'integration-discovery.json');uids=discovery_uids(discovery);require(DYNAMIC_UID in uids,'missing exact dynamic UID')
    selected=[t for t in discovery['tests'] if t['uid']==DYNAMIC_UID]
    require(len(selected)==1 and selected[0].get('location',{}).get('file','').replace('\\','/').endswith('/test/SharpLink.IntegrationTests/RuntimeAssemblyIntegrationTests.PhysicalOwnership.cs'),'dynamic UID source mapping')
    case=output/'dynamic-module-retirement';case.mkdir()
    code,timeout=unit.execute(test_args(host,assembly,DYNAMIC_UID,case),root,unit.private_test_environment(env,host),180,case/'worker.log')
    validate_integration(report(case),code,timeout);require(identity==hashes(assembly.parent),'integration DLLs changed')
    print('PIN ACTUAL DYNAMIC PASS module-retirement cases=1 separate_from_module_configured_mechanism=true',flush=True)

def read_measurement(path):
    require(path.is_file() and not path.is_symlink() and 0<path.stat().st_size<=131072,'missing/noncanonical/oversized mechanism report')
    return read_report(path)

def validate_measurement(d,arm,pair,source_commit=None):
    require(isinstance(d,dict) and type(d.get('schemaVersion')) is int and d['schemaVersion']==1,'mechanism schema')
    require(d.get('status')=='pass' and d.get('failure') is None and d.get('arm')==arm and type(d.get('pair')) is int and d['pair']==pair,'mechanism slot/status')
    require(d.get('sourceTree')==(BASE_TREE if arm=='baseline' else SOURCE_TREE) and d.get('reviewedSourceCommit')==(source_commit or LABELS[arm]),'mechanism source identity')
    require(d.get('runtimeVersion')=='10.0.14' and d.get('runtime')=='.NET 10.0.14' and d.get('architecture')=='X64' and type(d.get('processorCount')) is int and d['processorCount']==4,'mechanism runtime/configuration')
    require(integer(d.get('stopwatchFrequency'),1) and d.get('cpuTicksPerSecond')==10000000,'clock units')
    require(d.get('scope')==SCOPE and d.get('boundary')==BOUNDARY,'measurement interpretation/boundary')
    require(d.get('dynamicScope')=='module_token_registration_only_not_DynamicChannel_or_module_lease_dispatch' and d.get('transport')=='in_memory_validating_sink_with_deterministic_per_item_credit_return' and d.get('measuredProducer')=='synchronous-finite','mechanism limitations not declared')
    rows=d.get('cases');require(isinstance(rows,list) and all(isinstance(r,dict) for r in rows) and [r.get('case') for r in rows]==CASES[pair-1:]+CASES[:pair-1],'eight-cell census/order')
    for row in rows:
        route,n=row['case'].rsplit('-n',1);n=int(n)
        require(row.get('passed') is True and row.get('route')==ROUTES[route] and type(row.get('itemsPerInvocation')) is int and row['itemsPerInvocation']==n,'cell topology/identity')
        require(row.get('producer')=='synchronous-finite' and type(row.get('warmupSeconds')) is int and row['warmupSeconds']==1 and type(row.get('measureSeconds')) is int and row['measureSeconds']==2,'fixed producer/windows')
        require(all(integer(row.get(k),1 if k in ('ops','cpuTicks','elapsedTicks') else 0) for k in ('ops','allocatedBytes','cpuTicks','elapsedTicks')),'cost counters/CPU resolution')
        require(row['elapsedTicks']>=2*d['stopwatchFrequency'],'measurement window too short')
        ops=row['ops']
        for key,value in dict(dataFrames=ops*n,completeFrames=ops,payloadChecksum=ops*n*(n-1)//2,producerGets=ops,producerMoves=ops*(n+1),producerDisposes=ops,finalQueuedBytes=0,sendStates=0,sendWaiters=0).items():
            require(integer(row.get(key)) and row[key]==value,'wire/producer/ownership conservation '+key)
        require(row.get('callMapEmpty') is True and row.get('callStatesReleased') is True,'retained call state')
        require(integer(row.get('connectionWindow'),1) and type(row.get('sendCredit')) is int and row['sendCredit']==row['connectionWindow'],'send credit not restored')
    control=d.get('asyncControl')
    require(isinstance(control,dict) and control.get('passed') is True and control.get('scope')=='one_cold_async_producer_control_allocation_only_not_timing_or_benefit','missing async allocation control')
    for key,value in dict(ops=32,producerGets=32,producerMoves=64,producerDisposes=32).items():require(type(control.get(key)) is int and control[key]==value,'async control census')
    require(integer(control.get('allocatedBytes')) and control.get('callStatesReleased') is True and control.get('callMapEmpty') is True,'async allocation/retained states')
    require(not set(control).intersection({'cpuTicks','elapsedTicks','qps','p99'}),'async control not a timing cell')
    return rows

def validate_loaded_identity(d,binary,expected,host,frozen=None):
    require(d.get('binaryHashes')==expected,'worker DLL inventory differs')
    framework=Path(host).parent/'shared';core=framework/'Microsoft.NETCore.App/10.0.14'
    require(d.get('frameworkLocations')==[str(core/'System.Private.CoreLib.dll')],'private runtime framework locations')
    rows=d.get('loadedAssemblies');require(isinstance(rows,list) and rows and len(rows)==d.get('loadedAssemblyCount'),'loaded assembly inventory')
    names=[]
    for row in rows:
        require(isinstance(row,dict) and set(row)=={'name','location','mvid','sha256'},'loaded identity shape')
        path=Path(row['location']);require(path.is_absolute() and path.resolve()==path and path.is_file(),'loaded assembly canonical path')
        require(path.parent in (binary,core),'loaded assembly outside own-arm/private-runtime closure')
        require(hashlib.sha256(path.read_bytes()).hexdigest()==row['sha256'],'loaded assembly disk hash')
        uuid.UUID(row['mvid']);names.append(row['name'])
        if path.parent==binary:require(expected.get(path.name)==row['sha256'],'own-arm binary hash')
        if frozen is not None and row['name']+'.dll' in frozen:
            require(path.parent==binary and row['sha256']==frozen[row['name']+'.dll'],'frozen package/product resolved outside own-bin')
        if row['name'].startswith('SharpLink.') or row['name']=='PinStreamModule':require(path.parent==binary,'mixed product/module arm')
    require(len(set(names))==len(names) and {'SharpLink.Runtime','SharpLink.Server','SharpLink.Abstractions','SharpLink.Benchmarks','PinStreamModule'}.issubset(names),'missing/duplicate product identities')

def scalar_rows(d):
    rows=[]
    for case in d['cases']:
        row=dict(pair=d['pair'],arm=d['arm'])
        # Every row field is an individually validated bounded scalar.
        for key in ('case','route','itemsPerInvocation','ops','allocatedBytes','cpuTicks','elapsedTicks','warmupSeconds','measureSeconds','producer','dataFrames','completeFrames','payloadChecksum','producerGets','producerMoves','producerDisposes','callMapEmpty','callStatesReleased','finalQueuedBytes','sendStates','sendWaiters','sendCredit','connectionWindow','passed'):row[key]=case[key]
        row.update(stopwatchFrequency=d['stopwatchFrequency'],cpuTicksPerSecond=d['cpuTicksPerSecond'])
        rows.append(row)
    return rows

def paired_summaries(results):
    require(len(results)==12 and {(r['pair'],r['arm']) for r in results}==set(schedule()),'all twelve process slots required')
    indexed={(r['pair'],r['arm']):r for r in results};rows=[]
    for pair in range(1,7):
        b,c=(indexed[pair,arm] for arm in ('baseline','candidate'))
        for name in CASES:
            x=next(row for row in b['cases'] if row['case']==name);y=next(row for row in c['cases'] if row['case']==name)
            bc=x['cpuTicks']/1e7/x['ops']*1e6;cc=y['cpuTicks']/1e7/y['ops']*1e6
            be=x['elapsedTicks']/b['stopwatchFrequency']/x['ops']*1e6;ce=y['elapsedTicks']/c['stopwatchFrequency']/y['ops']*1e6
            ba=x['allocatedBytes']/x['ops'];ca=y['allocatedBytes']/y['ops']
            row=dict(pair=pair,order='AB' if pair%2 else 'BA',case=name,baseline_cpu_us_per_invocation=bc,candidate_cpu_us_per_invocation=cc,cpu_ratio=cc/bc,cpu_us_per_invocation_delta=cc-bc,baseline_elapsed_us_per_invocation=be,candidate_elapsed_us_per_invocation=ce,elapsed_ratio=ce/be,baseline_allocated_bytes_per_invocation=ba,candidate_allocated_bytes_per_invocation=ca,allocated_bytes_per_invocation_delta=ca-ba)
            require(all(math.isfinite(v) for v in row.values() if type(v) is float),'nonfinite paired summary');rows.append(row)
    require(len(rows)==48,'48 paired summaries required');return rows

def run_mechanism(roots,sdk,host,env,output,unit_identity):
    source=HERE/'fixture';verify_fixture_identity_source(source);inputs=('worker/Program.cs','worker/Worker.csproj','module/Contract.cs','module/Module.csproj')
    require(all((source/p).is_file() and not (source/p).is_symlink() for p in inputs),'console fixture absent/noncanonical')
    fixture={p:hashlib.sha256((source/p).read_bytes()).hexdigest() for p in inputs}
    reader=build_identity_reader(sdk,env,output)
    binaries={};identities={};generated={};closures={}
    # Candidate bytes are precisely those used by focused correctness; do not recompile them.
    closures['candidate']=freeze_closure(roots['candidate'],unit_identity,output/'frozen-candidate')
    code,timeout=unit.execute([sdk,'build',unit.PROJECT,'-c','Release','-m:1','-nr:false','--disable-build-servers','-p:UseSharedCompilation=false','-v','minimal'],roots['baseline'],env,900,output/'baseline-product-build.log')
    require(code==0 and not timeout,'baseline canonical product build failed')
    baseline_identity=snapshot_build_identity(roots['baseline'])
    closures['baseline']=freeze_closure(roots['baseline'],baseline_identity,output/'frozen-baseline')
    for arm in ('baseline','candidate'):
        copy=output/('console-'+arm);copy.mkdir()
        for name in inputs:
            (copy/name).parent.mkdir(parents=True,exist_ok=True);shutil.copy2(source/name,copy/name)
        require({str(p.relative_to(copy)):hashlib.sha256(p.read_bytes()).hexdigest() for p in copy.rglob('*') if p.is_file()}==fixture,'fixture source differs')
        binary=output/('bin-'+arm)
        identity=fixture_identity(arm,git(roots[arm],'rev-parse','HEAD'),git(roots[arm],'rev-parse','HEAD^{tree}'))
        verify_worker_build_properties(copy,sdk,env,output,identity)
        code,timeout=unit.execute([sdk,'build',str(copy/'worker/Worker.csproj'),'-c','Release','-m:1','-nr:false','--disable-build-servers','-p:UseSharedCompilation=false','-p:FrozenProductBin='+str(closures[arm]['directory']),'-p:FrozenGenerator='+str(closures[arm]['generator']),*fixture_build_properties(identity),'-o',str(binary)],roots[arm],env,900,output/('console-build-'+arm+'.log'))
        require(code==0 and not timeout,'mechanism console build failed')
        verify_worker_identity(copy,binary,identity,reader,host,env,output)
        generated[arm]={str(p.relative_to(copy)):hashlib.sha256(p.read_bytes()).hexdigest() for p in copy.rglob('*.cs') if 'generated' in p.parts}
        require(generated[arm],'generated fixture output evidence absent')
        verify_fixture_references(copy,closures[arm])
        assert_closure(binary,closures[arm]['hashes'],'fixture-output-'+arm)
        verify_fixture_deps(binary/'SharpLink.Benchmarks.deps.json');verify_fixture_runtimeconfig(binary/'SharpLink.Benchmarks.runtimeconfig.json')
        binaries[arm]=binary;identities[arm]=hashes(binary)
        require({'SharpLink.Benchmarks.dll','SharpLink.Runtime.dll','SharpLink.Server.dll','SharpLink.Abstractions.dll','PinStreamModule.dll'}.issubset(identities[arm]),'mechanism binaries absent')
    require(generated['baseline']==generated['candidate'],'same generated fixture differs across arms')
    assert_closure(binaries['candidate'],{name:unit_identity[name] for name in CLOSURE_NAMES},'unit-mechanism')
    (output/'mechanism-identities.json').write_text(json.dumps(dict(fixture=fixture,dlls=identities,generated=generated,closures={arm:dict(hashes=value['hashes'],libraries=value['libraries'],generator_sha256=value['generator_sha256']) for arm,value in closures.items()}),indent=2)+'\n')
    results=[];failures=[]
    for pair,arm in schedule():
        path=output/('pair-'+str(pair)+'-'+arm+'.json')
        try:
            require(hashes(binaries[arm])==identities[arm],'binary changed before slot');assert_closure(closures[arm]['directory'],closures[arm]['hashes'],'frozen-before-slot')
            code,timeout=unit.execute([host,str(binaries[arm]/'SharpLink.Benchmarks.dll'),'--arm',arm,'--pair',str(pair),'--output',str(path)],roots[arm],unit.private_test_environment(env,host),120,output/(path.stem+'.log'))
            require(code==0 and not timeout,'mechanism worker failure/timeout')
            d=read_measurement(path);validate_measurement(d,arm,pair,git(roots[arm],'rev-parse','HEAD'));validate_loaded_identity(d,binaries[arm],identities[arm],host,closures[arm]['hashes'])
            require(hashes(binaries[arm])==identities[arm],'binary changed after slot');results.append(d)
            for row in scalar_rows(d):print('PIN MECHANISM SCALAR '+json.dumps(row,sort_keys=True,separators=(',',':'),allow_nan=False),flush=True)
            a=d['asyncControl'];print('PIN ASYNC CONTROL '+json.dumps(dict(pair=pair,arm=arm,ops=32,allocatedBytes=a['allocatedBytes'],scope=a['scope'],callMapEmpty=a['callMapEmpty'],callStatesReleased=a['callStatesReleased'],passed=True),sort_keys=True),flush=True)
        except Exception as error:
            failures.append(dict(pair=pair,arm=arm,type=type(error).__name__,detail=str(error)));print('PIN MECHANISM SLOT FAIL pair='+str(pair)+' arm='+arm+' class='+type(error).__name__,flush=True)
    (output/'mechanism-failures.json').write_text(json.dumps(failures,indent=2)+'\n')
    require(not failures,'failed fixed slots retained; no replacements or retries')
    for row in paired_summaries(results):print('PIN MECHANISM PAIR '+json.dumps(row,sort_keys=True,separators=(',',':'),allow_nan=False),flush=True)
    print('PIN MECHANISM COMPLETE pairs=6 arms=2 cells=8 raw_rows=96 paired_rows=48 automatic_performance_approval=false',flush=True)

def main():
    p=argparse.ArgumentParser();p.add_argument('--release-root',type=Path,required=True);p.add_argument('--baseline-root',type=Path);p.add_argument('--candidate-root',type=Path);p.add_argument('--provenance-only',action='store_true');a=p.parse_args()
    r=verify_release(a.release_root.resolve(),os.environ.get('GITHUB_SHA',''),os.environ.get('GITHUB_REF',''))
    verify_fixture_identity_source(HERE/'fixture')
    if a.provenance_only:
        with open(os.environ['GITHUB_OUTPUT'],'a') as f:f.write('baseline_sha='+r['baseline_sha']+'\ncandidate_sha='+r['candidate_sha']+'\n')
        print('PIN PROVENANCE PASS '+json.dumps(r,sort_keys=True));return 0
    require(a.baseline_root is not None and a.candidate_root is not None,'missing source roots')
    roots={'baseline':a.baseline_root.resolve(),'candidate':a.candidate_root.resolve()}
    for arm,root in roots.items():verify_source(root,r,arm)
    env={k:v for k,v in os.environ.items() if not k.startswith(('TUNIT_','TESTINGPLATFORM_'))};env.update(DOTNET_PROCESSOR_COUNT='4',DOTNET_CLI_TELEMETRY_OPTOUT='1',TESTINGPLATFORM_TELEMETRY_OPTOUT='1',PYTHONDONTWRITEBYTECODE='1')
    sdk,host=unit.verify_hosts(roots['candidate'],env);output=Path(tempfile.mkdtemp(prefix='pin-mechanism-',dir=env.get('RUNNER_TEMP')))
    try:
        identity=run_focused(roots['candidate'],sdk,host,env,output)
        run_dynamic(roots['candidate'],sdk,host,env,output,identity)
        run_mechanism(roots,sdk,host,env,output,identity)
        for arm,root in roots.items():verify_source(root,r,arm)
        print('PIN GATE PASS focused_and_actual_dynamic_before_mechanism=true full_RPC_target_claim=false TCP14_and_AOT_not_run=true',flush=True);return 0
    except Exception as error:
        (output/'failure.json').write_text(json.dumps(dict(type=type(error).__name__,detail=str(error)),indent=2)+'\n')
        print('PIN GATE FAIL class='+type(error).__name__+' classification='+(str(error)[:180] if isinstance(error,GateError) else 'unclassified'),flush=True);return 1

if __name__=='__main__':
    try:sys.exit(main())
    except Exception as error:print('PIN PREFLIGHT FAIL class='+type(error).__name__,flush=True);sys.exit(1)
