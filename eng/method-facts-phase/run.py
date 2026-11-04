#!/usr/bin/env python3
"""Exactly two frozen-M0 instrumented runs; never a performance comparison."""
import hashlib
import json
import os
from pathlib import Path
import shutil
import signal
import subprocess
import sys
import time

M0 = '37548b7a35d50803212d2b0a637306636cc0a1d3'
TOOL_BASE = '47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f'
FROZEN_TOOL_FILES = {'Program.cs': '75ce82ff3ccb3dc14ab62802a14a68f894e98708696ab93c7865fedb82fd1413', 'OriginalService.cs': 'ea148d46c46f5c3e581921a0bb6764984ce03efc22ea1444ea7e67cfe6492963', 'CausalProbe.csproj': 'f3bedb4a15496b2030e7a05cc95795fc3260028355411e96bb7e49e0478d9f39', 'prepare_controlled.py': '2da0f9af8e4d88115142a2e668b561718c5c60e107cdfd192bcbea98d74fbedf', 'runtime_contract.py': '960dc0a2acee734bfb827d69f40302c280451cd1c06923a4ddf8566483089d11', 'spec.py': '46935b5eaa13417d745e53456e293e3a00e57db489ca4f8dae5c73fd6551307b'}
TRACE_VERSION = '10.0.750501'
PROVIDERS = 'Microsoft-Windows-DotNETRuntime:0x1000010019:5,SharpLink-MethodFacts-Phases:0xffffffffffffffff:5'


def write(path, data):
    path.write_text(json.dumps(data, indent=2) + '\n')


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def run(command, output, *, cwd=None, env=None, timeout=180):
    """Only own child group is stopped; failures are retained and never retried."""
    command = list(map(str, command))
    with output.open('x') as log:
        p = subprocess.Popen(command, cwd=cwd, env=env, stdout=log, stderr=subprocess.STDOUT, start_new_session=True)
        try:
            rc = p.wait(timeout=timeout)
        except subprocess.TimeoutExpired:
            os.killpg(p.pid, signal.SIGTERM)
            try:
                p.wait(timeout=10)
            except subprocess.TimeoutExpired:
                os.killpg(p.pid, signal.SIGKILL)
                p.wait(timeout=10)
            raise RuntimeError(f'owned process timeout: {output.name}')
    if rc:
        raise RuntimeError(f'owned process exit {rc}: {output.name}')
    return command


SUMMARY_FIELDS = {'schemaVersion', 'purpose', 'schedulingLimit', 'sidecarMatched', 'parseStatus', 'coverageStatus',
                  'eventCount', 'firstEventMs', 'lastEventMs', 'lostEvents', 'phaseCount', 'phaseClock', 'zeroMeaning',
                  'codeVersionMeaning', 'providers', 'targetCoverage', 'unresolvedMethodEvents', 'incompleteGcCollections',
                  'incompleteSuspensions', 'runtimeEventCounts', 'phaseTargetColumns', 'phaseColumns', 'phases', 'warnings'}


def bounded_summary(summary):
    def bound(value, depth=0):
        if depth > 12: return 'truncated-depth'
        if isinstance(value, str): return value[:400]
        if isinstance(value, list): return [bound(x, depth+1) for x in value[:54]]
        if isinstance(value, dict): return {str(k)[:100]: bound(v, depth+1) for k,v in list(value.items())[:64]}
        return value
    result = bound({k:v for k,v in summary.items() if k in SUMMARY_FIELDS})
    result['boundedOutputChanged'] = result != {k:v for k,v in summary.items() if k in SUMMARY_FIELDS}
    if len(json.dumps(result)) > 40000:
        result.pop('phases', None)
        result['boundedOutputChanged'] = True
        result['phaseRowsOmittedFromOutput'] = True
    if len(json.dumps(result)) > 40000:
        result = {k:result.get(k) for k in ['parseStatus','coverageStatus','phaseCount','lostEvents','sidecarMatched']}
        result['boundedOutputChanged'] = True
    return result


