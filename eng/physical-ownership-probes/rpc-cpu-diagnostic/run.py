#!/usr/bin/env python3
"""Reviewed hosted C1 only. Baseline-only CPU attribution, never benchmark acceptance."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import signal
import subprocess
import sys
import time

HERE=Path(__file__).resolve().parent
sys.path.insert(0,str(HERE.parent))
from logical_forwarder_common import capture, run, write, verify_source, validate_runtime, pin_snapshot, verify_process
from contract import SOURCE,TREE,SDK,RUNTIME,CASES,CAP,TIMEOUT,PROVIDERS,record_command,script_command
from verify_request import main as verify_request

def hashes(root):
    return {str(p.relative_to(root)):hashlib.sha256(p.read_bytes()).hexdigest() for p in sorted(root.rglob('*')) if p.is_file()}

def environment(runtime):
    allowed={'DOTNET_GENERATE_ASPNET_CERTIFICATE','DOTNET_CLI_HOME','DOTNET_CLI_TELEMETRY_OPTOUT','DOTNET_NOLOGO','DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER',
             'DOTNET_INSTALL_DIR','DOTNET_ROOT','DOTNET_MULTILEVEL_LOOKUP','DOTNET_SKIP_FIRST_TIME_EXPERIENCE'}
    bad=[k for k in os.environ if k.startswith(('COMPlus_','CORECLR_','COR_')) or (k.startswith('DOTNET_') and k not in allowed)]
    if bad: raise ValueError('Conflicting incoming runtime overrides: '+','.join(bad))
    return dict(os.environ,DOTNET_ROOT=str(runtime.parent),DOTNET_MULTILEVEL_LOOKUP='0',DEBUGINFOD_URLS='',LC_ALL='C')

def map_paths(case):
    pidfile=case/'pid.json'
    if not pidfile.exists(): return []
    pid=json.loads(pidfile.read_text())['pid']
    if type(pid) is not int or pid<=0: raise ValueError('Invalid own-child PID receipt')
    # Exact runtime-owned maps only; no process attach and no broad /tmp glob.
    return [p for p in (Path(f'/tmp/perf-{pid}.map'),Path(f'/tmp/perfinfo-{pid}.map')) if p.is_file()]

def bytes_in(case):
    return sum(p.stat().st_size for p in case.rglob('*') if p.is_file())+sum(p.stat().st_size for p in map_paths(case))

def bounded(command,case,name,env,deadline):
    command=list(map(str,command)); started=time.monotonic(); reason=None
    write(case/(name+'-command.json'),command)
    with (case/(name+'.stdout')).open('w') as out,(case/(name+'.stderr')).open('w') as err:
        process=subprocess.Popen(command,stdout=out,stderr=err,env=env,start_new_session=True)
        try:
            while process.poll() is None:
                if time.monotonic()>=deadline: reason='per-case 180s deadline'; break
                if bytes_in(case)>CAP: reason='per-case 256MiB cap'; break
                time.sleep(.1)
            if time.monotonic()>=deadline: reason=reason or 'per-case 180s deadline (post-exit)'
            if reason:
                try: os.killpg(process.pid,signal.SIGTERM)
                except ProcessLookupError: pass
                try: process.wait(timeout=3)
                except subprocess.TimeoutExpired: pass
                finally:
                    # Leader exit does not prove its dotnet descendant stopped.
                    try: os.killpg(process.pid,signal.SIGKILL)
                    except ProcessLookupError: pass
                process.wait()
            code=process.wait()
        except BaseException:
            try: os.killpg(process.pid,signal.SIGKILL)
            except ProcessLookupError: pass
            process.wait(); raise
        finally:
            if time.monotonic()>=deadline: reason=reason or 'per-case 180s deadline (post-exit)'
            write(case/(name+'-exit.json'),{'command':command,'exitCode':process.returncode,'reason':reason,'elapsedSeconds':time.monotonic()-started,'bytesObserved':bytes_in(case)})
    if reason or code or bytes_in(case)>CAP: raise RuntimeError(f'{name} failed: {reason or code}; no replacement')

def validate(data,mode,count,snapshot,runtime):
    verify_process(data,runtime.parent)
    dlls={p.name:hashlib.sha256(p.read_bytes()).hexdigest() for p in snapshot.glob('*.dll')}
    if data['binaryHashes']!=dlls or data['sourceRevision']!=SOURCE: raise ValueError('Source/binary identity mismatch')
    if (data['mode'],data['itemCount'],data['workers'],data['warmupSeconds'],data['requestedSeconds'],data['batch'],data['maxOperations']) != (mode,count,1,10,20,256,1000000): raise ValueError('Workload mismatch')
    if not data['complete'] or data['operations']<=0 or data['operations']%256: raise ValueError('Incomplete operations')
    if (data['transport'],data['readyConnections'],data['requestTimeout'],data['clientInterceptors'],data['serverInterceptors'],data['serverGc'])!=('tcp-loopback',1,'disabled',1,0,False): raise ValueError('Runtime/path mismatch')
    if any(data[k]!='default' for k in ('tieredCompilation','tieredPgo','readyToRun')): raise ValueError('Nondefault tiering')
    if not data['interceptorObserved'] or not any('InterceptedAsyncEnumerable' in t for t in data['wrapperTypes']): raise ValueError('Wrapper missing')
    if (mode=='dynamic-intercepted') != ('ModuleRpcChannel' in data['channelType']) or (mode=='dynamic-intercepted') != ('InvokeServerStreamingDeferred' in data['streamType']): raise ValueError('Wrong static/dynamic path')
    loaded=data['loadedAssemblies']; names=[a['name'] for a in loaded]
    if len(names)!=len(set(names)) or not {'SharpLink.Client','SharpLink.Server','SharpLink.Runtime','SharpLink.Abstractions','RpcCpuProbe','ScalarStreamModule'}<=set(names): raise ValueError('Incomplete assembly census')
    for a in loaded:
        p=Path(a['location'])
        if p.parent!=snapshot or p.name not in dlls or not a['mvid']: raise ValueError('Loaded assembly outside snapshot')
        if a['name'].startswith('SharpLink.') and a['productVersion'].split('+')[-1]!=SOURCE: raise ValueError('Wrong product source stamp')

def main():
    verify_request()
    parser=argparse.ArgumentParser()
    for name in ('source','build-dotnet','runtime-dotnet','gate','output'): parser.add_argument('--'+name,type=Path,required=True)
    args=parser.parse_args(); output=args.output.resolve(); output.mkdir(parents=True,exist_ok=False)
    root=args.source.resolve(strict=True); build=args.build_dotnet.resolve(strict=True); runtime=args.runtime_dotnet.resolve(strict=True)
    gate=json.loads((args.gate/'gate.json').read_text())
    if not gate['passed'] or gate['command']!=[gate['perf'],'stat','-e','task-clock:u','--','/usr/bin/true']: raise ValueError('No exact passed gate')
    perf=Path(gate['perf']); env=environment(runtime)
    verify_source(root,SOURCE)
    if capture(['git','rev-parse','HEAD^{tree}'],root)!=TREE or capture([build,'--version'])!=SDK: raise ValueError('Source tree/SDK mismatch')
    runtimes=validate_runtime(runtime)
    write(output/'identity.json',{'source':SOURCE,'tree':TREE,'toolRequest':capture(['git','rev-parse','HEAD'],HERE),'sdk':SDK,'runtime':runtimes,'frameworkHashes':hashes(runtime.parent/'shared'),'schedule':CASES,'diagnosticOnly':True})
    common=['-c','Release','-m:1','-nr:false','-p:UseSharedCompilation=false']
    run([build,'build',root/'test/SharpLink.UnitTests/SharpLink.UnitTests.csproj',*common],output/'product-build.log',root)
    verify_source(root,SOURCE)
    libraries=root/'test/SharpLink.UnitTests/bin/Release/net10.0'; generator=root/'src/SharpLink.Generator/bin/Release/netstandard2.0/SharpLink.Generator.dll'
    prep=output/'build'; prep.mkdir()
    for file in ('Directory.Build.props','Directory.Build.targets','Directory.Packages.props'): (prep/file).write_text('<Project />\n')
    (prep/'global.json').write_text(json.dumps({'sdk':{'version':SDK,'rollForward':'disable'}}))
    for name in ('module','probe','decoder'):
        shutil.copytree(HERE/name,prep/name)
        run([build,'build',prep/name/(name+'.csproj'),*common,f'-p:LibraryRoot={libraries}',f'-p:GeneratorPath={generator}'],output/(name+'-build.log'),prep)
    snapshot=output/'snapshot'; shutil.copytree(prep/'probe/bin/Release/net10.0',snapshot)
    shutil.copy2(prep/'module/bin/Release/net10.0/ScalarStreamModule.dll',snapshot/'ScalarStreamModule.dll')
    pin_snapshot(snapshot)
    decoder=prep/'decoder/bin/Release/net10.0'
    config=decoder/'decoder.runtimeconfig.json'; d=json.loads(config.read_text()); d['runtimeOptions']['framework']['version']=RUNTIME; d['runtimeOptions']['rollForward']='Disable'; write(config,d)
    before=hashes(snapshot); write(output/'snapshot.json',before); write(output/'decoder.json',hashes(decoder))
    run([build,'build-server','shutdown'],output/'build-server-shutdown.log',prep)
    stage=time.monotonic()
    for mode,count in CASES:
        case=output/f'{mode}-{count}'; case.mkdir(); deadline=min(time.monotonic()+TIMEOUT,stage+600)
        try:
            capture_env=dict(env,DOTNET_PerfMapEnabled='3',DOTNET_EnableEventPipe='1',DOTNET_EventPipeOutputPath=str(case/'runtime.nettrace'),DOTNET_EventPipeOutputStreaming='1',DOTNET_EventPipeCircularMB='40',DOTNET_EventPipeConfig=PROVIDERS,SHARPLINK_BENCHMARK_SHA=SOURCE)
            child=[runtime,snapshot/'RpcCpuProbe.dll',mode,str(count),case]
            bounded(record_command(perf,case/'perf.data',child),case,'record',capture_env,deadline)
            for p in map_paths(case): shutil.copy2(p,case/p.name)
            validate(json.loads((case/'result.json').read_text()),mode,count,snapshot,runtime)
            if hashes(snapshot)!=before: raise ValueError('Snapshot changed')
            bounded(script_command(perf,case/'perf.data'),case,'perf-script',env,deadline)
            (case/'perf-script.stdout').rename(case/'perf-script.txt')
            bounded([runtime,decoder/'decoder.dll',case/'runtime.nettrace',case/'decoded'],case,'decode',env,deadline)
            bounded([sys.executable,HERE/'analyze.py',case,'--output',case/'analysis.json'],case,'analyze',env,deadline)
            write(case/'complete.json',{'status':'completed','diagnosticOnly':True,'bytes':bytes_in(case)})
        except BaseException as error:
            write(case/'failure.json',{'error':repr(error),'policy':'stop first failure; no retry/replacement/alternate host'})
            raise
        finally:
            for p in map_paths(case):
                destination=case/p.name
                if not destination.exists(): shutil.copy2(p,destination)
            write(case/'inventory.json',hashes(case))
    write(output/'complete.json',{'status':'completed','cases':len(CASES),'performanceAcceptance':'not evaluated; raw independent review required'})
if __name__=='__main__': main()
