#!/usr/bin/env python3
"""Strict finite-protocol evidence validation; descriptive metrics, never a release gate."""
import math, statistics
STATES=('off','on-no-update','post-update')
ORDERS=[['off','on-no-update','post-update'],['off','post-update','on-no-update'],['on-no-update','off','post-update'],['on-no-update','post-update','off'],['post-update','off','on-no-update'],['post-update','on-no-update','off']]

def runtime_environment(runtime):
    from pathlib import PurePosixPath
    root=str(PurePosixPath(runtime).parent)
    return dict(DOTNET_ROOT=root,DOTNET_ROOT_X64=root,DOTNET_MULTILEVEL_LOOKUP='0',DOTNET_ROLL_FORWARD='Disable',DOTNET_TieredCompilation='1',DOTNET_TieredPGO='1',DOTNET_TC_QuickJit='1',DOTNET_TC_QuickJitForLoops='1',DOTNET_ReadyToRun='1',DOTNET_gcServer='0',DOTNET_CLI_TELEMETRY_OPTOUT='1',DOTNET_NOLOGO='1')

def need(condition,message):
    if not condition: raise ValueError(message)

def integer(value,minimum=0):
    return type(value) is int and value>=minimum

def validate_capture(c,enabled,generation):
    for k in ('SameConnectionObjects','SameSessionObjects','AllReady','AuthenticatedTls'):
        need(c[k] is True,'Failed physical/session/TLS witness: '+k)
    need(c['ClientReady']==2 and c['ServerPhysical']==2 and c['AuthenticationCalls']==2,'Cohort changed')
    need(c['CatalogGeneration']==generation,'Generation changed')
    need(c['MaintenanceConfigured'] is enabled and c['EnrollmentPresent'] is enabled and c['EnrollmentAbsent'] is not enabled,'Enrollment/configuration wrong')
    need(len(c['TlsProtocols'])==2 and all(x in ('Tls12','Tls13') for x in c['TlsProtocols']),'Wrong TLS')
    need(len(c['TlsCipherSuites'])==2 and all(isinstance(x,str) and x for x in c['TlsCipherSuites']),'Missing TLS ciphers')
    need(len(c['Capabilities'])==2 and all(('CooperativeMaintenance' in x)==enabled for x in c['Capabilities']),'Capability mismatch')

