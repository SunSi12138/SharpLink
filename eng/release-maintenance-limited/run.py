#!/usr/bin/env python3
"""Run the predeclared finite serial order. No retries, extra cells, or thresholds."""
import argparse, json, os, time
from pathlib import Path
from prepare import inventory, sha
from validate import ORDERS, validate_process, validate_artifacts, runtime_environment
from process import execute
from archive import archive_closure

def main():
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('--build',type=Path,required=True);p.add_argument('--runtime-dotnet',type=Path,required=True);p.add_argument('--output',type=Path,required=True)
    a=p.parse_args();build=a.build.resolve();runtime=a.runtime_dotnet.resolve();out=a.output.resolve();out.mkdir(parents=True,exist_ok=False)
    expected=json.loads((build/'binary-identity.json').read_text()); inputs=json.loads((build/'input-identity.json').read_text())
    if inventory(build/'build/bin') != expected: raise ValueError('Binary identity drift before run')
    if inventory(runtime.parent)!=inputs['runtimeClosure']:raise ValueError('Runtime closure drift before run')
    if any(sha(Path(path)) != digest for path,digest in inputs['runtimeFiles'].items()):raise ValueError('Runtime identity drift')
    if str(runtime) not in inputs['runtimeFiles']:raise ValueError('Unrecorded runtime host')
    env=os.environ.copy()
    # Explicit, identical policy for every process. Private host isolates the patched runtime.
    for key in list(env):
        if key.startswith(('DOTNET_','COMPlus_')): del env[key]
    env.update(runtime_environment(str(runtime)))
    dll=build/'build/bin/HotCostProbe/release/SharpLink.UnitTests.dll'
    (out/'protocol.json').write_text(json.dumps({'runtimeDotnet':str(runtime),'buildRoot':str(build),'outputRoot':str(out),'orders':ORDERS,'warmupCalls':1250,'rounds':5,'callsPerRound':5000,'processCount':18,'processTimeoutSeconds':90,'environment':{k:v for k,v in env.items() if k.startswith('DOTNET_')},'binaryIdentity':expected,'inputIdentity':inputs},indent=2)+'\n')
    rows=[]
    with (out/'order-receipts.jsonl').open('x') as receipts:
        for block,order in enumerate(ORDERS):
            for position,state in enumerate(order):
                index=len(rows);raw=out/f'{index:02d}-{state}.json';command=[str(runtime),'exec','--fx-version','10.0.14','--roll-forward','Disable',str(dll),state,str(raw)]
                start=time.time_ns();base={'index':index,'block':block,'position':position,'state':state,'output':raw.name,'command':command,'startedUnixNs':start}
                receipts.write(json.dumps(dict(base,event='started'))+'\n');receipts.flush()
                host=execute(command,out,f'{index:02d}-{state}',env)
                receipts.write(json.dumps(dict(base,event='finished',endedUnixNs=host['endedUnixNs'],returncode=host['exitCode'],status=host['status'],processGroupQuiescent=host['processGroupQuiescent']))+'\n');receipts.flush()
                if host['status']!='success' or host['exitCode']!=0 or not host['processGroupQuiescent']:
                    raise ValueError('Process failed/timed out/leaked or cleanup uncertain; remaining plan forbidden')
                data=json.loads(raw.read_text());validate_process(data,state)
                if data['HostPath'] != str(runtime) or data['CoreLibPath'] not in inputs['runtimeFiles'] or data['CoreLibSha256'] != inputs['runtimeFiles'][data['CoreLibPath']]:raise ValueError('Loaded host/CoreLib differ from pinned runtime')
                if inventory(build/'build/bin') != expected:raise ValueError('Binary identity drift after process')
                if inventory(runtime.parent)!=inputs['runtimeClosure']:raise ValueError('Runtime closure drift after process')
                if any(sha(Path(path)) != digest for path,digest in inputs['runtimeFiles'].items()):raise ValueError('Runtime identity drift after process')
                rows.append(data)
                archive_closure(rows,build,out,expected)
    validated=validate_artifacts(out,build)
    (out/'summary.json').write_text(json.dumps(validated,indent=2)+'\n')

if __name__=='__main__':main()
