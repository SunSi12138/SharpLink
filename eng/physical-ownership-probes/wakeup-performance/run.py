#!/usr/bin/env python3
"""W1 full-product actual TCP diagnostic. Every planned sample attempted once."""
import argparse
import datetime
import json
import math
import os
from pathlib import Path
import subprocess
import sys

HERE=Path(__file__).resolve().parent
SOURCE=HERE.parent
sys.path.insert(0,str(SOURCE))
sys.path.insert(0,str(SOURCE/'tcp'))
from logical_forwarder_common import (SDK,PACKAGES,capture,run,write,verify_source,validate_runtime,
                                     verify_dependencies,pin_snapshot,verify_process)
from run_matrix import execute_sample
from protocol import (BASELINE,CANDIDATE,CANDIDATE_ORIGINAL,CANDIDATE_TREE,TOPOLOGIES,WARMUP,SECONDS,
                      topology,schedule,cpu_set,validate_ticks)


def clean_environment(runtime):
    env=dict(os.environ,DOTNET_ROOT=str(runtime.parent),DOTNET_MULTILEVEL_LOOKUP='0')
    # Prevent inherited JIT/profiler/GC/TP knobs from creating an undeclared profile.
    allowed={'DOTNET_CLI_TELEMETRY_OPTOUT','DOTNET_NOLOGO','DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER',
             'DOTNET_ROOT','DOTNET_MULTILEVEL_LOOKUP','DOTNET_INSTALL_DIR','DOTNET_SKIP_FIRST_TIME_EXPERIENCE'}
    for name in list(env):
        if name.startswith(('COMPlus_','CORECLR_','COR_')) or (name.startswith('DOTNET_') and name not in allowed): del env[name]
    return env