def identity_summary(manifest):
    keys = ['source','toolBase','toolHead','sdk','runtimeListing','traceVersion','providers','frozenToolFiles',
            'runtimeHostSha256','coreLibSha256','diagnosticSource','binaries','parserBinaries','commands','kernelScheduling']
    result = {k: manifest.get(k, 'unavailable') for k in keys}
    topology = manifest.get('topology', {})
    selected = topology.get('selectedCpus', [])
    result['selectedCpus'] = selected
    result['selectedTopology'] = [r for r in topology.get('topology', []) if r['cpu'] in selected]
    result['actualRuntime'] = {k:manifest.get('describe', {}).get(k, 'unavailable') for k in
                               ['runtimeVersion','coreFrameworkLocation','processPath','processorCount','allowedCpuList','serverGC','identity']}
    return result


def validate_phases(rows):
    expected = [('process', 'process-start', -1, 0)]
    for cell in ['I/T', 'P/T', 'I/U', 'P/U']:
        expected += [(cell, 'cell-start', -1, 0), (cell, 'warmup-end', -1, 1000)]
        for chunk in range(5):
            expected += [(cell, 'chunk-start', chunk, 1000 + chunk * 2000),
                         (cell, 'chunk-end', chunk, 1000 + (chunk + 1) * 2000)]
        expected += [(cell, 'cell-end', -1, 11000)]
    expected += [('process', 'process-end', -1, 44000)]
    if len(rows) != len(expected):
        raise ValueError('Incomplete phase rows')
    previous = -1
    frequency = rows[0]['frequency']
    for sequence, (row, shape) in enumerate(zip(rows, expected)):
        if row['sequence'] != sequence or tuple(row[k] for k in ['cell', 'boundary', 'chunk', 'completed']) != shape:
            raise ValueError('Reordered/invalid phase row')
        if row['timestamp'] <= previous or row['frequency'] != frequency or frequency <= 0:
            raise ValueError('Invalid phase clock')
        previous = row['timestamp']


