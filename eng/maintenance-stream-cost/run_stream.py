#!/usr/bin/env python3
"""Actual matched TLS stream observations; no performance threshold or equivalence claim."""
import argparse,datetime,hashlib,json,os,shutil,subprocess
from pathlib import Path
p=argparse.ArgumentParser(description=__doc__);p.add_argument('--run-name',required=True);p.add_argument('--expected-runtime',required=True);a=p.parse_args();r=Path(__file__).resolve().parent;out=r/'evidence'/a.run_name;out.mkdir(exist_ok=False);host=Path(shutil.which('dotnet')).resolve()
sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest();modes={'fullopts':{'DOTNET_TieredCompilation':'0'},'pgo':{'DOTNET_TieredCompilation':'1','DOTNET_TieredPGO':'1','DOTNET_TC_QuickJit':'1','DOTNET_TC_QuickJitForLoops':'1','DOTNET_TC_CallCountingDelayMs':'0','DOTNET_TC_CallCountThreshold':'30'}}
prov={'StartedUtc':datetime.datetime.now(datetime.timezone.utc).isoformat(),'ExpectedRuntime':a.expected_runtime,'Scope':'32768 integer items per stream;2 warmup and5 observed streams/process;3 processes/cell;real TLS/auth actual pool2;finite1h method deadline;4096-byte server stream credit. PGO accelerated promotion. Whole-process managed allocations, not helper caller-thread allocation.','ProbeSources':{str(p.relative_to(r)):sha(p) for d in ['probe','contracts','services'] for p in (r/d).glob('*') if p.is_file()},'RunnerSha256':sha(Path(__file__)),'Arms':{},'Launches':[]}
for arm in ['published-prerequisites','candidate']:
 d=out/arm;d.mkdir();binary=r/f'evidence/{arm}/build/bin/HotCostProbe/release/SharpLink.UnitTests.dll';env={k:v for k,v in os.environ.items() if not k.startswith(('DOTNET_','COMPlus_'))};env.update(DOTNET_ROOT=str(host.parent),DOTNET_ROLL_FORWARD='LatestPatch')
 with (d/'metadata.log').open('w') as f:res=subprocess.run([str(host),str(binary),'metadata',str(d/'metadata.json')],stdout=f,stderr=subprocess.STDOUT,env=env,timeout=90)
 assert res.returncode==0;binding=json.loads((d/'metadata.json').read_text())['RuntimeBinding'];assert binding['RuntimeVersion']==a.expected_runtime and binding['Architecture']=='X64';rd=Path(binding['CoreLibPath']).parent;fxr=list(host.parent.glob('host/fxr/*/libhostfxr.so'));assert len(fxr)==1,'Use an isolated host with exactly one fxr.'
 prov['Arms'][arm]={'Binary':str(binary),'RuntimeBinding':binding,'RuntimeFiles':{str(p):sha(p) for p in [host,rd/'System.Private.CoreLib.dll',rd/'libcoreclr.so',rd/'libclrjit.so',rd/'libhostpolicy.so',*fxr]},'Sources':{str(p.relative_to(r/'arms'/arm)):sha(p) for p in (r/'arms'/arm/'src').rglob('*') if p.is_file()},'Binaries':{p.name:sha(p) for p in binary.parent.glob('*.dll')},'AuxiliaryBinaries':{str(p.relative_to(binary.parent.parent.parent)):sha(p) for p in binary.parent.parent.parent.glob('HotCostDynamic.*/release/*.dll')}}
for mode,settings in modes.items():
 for rep in range(3):
  for arm in (['published-prerequisites','candidate'] if rep%2==0 else ['candidate','published-prerequisites']):
   d=out/arm;name=f'{mode}-{rep}';env={k:v for k,v in os.environ.items() if not k.startswith(('DOTNET_','COMPlus_'))};env.update(settings);env.update(DOTNET_ROOT=str(host.parent),DOTNET_ROLL_FORWARD='LatestPatch',DOTNET_ReadyToRun='0');row={'Arm':arm,'Mode':mode,'Repetition':rep,'Settings':{k:v for k,v in env.items() if k.startswith('DOTNET_')},'StartedUtc':datetime.datetime.now(datetime.timezone.utc).isoformat()}
   try:
    with (d/(name+'.log')).open('w') as f:res=subprocess.run([str(host),prov['Arms'][arm]['Binary'],'hot','32768',str(d/(name+'.json'))],stdout=f,stderr=subprocess.STDOUT,env=env,timeout=180)
    row['ExitCode']=res.returncode
   except subprocess.TimeoutExpired:row['ExitCode']='timeout'
   row['FinishedUtc']=datetime.datetime.now(datetime.timezone.utc).isoformat();prov['Launches'].append(row);(out/'provenance.json').write_text(json.dumps(prov,indent=2)+'\n');print(arm,name,row['ExitCode'],flush=True)
   if row['ExitCode']!=0:raise RuntimeError('Retained failed launch: '+str(d/(name+'.log')))
   j=json.loads((d/(name+'.json')).read_text());assert j['RuntimeBinding']['RuntimeVersion']==a.expected_runtime
prov['FinishedUtc']=datetime.datetime.now(datetime.timezone.utc).isoformat();(out/'provenance.json').write_text(json.dumps(prov,indent=2)+'\n')
