import json, os, signal, subprocess, time

# Local base preserves the reviewed helper exception hierarchy.
GateError = ValueError

# Process isolation helpers from reviewed cleanup tools f719b25f33cea75be36dd7a1eb3e10b8b42b79a9.
# Hardened: outcome-write failure after a cleaned timeout/leak must also remain fatal.
class UnsafeProcessGroup(GateError):
    pass


def group_alive(pid):
    listing = subprocess.check_output(['ps', '-eo', 'pgid=,stat='], text=True)
    return any(int(parts[0]) == pid and not parts[1].startswith('Z')
               for line in listing.splitlines() if len(parts := line.split()) == 2)


def execute(command, cwd, env, timeout, output):
    # Once Popen succeeds, EVERY failure to establish quiescence is fatal to the
    # whole campaign, not a normal failed stage. Retain monitoring/cleanup errors
    # locally too; a missing ps result is not evidence that descendants exited.
    process = None
    status = dict(exit=None, timed_out=False, descendantLeak=None,
                  processGroupQuiescent=False, command=list(map(str, command)))
    unsafe_error = None
    try:
        with output.open('xb') as stream:
            process = subprocess.Popen(status['command'], cwd=cwd, env=env, stdout=stream,
                stderr=subprocess.STDOUT, start_new_session=True)
            try:
                status['exit'] = process.wait(timeout=timeout)
            except subprocess.TimeoutExpired:
                status['timed_out'] = True
                os.killpg(process.pid, signal.SIGKILL)
                status['exit'] = process.wait()
            status['descendantLeak'] = group_alive(process.pid)
            if status['descendantLeak']:
                os.killpg(process.pid, signal.SIGKILL)
                until = time.monotonic() + 5
                while group_alive(process.pid) and time.monotonic() < until:
                    time.sleep(.05)
            status['processGroupQuiescent'] = not group_alive(process.pid)
    except Exception as error:
        if process is None:
            raise
        unsafe_error = error
        status['monitorError'] = dict(type=type(error).__name__, detail=str(error))
    finally:
        if process is not None:
            try:
                with output.with_suffix(output.suffix + '.status.json').open('x') as record:
                    record.write(json.dumps(status, indent=2) + '\n')
            except Exception as error:
                # A status-write error must not downgrade an unsafe group to a
                # recoverable OSError that lets the next stage launch.
                if (unsafe_error is not None or not status['processGroupQuiescent'] or
                        status['timed_out'] or status['descendantLeak']):
                    raise UnsafeProcessGroup('unsafe process group; status could not be retained') from error
                raise
    if unsafe_error is not None:
        raise UnsafeProcessGroup('unable to prove test process group quiescence') from unsafe_error
    if not status['processGroupQuiescent']:
        raise UnsafeProcessGroup('test process group remains unsafe')
    return (status['exit'] if not status['descendantLeak'] else -1), status['timed_out']
