#!/usr/bin/env python3
"""Bounded owned-process lifecycle, adapted from reviewed FLUSH1 command/group_alive.
Only hosted fixed SDK/restore/build commands; no retries. Tests use pure Python.
"""
import argparse, datetime, json, os, signal, subprocess, time
from pathlib import Path
from verify_request import strict_json

def plan(phase):
 return {'sdk':(['dotnet','--version'],30),'restore':(['dotnet','restore','Sharplink.slnx','--disable-build-servers','-nodeReuse:false'],600),'build':(['dotnet','build','Sharplink.slnx','--no-restore','-c','Release','-v','minimal','--disable-build-servers','--no-incremental','-nodeReuse:false','-p:UseSharedCompilation=false','-p:EmitCompilerGeneratedFiles=true'],1200)}[phase]
def now():return datetime.datetime.now(datetime.timezone.utc).isoformat()
def save(path,data):
 temporary=path.with_suffix('.writing');temporary.write_text(json.dumps(data,indent=2)+'\n');os.replace(temporary,path)
def group_alive(pid):
 listing=subprocess.check_output(['ps','-eo','pgid=,stat='],text=True,timeout=5)
 return any(int(parts[0])==pid and not parts[1].startswith('Z') for line in listing.splitlines() if len(parts:=line.split())==2)
class Cancelled(BaseException):pass
def execute(argv,cwd,timeout,path,capture=False):
 if path.exists():raise ValueError('No command receipt overwrite/retry')
 receipt={'argv':argv,'cwd':str(cwd),'timeoutSeconds':timeout,'startedUtc':now(),'exitCode':None,'pid':None,'processGroupQuiescent':False,'completed':False};save(path,receipt);process=None;old={}
 def cancel(signum,frame):raise Cancelled('signal '+str(signum))
 for signum in (signal.SIGTERM,signal.SIGINT):old[signum]=signal.signal(signum,cancel)
 try:
  process=subprocess.Popen(argv,cwd=cwd,start_new_session=True,stdout=subprocess.PIPE if capture else None,stderr=subprocess.STDOUT if capture else None,text=True)
  receipt['pid']=process.pid;save(path,receipt)
  try:
   if capture:receipt['capturedOutput']=process.communicate(timeout=timeout)[0]
   else:process.wait(timeout=timeout)
   receipt['exitCode']=process.returncode
  except subprocess.TimeoutExpired:receipt['timedOut']=True
 except Cancelled as error:receipt['cancelled']=str(error)
 except (OSError,subprocess.SubprocessError) as error:receipt['error']=repr(error)
 finally:
  # A repeated cancellation must be recorded, not ignored or allowed to abort cleanup.
  def cleanup_cancel(signum,frame):receipt['cancelled']='signal '+str(signum)+' during cleanup'
  for signum in old:signal.signal(signum,cleanup_cancel)
  try:
   if process is not None:
    if group_alive(process.pid):
     if not receipt.get('timedOut') and not receipt.get('cancelled'):receipt['descendantLeak']=True
     try:os.killpg(process.pid,signal.SIGKILL)
     except ProcessLookupError:pass
     until=time.monotonic()+5
     while group_alive(process.pid) and time.monotonic()<until:time.sleep(.05)
    try:process.wait(timeout=5);receipt['exitCode']=process.returncode
    except subprocess.TimeoutExpired:receipt['cleanupTimedOut']=True
    receipt['processGroupQuiescent']=not group_alive(process.pid)
  except (OSError,subprocess.SubprocessError) as error:receipt['cleanupError']=repr(error)
  receipt.update(completed=True,finishedUtc=now());save(path,receipt)
  # Re-persist cancellation recorded while finalizing the first receipt snapshot.
  if receipt.get('cancelled'):save(path,receipt)
  for signum,handler in old.items():signal.signal(signum,handler)
 return receipt

def safe(receipt):
 return receipt.get('completed') is True and receipt.get('processGroupQuiescent') is True and not any(receipt.get(k) for k in ('timedOut','cancelled','descendantLeak','cleanupTimedOut','cleanupError','error'))
def lifecycle(source,out,step_outcomes):
 receipts={name:strict_json((out/('process-'+name+'.json')).read_text()) for name in ('sdk','restore','build') if (out/('process-'+name+'.json')).is_file()};reasons=[]
 for name,receipt in receipts.items():
  argv,timeout=plan(name)
  if receipt.get('argv')!=argv or receipt.get('timeoutSeconds')!=timeout or receipt.get('cwd')!=str(source) or not safe(receipt):reasons.append(name+': unverified or unsafe lifecycle')
 if set(step_outcomes)!=set(('sdk','restore','build')):reasons.append('Missing original workflow step outcomes')
 for name in ('sdk','restore','build'):
  outcome=step_outcomes.get(name);receipt=receipts.get(name)
  if receipt is None:
   if outcome!='skipped':reasons.append(name+': missing receipt for non-skipped step')
  elif outcome!=('success' if safe(receipt) and receipt.get('exitCode')==0 else 'failure'):reasons.append(name+': original step outcome differs from receipt or was cancelled')
 sdk=receipts.get('sdk',{})
 if sdk.get('exitCode')!=0 or sdk.get('capturedOutput','').strip()!='10.0.114':reasons.append('SDK not established')
 if 'restore' not in receipts:reasons.append('Restore not observed')
 elif receipts['restore'].get('exitCode')==0 and 'build' not in receipts:reasons.append('Successful restore without completed build receipt')
 result={'originalStepOutcomes':step_outcomes,'safeToAnalyze':not reasons,'reasons':reasons,'phasesPresent':sorted(receipts),'originalExitCodes':{name:r.get('exitCode') for name,r in receipts.items()}}
 save(out/'process-lifecycle.json',result)
 if os.environ.get('GITHUB_OUTPUT'):
  with Path(os.environ['GITHUB_OUTPUT']).open('a') as stream:stream.write('safe='+str(not reasons).lower()+'\n')
 if reasons:raise ValueError('; '.join(reasons))
 return result

if __name__=='__main__':
 p=argparse.ArgumentParser();p.add_argument('phase',choices=['sdk','restore','build','lifecycle']);p.add_argument('--source',type=Path,required=True);p.add_argument('--output',type=Path,required=True);p.add_argument('--sdk-outcome');p.add_argument('--restore-outcome');p.add_argument('--build-outcome');a=p.parse_args();source=a.source.resolve()
 if a.phase=='lifecycle':lifecycle(source,a.output,{'sdk':a.sdk_outcome,'restore':a.restore_outcome,'build':a.build_outcome})
 else:
  argv,budget=plan(a.phase);r=execute(argv,source,budget,a.output/('process-'+a.phase+'.json'),capture=a.phase=='sdk')
  if not safe(r) or r['exitCode']!=0 or (a.phase=='sdk' and r.get('capturedOutput','').strip()!='10.0.114'):raise SystemExit(1)
