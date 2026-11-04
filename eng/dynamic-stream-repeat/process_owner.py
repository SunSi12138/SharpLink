"""Dedicated Linux supervisor. Timeout is failure, never evidence of join."""
import ctypes
import json
import os
from pathlib import Path
import signal
import select
import stat
import subprocess
import time


class CommandFailure(RuntimeError):
    def __init__(self, outcome, causes):
        self.outcome, self.causes = outcome, tuple(causes)
        super().__init__('Owned command failed')


def require(ok, reason):
    if not ok:
        raise ValueError(reason)


def process_table():
    result = {}
    for p in Path('/proc').iterdir():
        if p.name.isdigit():
            try:
                s = (p / 'stat').read_text().rsplit(')', 1)[1].split()
                result[int(p.name)] = (int(s[1]), int(s[2]), s[19], s[0])
            except (FileNotFoundError, ProcessLookupError):
                pass
    return result


def enable_subreaper():
    # This dedicated supervisor owns all child waits. Do not inherit auto-reaping.
    require(signal.getsignal(signal.SIGCHLD)==signal.SIG_DFL,'Unexpected child signal disposition')
    signal.signal(signal.SIGCHLD,signal.SIG_DFL)
    require(hasattr(os, 'pidfd_open') and hasattr(signal, 'pidfd_send_signal'), 'Linux pidfd support required')
    if ctypes.CDLL(None, use_errno=True).prctl(36, 1, 0, 0, 0) != 0:
        raise OSError(ctypes.get_errno(), 'Cannot establish child process ownership')
    require(not any(v[0] == os.getpid() for v in process_table().values()), 'Supervisor already has unrelated children')


