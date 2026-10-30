#!/usr/bin/env python3
"""Finite same-runner #964 follow-up; no retry and no performance verdict."""
import argparse, datetime, hashlib, itertools, json, os, shutil, signal, subprocess, time
from pathlib import Path
BASE='57f06f172d29fa5cfd4ea8e9b86a23f122df24ab'
HEAD='8ae9ee36ebec82f82e03185432601a830c865e95'
BASE_TREE='e5b72625377ffdcf5f5470e2ddd69be12a449d8e'
TREE='1f0c9291c7394d012d3241a9cf31482b0734b697'
STATES={'static-baseline':'baseline','runtime-no-update':'candidate','post-update':'candidate'}
OVERLAY=['test/SharpLink.Benchmarks/RpcSessionFlushPolicyEvidenceRunner.cs','test/SharpLink.Benchmarks/Program.cs','test/SharpLink.Benchmarks/ServerCallAdmissionHotPathBenchmarks.cs']
def launch_plan():
 rows=[]
 for block,order in enumerate(itertools.permutations(STATES)):
  for state in order:rows.append(dict(index=len(rows),block=block,state=state,arm=STATES[state]))
 return rows

def sha(p):return hashlib.sha256(p.read_bytes()).hexdigest()
def git(root,*args):return subprocess.check_output(['git',*args],cwd=root,text=True).strip()
def now():return datetime.datetime.now(datetime.timezone.utc).isoformat()
def group_alive(pid):
 listing=subprocess.check_output(['ps','-eo','pgid=,stat='],text=True,timeout=5)
 return any(int(parts[0])==pid and not parts[1].startswith('Z') for line in listing.splitlines() if len(parts:=line.split())==2)

def command(args,log,cwd,env,timeout=600):
 receipt={'argv':[str(x) for x in args],'cwd':str(cwd),'startedUtc':now(),'timeoutSeconds':timeout,'exitCode':None,'processGroupQuiescent':False}
 p=None
 try:
  with log.open('x') as output:
   p=subprocess.Popen(args,cwd=cwd,env=env,stdout=output,stderr=subprocess.STDOUT,start_new_session=True)
   try:receipt['exitCode']=p.wait(timeout=timeout)
   except subprocess.TimeoutExpired:
    receipt['timedOut']=True;os.killpg(p.pid,signal.SIGKILL)
    try:receipt['exitCode']=p.wait(timeout=5)
    except subprocess.TimeoutExpired:receipt['cleanupTimedOut']=True
 except OSError as error:receipt['error']=repr(error)
 finally:
  try:
   if p is not None:
    if group_alive(p.pid):
     receipt['descendantLeak']=True;os.killpg(p.pid,signal.SIGKILL);until=time.monotonic()+5
     while group_alive(p.pid) and time.monotonic()<until:time.sleep(.05)
    receipt['processGroupQuiescent']=not group_alive(p.pid)
  except (OSError,subprocess.SubprocessError) as error:receipt['cleanupError']=repr(error)
  receipt['finishedUtc']=now();log.with_suffix('.receipt.json').write_text(json.dumps(receipt,indent=2)+'\n')
 if receipt['exitCode']!=0 or not receipt['processGroupQuiescent'] or any(receipt.get(k) for k in ('timedOut','cleanupTimedOut','descendantLeak','error','cleanupError')):raise RuntimeError(f'Failed command retained: {log}')


