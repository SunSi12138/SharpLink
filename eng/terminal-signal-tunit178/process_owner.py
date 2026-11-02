"""Linux-only finite command owner for the isolated main terminal-signal job.

Raw stdout/stderr stay in the runner's private temporary directory. No uploader.
A deadline is a failure, never evidence that the process or its children joined.
"""
import ctypes
import json
import os
from pathlib import Path
import signal
import subprocess
import time


class CommandFailure(RuntimeError):
    """Keep original command/cleanup outcomes even when their receipt cannot be written."""
    def __init__(self, outcome, causes):
        self.outcome = outcome
        self.causes = tuple(causes)
        super().__init__(outcome['stage'] + ' failed; exit=' + str(outcome['exitCode']) +
                         ', timeout=' + str(outcome['timedOut']) +
                         ', executeErrors=' + repr(outcome['errors']) +
                         ', cleanupErrors=' + repr(outcome['cleanupErrors']) +
                         ', reportingErrors=' + repr(outcome['reportingErrors']) +
                         ', remainingOwned=' + repr(outcome.get('remainingOwned')))


def process_table():
    result = {}
    for path in Path('/proc').iterdir():
        if not path.name.isdigit():
            continue
        try:
            fields = (path / 'stat').read_text().rsplit(')', 1)[1].split()
            result[int(path.name)] = (int(fields[1]), int(fields[2]), fields[19], fields[0])
        except (FileNotFoundError, ProcessLookupError, PermissionError):
            pass
    return result


def enable_subreaper():
    if ctypes.CDLL(None, use_errno=True).prctl(36, 1, 0, 0, 0) != 0:
        raise OSError(ctypes.get_errno(), 'Cannot establish child process ownership')


def run_owned(label, argv, cwd, env, logs, seconds, expected_exit=0):
    """Exit is only a transport predicate; exact test qualification is separate."""
    stdout, stderr = logs / (label + '.stdout'), logs / (label + '.stderr')
    row = dict(stage=label, argv=[str(x) for x in argv], timeoutSeconds=seconds,
               exitCode=None, expectedExit=expected_exit, timedOut=False, errors=[], cleanupErrors=[], reportingErrors=[])
    causes = []
    owned = {}
    process = None
    started = time.monotonic()

    def census():
        table = process_table()
        selected = {pid for pid, start in owned.items()
                    if pid in table and table[pid][2] == start}
        # This supervisor has no other subprocesses: adopted children are ours.
        selected.update(pid for pid, entry in table.items() if entry[0] == os.getpid())
        if process is not None:
            selected.update(pid for pid, entry in table.items() if entry[1] == process.pid)
        while True:
            before = len(selected)
            selected.update(pid for pid, entry in table.items() if entry[0] in selected)
            if len(selected) == before:
                break
        for pid in selected:
            owned[pid] = table[pid][2]
        return {pid: table[pid] for pid in selected}

    def reap():
        if process is not None:
            process.poll()
        for pid in list(census()):
            if process is not None and pid == process.pid:
                continue
            try:
                os.waitpid(pid, os.WNOHANG)
            except (ChildProcessError, ProcessLookupError):
                pass

    def signal_owned(sig):
        for pid, entry in census().items():
            if entry[3] == 'Z':
                continue
            try:
                current = process_table().get(pid)
                if current is not None and current[2] == entry[2]:
                    os.kill(pid, sig)
            except ProcessLookupError:
                pass
            except BaseException as error:
                causes.append(error)
                row['cleanupErrors'].append(type(error).__name__ + ': ' + str(error))

    old_handlers = {}
    def interrupted(signum, _frame):
        raise InterruptedError('Supervisor received signal ' + str(signum))
    try:
        for sig in (signal.SIGTERM, signal.SIGINT):
            old_handlers[sig] = signal.signal(sig, interrupted)
        with stdout.open('wb') as output, stderr.open('wb') as errors:
            process = subprocess.Popen(argv, cwd=cwd, env=env, stdout=output,
                                       stderr=errors, start_new_session=True)
            while True:
                census()
                code = process.poll()
                if code is not None:
                    row['exitCode'] = code
                    break
                if time.monotonic() - started >= seconds:
                    row['timedOut'] = True
                    raise TimeoutError(label + ' exceeded its process deadline')
                time.sleep(.05)
    except BaseException as error:
        causes.append(error)
        row['errors'].append(type(error).__name__ + ': ' + str(error))
    finally:
        # Continue cleanup after an interrupt; do not let a second SIGTERM skip it.
        for sig in old_handlers:
            signal.signal(sig, signal.SIG_IGN)
        try:
            reap()
            left = census()
            if left:
                row['cleanupErrors'].append('Owned processes remained after command exit/deadline')
                for sig, budget in ((signal.SIGTERM, 5), (signal.SIGKILL, 10)):
                    signal_owned(sig)
                    until = time.monotonic() + budget
                    while census() and time.monotonic() < until:
                        reap()
                        time.sleep(.05)
            reap()
            row['remainingOwned'] = census()
        except BaseException as error:
            causes.append(error)
            row['cleanupErrors'].append(type(error).__name__ + ': ' + str(error))
        if process is not None:
            try:
                row['exitCode'] = process.wait(timeout=1)
            except BaseException as error:
                causes.append(error)
                row['cleanupErrors'].append(type(error).__name__ + ': ' + str(error))
        for sig, handler in old_handlers.items():
            signal.signal(sig, handler)
        row['elapsedSeconds'] = round(time.monotonic() - started, 3)
        try:
            (logs / (label + '.json')).write_text(json.dumps(row, indent=2) + '\n')
        except BaseException as error:
            causes.append(error)
            row['reportingErrors'].append(type(error).__name__ + ': ' + str(error))
    if (row['exitCode'] != expected_exit or row['timedOut'] or row['errors'] or
            row['cleanupErrors'] or row['reportingErrors'] or row.get('remainingOwned')):
        failure = CommandFailure(row, causes)
        if causes:
            raise failure from causes[0]
        raise failure
    return stdout, stderr
