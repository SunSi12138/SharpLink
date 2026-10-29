#!/usr/bin/env python3
"""Exactly two fresh baseline processes; diagnostic collection is not a benchmark."""
import argparse
import datetime
import json
import os
from pathlib import Path
import signal
import subprocess
import sys
import tempfile
import time
from common import ROOT, REPO, SOURCE, TREE, CONTRACT, strict_json, write, inventory, source_inventory, select_cpus, old_identity
from tool import verify as verify_collector


def require(value, code):
    if not value: raise ValueError(code)


def process_group_alive(pid):
    listing = subprocess.check_output(['ps', '-eo', 'pgid=,stat='], text=True)
    return any(int(p[0]) == pid and not p[1].startswith('Z')
               for line in listing.splitlines() if len(p := line.split()) == 2)


def target_command(runtime, prepared, variant, slot, affinity):
    return list(map(str, ['taskset', '-c', ','.join(map(str, affinity)), runtime, 'exec',
                         prepared / 'baseline/PhysicalTcpProbe.dll', variant,
                         slot / 'result.json', slot / 'ready.json', slot / 'go']))


def command_for(runtime, prepared, variant, slot, affinity, collector):
    target = target_command(runtime, prepared, variant, slot, affinity)
    if variant == 'control': return target
    require(variant == 'trace', 'VARIANT')
    providers = ('Microsoft-DotNETCore-SampleProfiler:0:4,'
                 'Microsoft-Windows-DotNETRuntime:' + CONTRACT['traceKeywords'] + ':5,'
                 'SharpLink-DiagnosticPhase:0x1:4')
    # Major applies only to Microsoft's net8 collector. Child runtimeconfig and env
    # remain Disable/private14. taskset execs the one target; no build/wrapper child.
    return list(map(str, [runtime, '--roll-forward', 'Major', collector / 'dotnet-trace.dll',
        'collect', '--providers', providers, '--buffersize', CONTRACT['bufferMegabytes'],
        '--buffering-mode', 'Drop', '--duration', '00:00:02:00', '--output', slot / 'trace.nettrace',
        '--', *target]))


def execute(command, variant, slot, env, affinity):
    record = dict(variant=variant, status='not-started', targetPid=None, exit=None,
                  processGroupQuiescent=False, command=command)
    process = None
    started = time.monotonic()
    try:
        with (slot / 'process.log').open('xb') as stream:
            process = subprocess.Popen(command, stdout=stream, stderr=subprocess.STDOUT,
                                       env=env, start_new_session=True)
            record['collectorOrTargetPid'] = process.pid
            while not (slot / 'ready.json').exists():
                require(process.poll() is None, 'EARLY_EXIT')
                require(time.monotonic() - started < 45, 'READY_TIMEOUT')
                time.sleep(.05)
            # Fixture writes ready atomically, then waits for the atomic GO below.
            ready = strict_json((slot / 'ready.json').read_text())
            require(type(ready) is dict and set(ready) == {'schema','mode','pid','sourceRevision'}, 'READY_SCHEMA')
            pid = ready.get('pid')
            require(type(ready.get('schema')) is int and ready.get('schema') == 1 and ready.get('mode') == variant and
                    ready.get('sourceRevision') == SOURCE and type(pid) is int and pid > 0, 'READY_IDENTITY')
            require(os.getpgid(pid) == process.pid, 'TARGET_PROCESS_GROUP')
            require(sorted(os.sched_getaffinity(pid)) == affinity, 'TARGET_AFFINITY')
            record['targetPid'] = pid
            pending = slot / 'go.pending'; pending.write_text(f'GO {variant} {pid}\n'); pending.rename(slot / 'go')
            while process.poll() is None:
                require(time.monotonic() - started < CONTRACT['processTimeoutSeconds'], 'PROCESS_TIMEOUT')
                trace = slot / 'trace.nettrace'
                require(not trace.exists() or trace.stat().st_size <= CONTRACT['traceFileLimitBytes'], 'TRACE_SIZE')
                time.sleep(.10)
            record['exit'] = process.returncode
            if variant == 'trace':
                trace = slot / 'trace.nettrace'
                require(trace.is_file() and 0 < trace.stat().st_size <= CONTRACT['traceFileLimitBytes'], 'FINAL_TRACE_SIZE')
            require(process.returncode == 0, 'PROCESS_EXIT')
            require(not process_group_alive(process.pid), 'DESCENDANT_LEAK')
            record['status'] = 'success'
    except (ValueError, OSError, TypeError, KeyError, subprocess.SubprocessError) as error:
        record.update(status='failed', errorType=type(error).__name__, detail=str(error))
    finally:
        if process is not None:
            try:
                if process_group_alive(process.pid):
                    record['status'] = 'failed'
                    try: os.killpg(process.pid, signal.SIGKILL)
                    except ProcessLookupError: pass  # Exit raced the same-group check; not a retry.
                process.wait(timeout=5)
                record['exit'] = process.returncode
                limit = time.monotonic() + 5
                while process_group_alive(process.pid) and time.monotonic() < limit: time.sleep(.05)
                record['processGroupQuiescent'] = not process_group_alive(process.pid)
            except (OSError, subprocess.SubprocessError) as cleanup:
                record.update(status='failed', cleanupErrorType=type(cleanup).__name__, processGroupQuiescent=False)
        record['wallSeconds'] = time.monotonic() - started
        write(slot / 'host.json', record)
    return record