def validate_process(data,state):
    need(state in STATES,'Unknown state')
    need(data['Protocol']=='maintenance-tls-unary-limited-v1','Wrong protocol')
    need(data['RuntimeVersion']=='10.0.14' and data['Architecture']=='X64','Wrong runtime/architecture')
    need(data['Settings']=={'DOTNET_TieredCompilation':'1','DOTNET_TieredPGO':'1','DOTNET_TC_QuickJit':'1','DOTNET_TC_QuickJitForLoops':'1','DOTNET_ReadyToRun':'1','DOTNET_gcServer':'0'},'Runtime settings differ')
    n=data['Network'];enabled=state!='off';post=state=='post-update'
    need(n['State']==state and n['MaintenanceEnabled'] is enabled,'State mismatch')
    need((n['WarmupCalls'],n['RoundCount'],n['CallsPerRound'],n['MeasuredRpcCalls'])==(1250,5,5000,25000),'Protocol count changed')
    need(n['BeforeResult']==30 and n['Result']==(31 if post else 30) and n['HeldProxyAcrossPhases'] is True,'Held proxy witness failed')
    need(integer(n['OriginalGeneration']) and integer(n['SteadyGeneration']),'Invalid generation')
    need(n['GenerationAdvanced'] is post and (n['SteadyGeneration']>n['OriginalGeneration'] if post else n['SteadyGeneration']==n['OriginalGeneration']),'Unexpected generation transition')
    need(n['UpdateCount']==int(post) and n['UpdateStage']==('Succeeded' if post else 'NotRequested'),'Unexpected update')
    need(integer(n['UpdateElapsedTicks'],1 if post else 0) and integer(n['UpdateAllocatedBytes']),'Invalid update cost')
    need(all(integer(n[k]) for k in ('UpdateStartedTicks','UpdateEndedTicks','UpdateAllocatedBefore','UpdateAllocatedAfter')),'Invalid raw update counters')
    need(n['UpdateEndedTicks']-n['UpdateStartedTicks']==n['UpdateElapsedTicks'] and n['UpdateAllocatedAfter']-n['UpdateAllocatedBefore']==n['UpdateAllocatedBytes'],'Update counters do not reconcile')
    need(post or all(n[k]==0 for k in ('UpdateElapsedTicks','UpdateAllocatedBytes','UpdateStartedTicks','UpdateEndedTicks','UpdateAllocatedBefore','UpdateAllocatedAfter')),'No-update arm has update cost')
    need(integer(n['OldCreated'],1),'Old implementation unused')
    need(integer(n['OldDisposed']) and integer(n['ReplacementCreated']),'Invalid implementation counters')
    need((n['OldDisposed']>0 and n['ReplacementCreated']>0) if post else (n['OldDisposed']==0 and n['ReplacementCreated']==0),'Actual replacement witness failed')
    for name in ('Before','PreUpdate'):validate_capture(n[name],enabled,n['OriginalGeneration'])
    for name in ('Resumed','After'):validate_capture(n[name],enabled,n['SteadyGeneration'])
    need(integer(n['Frequency'],1),'Invalid stopwatch frequency')
    need(len(n['Rounds'])==5,'Incomplete rounds')
    prior_ended=n['UpdateEndedTicks'];prior_allocated=n['UpdateAllocatedAfter']
    for i,r in enumerate(n['Rounds']):
        need(r['Round']==i and r['Calls']==5000 and r['Frequency']==n['Frequency'],'Round sequence/count/frequency differs')
        need(integer(r['ElapsedTicks'],1) and integer(r['AllocatedBytes']),'Invalid elapsed/alloc')
        need(all(integer(r[k]) for k in ('StartedTicks','EndedTicks','AllocatedBefore','AllocatedAfter')),'Invalid raw round counters')
        need(r['EndedTicks']-r['StartedTicks']==r['ElapsedTicks'] and r['AllocatedAfter']-r['AllocatedBefore']==r['AllocatedBytes'],'Round counters do not reconcile')
        need(prior_ended<=r['StartedTicks'] and prior_allocated<=r['AllocatedBefore'],'Update/round intervals overlap or cumulative allocation regresses')
        prior_ended=r['EndedTicks'];prior_allocated=r['AllocatedAfter']
        ticks=r['LatencyTicks'];need(len(ticks)==5000 and all(integer(t,1) for t in ticks),'Missing or invalid raw latency ticks')
        need(sum(ticks)<=r['ElapsedTicks'],'RPC elapsed exceeds round wall time')
        validate_capture(r['Before'],enabled,n['SteadyGeneration']);validate_capture(r['After'],enabled,n['SteadyGeneration'])

def expected_partial(data):
    n=data['Network'];state=n['State']
    update_keys=('OriginalGeneration','SteadyGeneration','UpdateCount','UpdateStage','UpdateStartedTicks','UpdateEndedTicks','UpdateElapsedTicks','UpdateAllocatedBefore','UpdateAllocatedAfter','UpdateAllocatedBytes','Frequency')
    return [{'Kind':'update','State':state,'Update':{k:n[k] for k in update_keys}}]+[
        {'Kind':'round','State':state,'Measurement':{k:v for k,v in r.items() if k not in ('Before','After')}} for r in n['Rounds']]

def read_partial(path):
    from verify_request import strict_json
    raw=path.read_text()
    need(raw.endswith('\n'),'Truncated/unflushed final partial checkpoint')
    return [strict_json(line) for line in raw.splitlines()]

def validate_partial(data, records):
    need(isinstance(records,list) and len(records)==6 and records==expected_partial(data),'Missing/truncated/mismatched partial checkpoint sequence')