def directory_fd(path):
    fd = os.open(path, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
    try:
        require(stat.S_ISDIR(os.fstat(fd).st_mode), 'Output directory is not ordinary')
    except BaseException as original:
        try:os.close(fd)
        except BaseException as closing:raise BaseExceptionGroup('Directory validation and close failed',[original,closing])
        raise
    return fd


def open_exclusive(dfd, name):
    require('/' not in name and name not in ('.', '..'), 'Unsafe receipt name')
    return os.open(name, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW | os.O_NONBLOCK, 0o600, dir_fd=dfd)


def write_json_fd(fd, value):
    payload = (json.dumps(value, allow_nan=False, indent=2) + '\n').encode()
    position = 0
    while position < len(payload):
        n = os.write(fd, payload[position:])
        require(n > 0, 'Receipt write made no progress')
        position += n
    os.fsync(fd)


def pidfd_exited(fd):
    poller=select.poll();poller.register(fd,select.POLLIN)
    events=poller.poll(0)
    for _,flags in events:
        require(not flags & (select.POLLNVAL | select.POLLERR),'Invalid pidfd readiness')
    return bool(events)


def run_owned(label, argv, cwd, env, logs, seconds, pass_fds=()):
    require(seconds > 0, 'Nonpositive deadline')
    row = dict(stage=label, argv=[str(x) for x in argv], timeoutSeconds=seconds, exitCode=None,
               timedOut=False, cancelled=False, errors=[], cleanupErrors=[], reportingErrors=[], remainingOwned=None)
    causes=[]; process=None; owned={}; pidfds={}; opened=[]; handlers={}; cancellations=[]; cleaning=False
    start=time.monotonic(); dfd=None; receipt=None; identities={}
    def error(exc, kind):
        causes.append(exc);row[kind].append(type(exc).__name__)
    def census():
        table=process_table()
        selected={pid for pid, birth in owned.items() if pid in table and table[pid][2]==birth}
        selected.update(pid for pid, entry in table.items() if entry[0]==os.getpid())
        while True:
            before=len(selected)
            selected.update(pid for pid, entry in table.items() if entry[0] in selected)
            if len(selected)==before:break
        for pid in selected:
            birth=table[pid][2]
            if pid not in owned or owned[pid]!=birth:
                if pid in pidfds:
                    os.close(pidfds.pop(pid))
                try:
                    fd=os.pidfd_open(pid)
                except ProcessLookupError:
                    continue
                if process_table().get(pid, (None,None,None))[2]!=birth:
                    os.close(fd);continue
                owned[pid]=birth;pidfds[pid]=fd
        return {pid: entry for pid,entry in table.items() if owned.get(pid)==entry[2]}
    def reap_known():
        if process is not None:
            try:process.poll()
            except BaseException as exc:error(exc,'cleanupErrors')
        for pid in list(pidfds):
            if process is not None and pid==process.pid:continue
            try:os.waitpid(pid,os.WNOHANG)
            except (ChildProcessError,ProcessLookupError):pass
            except BaseException as exc:error(exc,'cleanupErrors')
    def terminate_known(sig):
        # Held pidfds name the original process even when /proc is unavailable.
        # A census failure must never skip attempts to stop those known processes.
        for fd in list(pidfds.values()):
            try:signal.pidfd_send_signal(fd,sig)
            except ProcessLookupError:pass
            except BaseException as exc:error(exc,'cleanupErrors')
        if process is not None and process.pid not in pidfds:
            try:process.send_signal(sig)
            except ProcessLookupError:pass
            except BaseException as exc:error(exc,'cleanupErrors')
    def known_alive():
        alive=False
        for fd in list(pidfds.values()):
            try:alive=not pidfd_exited(fd) or alive
            except BaseException as exc:
                error(exc,'cleanupErrors');alive=True
        return alive
    def interrupted(sig,_frame):
        cancellations.append(sig)
        if not cleaning:raise InterruptedError('Supervisor cancellation')
    try:
        dfd=directory_fd(logs)
        for name in ('stdout','stderr','COMMAND.json'):
            opened.append(open_exclusive(dfd,name))
        receipt=opened[2]
        identities={name:(os.fstat(fd).st_dev,os.fstat(fd).st_ino) for name,fd in zip(('stdout','stderr','COMMAND.json'),opened)}
        write_json_fd(open_exclusive_and_track(dfd,'START.json',opened),dict(stage=label,startedMonotonic=start))
        for sig in (signal.SIGTERM,signal.SIGINT):handlers[sig]=signal.signal(sig,interrupted)
        process=subprocess.Popen([str(x) for x in argv],cwd=cwd,env=env,stdin=subprocess.DEVNULL,
                                 stdout=opened[0],stderr=opened[1],start_new_session=True,pass_fds=pass_fds)
        if process.poll() is None:
            try:pidfds[process.pid]=os.pidfd_open(process.pid)
            except ProcessLookupError:
                if process.poll() is None:raise
        while True:
            census();code=process.poll()
            if code is not None:row['exitCode']=code;break
            if time.monotonic()-start>=seconds:
                row['timedOut']=True;raise TimeoutError('External process deadline')
            time.sleep(.05)
    except BaseException as exc:error(exc,'errors')
    finally:
        cleaning=True
        reap_known()
        census_failed=False
        try:active=census()
        except BaseException as exc:
            error(exc,'cleanupErrors');active=None;census_failed=True
        if active or active is None or known_alive():
            row['cleanupErrors'].append('OwnedProcessesAfterCommand')
            for sig,budget in ((signal.SIGTERM,5),(signal.SIGKILL,10)):
                until=time.monotonic()+budget
                while True:
                    terminate_known(sig);reap_known()
                    if not census_failed:
                        try:active=census()
                        except BaseException as exc:
                            error(exc,'cleanupErrors');active=None;census_failed=True
                    live=known_alive()
                    # Unknown descendants remain an error even if all known pidfds exited.
                    if not live and (census_failed or not active):break
                    if time.monotonic()>=until:break
                    time.sleep(.05)
        reap_known()
        try:row['remainingOwned']=census()
        except BaseException as exc:error(exc,'cleanupErrors');row['remainingOwned']=None
        if process is not None:
            try:row['exitCode']=process.wait(timeout=1)
            except BaseException as exc:error(exc,'cleanupErrors')
        for sig,old in handlers.items():
            try:signal.signal(sig,old)
            except BaseException as exc:error(exc,'cleanupErrors')
        row['cancelled']=bool(cancellations)
        if cancellations:row['errors'].append('CancelledCommand')
        for fd in pidfds.values():
            try:os.close(fd)
            except BaseException as exc:error(exc,'cleanupErrors')
        if dfd is not None:
            try:
                current=logs.lstat();original=os.fstat(dfd)
                require(stat.S_ISDIR(current.st_mode) and (current.st_dev,current.st_ino)==(original.st_dev,original.st_ino), 'Command directory replaced')
                for name,identity in identities.items():
                    current=os.stat(name,dir_fd=dfd,follow_symlinks=False)
                    require(stat.S_ISREG(current.st_mode) and current.st_nlink==1 and (current.st_dev,current.st_ino)==identity, 'Command output replaced')
            except BaseException as exc:error(exc,'reportingErrors')
        row['elapsedSeconds']=round(time.monotonic()-start,3)
        if receipt is not None:
            try:write_json_fd(receipt,row)
            except BaseException as exc:error(exc,'reportingErrors')
        for fd in reversed(opened):
            try:os.close(fd)
            except BaseException as exc:error(exc,'reportingErrors')
        if dfd is not None:
            try:os.close(dfd)
            except BaseException as exc:error(exc,'reportingErrors')
    if (row['exitCode']!=0 or row['timedOut'] or row['cancelled'] or row['errors'] or row['cleanupErrors'] or
            row['reportingErrors'] or type(row['remainingOwned']) is not dict or row['remainingOwned']):
        failure=CommandFailure(row,causes)
        if causes:raise failure from causes[0]
        raise failure
    return row


def open_exclusive_and_track(dfd,name,opened):
    fd=open_exclusive(dfd,name);opened.append(fd);return fd
