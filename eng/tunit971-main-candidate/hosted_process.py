"""Standard hosted job adapter, NOT Linux owner/descendant-join equivalence.
Only direct-process terminal is proved. Timeout/cancellation always fail; bounded
best-effort group/tree cleanup never qualifies a testcase. Raw files stay private.
"""
import json
import os
from pathlib import Path
import re
import signal
import stat
import subprocess
import sys
import time
import traceback

QUALIFICATION='GitHub-hosted standard job lifecycle; no independent descendant-join proof'


class HostedCommandFailure(Exception):
    def __init__(self,outcome,causes):
        super().__init__('Hosted command did not qualify')
        self.outcome=outcome;self.causes=causes


class CancelledCommand(Exception):pass
class CommandDeadline(Exception):pass


def private_graph(error,seen=None,formatting_errors=None):
    seen={} if seen is None else seen
    formatting_errors=[] if formatting_errors is None else formatting_errors
    if id(error) in seen:return {'ref':seen[id(error)]}
    identity=len(seen);seen[id(error)]=identity
    result={'id':identity,'type':type(error).__module__+'.'+type(error).__qualname__}
    try:result['message']=str(error)
    except BaseException as failure:formatting_errors.append(failure);result['messageUnavailable']=True
    try:result['traceback']=''.join(traceback.format_exception(type(error),error,error.__traceback__,chain=False))
    except BaseException as failure:formatting_errors.append(failure);result['tracebackUnavailable']=True
    if isinstance(error,BaseExceptionGroup):result['exceptions']=[private_graph(e,seen,formatting_errors) for e in error.exceptions]
    if isinstance(error,HostedCommandFailure):result['commandOutcome']=error.outcome;result['commandCauses']=[private_graph(e,seen,formatting_errors) for e in error.causes]
    if error.__cause__ is not None:result['cause']=private_graph(error.__cause__,seen,formatting_errors)
    if error.__context__ is not None:result['context']=private_graph(error.__context__,seen,formatting_errors)
    return result


def ordinary_directory(path):
    for item in [*reversed(path.parents),path]:
        info=item.lstat()
        if not stat.S_ISDIR(info.st_mode) or getattr(info,'st_file_attributes',0)&0x400:
            raise ValueError('Hosted directory is symlink/reparse/special')