def metrics(rounds):
    f=rounds[0]['Frequency'];need(all(r['Frequency']==f for r in rounds),'Mixed stopwatch frequencies')
    ticks=sorted(t for r in rounds for t in r['LatencyTicks']);calls=sum(r['Calls'] for r in rounds)
    return {'calls':calls,'rounds':len(rounds),'qps':calls*f/sum(r['ElapsedTicks'] for r in rounds),
        'p99Microseconds':ticks[math.ceil(.99*len(ticks))-1]*1e6/f,
        'allocatedBytesPerRpcProcessWide':sum(r['AllocatedBytes'] for r in rounds)/calls}

def summarize(rows):
    expected=[s for order in ORDERS for s in order];need(len(rows)==18,'Incomplete process set')
    for row,state in zip(rows,expected):validate_process(row,state)
    result={'protocol':'maintenance-tls-unary-limited-v1','scope':'Limited loopback unary TLS Add, serial await, two authenticated connections; descriptive only; not historical native/layout probe or old/full performance gate.',
        'latency':'Nearest-rank P99 of every complete logical RPC timestamp pair; no sampling. QPS uses full measured round wall time.',
        'allocation':'GC.GetTotalAllocatedBytes(precise:true) delta is process-wide client+server+runtime allocation, not client-only allocation.',
        'states':{},'pairedProcessRatios':[]}
    for state in STATES:
        selected=[r['Network'] for r in rows if r['Network']['State']==state];process=[metrics(n['Rounds']) for n in selected]
        result['states'][state]={'pooled':metrics([r for n in selected for r in n['Rounds']]),'processes':process,
            'medianProcess':{k:statistics.median(p[k] for p in process) for k in ('qps','p99Microseconds','allocatedBytesPerRpcProcessWide')},
            'updateCosts':[{'elapsedTicks':n['UpdateElapsedTicks'],'frequency':n['Frequency'],'allocatedBytesProcessWide':n['UpdateAllocatedBytes']} for n in selected]}
    for block,order in enumerate(ORDERS):
        m={state:metrics(rows[block*3+i]['Network']['Rounds']) for i,state in enumerate(order)}
        for state in STATES[1:]:
            result['pairedProcessRatios'].append({'block':block,'state':state,'relativeTo':'off',**{k:m[state][k]/m['off'][k] if m['off'][k] else None for k in ('qps','p99Microseconds','allocatedBytesPerRpcProcessWide')}})
    return result