def validate_sample(data,arm,workers,cpus,prepared,hashes,runtime):
    verify_process(data,runtime.parent)
    if (data['sourceRevision'],data['binaryHashes'])!=((BASELINE if arm=='baseline' else CANDIDATE),hashes[arm]):raise ValueError('Source/binary identity mismatch')
    if (data['mode'],data['itemCount'],data['workers'],data['warmupSeconds'],data['requestedSeconds'])!=('static-intercepted',100,workers,WARMUP,SECONDS):raise ValueError('Workload mismatch')
    if (data['processorCount'],data['tieredCompilation'],data['tieredPgo'],data['readyToRun'])!=(2,'default','default','default'):raise ValueError('Runtime/affinity profile mismatch')
    if (data['transport'],data['connectionPoolMin'],data['connectionPoolMax'],data['readyConnections'],data['requestTimeout'],data['heartbeatIntervalSeconds'],data['serverInterceptors'],data['clientInterceptors'])!=('tcp-loopback',1,1,1,'disabled',3600,0,1):raise ValueError('TCP path contract mismatch')
    if data['validationFailures']!=0 or type(data['operations']) is not int or data['operations']<=0:raise ValueError('No valid operations')
    if not data['interceptorObserved'] or not any('InterceptedAsyncEnumerable' in t for t in data['wrapperTypes']) or 'ModuleRpcChannel' in data['channelType'] or 'InvokeServerStreamingDeferred' in data['streamType']:raise ValueError('Static intercepted path missing')
    if not data['threadAffinities'] or any(cpu_set(t['allowed'])!=set(cpus) for t in data['threadAffinities']):raise ValueError('Post-measurement thread affinity differs')
    expected=BASELINE if arm=='baseline' else CANDIDATE
    loaded=data['loadedAssemblies']; names=[a['name'] for a in loaded]
    if len(set(names))!=len(names) or not {'SharpLink.Runtime','SharpLink.Client','SharpLink.Server','SharpLink.Abstractions','PhysicalTcpProbe','ScalarStreamModule'}<=set(names):raise ValueError('Loaded assembly inventory missing/duplicated')
    for assembly in loaded:
        path=Path(assembly['location'])
        if path.parent!=prepared/arm or path.name not in hashes[arm] or not assembly['mvid']:raise ValueError('Assembly outside snapshot')
        if assembly['name'].startswith('SharpLink.') and assembly['productVersion'].split('+')[-1]!=expected:raise ValueError('Product source revision mismatch')
    for key in ('operationsPerSecond','p50Us','p99Us','p999Us','allocatedBytesPerOperation','cpuUsPerOperation','totalAllocatedBytes','totalCpuMicroseconds'):
        if type(data[key]) not in (int,float) or not math.isfinite(data[key]) or data[key]<0:raise ValueError('Invalid metric '+key)


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    for name in ('baseline','candidate','build-dotnet','runtime-dotnet','output'):parser.add_argument('--'+name,type=Path,required=True)
    args=parser.parse_args(); output=args.output.resolve(); output.mkdir(parents=True,exist_ok=False)
    tools=SOURCE.parent.parent; build=args.build_dotnet.resolve(strict=True); runtime=args.runtime_dotnet.resolve(strict=True)
    roots={'baseline':args.baseline.resolve(strict=True),'candidate':args.candidate.resolve(strict=True)}
    tool_sha=capture(['git','rev-parse','HEAD'],tools);verify_source(tools,tool_sha)
    for arm,expected in [('baseline',BASELINE),('candidate',CANDIDATE)]:verify_source(roots[arm],expected)
    if capture(['git','rev-parse','HEAD^{tree}'],roots['candidate'])!=CANDIDATE_TREE:raise ValueError('Wrong candidate tree')
    if capture([build,'--version'],tools)!=SDK:raise ValueError('Wrong SDK')
    runtimes=validate_runtime(runtime)
    for name,command in [('lscpu',['lscpu']),('lscpu-mapping',['lscpu','-e=CPU,CORE,SOCKET,NODE,ONLINE']),('uname',['uname','-a']),('runtime-info',[runtime,'--info']),('build-info',[build,'--info'])]:run(command,output/(name+'.txt'),tools)
    topo=topology(os.sched_getaffinity(0)); write(output/'topology.json',topo)
    planned=schedule(topo['selected']); write(output/'schedule.json',planned)
    write(output/'experiment.json',{'toolRevision':tool_sha,'baseline':BASELINE,'candidate':CANDIDATE,'candidateOriginal':CANDIDATE_ORIGINAL,'candidateTree':CANDIDATE_TREE,'sdk':SDK,'runtimes':runtimes,
        'skippedBeforeTiming':[kind for kind in TOPOLOGIES if not topo['selected'][kind]],'plannedSamples':len(planned),
        'boundary':'Historical f724 vs fenced write-elision candidate, not current dev or official2.0.3. Closed-loop diagnostic, no same-arrival-load or final10/20 acceptance. No steady-state Tier1 claim.',
        'runner':{k:os.environ.get(k) for k in ('GITHUB_SHA','GITHUB_RUN_ID','GITHUB_RUN_ATTEMPT','RUNNER_ARCH','ImageOS','ImageVersion')}})
    if not planned:raise ValueError('No required topology available; no timing, no substitution')
    for arm,root in roots.items():
        logs=output/arm;logs.mkdir();common=['-c','Release','-m:1','-nr:false','-p:UseSharedCompilation=false']
        run([build,'build','test/SharpLink.UnitTests/SharpLink.UnitTests.csproj',*common,f'-bl:{logs/"build.binlog"}'],logs/'build.log',root)
        for package in PACKAGES:
            run([build,'pack',f'src/SharpLink.{package}/SharpLink.{package}.csproj',*common,'-o',logs/'packages',f'-bl:{logs/("pack-"+package+".binlog")}'],logs/f'pack-{package}.log',root)
        run(['bash','eng/verify-packages.sh',logs/'packages','3.0.0'],logs/'package-verification.log',root)
        verify_source(root,BASELINE if arm=='baseline' else CANDIDATE)
    prepared=output/'prepared'
    run([sys.executable,SOURCE/'tcp/prepare.py','--baseline-libraries',roots['baseline']/'test/SharpLink.UnitTests/bin/Release/net10.0','--candidate-libraries',roots['candidate']/'test/SharpLink.UnitTests/bin/Release/net10.0','--baseline-generator',roots['baseline']/'src/SharpLink.Generator/bin/Release/netstandard2.0/SharpLink.Generator.dll','--probe-source',SOURCE/'tcp/wakeup-probe','--dotnet',build,'--output',prepared],output/'prepare.log',tools)
    hashes=verify_dependencies(prepared/'baseline',prepared/'candidate',('PhysicalTcpProbe.dll','ScalarStreamModule.dll'));write(output/'snapshot-integrity.json',hashes)
    for arm in roots:pin_snapshot(prepared/arm)
    run([build,'build-server','shutdown'],output/'build-server-shutdown.log',tools)
    env=clean_environment(runtime);write(output/'runtime-environment.json',{k:v for k,v in env.items() if k.startswith(('DOTNET_','COMPlus_','CORECLR_','COR_'))})
    # Untimed fixed-work path/concurrency proof on BOTH arms, workers and available topologies before ANY timing.
    preflight=output/'preflight';preflight.mkdir()
    for kind in TOPOLOGIES:
        cpus=topo['selected'][kind]
        if not cpus:continue
        for workers in (1,8):
            for arm in ('baseline','candidate'):
                key=f'{kind}-c{workers}-{arm}';dest=preflight/(key+'.json')
                command=['taskset','-c',','.join(map(str,cpus)),str(runtime),str(prepared/arm/'PhysicalTcpProbe.dll'),'static-intercepted','100',str(workers),'0','0',str(dest),'preflight']
                code=execute_sample(command,preflight/(key+'.log'),preflight/(key+'.host.json'),arm,0,dict(env,SHARPLINK_BENCHMARK_SHA=BASELINE if arm=='baseline' else CANDIDATE),90)
                if code:raise ValueError('Preflight failed; no timing: '+key)
                data=json.loads(dest.read_text());verify_process(data,runtime.parent)
                if (data['workers'],data['maxActive'],data['operations'],data['readyConnections'],data['binaryHashes'],data['sourceRevision'])!=(workers,workers,128*workers,1,hashes[arm],BASELINE if arm=='baseline' else CANDIDATE):raise ValueError('Preflight proof mismatch')
    write(output/'measurement-start.json',{'timestampUtc':datetime.datetime.now(datetime.timezone.utc).isoformat(),'policy':'All builds and untimed preflights complete; no trace/disassembly/build after this marker.'})
    reports={}
    for kind in TOPOLOGIES:
        if not topo['selected'][kind]:continue
        root=output/kind
        for directory in ('runs','ticks'):(root/directory).mkdir(parents=True)
        reports[kind]={'status':'Passed','inventoryErrors':[],'samples':{}}
    for cell in planned:
        kind,arm,workers,name,cpus=(cell[k] for k in ('topology','arm','workers','sample','cpus'))
        root=output/kind;dest=root/'runs'/(name+'.json');ticks=root/'ticks'/(name+'.json')
        command=['taskset','-c',','.join(map(str,cpus)),str(runtime),str(prepared/arm/'PhysicalTcpProbe.dll'),'static-intercepted','100',str(workers),str(WARMUP),str(SECONDS),str(dest),'measure',str(ticks)]
        try:
            code=execute_sample(command,root/'runs'/(name+'.log'),root/'runs'/(name+'.host.json'),arm,cell['round'],dict(env,SHARPLINK_BENCHMARK_SHA=BASELINE if arm=='baseline' else CANDIDATE),WARMUP+SECONDS+90)
            if code:raise ValueError(f'Exit {code}')
            data=json.loads(dest.read_text());validate_sample(data,arm,workers,cpus,prepared,hashes,runtime);validate_ticks(json.loads(ticks.read_text()),data)
            reports[kind]['samples'][name]={'status':'Passed'}
            print(kind,name,'QPS',data['operationsPerSecond'],'P99',data['p99Us'],flush=True)
        except (OSError,ValueError,KeyError,TypeError,RuntimeError,subprocess.TimeoutExpired) as error:
            reports[kind]['status']='Failed';reports[kind]['samples'][name]={'status':'Failed','reason':repr(error)}
        write(root/'sample-validation.json',reports[kind])
    for kind,report in reports.items():
        root=output/kind
        expected={c['sample'] for c in planned if c['topology']==kind}
        inventories={'raw':{p.stem for p in (root/'runs').glob('*.json') if not p.name.endswith('.host.json')},'host':{p.name.removesuffix('.host.json') for p in (root/'runs').glob('*.host.json')},'ticks':{p.stem for p in (root/'ticks').glob('*.json')}}
        for name,actual in inventories.items():
            if actual!=expected:report['inventoryErrors'].append({'kind':name,'missing':sorted(expected-actual),'unexpected':sorted(actual-expected)});report['status']='Failed'
        write(root/'sample-validation.json',report)
        run([sys.executable,SOURCE/'tcp/summarize.py','--input',root,'--validation-manifest',root/'sample-validation.json'],root/'summary.log',tools,check=False)
        if report['status']=='Passed':
            summary=json.loads((root/'summary.json').read_text())
            if len(summary)!=2 or {r['workers'] for r in summary}!={1,8} or any(r['pairs']!=6 for r in summary):raise ValueError('Summary schedule mismatch')
    failed=any(r['status']!='Passed' for r in reports.values())
    write(output/'complete.json',{'status':'Failed' if failed else 'Passed','plannedSamples':len(planned),'topologies':list(reports),'performanceAcceptance':'Not evaluated; independent raw review required'})
    if failed:raise ValueError('Original W1 schedule retained failures; no replacements or retry')

if __name__=='__main__':main()
