"""Bounded process-group execution. Any timeout, leak or uncertain cleanup fails closed."""
import json, os, signal, subprocess, time

def group_alive(pid, timeout=5):
    listing=subprocess.check_output(['ps','-eo','pgid=,stat='],text=True,timeout=timeout)
    return any(int(parts[0])==pid and not parts[1].startswith('Z') for line in listing.splitlines() if len(parts:=line.split())==2)

def execute(command, output, name, env, timeout=90, cwd=None):
    host=dict(command=command,status='not-started',exitCode=None,pid=None,processGroupQuiescent=False,timedOut=False,descendantLeak=False,startedUnixNs=time.time_ns())
    process=None;start=time.monotonic()
    try:
        with (output/f'{name}.log').open('x') as log:
            process=subprocess.Popen(command,cwd=output if cwd is None else cwd,env=env,stdout=log,stderr=subprocess.STDOUT,start_new_session=True)
            host['pid']=process.pid
            try:
                code=process.wait(timeout=timeout)
                host.update(exitCode=code,status='success' if code==0 else 'failed')
            except subprocess.TimeoutExpired:
                host.update(status='failed',timedOut=True)
    except Exception as error:
        host.update(status='failed',error=repr(error))
    finally:
        if process is not None:
            try:
                alive=group_alive(process.pid)
                if alive or process.poll() is None:
                    host.update(status='failed',descendantLeak=not host['timedOut'])
                    try:os.killpg(process.pid,signal.SIGKILL)
                    except ProcessLookupError:pass
                    host['exitCode']=process.wait(timeout=5)
                    until=time.monotonic()+5
                    while True:
                        remaining=until-time.monotonic()
                        if remaining<=0:raise TimeoutError('Process group remained nonquiescent past cleanup deadline')
                        if not group_alive(process.pid,timeout=min(5,remaining)):break
                        time.sleep(min(.05,max(0,until-time.monotonic())))
                host['processGroupQuiescent']=True
            except Exception as error:
                host.update(status='failed',processGroupQuiescent=False,cleanupError=repr(error))
                # Best effort only: failure is permanent even if the final kill succeeds.
                try:os.killpg(process.pid,signal.SIGKILL)
                except OSError:pass
                try:host['exitCode']=process.wait(timeout=5)
                except Exception as wait_error:host['cleanupWaitError']=repr(wait_error)
        host.update(endedUnixNs=time.time_ns(),wallSeconds=time.monotonic()-start)
        (output/f'{name}.host.json').write_text(json.dumps(host,indent=2)+'\n')
    return host
