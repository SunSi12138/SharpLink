#!/usr/bin/env python3
"""Six bounded untimed owner observations, never a performance retry."""
import argparse
import datetime
import importlib.util
import os
from pathlib import Path
import subprocess
import sys

ROOT = Path(__file__).resolve().parent
sys.path.insert(0, str(ROOT.parent / 'official-version-comparison'))
from common import CONTRACT as PRODUCT, inventory, select_cpus, source_inventory, strict_json, write
_spec = importlib.util.spec_from_file_location('official_comparison_runner', ROOT.parent / 'official-version-comparison/run.py')
_previous = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(_previous)
execute, observation = _previous.execute, _previous.observation
sys.path.pop(0)
from identity import framework_policy

PROTOCOL = strict_json((ROOT / 'protocol.json').read_text())


def schedule():
    return [dict(id=f'{condition}-{arm}', condition=condition, arm=arm)
            for index, condition in enumerate(PROTOCOL['conditions'])
            for arm in (('official', 'dev') if index != 1 else ('dev', 'official'))]


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--prepared', type=Path, required=True)
    p.add_argument('--runtime-dotnet', type=Path, required=True)
    p.add_argument('--output', type=Path, required=True)
    p.add_argument('--reviewed-tool-sha', required=True)
    p.add_argument('--quiet-window-record', type=Path, required=True)
    a = p.parse_args()
    sha = subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=ROOT, text=True).strip()
    if sha != a.reviewed_tool_sha or subprocess.check_output(['git', 'status', '--porcelain'], cwd=ROOT, text=True).strip():
        raise ValueError('Exact reviewed clean tool checkout required')
    prepared = a.prepared.resolve(strict=True)
    manifest = strict_json((prepared / 'prepared.json').read_text())
    if (manifest['toolRevision'], manifest['fixturePurpose'], manifest['contract']) != (sha, 'cleanup-diagnostic', PRODUCT):
        raise ValueError('Wrong independent preparation/fixture purpose')
    if manifest['toolSourceHashes'] != source_inventory(ROOT.parent / 'official-version-comparison') or manifest['fixtureSourceHashes'] != source_inventory(ROOT / 'fixture'):
        raise ValueError('Prepared source inventory differs from reviewed files')
    for arm in ('official', 'dev'):
        if inventory(prepared / arm) != manifest['arms'][arm]['snapshot']:
            raise ValueError('Own-arm frozen binary snapshot changed')
    runtime = a.runtime_dotnet.resolve(strict=True)
    runtimes = subprocess.check_output([runtime, '--list-runtimes'], text=True)
    if {tuple(line.split()[:2]) for line in runtimes.splitlines()} != {('Microsoft.NETCore.App', '10.0.14'), ('Microsoft.AspNetCore.App', '10.0.14')} or subprocess.check_output([runtime, '--list-sdks'], text=True).strip():
        raise ValueError('Exact private runtime-only framework14 required')
    policy = framework_policy(runtime)
    os_release = Path('/etc/os-release').read_text()
    if 'ID=ubuntu' not in os_release or 'VERSION_ID="24.04"' not in os_release or os.environ.get('GITHUB_ACTIONS') != 'true':
        raise ValueError('Reviewed hosted ubuntu24.04 topology required')
    mapping = subprocess.check_output(['lscpu', '-p=CPU,CORE,SOCKET,ONLINE'], text=True)
    allowed = sorted(os.sched_getaffinity(0))
    selected = select_cpus(mapping, allowed)
    quiet = strict_json(a.quiet_window_record.read_text())
    if quiet.get('noOtherClrBuildTest') is not True or not quiet.get('owner') or not quiet.get('timestampUtc'):
        raise ValueError('Root-released quiet-window receipt required')
    output = a.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    write(output / 'protocol.json', PROTOCOL)
    write(output / 'schedule.json', schedule())
    write(output / 'prepared.json', manifest)
    write(output / 'framework-policy.json', policy)
    write(output / 'quiet-window.json', quiet)
    write(output / 'topology-selection.json', dict(mapping=mapping, allowed=allowed, selected=selected, claim='Guest mapping only; no physical isolation claim'))
    write(output / 'before.json', observation())
    runs = output / 'runs'; runs.mkdir()
    env = {k:v for k,v in os.environ.items() if not k.startswith(('DOTNET_', 'COMPlus_'))}
    env.update(DOTNET_ROOT=str(runtime.parent), DOTNET_MULTILEVEL_LOOKUP='0', DOTNET_ROLL_FORWARD='Disable')
    failures = []
    for slot in schedule():
        name, arm = slot['id'], slot['arm']
        command = ['taskset', '-c', ','.join(map(str, selected)), runtime,
                   prepared / arm / 'PhysicalTcpProbe.dll', slot['condition'], runs / f'{name}.json']
        host = execute(command, runs, name, dict(env, SHARPLINK_BENCHMARK_SHA=PRODUCT[f'{arm}Source']))
        if host['status'] != 'success':
            failures.append(dict(slot=name, status=host['status'], exitCode=host['exitCode']))
        write(output / 'failures.json', failures)
    write(output / 'after.json', observation())
    write(output / 'complete.json', dict(attemptedProcesses=6, timedProcesses=0, warmupSeconds=0,
                                        failureSlots=failures, decisionBoundary=PROTOCOL['decisionBoundary']))
    print('UNTIMED_SIX_SLOTS_EXHAUSTED; original failures retained; offline owner analysis required')
    if failures:
        raise SystemExit(1)


if __name__ == '__main__':
    main()