def main():
    tools, source, output, build, runtime, trace = map(lambda p: Path(p).resolve(), sys.argv[1:])
    output.mkdir(parents=True, exist_ok=False)
    original = tools / 'eng/method-facts-causal'
    for name, expected in FROZEN_TOOL_FILES.items():
        if sha(original / name) != expected:
            raise ValueError('Frozen tool/fixture content differs: ' + name)
    sys.path.insert(0, str(original))
    from prepare_controlled import topology_snapshot, pin_runtimeconfig, validate_runtime_listing, verify_source
    from runtime_contract import validate_metadata
    manifest = {'mode': 'instrumented-diagnostic-only', 'source': M0, 'toolBase': TOOL_BASE,
                'traceVersion': TRACE_VERSION, 'providers': PROVIDERS, 'plannedProcesses': 2,
                'frozenToolFiles': FROZEN_TOOL_FILES, 'callsPerProcess': 44000, 'rawUpload': False, 'commands': [], 'executions': []}
    try:
        verify_source(source, M0)
        tool_sha = subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=tools, text=True).strip()
        manifest['toolHead'] = tool_sha
        topology = topology_snapshot()
        manifest['topology'] = topology
        affinity = ','.join(map(str, topology['selectedCpus']))
        env = dict(os.environ, DOTNET_ROOT=str(runtime.parent), DOTNET_MULTILEVEL_LOOKUP='0')
        build_env = dict(os.environ, DOTNET_ROOT=str(build.parent), DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER='1')
        sdks = subprocess.check_output([runtime, '--list-sdks'], text=True)
        runtimes = subprocess.check_output([runtime, '--list-runtimes'], text=True)
        validate_runtime_listing(runtime, runtimes, sdks)
        manifest['runtimeListing'] = runtimes
        manifest['sdk'] = subprocess.check_output([build, '--version'], cwd=source, text=True).strip()
        if manifest['sdk'] != '10.0.102':
            raise ValueError('SDK must match original frozen 10.0.102')
        manifest['kernelScheduling'] = {'captured': False, 'reason': 'No grant to elevate or change tracing permissions; ordinary EventPipe only.',
                                       'uid': os.getuid(), 'kernel': os.uname().release}
        stage = output / 'probe-source'
        shutil.copytree(tools / 'eng/method-facts-phase/probe', stage)
        parser = output / 'parser-source'
        shutil.copytree(tools / 'eng/method-facts-phase/parser', parser)
        app, parser_app = output / 'app', output / 'parser'
        for project, target in [(stage / 'CausalProbe.csproj', app), (parser / 'TraceParser.csproj', parser_app)]:
            command = [build, 'publish', project, '-c', 'Release', '-m:1', '-nr:false', '-p:UseSharedCompilation=false',
                       f'-p:EvidenceSourceRoot={source}', '-o', target]
            if target == parser_app:
                command.append('-p:RestoreLockedMode=true')
            manifest['commands'].append(run(command, output / (target.name + '-build.log'), cwd=source, env=build_env, timeout=900))
        pin_runtimeconfig(app / 'CausalProbe.runtimeconfig.json')
        # Parser is also run on private 10.0.11; it is not part of the target probe.
        pin_runtimeconfig(parser_app / 'TraceParser.runtimeconfig.json')
        manifest['binaries'] = {p.name: {'sha256': sha(p), 'bytes': p.stat().st_size} for p in sorted(app.glob('*.dll'))}
        manifest['parserBinaries'] = {p.name: sha(p) for p in sorted(parser_app.glob('*.dll'))}
        run([build, 'build-server', 'shutdown'], output / 'build-shutdown.log', env=build_env)
        run([trace, '--version'], output / 'trace-version.log')
        run([trace, 'collect', '--help'], output / 'trace-help.log')
        if TRACE_VERSION not in (output / 'trace-version.log').read_text():
            raise ValueError('Unexpected dotnet-trace version')
        prefix = ['taskset', '-c', affinity, runtime, app / 'CausalProbe.dll']
        run(prefix + ['describe'], output / 'describe.json', env=env)
        metadata = json.loads((output / 'describe.json').read_text())['metadata']
        validate_metadata(metadata, {'runtimeContract': {'privateRoot': str(runtime.parent), 'selectedCpus': topology['selectedCpus']}})
        if metadata['identity'] != 'sharplink-3.0-api5-complete-method-facts-v1':
            raise ValueError('Unexpected generated ABI')
        manifest['describe'] = metadata
        manifest['runtimeHostSha256'] = sha(runtime)
        manifest['coreLibSha256'] = sha(Path(metadata['coreFrameworkLocation']))
        manifest['diagnosticSource'] = {str(f.relative_to(stage)): sha(f) for f in stage.glob('*.cs*')}
        manifest['allBuildsComplete'] = time.time()
        write(output / 'diagnostic-manifest.json', manifest)
        print('diagnostic-preflight-identity: ' + json.dumps(identity_summary(manifest)), flush=True)
        for index in range(2):
            folder = output / f'process-{index}'
            folder.mkdir()
            target_trace = folder / 'runtime.nettrace'
            # Launch suspension lets EventPipe start before Main and all RPC phases.
            command = ['taskset', '-c', affinity, trace, 'collect', '--providers', PROVIDERS,
                       '--buffersize', '256', '--output', target_trace, '--show-child-io', '--',
                       runtime, app / 'CausalProbe.dll', 'diagnostic', folder / 'phases.jsonl']
            entry = {'index': index, 'command': list(map(str, command)), 'startUtc': time.time(), 'status': 'started'}
            manifest['executions'].append(entry)
            write(output / 'diagnostic-manifest.json', manifest)
            # The trace tool uses SDK runtime; its launched child explicitly uses frozen private runtime.
            trace_env = dict(os.environ, DOTNET_ROOT=str(build.parent), DOTNET_MULTILEVEL_LOOKUP='0')
            run(command, folder / 'trace.log', env=trace_env, timeout=180)
            phases = [json.loads(l) for l in (folder / 'phases.jsonl').read_text().splitlines()]
            validate_phases(phases)
            child_rows = []
            for line in (folder / 'trace.log').read_text().splitlines():
                if line.startswith('{'):
                    try:
                        child_rows.append(json.loads(line))
                    except json.JSONDecodeError:
                        pass
            descriptions = [r['metadata'] for r in child_rows if r.get('mode') == 'diagnostic' and 'metadata' in r]
            completions = [r for r in child_rows if r.get('complete') is True]
            if len(descriptions) != 1 or completions != [{'complete': True, 'mode': 'diagnostic-only', 'chunks': 20, 'completedCalls': 44000}]:
                raise ValueError('Missing child identity/completion record')
            validate_metadata(descriptions[0], {'runtimeContract': {'privateRoot': str(runtime.parent), 'selectedCpus': topology['selectedCpus']}})
            for key in ['binaryIdentities', 'modules', 'identity', 'runtimeVersion', 'coreFrameworkLocation']:
                if descriptions[0][key] != metadata[key]:
                    raise ValueError('Child changed frozen identity: ' + key)
            entry.update(status='collected', endUtc=time.time(), traceSha256=sha(target_trace), traceBytes=target_trace.stat().st_size)
            try:
                run([runtime, parser_app / 'TraceParser.dll', target_trace, folder / 'parsed', folder / 'phases.jsonl'], folder / 'parse.log', env=env)
            finally:
                summary_path = folder / 'parsed/summary.json'
                summary = {'parseStatus': 'summary-unavailable', 'coverageStatus': 'unknown'}
                if summary_path.is_file():
                    try:
                        summary = bounded_summary(json.loads(summary_path.read_text()))
                    except (ValueError, OSError):
                        summary = {'parseStatus': 'summary-unreadable', 'coverageStatus': 'unknown'}
                entry['summary'] = summary
                write(output / 'diagnostic-manifest.json', manifest)
                print('phase-diagnostic: ' + json.dumps({'index': index, 'traceSha256': entry['traceSha256'], 'traceBytes': entry['traceBytes'], 'command': entry['command'], 'summary': summary}), flush=True)
        manifest['complete'] = True
        write(output / 'diagnostic-manifest.json', manifest)
        print('diagnostic-complete: ' + json.dumps({'source': M0, 'toolHead': tool_sha, 'processes': 2,
              'callsPerProcess': 44000, 'performanceConclusion': 'none; instrumented mechanism diagnosis only',
              'kernelSchedulingCaptured': False, 'binaries': manifest['binaries'],
              'sdk': manifest['sdk'], 'runtime': metadata['runtimeVersion'], 'coreLib': metadata['coreFrameworkLocation'],
              'coreLibSha256': manifest['coreLibSha256'], 'runtimeHostSha256': manifest['runtimeHostSha256'],
              'selectedCpus': topology['selectedCpus'], 'selectedTopology': [r for r in topology['topology'] if r['cpu'] in topology['selectedCpus']],
              'traceVersion': TRACE_VERSION, 'providers': PROVIDERS, 'frozenToolFiles': FROZEN_TOOL_FILES,
              'diagnosticSource': manifest['diagnosticSource'], 'commands': manifest['commands'],
              'traceCommands': [e['command'] for e in manifest['executions']]}), flush=True)
    except Exception as error:
        manifest['complete'] = False
        manifest['failure'] = f'{type(error).__name__}: {error}'
        write(output / 'diagnostic-manifest.json', manifest)
        print('diagnostic-failure-identity: ' + json.dumps(identity_summary(manifest)), flush=True)
        print('diagnostic-failure: ' + manifest['failure'], flush=True)
        raise


if __name__ == '__main__':
    main()
