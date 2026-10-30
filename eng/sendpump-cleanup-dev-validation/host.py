"""Pinned host/runtime and process helpers reused from 7e2be45e7 unit tooling."""
import json
import os
from pathlib import Path
import signal
import subprocess
import time
from validate import GateError, require, strict_json
PROJECT = 'test/SharpLink.UnitTests/SharpLink.UnitTests.csproj'
ASSEMBLY = 'test/SharpLink.UnitTests/bin/Release/net10.0/SharpLink.UnitTests.dll'
RUNTIME_VERSION = '10.0.14'
def require_pinned_sdk(root: Path, env: dict) -> str:
    sdk = subprocess.check_output([env.get('SDK_DOTNET', 'dotnet'), '--version'], cwd=root, env=env, text=True).strip()
    # Keep the full value for the gate; truncate only its ordinary diagnostic log.
    actual = sdk[:160]
    print('SDK_SELECTION ' + json.dumps({'actual': actual, 'expected': '10.0.102',
          'truncated': len(sdk) > len(actual)}, sort_keys=True), flush=True)
    require(sdk == '10.0.102',
            f'unexpected SDK: actual={json.dumps(actual)} expected=10.0.102')
    return sdk

def verify_hosts(root: Path, env: dict) -> tuple[str, str]:
    sdk_host = env.get('SDK_DOTNET', '')
    runtime_root = Path(env.get('PRIVATE_RUNTIME', ''))
    require(Path(sdk_host).is_absolute() and Path(sdk_host).is_file(), 'missing absolute SDK host')
    require(runtime_root.is_absolute() and (runtime_root / 'dotnet').is_file(), 'missing absolute private runtime host')
    require(Path(sdk_host).parent.resolve() != runtime_root.resolve(), 'build and test hosts must be separate')
    require_pinned_sdk(root, env)
    sdk_root = Path(sdk_host).parent
    sdk_lines = subprocess.check_output([sdk_host, '--list-sdks'], env=env, text=True).splitlines()
    require(sdk_lines == [f'10.0.102 [{sdk_root / "sdk"}]'], 'unexpected build SDK inventory')
    sdk_runtime_lines = subprocess.check_output([sdk_host, '--list-runtimes'], env=env, text=True).splitlines()
    expected_build = [f'{name} 10.0.2 [{sdk_root / "shared" / name}]'
                      for name in ('Microsoft.AspNetCore.App', 'Microsoft.NETCore.App')]
    require(sorted(sdk_runtime_lines) == expected_build, 'unexpected bundled build-runtime inventory')
    test_host = str(runtime_root / 'dotnet')
    require(not subprocess.check_output([test_host, '--list-sdks'], env=env, text=True).strip(),
            'private test runtime must not contain an SDK')
    runtime_lines = subprocess.check_output([test_host, '--list-runtimes'], env=env, text=True).splitlines()
    expected_test = [f'{name} {RUNTIME_VERSION} [{runtime_root / "shared" / name}]'
                     for name in ('Microsoft.AspNetCore.App', 'Microsoft.NETCore.App')]
    require(sorted(runtime_lines) == expected_test, 'unexpected private test-runtime inventory')
    print('HOSTS ' + json.dumps({'build_sdk': '10.0.102', 'sdk_bundled_runtime': '10.0.2',
          'test_coreclr': RUNTIME_VERSION, 'test_aspnetcore': RUNTIME_VERSION,
          'sdk_host': sdk_host, 'test_host': test_host}, sort_keys=True), flush=True)
    return sdk_host, test_host

def private_test_environment(env: dict, test_host: str) -> dict:
    result = {key: value for key, value in env.items() if not key.startswith('GITHUB_')}
    root = str(Path(test_host).parent)
    result.update({'CI': 'true', 'DOTNET_ROOT': root, 'DOTNET_ROOT_X64': root,
                   'DOTNET_HOST_PATH': test_host, 'PATH': root + os.pathsep + env.get('PATH', '')})
    return result

def require_test_runtime(result: dict) -> str:
    actual = result.get('runtimeVersion')
    print('RUNTIME_SELECTION ' + json.dumps({'actual': str(actual)[:80],
          'expected': '.NET ' + RUNTIME_VERSION}, sort_keys=True), flush=True)
    require(actual == '.NET ' + RUNTIME_VERSION,
            f'actual test CoreCLR must be exactly .NET {RUNTIME_VERSION}')
    return actual

def verify_allocation_child_runtimes(report: dict) -> list[dict]:
    scenarios = ('SuccessfulSuspensionsShouldNotAllocateAfterWarmup',
                 'RegisteredConsumerSuspensionsShouldNotAllocateAfterWarmup')
    expected = {f'SharpLink.UnitTests.Runtime.ReadOwnershipPipeReaderLifetimeTests.1.1.{scenario}'
                '(System.Threading.CancellationToken).1.1.0': scenario for scenario in scenarios}
    marker = 'READ_OWNERSHIP_ALLOCATION_VERIFIED|'
    records = {}
    for group in report.get('groups', []):
        for test in group.get('tests', []):
            output = test.get('output', '')
            require(isinstance(output, str), 'invalid child output field')
            for line in output.splitlines():
                if not line.startswith(marker):
                    continue
                uid = test.get('id')
                require(uid in expected and uid not in records, 'unexpected or duplicate child-runtime marker')
                require(test.get('status') == 'passed', 'child-runtime witness test did not pass')
                record = strict_json(line[len(marker):])
                require(isinstance(record, dict) and record.get('scenario') == expected[uid],
                        'child-runtime scenario mismatch')
                require(record.get('runtime') == RUNTIME_VERSION and record.get('architecture') == 'X64'
                        and type(record.get('executed')) is int and record['executed'] == 1
                        and record.get('passed') is True, 'allocation child runtime/execution mismatch')
                records[uid] = {key: record[key] for key in ('scenario', 'runtime', 'architecture', 'executed')}
    require(set(records) == set(expected), 'missing required allocation child-runtime witness')
    return [records[uid] for uid in sorted(records)]

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
                if unsafe_error is not None or not status['processGroupQuiescent']:
                    raise UnsafeProcessGroup('unsafe process group; status could not be retained') from error
                raise
    if unsafe_error is not None:
        raise UnsafeProcessGroup('unable to prove test process group quiescence') from unsafe_error
    if not status['processGroupQuiescent']:
        raise UnsafeProcessGroup('test process group remains unsafe')
    return (status['exit'] if not status['descendantLeak'] else -1), status['timed_out']