def run(argv,*,cwd,output,label,timeout,expected_exit,env=None,platform=None):
    """No shell. Caller freezes argv and boundaries; this never prints argv/env/raw."""
    platform=sys.platform if platform is None else platform
    if platform not in ('win32','darwin'):raise ValueError('Hosted adapter supports only selected Windows/macOS jobs')
    if type(timeout) is not int or not 1<=timeout<=600:raise ValueError('Unsupported hosted command deadline')
    if type(expected_exit) is not int or expected_exit not in (0,2):raise ValueError('Unsupported hosted expected exit')
    if not re.fullmatch(r'[a-z0-9][a-z0-9-]{0,79}',label):raise ValueError('Unsupported hosted command label')
    if not argv or any(not isinstance(x,str) or '\0' in x for x in argv):raise ValueError('Invalid hosted argv')
    output=Path(output).absolute();ordinary_directory(output.parent);output.mkdir(parents=False,exist_ok=False)
    ordinary_directory(output);directory_identity=(output.stat().st_dev,output.stat().st_ino)
    outcome={'label':label,'expectedExit':expected_exit,'exitCode':None,'timedOut':False,'cancelled':False,
             'directProcessTerminal':False,'cleanupAttempted':False,'descendantJoinProven':False,
             'ownerQualification':QUALIFICATION,'cleanupErrors':[],'reportingErrors':[]}
    process=None;errors=[];old_handlers={};cancelled=[];capture_identities={};report=None;report_identity=None;start=time.monotonic()
    def on_signal(number,frame):cancelled.append(number)
    def append_cleanup(action):
        try:action()
        except BaseException as error:errors.append(error);outcome['cleanupErrors'].append(type(error).__name__)
    def safe_poll():
        try:return process.poll()
        except BaseException as error:
            errors.append(error);outcome['cleanupErrors'].append(type(error).__name__);return None
    def kill_tree():
        if platform=='darwin':
            try:os.killpg(process.pid,signal.SIGTERM)
            except ProcessLookupError:pass
            try:
                process.wait(timeout=3)
                return  # Do not target a group ID after reaping its original primary.
            except subprocess.TimeoutExpired:pass
            # The direct child remains unreaped here; group cleanup is best effort,
            # not a claim about descendants that changed sessions/groups.
            try:os.killpg(process.pid,signal.SIGKILL)
            except ProcessLookupError:pass
        else:
            # Local-only PID tree operation; never wildcard/image/remote target.
            # Bounded subprocess.run kills and waits taskkill itself on timeout.
            system=Path(os.environ['SystemRoot'])/'System32/taskkill.exe'
            result=subprocess.run([str(system),'/PID',str(process.pid),'/T','/F'],stdin=subprocess.DEVNULL,
                                  stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL,timeout=5,check=False)
            if result.returncode!=0:raise RuntimeError('Windows best-effort taskkill failed')
    try:
        # Retain the originally created file handle before any child starts.
        # Never reopen a receipt path after child execution.
        report=(output/'COMMAND.json').open('x')
        info=os.fstat(report.fileno());report_identity=(info.st_dev,info.st_ino)
        for name in ('SIGINT','SIGTERM','SIGBREAK'):
            number=getattr(signal,name,None)
            if number is not None and number not in old_handlers:
                old_handlers[number]=signal.getsignal(number);signal.signal(number,on_signal)
        with (output/(label+'.stdout')).open('xb') as stdout,(output/(label+'.stderr')).open('xb') as stderr:
            for name,stream in [('stdout',stdout),('stderr',stderr)]:
                info=os.fstat(stream.fileno());capture_identities[name]=(info.st_dev,info.st_ino)
            options={'start_new_session':True} if platform=='darwin' else {'creationflags':getattr(subprocess,'CREATE_NEW_PROCESS_GROUP',512)}
            process=subprocess.Popen(argv,cwd=cwd,env=env,stdin=subprocess.DEVNULL,stdout=stdout,stderr=stderr,shell=False,**options)
            while True:
                if cancelled:outcome['cancelled']=True;raise CancelledCommand('Hosted command cancellation')
                code=process.poll()
                if code is not None:break
                if time.monotonic()-start>=timeout:outcome['timedOut']=True;raise CommandDeadline('Hosted command timeout')
                time.sleep(.05)
            outcome['exitCode']=process.wait(timeout=1);outcome['directProcessTerminal']=True
            if outcome['exitCode']!=expected_exit:raise ValueError('Unexpected hosted command exit')
    except BaseException as error:errors.append(error)
    finally:
        if process is not None and safe_poll() is None:
            outcome['cleanupAttempted']=True
            append_cleanup(kill_tree)
            if safe_poll() is None:append_cleanup(process.kill)
            append_cleanup(lambda:process.wait(timeout=10))
        if process is not None:
            outcome['exitCode']=safe_poll();outcome['directProcessTerminal']=outcome['exitCode'] is not None
        for number,handler in old_handlers.items():append_cleanup(lambda n=number,h=handler:signal.signal(n,h))
        # Re-sample AFTER every restoration: a signal delivered while restoring
        # an earlier handler must not disappear at the terminal boundary.
        outcome['cancelled']=outcome['cancelled'] or bool(cancelled)
        if outcome['cancelled'] and not any(isinstance(e,CancelledCommand) for e in errors):errors.append(CancelledCommand('Hosted cancellation at terminal boundary'))
        outcome['seconds']=round(time.monotonic()-start,3)
        if process is not None and not outcome['directProcessTerminal']:errors.append(RuntimeError('Direct process did not terminate; job must fail'))
        guard_ok=False
        try:
            ordinary_directory(output)
            if (output.stat().st_dev,output.stat().st_ino)!=directory_identity:raise ValueError('Hosted command directory identity changed')
            for name,identity in capture_identities.items():
                info=(output/(label+'.'+name)).lstat()
                if not stat.S_ISREG(info.st_mode) or getattr(info,'st_file_attributes',0)&0x400 or (info.st_dev,info.st_ino)!=identity:
                    raise ValueError('Hosted capture file identity changed')
            if report_identity is not None:
                info=(output/'COMMAND.json').lstat()
                if not stat.S_ISREG(info.st_mode) or getattr(info,'st_file_attributes',0)&0x400 or (info.st_dev,info.st_ino)!=report_identity:
                    raise ValueError('Hosted receipt file identity changed')
            guard_ok=True
        except BaseException as guard:errors.append(guard)
        if guard_ok and report is not None:
            try:
                formatting=[];graph=[private_graph(e,formatting_errors=formatting) for e in errors]
                errors.extend(formatting);outcome['reportingErrors'].extend(type(e).__name__ for e in formatting)
                private={'outcome':outcome,'argv':argv,'errors':graph,'formatterErrors':[private_graph(e) for e in formatting]}
                report.write(json.dumps(private,indent=2)+'\n');report.flush()
            except BaseException as reporting:
                errors.append(reporting);outcome['reportingErrors'].append(type(reporting).__name__)
        # Rejected paths are never opened/written. Caller must persist this full
        # HostedCommandFailure graph to its separately guarded top-level sink.
        if report is not None:append_cleanup(report.close)
    if errors:raise HostedCommandFailure(outcome,errors)
    return outcome