def validate_dataset(protocol, inputs, binaries, receipts, hosts, rows, fixture_hashes, product_hashes, partials):
    """Validate retained raw evidence independently; never trusts a supplied summary."""
    import re
    from pathlib import PurePosixPath
    from prepare import CANDIDATE, TREE
    need(protocol['orders']==ORDERS and (protocol['warmupCalls'],protocol['rounds'],protocol['callsPerRound'],protocol['processCount'],protocol['processTimeoutSeconds'])==(1250,5,5000,18,90),'Retained protocol changed')
    need(protocol['inputIdentity']==inputs and protocol['binaryIdentity']==binaries,'Conflicting retained identities')
    need((inputs['candidate'],inputs['candidateTree'],inputs['sdk'],inputs['runtime'])==(CANDIDATE,TREE,'10.0.102','10.0.14'),'Wrong source/toolchain identity')
    need(inputs['fixtureSources']==fixture_hashes and bool(fixture_hashes),'Fixture differs from reviewed bytes')
    need(inputs['productSources']==product_hashes and bool(product_hashes),'Product source closure differs')
    for table in (binaries,inputs['runtimeClosure'],inputs['runtimeFiles'],fixture_hashes,product_hashes):
        need(bool(table) and all(isinstance(k,str) and k and isinstance(v,str) and re.fullmatch('[0-9a-f]{64}',v) for k,v in table.items()),'Empty/invalid identity closure')
    runtime=PurePosixPath(protocol['runtimeDotnet']);build=PurePosixPath(protocol['buildRoot']);output=PurePosixPath(protocol['outputRoot'])
    need(protocol['environment']==runtime_environment(str(runtime)),'Launch environment differs')
    need(all(x.is_absolute() for x in (runtime,build,output)) and runtime.name=='dotnet','Invalid absolute launch roots')
    dll=build/'build/bin/HotCostProbe/release/SharpLink.UnitTests.dll'
    for required in ('HotCostProbe/release/SharpLink.UnitTests.dll','HotCostDynamic.Contracts/release/HotCostDynamic.Contracts.dll','HotCostDynamic.Services/release/HotCostDynamic.Services.dll'):
        need(required in binaries,'Missing auxiliary/probe binary closure')
    required_runtime=('dotnet','shared/Microsoft.NETCore.App/10.0.14/System.Private.CoreLib.dll','shared/Microsoft.NETCore.App/10.0.14/libcoreclr.so','shared/Microsoft.NETCore.App/10.0.14/libclrjit.so','shared/Microsoft.NETCore.App/10.0.14/libhostpolicy.so','host/fxr/10.0.14/libhostfxr.so')
    for relative in required_runtime:
        absolute=str(runtime.parent/relative)
        need(relative in inputs['runtimeClosure'] and inputs['runtimeFiles'].get(absolute)==inputs['runtimeClosure'][relative],'Host/CoreLib/CLR/JIT closure mismatch')
    need(len(receipts)==36 and len(hosts)==18 and len(rows)==18 and len(partials)==18,'Incomplete launches/terminal receipts/raw results')
    prior_end=0
    for i,state in enumerate(s for order in ORDERS for s in order):
        name=f'{i:02d}-{state}';command=[str(runtime),'exec','--fx-version','10.0.14','--roll-forward','Disable',str(dll),state,str(output/(name+'.json'))]
        first,last=receipts[i*2:i*2+2];host=hosts[i];row=rows[i]
        for receipt,event in ((first,'started'),(last,'finished')):
            need((receipt['index'],receipt['block'],receipt['position'],receipt['state'],receipt['output'],receipt['event'],receipt['command'])==(i,i//3,i%3,state,name+'.json',event,command),'Wrong/mixed command/order receipt')
        need(first['startedUnixNs']==last['startedUnixNs'] and integer(first['startedUnixNs'],1),'Invalid receipt start')
        need(integer(host['startedUnixNs'],1) and integer(host['endedUnixNs'],1) and integer(last['endedUnixNs'],1),'Invalid terminal times')
        need(prior_end<=first['startedUnixNs']<=host['startedUnixNs']<=host['endedUnixNs']==last['endedUnixNs'],'Overlapping/out-of-order process evidence')
        prior_end=last['endedUnixNs']
        need(type(last['returncode']) is int and last['returncode']==0 and last['status']=='success' and last['processGroupQuiescent'] is True,'Non-success terminal receipt')
        need(host['command']==command and host['status']=='success' and type(host['exitCode']) is int and host['exitCode']==0 and integer(host['pid'],1) and host['processGroupQuiescent'] is True and host['timedOut'] is False and host['descendantLeak'] is False and not any(k in host for k in ('error','cleanupError','cleanupWaitError')),'Failed/uncertain process group')
        need(isinstance(host['wallSeconds'],(int,float)) and math.isfinite(host['wallSeconds']) and 0<=host['wallSeconds']<=120,'Invalid process duration')
        validate_process(row,state)
        validate_partial(row,partials[i])
        corelib=str(runtime.parent/'shared/Microsoft.NETCore.App/10.0.14/System.Private.CoreLib.dll')
        need(row['HostPath']==str(runtime) and row['CoreLibPath']==corelib and row['CoreLibSha256']==inputs['runtimeFiles'][corelib],'Loaded runtime differs')
        closure=row['Network']['LoadedAssemblies'];need(isinstance(closure,list) and bool(closure),'Empty loaded assembly closure')
        names=set()
        for assembly in closure:
            path=PurePosixPath(assembly['Path']);names.add(assembly['Name'])
            need(path.is_absolute() and '..' not in path.parts and type(assembly['Collectible']) is bool,'Invalid assembly path/context')
            if path.is_relative_to(build/'build/bin'):
                digest=binaries.get(str(path.relative_to(build/'build/bin')))
            elif path.is_relative_to(runtime.parent):
                digest=inputs['runtimeClosure'].get(str(path.relative_to(runtime.parent)))
            else:raise ValueError('Loaded assembly outside frozen binary/runtime closures')
            need(digest is not None and assembly['Sha256']==digest,'Loaded assembly hash mismatch')
        need({'System.Private.CoreLib','SharpLink.UnitTests','SharpLink.Client','SharpLink.Server','SharpLink.Runtime','SharpLink.Abstractions','HotCostDynamic.Contracts','HotCostDynamic.Services'}<=names,'Incomplete loaded framework/product/fixture closure')
        need(sum(a['Name']=='HotCostDynamic.Services' and a['Collectible'] for a in closure)==2,'Independent old/replacement load-context witness missing')
    return summarize(rows)

def validate_artifacts(output, build):
    from pathlib import Path
    from prepare import ROOT, sha, git
    from verify_request import strict_json
    output=Path(output);build=Path(build)
    load=lambda p:strict_json(p.read_text())
    protocol=load(output/'protocol.json');inputs=load(build/'input-identity.json');binaries=load(build/'binary-identity.json')
    build_host=load(build/'build.host.json')
    need(build_host['command']==load(build/'build-command.json') and build_host['status']=='success' and type(build_host['exitCode']) is int and build_host['exitCode']==0 and build_host['processGroupQuiescent'] is True and build_host['timedOut'] is False and build_host['descendantLeak'] is False and not any(k in build_host for k in ('error','cleanupError','cleanupWaitError')),'Build did not terminate cleanly')
    fixture={str(p.relative_to(ROOT)):sha(p) for p in sorted(ROOT.rglob('*')) if p.is_file() and '__pycache__' not in str(p)}
    repo=ROOT.parents[1]
    products={p:sha(repo/p) for p in git(repo,'ls-files').splitlines() if p.startswith('src/') or p in ('global.json','Directory.Build.props','Directory.Build.targets','Directory.Packages.props','NuGet.config')}
    receipts=[strict_json(line) for line in (output/'order-receipts.jsonl').read_text().splitlines()]
    names=[f'{i:02d}-{state}' for i,state in enumerate(s for order in ORDERS for s in order)]
    need(sorted(p.name for p in output.glob('[0-9][0-9]-*.json'))==sorted(name+suffix for name in names for suffix in ('.json','.host.json')),'Missing/extra raw/host evidence files')
    rows=[load(output/(n+'.json')) for n in names]
    need(sorted(p.name for p in output.glob('[0-9][0-9]-*.partial.jsonl'))==sorted(n+'.json.partial.jsonl' for n in names),'Missing/extra partial checkpoint files')
    partials=[]
    for name in names:
        partials.append(read_partial(output/(name+'.json.partial.jsonl')))
    result=validate_dataset(protocol,inputs,binaries,receipts,[load(output/(n+'.host.json')) for n in names],rows,fixture,products,partials)
    from archive import validate_archive
    validate_archive(rows,protocol['buildRoot'],output,binaries)
    return result

if __name__=='__main__':
    import argparse,json
    from pathlib import Path
    p=argparse.ArgumentParser(description='Recompute metrics only after strict raw receipt/closure validation')
    p.add_argument('--output',type=Path,required=True);p.add_argument('--build',type=Path,required=True);p.add_argument('--summary',type=Path,required=True)
    a=p.parse_args();result=validate_artifacts(a.output,a.build)
    with a.summary.open('x') as stream:json.dump(result,stream,indent=2);stream.write('\n')
