#!/usr/bin/env python3
"""Reviewed launch entry. Cold gates first; never builds and never substitutes failed slots."""
import argparse
import datetime
import json
import os
from pathlib import Path
import subprocess
import time
from common import strict_json, ARMS, CONTRACT, ROOT, digest, inventory, schedule, select_cpus, source_inventory, write
from validate import validate_sample


def observation():
    paths = ['/proc/stat', '/proc/loadavg', '/proc/self/status', '/proc/self/cgroup',
             '/sys/fs/cgroup/cpu.max', '/sys/fs/cgroup/cpu.stat', '/sys/fs/cgroup/cpuset.cpus.effective']
    return {'utc': datetime.datetime.now(datetime.timezone.utc).isoformat(),
            'files': {p: Path(p).read_text() for p in paths if Path(p).is_file()}}


def execute(command, root, name, env):
    host = {'command': list(map(str, command)), 'before': observation(), 'status': 'not-started', 'exitCode': None}
    start = time.monotonic()
    try:
        with (root / f'{name}.log').open('w') as stream:
            result = subprocess.run(list(map(str, command)), stdout=stream, stderr=subprocess.STDOUT,
                                    env=env, timeout=120)
        host.update(exitCode=result.returncode, status='success' if result.returncode == 0 else 'failed')
    except (subprocess.TimeoutExpired, OSError) as e:
        host.update(status='failed', error=repr(e))
    finally:
        host.update(after=observation(), wallSeconds=time.monotonic() - start)
        write(root / f'{name}.host.json', host)
    return host


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--prepared', type=Path, required=True)
    p.add_argument('--runtime-dotnet', type=Path, required=True)
    p.add_argument('--output', type=Path, required=True)
    p.add_argument('--reviewed-tool-sha', required=True, help='Exact independently reviewed tool HEAD, supplied by root after release')
    p.add_argument('--quiet-window-record', type=Path, required=True)
    a = p.parse_args()
    sha = subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=ROOT, text=True).strip()
    dirty = subprocess.check_output(['git', 'status', '--porcelain'], cwd=ROOT, text=True).strip()
    if sha != a.reviewed_tool_sha or dirty:
        raise ValueError('Tool HEAD must be reviewed exact clean commit')
    prepared = a.prepared.resolve(strict=True)
    manifest = strict_json((prepared / 'prepared.json').read_text())
    if manifest['contract'] != CONTRACT or manifest['toolRevision'] != sha or manifest['toolSourceHashes'] != source_inventory(ROOT):
        raise ValueError('Prepared contract/tool revision differs')
    for arm in ARMS:
        if inventory(prepared / arm) != manifest['arms'][arm]['snapshot']:
            raise ValueError(f'Prepared snapshot changed: {arm}')
    runtime = a.runtime_dotnet.resolve(strict=True)
    listing = subprocess.check_output([runtime, '--list-runtimes'], text=True)
    if {tuple(l.split()[:2]) for l in listing.splitlines()} != {('Microsoft.NETCore.App', '10.0.14'), ('Microsoft.AspNetCore.App', '10.0.14')}:
        raise ValueError('Runtime installation must contain exactly both 10.0.14 frameworks')
    if subprocess.check_output([runtime, '--list-sdks'], text=True).strip():
        raise ValueError('Use runtime-only private installation')
    os_release = Path('/etc/os-release').read_text()
    if 'ID=ubuntu' not in os_release or 'VERSION_ID="24.04"' not in os_release or os.environ.get('GITHUB_ACTIONS') != 'true':
        raise ValueError('This protocol requires hosted ubuntu24.04; no local substitution')
    mapping = subprocess.check_output(['lscpu', '-p=CPU,CORE,SOCKET,ONLINE'], text=True)
    affinity = select_cpus(mapping, os.sched_getaffinity(0))
    affinity_arg = ','.join(map(str, affinity))
    quiet = strict_json(a.quiet_window_record.read_text())
    if quiet.get('noOtherClrBuildTest') is not True or not quiet.get('owner') or not quiet.get('timestampUtc'):
        raise ValueError('Root-coordinated quiet window record required')
    output = a.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    write(output / 'quiet-window.json', quiet)
    write(output / 'schedule.json', schedule())
    write(output / 'prepared.json', manifest)
    write(output / 'before.json', observation())
    write(output / 'topology-selection.json', {'mapping': mapping, 'allowed': sorted(os.sched_getaffinity(0)), 'selected': affinity, 'claim': 'guest topology only; physical isolation unknown'})
    for filename, command in [('lscpu.txt', ['lscpu']), ('topology.txt', ['lscpu', '-e=CPU,CORE,SOCKET,NODE,ONLINE']),
                              ('runtime-info.txt', [runtime, '--info'])]:
        with (output / filename).open('w') as f:
            subprocess.run(command, stdout=f, stderr=subprocess.STDOUT, check=True)
    env = {k: v for k, v in os.environ.items() if not k.startswith(('DOTNET_', 'COMPlus_'))}
    env.update(DOTNET_ROOT=str(runtime.parent), DOTNET_MULTILEVEL_LOOKUP='0', DOTNET_ROLL_FORWARD='Disable')
    failures = []
    cold = output / 'cold'
    cold.mkdir()
    for count in CONTRACT['items']:
        for mode in CONTRACT['modes']:
            for arm in ARMS:
                name = f'{mode}-n{count}-{arm}'
                slot = dict(arm=arm, mode=mode, items=count, affinity=affinity)
                command = ['taskset', '-c', affinity_arg, runtime, prepared / arm / 'PhysicalTcpProbe.dll',
                           mode, count, 1, 0, 0, cold / f'{name}.json', 'validate']
                host = execute(command, cold, name, dict(env, SHARPLINK_BENCHMARK_SHA=CONTRACT[f'{arm}Source']))
                try:
                    if host['status'] != 'success':
                        raise ValueError('Cold process did not exit successfully')
                    validate_sample(strict_json((cold / f'{name}.json').read_text()), slot, prepared, manifest, runtime, cold=True)
                except (OSError, ValueError, KeyError, TypeError) as e:
                    failures.append({'slot': name, 'phase': 'cold', 'error': str(e)})
    write(output / 'cold-gate.json', {'status': 'Failed' if failures else 'Passed', 'failures': failures})
    if failures:
        write(output / 'not-attempted.json', schedule())
        raise ValueError('Cold correctness/path gate failed: zero timed slots launched')
    runs = output / 'runs'
    ticks = output / 'ticks'
    runs.mkdir(); ticks.mkdir()
    write(output / 'measurement-start.json', observation())
    for slot in schedule():
        name, arm = slot['id'], slot['arm']
        command = ['taskset', '-c', affinity_arg, runtime, prepared / arm / 'PhysicalTcpProbe.dll', slot['mode'],
                   slot['items'], 1, 3, 6, runs / f'{name}.json', 'measure', ticks / f'{name}.json']
        host = execute(command, runs, name, dict(env, SHARPLINK_BENCHMARK_SHA=CONTRACT[f'{arm}Source']))
        if host['status'] != 'success':
            failures.append({'slot': name, 'phase': 'measure', 'host': host})
        write(output / 'failures.json', failures)
    write(output / 'after.json', observation())
    print('Declared schedule exhausted; offline validation and summary still required. No acceptance claim.')
    if failures:
        raise SystemExit(1)


if __name__ == '__main__':
    main()
