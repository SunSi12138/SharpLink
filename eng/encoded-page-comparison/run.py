#!/usr/bin/env python3
"""One finite 8+8+32 process plan. No builds, uploads, substitutions or retries."""
import argparse
import datetime
import os
from pathlib import Path
import signal
import subprocess
import time
from common import strict_json, ARMS, CONTRACT, ROOT, inventory, schedule, gate_schedule, select_cpus, source_inventory, write
from validate import validate_sample, validate_unit_gate_record
from identity import framework_policy, archive_framework_policy


def observation():
    paths = ['/proc/stat', '/proc/loadavg', '/proc/self/status', '/proc/self/cgroup',
             '/sys/fs/cgroup/cpu.max', '/sys/fs/cgroup/cpu.stat', '/sys/fs/cgroup/cpuset.cpus.effective']
    return {'utc': datetime.datetime.now(datetime.timezone.utc).isoformat(),
            'files': {p: Path(p).read_text() for p in paths if Path(p).is_file()}}


def launch_plan():
    result = []
    for phase in CONTRACT['phaseSchema']:
        for slot in schedule() if phase == 'normal-measure' else gate_schedule(phase):
            result.append(dict(slot, phase=phase, launchIndex=len(result)))
    if len(result) != 48:
        raise ValueError('The frozen campaign must have exactly 48 planned processes')
    return result


def command_for(slot, prepared, runtime, output, affinity):
    phase = slot['phase']
    directory = output / {'strict-cleanup': 'strict', 'normal-validate': 'normal', 'normal-measure': 'runs'}[phase]
    timed = phase == 'normal-measure'
    command = ['taskset', '-c', ','.join(map(str, affinity)), runtime,
               prepared / slot['arm'] / 'PhysicalTcpProbe.dll', slot['mode'], slot['items'], 1,
               3 if timed else 0, 6 if timed else 0, directory / f"{slot['id']}.json", phase]
    if timed:
        command.append(output / 'ticks' / f"{slot['id']}.json")
    return list(map(str, command)), directory


def group_alive(pid):
    listing = subprocess.check_output(['ps', '-eo', 'pgid=,stat='], text=True)
    return any(int(parts[0]) == pid and not parts[1].startswith('Z')
               for line in listing.splitlines() if len(parts := line.split()) == 2)