def main():
 p=argparse.ArgumentParser();p.add_argument('--baseline',type=Path,required=True);p.add_argument('--candidate',type=Path,required=True);p.add_argument('--output',type=Path,required=True);a=p.parse_args()
 root=Path(__file__).resolve().parents[2];out=a.output.resolve();out.mkdir(exist_ok=False,parents=True)
 arms={'baseline':a.baseline.resolve(),'candidate':a.candidate.resolve()};rows=launch_plan()
 manifest={'scope':'finite-descriptive-not-performance-acceptance','baseline':BASE,'candidate':HEAD,'candidateTree':TREE,'plannedLaunches':rows,'launches':[],'toolSha':git(root,'rev-parse','HEAD'),'toolTree':git(root,'rev-parse','HEAD^{tree}'),'startedUtc':now(),'runId':os.environ.get('GITHUB_RUN_ID'),'runAttempt':os.environ.get('GITHUB_RUN_ATTEMPT'),'overlays':{},'binaries':{},'sourceRoots':{k:str(v) for k,v in arms.items()},'outputRoot':str(out)}
 save=lambda:(out/'execution.json').write_text(json.dumps(manifest,indent=2)+'\n')
 save();env={k:v for k,v in os.environ.items() if not k.startswith(('COMPlus_','DOTNET_Tier','DOTNET_TC_','DOTNET_Jit'))};env.update(DOTNET_CLI_TELEMETRY_OPTOUT='1',DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER='1')
 command(['dotnet','--info'],out/'dotnet-info.log',root,env)
 command(['uname','-a'],out/'uname.log',root,env);command(['lscpu'],out/'cpu.log',root,env)
 if subprocess.check_output(['dotnet','--version'],cwd=root,text=True).strip()!='10.0.114':raise ValueError('Exact SDK10.0.114 required')
 for name,source in arms.items():
  expected=BASE if name=='baseline' else HEAD
  if git(source,'rev-parse','HEAD')!=expected or git(source,'status','--porcelain'):raise ValueError('Source not pristine exact frozen commit')
  if git(source,'rev-parse','HEAD^{tree}')!=(BASE_TREE if name=='baseline' else TREE):raise ValueError('Candidate tree changed')
  manifest.setdefault('sourceTrees',{})[name]=git(source,'rev-parse','HEAD^{tree}')
  manifest.setdefault('sourceInputs',{})[name]={str(q.relative_to(source)):sha(q) for q in sorted((source/'src').rglob('*')) if q.is_file()}
  for rel in OVERLAY:
   src=root/'eng/release-flush-limited/RpcSessionFlushPolicyEvidenceRunner.cs' if rel.endswith('RpcSessionFlushPolicyEvidenceRunner.cs') else root/rel
   shutil.copyfile(src,source/rel);manifest['overlays'][rel]=sha(src)
  command(['dotnet','build','test/SharpLink.Benchmarks/SharpLink.Benchmarks.csproj','-c','Release','-m:1','-p:UseSharedCompilation=false','-v','minimal'],out/f'{name}-build.log',source,env,900)
  binary=source/'test/SharpLink.Benchmarks/bin/Release/net10.0/SharpLink.Benchmarks.dll'
  manifest.setdefault('overlayStatus',{})[name]=git(source,'status','--porcelain')
  if set(git(source,'diff','--name-only').splitlines())-set(OVERLAY):raise ValueError('Unexpected source modification')
  manifest['binaries'][name]={str(q.relative_to(binary.parent)):sha(q) for q in sorted(binary.parent.rglob('*')) if q.is_file()}
  # Archive actual whole execution closure, not just the entry assembly.
  shutil.copytree(binary.parent,out/'binaries'/name)
 save();command(['dotnet','build-server','shutdown'],out/'build-server-shutdown.log',root,env)
 for row in rows:
  launch=dict(row,startedUtc=now(),status='running');manifest['launches'].append(launch);save()
  source=arms[row['arm']];sample=out/f"{row['index']:02d}-{row['state']}.json"
  e=env|{'SHARPLINK_BENCHMARK_SHA':BASE if row['arm']=='baseline' else HEAD}
  try:
   command(['dotnet','exec','--fx-version','10.0.14',str(source/'test/SharpLink.Benchmarks/bin/Release/net10.0/SharpLink.Benchmarks.dll'),'--rpc-session-flush-policy-evidence',row['state'],str(sample)],out/f"{row['index']:02d}-{row['state']}.log",source,e,180)
   launch['status']='completed'
  except BaseException:
   launch['status']='failed';raise
  finally:launch['finishedUtc']=now();save()
 for name,source in arms.items():
  before=manifest['sourceInputs'][name];after={rel:sha(source/rel) for rel in before}
  if before!=after:raise ValueError('Product source changed during execution')
  binary=source/'test/SharpLink.Benchmarks/bin/Release/net10.0'
  after={str(q.relative_to(binary)):sha(q) for q in sorted(binary.rglob('*')) if q.is_file()}
  if after!=manifest['binaries'][name]:raise ValueError('Executed binary closure changed')
 manifest['finishedUtc']=now();manifest['completed']=True;save()
if __name__=='__main__':main()