def main():
    p = argparse.ArgumentParser()
    for name in ('prepared', 'runtime-dotnet', 'runtime-archives', 'collector', 'collector-package',
                 'analyzer', 'quiet-window-record', 'output'):
        p.add_argument('--' + name, type=Path, required=True)
    p.add_argument('--reviewed-tool-sha', required=True)
    a = p.parse_args()
    phase = 'SETUP'
    output = a.output.resolve()
    try:
        require(not output.exists() and not output.is_relative_to(REPO), 'OUTPUT_NEW_EXTERNAL')
        output.mkdir(parents=True)
        sha = subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=REPO, text=True).strip()
        require(sha == a.reviewed_tool_sha and not subprocess.check_output(
            ['git', 'status', '--porcelain', '--untracked-files=all'], cwd=REPO, text=True).strip(), 'TOOL_IDENTITY')
        prepared, runtime = a.prepared.resolve(strict=True), a.runtime_dotnet.resolve(strict=True)
        manifest = strict_json((prepared / 'prepared.json').read_text())
        require(manifest['toolRevision'] == sha and manifest['toolSourceHashes'] == source_inventory()
                and manifest['contract'] == CONTRACT and manifest['sourceRevision'] == SOURCE
                and manifest['sourceTree'] == TREE and set(manifest['arms']) == {'baseline'}, 'PREPARED_IDENTITY')
        require(inventory(prepared / 'baseline') == manifest['arms']['baseline']['snapshot'], 'SNAPSHOT')
        phase = 'RUNTIME'
        expected = [f'{name} 10.0.14 [{runtime.parent / "shared" / name}]'
                    for name in ('Microsoft.AspNetCore.App', 'Microsoft.NETCore.App')]
        require(sorted(subprocess.check_output([runtime, '--list-runtimes'], text=True).splitlines()) == expected
                and not subprocess.check_output([runtime, '--list-sdks'], text=True).strip(), 'RUNTIME_ONLY14')
        policy = old_identity.framework_policy(runtime)
        archive_policy = old_identity.archive_framework_policy(runtime, a.runtime_archives.resolve(strict=True))
        phase = 'TOOLS'
        verify_collector(a.collector_package, a.collector)
        sealed = strict_json((a.analyzer.parent / 'analyzer-manifest.json').read_text())
        require(sealed['toolRevision'] == sha and sealed['toolSourceHashes'] == source_inventory()
                and sealed['snapshot'] == inventory(a.analyzer), 'ANALYZER_IDENTITY')
        phase = 'HOST'
        osrelease = Path('/etc/os-release').read_text()
        require('ID=ubuntu' in osrelease and 'VERSION_ID="24.04"' in osrelease and
                os.environ.get('GITHUB_ACTIONS') == 'true', 'HOSTED_UBUNTU24')
        affinity = select_cpus(subprocess.check_output(['lscpu', '-p=CPU,CORE,SOCKET,ONLINE'], text=True),
                               os.sched_getaffinity(0))
        quiet = strict_json(a.quiet_window_record.read_text())
        require(quiet.get('noOtherClrBuildTest') is True and quiet.get('reviewedToolCommit') == sha
                and quiet.get('campaign') == CONTRACT['campaign'], 'QUIET_WINDOW')
        short = Path(tempfile.mkdtemp(prefix='sld-', dir='/tmp'))
        require(short.stat().st_mode & 0o777 == 0o700 and short.stat().st_uid == os.getuid(), 'SHORT_TEMP')
        write(output / 'short-temp.json', {'path': str(short), 'retained': True})
        env = {k: v for k, v in os.environ.items() if not k.startswith(
            ('DOTNET_', 'COMPlus_', 'GITHUB_', 'TUNIT_', 'TESTINGPLATFORM_', '_NT_SYMBOL'))}
        env.update(DOTNET_ROOT=str(runtime.parent), DOTNET_ROOT_X64=str(runtime.parent), DOTNET_HOST_PATH=str(runtime),
            DOTNET_MULTILEVEL_LOOKUP='0', DOTNET_ROLL_FORWARD='Disable', TMPDIR=str(short),
            DOTNET_EventPipeThreadSamplingRate=CONTRACT['sampleIntervalEnvironment'], SHARPLINK_BENCHMARK_SHA=SOURCE)
        write(output / 'launch.json', {'toolRevision': sha, 'contract': CONTRACT, 'affinity': affinity,
            'claim': 'same guest affinity; collector overhead and physical isolation unknown', 'quiet': quiet})
        from fixture_validation import validate_result
        completed = {}
        for variant in CONTRACT['variants']:
            phase = variant.upper()
            slot = output / variant; slot.mkdir()
            command = command_for(runtime, prepared, variant, slot, affinity, a.collector)
            host = execute(command, variant, slot, env, affinity)
            require(host['status'] == 'success' and host['processGroupQuiescent'], 'HOST_PROCESS')
            sample = strict_json((slot / 'result.json').read_text())
            validate_result(sample, variant, prepared, manifest, runtime, policy, archive_policy, affinity, host['targetPid'])
            write(slot / 'validated.json', {'status': 'VALIDATED', 'toolRevision': sha, 'pid': host['targetPid']})
            completed[variant] = sample
            verify_collector(a.collector_package, a.collector)
            require(inventory(prepared / 'baseline') == manifest['arms']['baseline']['snapshot'], 'POST_SNAPSHOT')
        phase = 'ANALYSIS'
        command = [str(runtime), str(a.analyzer / 'BaselineDiagnostics.Analyzer.dll'), '--trace',
                   str(output / 'trace/trace.nettrace'), '--output', str(output / 'analysis.json'),
                   '--expected-pid', str(completed['trace']['pid']),
                   '--start-tick', str(completed['trace']['markerStartTick']),
                   '--stop-tick', str(completed['trace']['markerStopTick']),
                   '--frequency', str(completed['trace']['stopwatchFrequency'])]
        with (output / 'analysis.log').open('xb') as stream:
            code = subprocess.run(command, env=env, stdout=stream, stderr=subprocess.STDOUT, timeout=180).returncode
        require(code == 0, 'ANALYSIS_INCOMPLETE')
        require(sealed['snapshot'] == inventory(a.analyzer), 'POST_ANALYZER')
        require(source_inventory() == manifest['toolSourceHashes'], 'POST_TOOL_SOURCE')
        write(output / 'complete.json', {'status': 'DiagnosticOnly', 'toolRevision': sha, 'variants': list(completed)})
        return 0
    except (ValueError, OSError, KeyError, TypeError, subprocess.SubprocessError) as error:
        if output.is_dir(): write(output / 'failure.json', {'phase': phase, 'type': type(error).__name__, 'detail': str(error)})
        print('DIAGNOSTIC_INCOMPLETE reason=' + phase, flush=True)
        return 1

if __name__ == '__main__': raise SystemExit(main())