def execute(command, root, slot, env):
    name = slot['id']
    host = {'command': command, 'slotId': name, 'phase': slot['phase'],
            'launchIndex': slot['launchIndex'], 'before': observation(),
            'status': 'not-started', 'exitCode': None, 'pid': None, 'processGroupQuiescent': False}
    start = time.monotonic()
    process = None
    try:
        with (root / f'{name}.log').open('x') as stream:
            process = subprocess.Popen(command, stdout=stream, stderr=subprocess.STDOUT,
                                       env=env, start_new_session=True)
            host['pid'] = process.pid
            try:
                code = process.wait(timeout=CONTRACT['processTimeoutSeconds'])
                host.update(exitCode=code, status='success' if code == 0 else 'failed')
            except subprocess.TimeoutExpired:
                host.update(status='failed', timedOut=True)
                os.killpg(process.pid, signal.SIGKILL)
                host['exitCode'] = process.wait()
    except OSError as error:
        host.update(status='failed', error=repr(error))
    finally:
        if process is not None:
            # A leaked descendant cannot contaminate another slot. A nonempty group is a
            # failed slot even if its parent returned zero; never silently repair its result.
            if group_alive(process.pid):
                host.update(status='failed', descendantLeak=True)
                os.killpg(process.pid, signal.SIGKILL)
                until = time.monotonic() + 5
                while group_alive(process.pid) and time.monotonic() < until:
                    time.sleep(0.05)
            host['processGroupQuiescent'] = not group_alive(process.pid)
        host.update(after=observation(), wallSeconds=time.monotonic() - start)
        write(root / f'{name}.host.json', host)
    return host


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--prepared', type=Path, required=True)
    p.add_argument('--runtime-dotnet', type=Path, required=True)
    p.add_argument('--output', type=Path, required=True)
    p.add_argument('--reviewed-tool-sha', required=True)
    p.add_argument('--quiet-window-record', type=Path, required=True)
    p.add_argument('--runtime-archives', type=Path, required=True)
    p.add_argument('--unit-gate-record', type=Path, required=True)
    a = p.parse_args()
    sha = subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=ROOT, text=True).strip()
    dirty = subprocess.check_output(['git', 'status', '--porcelain'], cwd=ROOT, text=True).strip()
    if sha != a.reviewed_tool_sha or dirty:
        raise ValueError('Tool HEAD must be reviewed exact clean commit')
    prepared = a.prepared.resolve(strict=True)
    manifest = strict_json((prepared / 'prepared.json').read_text())
    if (manifest['contract'] != CONTRACT or manifest['toolRevision'] != sha
            or manifest['toolSourceHashes'] != source_inventory(ROOT)
            or manifest['fixturePurpose'] != 'encoded-page-comparison'
            or manifest['fixtureSourceHashes'] != source_inventory(ROOT / 'fixture')):
        raise ValueError('Prepared contract, fixture or reviewed tool identity differs')
    unit_gate_record = validate_unit_gate_record(strict_json(a.unit_gate_record.read_text()), manifest['toolRevision'])
    for arm in ARMS:
        if inventory(prepared / arm) != manifest['arms'][arm]['snapshot']:
            raise ValueError(f'Prepared snapshot changed: {arm}')
    runtime = a.runtime_dotnet.resolve(strict=True)
    listing = subprocess.check_output([runtime, '--list-runtimes'], text=True)
    expected = [f'{name} 10.0.14 [{runtime.parent / "shared" / name}]'
                for name in ('Microsoft.AspNetCore.App', 'Microsoft.NETCore.App')]
    if sorted(listing.splitlines()) != expected:
        raise ValueError('Runtime installation must contain exactly both private 10.0.14 frameworks')
    if subprocess.check_output([runtime, '--list-sdks'], text=True).strip():
        raise ValueError('Use runtime-only private installation')
    # Build-copy12 is not the runtime-loaded dependency closure: AspNet14 wins
    # host version resolution. Verify the exact fixed shared-framework identities
    # before any process, without relaxing own-arm product/fixture provenance.
    dependency_policy = framework_policy(runtime)
    archive_policy = archive_framework_policy(runtime, a.runtime_archives.resolve(strict=True))
    os_release = Path('/etc/os-release').read_text()
    if 'ID=ubuntu' not in os_release or 'VERSION_ID="24.04"' not in os_release or os.environ.get('GITHUB_ACTIONS') != 'true':
        raise ValueError('This protocol requires hosted ubuntu24.04; no local substitution')
    mapping = subprocess.check_output(['lscpu', '-p=CPU,CORE,SOCKET,ONLINE'], text=True)
    affinity = select_cpus(mapping, os.sched_getaffinity(0))
    quiet = strict_json(a.quiet_window_record.read_text())
    if quiet.get('noOtherClrBuildTest') is not True or quiet.get('reviewedToolCommit') != sha or quiet.get('campaign') != CONTRACT['campaign']:
        raise ValueError('Same reviewed campaign quiet-window record required')
    output = a.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    plan = launch_plan()
    write(output / 'unit-gate.json', unit_gate_record)
    write(output / 'quiet-window.json', quiet)
    write(output / 'schedule.json', schedule())
    write(output / 'phase-schedule.json', plan)
    write(output / 'prepared.json', manifest)
    write(output / 'framework-policy.json', dependency_policy)
    write(output / 'framework-archive-policy.json', archive_policy)
    write(output / 'before.json', observation())
    write(output / 'topology-selection.json', {'mapping': mapping, 'allowed': sorted(os.sched_getaffinity(0)), 'selected': affinity, 'claim': 'guest topology only; physical isolation unknown'})
    env = {k: v for k, v in os.environ.items() if not k.startswith(('DOTNET_', 'COMPlus_', 'GITHUB_', 'TUNIT_', 'TESTINGPLATFORM_'))}
    env.update(DOTNET_ROOT=str(runtime.parent), DOTNET_ROOT_X64=str(runtime.parent),
               DOTNET_HOST_PATH=str(runtime), DOTNET_MULTILEVEL_LOOKUP='0', DOTNET_ROLL_FORWARD='Disable')
    for directory in ('strict', 'normal', 'runs', 'ticks'):
        (output / directory).mkdir()
    failures = []
    for phase in CONTRACT['phaseSchema']:
        phase_failures = []
        if phase == 'normal-measure' and failures:
            write(output / 'not-attempted.json', [s for s in plan if s['phase'] == phase])
            print('GATES_FAILED strict/normal; timed processes launched=0 of 32; no target conclusion')
            raise SystemExit(1)
        for slot in (s for s in plan if s['phase'] == phase):
            command, directory = command_for(slot, prepared, runtime, output, affinity)
            host = execute(command, directory, slot, dict(env, SHARPLINK_BENCHMARK_SHA=CONTRACT[f"{slot['arm']}Source"]))
            try:
                if host['status'] != 'success' or not host['processGroupQuiescent']:
                    raise ValueError('Process failed, timed out or retained a descendant')
                validate_sample(strict_json((directory / f"{slot['id']}.json").read_text()),
                                dict(slot, affinity=affinity), prepared, manifest, runtime, phase=phase,
                                framework_policy=dependency_policy, archive_policy=archive_policy)
            except (OSError, ValueError, KeyError, TypeError) as error:
                phase_failures.append({'slot': slot['id'], 'phase': phase, 'error': str(error)})
            write(output / 'failures.json', failures + phase_failures)
            if not host['processGroupQuiescent']:
                write(output / 'not-attempted.json', [s for s in plan if s['launchIndex'] > slot['launchIndex']])
                print(f"PROCESS_GROUP_UNSAFE phase={phase} slot={slot['id']}; remaining plan not attempted")
                raise SystemExit(1)
        failures.extend(phase_failures)
        if phase != 'normal-measure':
            name = 'strict' if phase == 'strict-cleanup' else 'normal'
            write(output / f'{name}-gate.json', {'status': 'Failed' if phase_failures else 'Passed', 'failures': phase_failures})
        print(f'PHASE {phase} attempted={sum(s["phase"] == phase for s in plan)} failures={len(phase_failures)}')
    write(output / 'after.json', observation())
    print('Declared plan exhausted once; offline validation required; incremental candidate/base only; no original official-version target or release acceptance claim')
    if failures:
        raise SystemExit(1)


if __name__ == '__main__':
    main()
