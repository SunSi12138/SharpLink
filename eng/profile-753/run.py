#!/usr/bin/env python3
"""One bounded EventPipe diagnosis; all attempts retained, no resampling."""
import argparse
import hashlib
import importlib.util
import json
import math
import os
from pathlib import Path
import re
import signal
import statistics
import subprocess
import time
import traceback

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]
ARMS = ('baseline', 'safe', 'normalized')
ORDERS = (ARMS, ('safe', 'normalized', 'baseline'), ('normalized', 'baseline', 'safe'))
TRANSPORT = 'src/SharpLink.Runtime/Transport/TransportConnection.cs'
APP = 'test/SharpLink.LoadTest/bin/Release/net10.0/SharpLink.LoadTest.dll'
MAX_BYTES = 2 * 1024 ** 3


def module(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    result = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(result)
    return result


PREPARE = module('profile_prepare', HERE / 'prepare.py')
VALIDATOR = module('profile_scenario_validator', ROOT / 'eng/perf/summarize-read-ownership.py')


def write(path, value):
    path.write_text(json.dumps(value, indent=2, allow_nan=False) + '\n')


def digest(path):
    value = hashlib.sha256()
    with path.open('rb') as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b''):
            value.update(block)
    return value.hexdigest()


def require(condition, message):
    if not condition:
        raise ValueError(message)


def git(root, *args):
    return subprocess.check_output(['git', *args], cwd=root, text=True).strip()


def execute(command, cwd, log, env=None, timeout=300):
    """Bound and reap the entire child process group, preserving partial outputs."""
    print('+', json.dumps([str(x) for x in command]), flush=True)
    outcome = {'command': [str(x) for x in command], 'timeoutSeconds': timeout,
               'startedUtc': time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime())}
    with log.open('w') as stream:
        process = subprocess.Popen(command, cwd=cwd, env=env, stdout=stream,
                                   stderr=subprocess.STDOUT, start_new_session=True)
        outcome['launcherPid'] = process.pid
        try:
            outcome['exitCode'] = process.wait(timeout=timeout)
        except subprocess.TimeoutExpired:
            outcome['timedOut'] = True
            os.killpg(process.pid, signal.SIGINT)
            try:
                process.wait(timeout=10)
            except subprocess.TimeoutExpired:
                os.killpg(process.pid, signal.SIGKILL)
                process.wait(timeout=10)
            outcome['exitCode'] = process.returncode
    outcome['finishedUtc'] = time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime())
    return outcome


def successful(outcome):
    return outcome['exitCode'] == 0 and not outcome.get('timedOut', False)


def checked(command, cwd, log, env=None, timeout=300):
    result = execute(command, cwd, log, env, timeout)
    write(log.with_suffix('.outcome.json'), result)
    require(successful(result), f'Command failed; see {log}')
    return result


def load_manifest(path):
    manifest = json.loads(path.read_text())
    for arm in ARMS:
        require(bool(re.fullmatch('[0-9a-f]{40}', manifest[arm])), 'Immutable arm SHA required')
    require(len({manifest[a] for a in ARMS}) == 3, 'Three distinct revisions required')
    expected = {'blocks': 3, 'warmupSeconds': 2, 'durationSeconds': 15,
                'sdkVersion': '10.0.112', 'runtimeVersion': '.NET 10.0.12',
                'processorCount': 4, 'traceToolVersion': '10.0.745401',
                'traceEventVersion': '3.2.8', 'traceBufferMegabytes': 256, 'retries': 0,
                'providers': 'Microsoft-DotNETCore-SampleProfiler:0:4,Microsoft-Windows-DotNETRuntime:0x40034019:4,SharpLink-753-ProfileWindow:1:4'}
    for key, value in expected.items():
        require(manifest[key] == value, f'Unreviewed manifest setting: {key}')
    return manifest


def plan():
    return [{'block': block + 1, 'armPosition': position + 1, 'arm': arm,
             'pairPosition': pair + 1, 'mode': mode,
             'runId': f'b{block + 1}-p{position + 1}-{arm}-{mode}'}
            for block, order in enumerate(ORDERS)
            for position, arm in enumerate(order)
            for pair, mode in enumerate(('untraced', 'traced') if block % 2 == 0 else ('traced', 'untraced'))]


def tuning_environment(environment):
    # Record only a small known non-secret set. Unknown prefixed keys are named,
    # never have their values published, and fail closed rather than altering them.
    allowed = {'DOTNET_ROOT', 'DOTNET_CLI_HOME', 'DOTNET_ROOT_X64', 'DOTNET_HOST_PATH', 'DOTNET_MULTILEVEL_LOOKUP',
               'DOTNET_CLI_TELEMETRY_OPTOUT', 'DOTNET_NOLOGO', 'DOTNET_SKIP_FIRST_TIME_EXPERIENCE',
               'DOTNET_GENERATE_ASPNET_CERTIFICATE', 'DOTNET_ADD_GLOBAL_TOOLS_TO_PATH',
               'DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE'}
    effective = {key: value for key, value in environment.items()
                 if key.upper().startswith(('DOTNET_', 'COMPLUS_', 'CORECLR_'))}
    unexpected = sorted(set(effective) - allowed)
    require(not unexpected, f'Unexpected runtime environment key(s); values withheld: {unexpected}')
    return effective


def check_budget(output):
    total = sum(path.stat().st_size for path in output.rglob('*') if path.is_file())
    require(total <= MAX_BYTES, f'Artifact evidence exceeds 2 GiB budget: {total}')


def prepare(output, work, manifest):
    work.mkdir(parents=True, exist_ok=True)
    for arm in ARMS:
        git(ROOT, 'cat-file', '-e', manifest[arm] + '^{commit}')
    for arm in ('baseline', 'safe'):
        git(ROOT, 'merge-base', '--is-ancestor', manifest[arm], manifest['normalized'])
        changes = git(ROOT, 'diff', '--name-only', manifest[arm], manifest['normalized']).splitlines()
        require([x for x in changes if x.startswith('src/')] == [TRANSPORT],
                'Unexpected production-source differences')
        build_names = {'global.json', 'nuget.config', 'packages.lock.json'}
        require(not [x for x in changes if x.startswith(('test/SharpLink.LoadTest/', 'test/SharpLink.LoadTestBase/'))
                     or Path(x).suffix in ('.props', '.targets', '.csproj') or Path(x).name.lower() in build_names],
                'LoadTest or ancestor build inputs differ across exact arms')
    provenance = {'manifest': manifest, 'harnessCommit': git(ROOT, 'rev-parse', 'HEAD'),
                  'plan': plan(), 'arms': {}, 'meaning': 'Identical boundary-only diagnostic harness on exact production sources.',
                  'buildOnlyFlags': ['EmitCompilerGeneratedFiles=true', 'UseSharedCompilation=false'],
                  'runtimeTuning': 'Default; no affinity, tiering, ReadyToRun or sampling-interval override.'}
    for arm in ARMS:
        tree = work / arm
        checked(['git', 'worktree', 'add', '--detach', str(tree), manifest[arm]], ROOT,
                output / f'{arm}-checkout.log')
        identity = {'commit': manifest[arm], 'tree': git(tree, 'rev-parse', 'HEAD^{tree}'),
                    'readerSha256': digest(tree / TRANSPORT), 'productionGitTree': git(tree, 'rev-parse', 'HEAD:src')}
        identity['instrumentation'] = PREPARE.instrument(tree, HERE / 'ProfileWindow753.cs')
        (output / f'{arm}-boundary.patch').write_text(git(tree, 'diff', '--', PREPARE.PROGRAM) + '\n')
        checked(['dotnet', 'build', 'test/SharpLink.LoadTest/SharpLink.LoadTest.csproj', '-c', 'Release',
                 '-v', 'minimal', '-m:1', '-nr:false', '-p:UseSharedCompilation=false',
                 '-p:EmitCompilerGeneratedFiles=true'], tree, output / f'{arm}-build.log')
        git(tree, 'diff', '--exit-code', '--', 'src')
        paths = list((tree / Path(APP).parent).glob('*.dll'))
        paths += list((tree / Path(APP).parent).glob('*.pdb'))
        paths += list((tree / Path(APP).parent).glob('*.deps.json'))
        paths += list((tree / Path(APP).parent).glob('*.runtimeconfig.json'))
        identity['executedBinarySha256'] = {str(p.relative_to(tree)): digest(p) for p in sorted(paths)}
        identity['generatedSourceSha256'] = {
            str(p.relative_to(tree)): digest(p) for p in sorted(tree.rglob('*.cs'))
            if 'obj' in p.relative_to(tree).parts}
        for project in ('SharpLink.Runtime', 'SharpLink.Client', 'SharpLink.Server'):
            source = tree / f'src/{project}/bin/Release/net10.0/{project}.dll'
            copied = tree / Path(APP).parent / f'{project}.dll'
            require(digest(source) == digest(copied), f'Copied {project} DLL does not match compiled source output')
            identity.setdefault('sourceBinarySha256', {})[str(source.relative_to(tree))] = digest(source)
        provenance['arms'][arm] = identity
        write(output / 'provenance.json', provenance)
    require(len({json.dumps(p['instrumentation'], sort_keys=True) for p in provenance['arms'].values()}) == 1,
            'Arms do not use byte-identical original and instrumented measurement sources')
    return provenance


def validate_report(doc, metadata, manifest, identity, run_id, process_id=None):
    require(doc['SchemaVersion'] == 1 and doc['SourceCommit'] == identity, 'Report source/schema mismatch')
    require(doc['Runtime'] == manifest['runtimeVersion'] and doc['ProcessorCount'] == manifest['processorCount'],
            'Actual runtime/processor count differs from pinned contract')
    require(len(doc['Results']) == 1, 'Expected one measured stage')
    VALIDATOR.validate_scenario(doc, 'tcp-add-c1', manifest)
    result = doc['Results'][0]
    require(result['RecorderVersion'] == 'worker-local-shared-capacity-v4' and
            result['RecorderMode'] == 'formal' and result['FormalComparable'] is True, 'Formal recorder mismatch')
    require(doc['Configuration']['TailObserver'] is False, 'Unexpected tail observer')
    for key in ('Failure', 'TailObserverFailure', 'SendQueueBackpressureRetries'):
        require(result[key] == 0, f'Nonzero required failure/retry count: {key}')
    for key in ('ValidationFailure', 'Cancelled'):
        require(result.get(key, 0) == 0, f'Nonzero {key}')
    success = result['Success']
    require(type(success) is int and success > 0 and success == result['OperationsStartedDuringMeasurement'] ==
            result['OperationsCompleted'] == result['SampleCount'], 'Incomplete successful operations')
    require(result['MaximumSampleCapacity'] >= success and result['TailObserverSampleCount'] == 0, 'Invalid latency population')
    require(metadata['schemaVersion'] == 1 and metadata['diagnosticOnly'] is True and
            metadata['workload'] == 'tcp-add-c1' and metadata['runId'] == run_id,
            'Marker sidecar identity mismatch')
    require(metadata['runtimeVersion'] == '10.0.12' and metadata['operations'] == success,
            'Marker runtime/operation mismatch')
    require(type(metadata['processId']) is int and metadata['processId'] > 0 and
            (process_id is None or metadata['processId'] == process_id), 'Marker process mismatch')
    ticks = [metadata[key] for key in ('beginTicks', 'startedTicks', 'stoppedTicks', 'endTicks')]
    require(all(type(t) is int and t > 0 for t in ticks) and ticks == sorted(ticks) and ticks[1] < ticks[2], 'Invalid marker ticks')
    frequency = metadata['stopwatchFrequency']
    require(type(frequency) is int and frequency > 0 and frequency == result['StopwatchFrequency'], 'Stopwatch frequency mismatch')
    require(math.isclose((ticks[2] - ticks[1]) / frequency, result['MeasurementDurationSeconds'], rel_tol=0, abs_tol=1e-7),
            'Marker admission duration does not match measured stage')
    window = result['MeasurementDurationSeconds'] + result['DrainDurationSeconds']
    require(abs((ticks[3] - ticks[0]) / frequency - window) <= 1, 'Marker outer window mismatch')
    evidence = result['Evidence']
    return {'workload': 'tcp-add-c1', 'transport': 'tcp', 'runId': run_id,
            'processId': metadata['processId'], 'commit': identity, 'runtimeVersion': '10.0.12',
            'stopwatchFrequency': frequency, **{key: metadata[key] for key in ('beginTicks', 'startedTicks', 'stoppedTicks', 'endTicks')}, 'operations': success, 'items': success, 'operationsStarted': success,
            'validationFailures': 0, 'failure': 0, 'cancelled': 0, 'processCpuMs': evidence['CpuMilliseconds'],
            'allocatedBytes': evidence['AllocatedBytes'], 'profileWindowSeconds': window,
            'measurementSeconds': result['MeasurementDurationSeconds'], 'drainSeconds': result['DrainDurationSeconds'],
            'qps': result['Qps'], 'p99Us': result['P99Us'], 'gen0': evidence['Gen0Collections'],
            'gen1': evidence['Gen1Collections'], 'gen2': evidence['Gen2Collections']}


def trace_command(trace, manifest, directory, child):
    return [str(trace), 'collect', '--show-child-io', '--providers', manifest['providers'], '--buffersize',
            str(manifest['traceBufferMegabytes']), '--output', str(directory / 'profile.nettrace'), '--', *child]


def environment_signature(doc):
    return tuple(doc[key] for key in ('OperatingSystem', 'OsArchitecture', 'ProcessArchitecture',
                                     'Runtime', 'ProcessorCount', 'ServerGc', 'GcLatencyMode'))


def verify_binaries(work, arm, provenance):
    for path, expected in provenance['arms'][arm]['executedBinarySha256'].items():
        require(digest(work / arm / path) == expected, 'Executed binary changed after build')


def calibrate(output, manifest, trace, parser, calibration):
    directory = output / 'calibration'
    directory.mkdir()
    env = dict(os.environ, SHARPLINK_PROFILE_RUN_ID='calibration',
               SHARPLINK_PROFILE_METADATA=str(directory / 'metadata.json'))
    checked(['dotnet', str(parser), '--self-test'], ROOT, directory / 'parser-self-tests.log')
    checked(trace_command(trace, manifest, directory,
                          ['dotnet', 'exec', str(calibration), str(directory / 'normalized.json')]),
            ROOT, directory / 'collect.log', env, 120)
    checked(['dotnet', str(parser), str(directory / 'profile.nettrace'), str(directory / 'normalized.json'),
             str(directory / 'profile.json'), '--calibration'], ROOT, directory / 'parse.log', timeout=180)
    profile = json.loads((directory / 'profile.json').read_text())
    require(profile['calibration'] is True and profile['rawEventsLost'] == 0 and profile['convertedEventsLost'] == 0, 'Calibration parser did not confirm complete calibration')
    check_budget(output)


def measure(output, work, manifest, provenance, trace, parser):
    index = []
    signatures = set()
    raw = output / 'raw'
    raw.mkdir()
    for planned in plan():
        entry = dict(planned, parser={'state': 'not-run'}, scenarioValidation={'state': 'not-run'}, childOutcome={'state': 'not-run'})
        arm = entry['arm']
        directory = raw / entry['runId']
        directory.mkdir()
        entry['directory'] = str(directory.relative_to(output))
        entry['identity'] = manifest[arm]
        index.append(entry)
        write(output / 'execution-index.json', index)
        try:
            verify_binaries(work, arm, provenance)
            env = dict(os.environ, SHARPLINK_COMMIT=manifest[arm], SHARPLINK_PROFILE_RUN_ID=entry['runId'],
                       SHARPLINK_PROFILE_METADATA=str(directory / 'metadata.json'))
            child = ['dotnet', 'exec', str(work / arm / APP), '--mode', 'local', '--transport', 'tcp',
                     '--operation', 'add', '--concurrency', '1', '--warmup', '2', '--duration', '15',
                     '--profile', 'balanced', '--min-connections', '1', '--max-connections', '1',
                     '--max-send-queue-bytes', '67108864', '--recording', 'formal',
                     '--maximum-recorded-operations', '30000000', '--metrics-port', '0',
                     '--json-output', str(directory / 'report.json')]
            command = trace_command(trace, manifest, directory, child) if entry['mode'] == 'traced' else child
            entry['collection'] = execute(command, work / arm, directory / 'collect.log', env, timeout=180)
            # Scenario validation is a separate outcome from the collector/launcher status.
            try:
                doc = json.loads((directory / 'report.json').read_text())
                metadata = json.loads((directory / 'metadata.json').read_text())
                normalized = validate_report(doc, metadata, manifest, manifest[arm], entry['runId'],
                                             entry['collection']['launcherPid'] if entry['mode'] == 'untraced' else None)
                signatures.add(environment_signature(doc))
                require(len(signatures) == 1, 'Different runtime/GC/OS environments across diagnostic processes')
                write(directory / 'normalized.json', normalized)
                entry['scenarioValidation'] = {'valid': True, 'childReportCompleted': True,
                                                'childPid': metadata['processId']}
            except Exception as error:
                entry['scenarioValidation'] = {'valid': False, 'error': str(error), 'traceback': traceback.format_exc()}
                raise
            entry['childOutcome'] = {'successfulExitConfirmed': successful(entry['collection']),
                                     'exitCode': 0 if successful(entry['collection']) else None,
                                     'source': 'direct-process' if entry['mode'] == 'untraced' else 'pinned-dotnet-trace-child-exit-propagation',
                                     'caveat': 'On collector failure, a separate child exit code is unavailable; the cell is invalid.'}
            require(successful(entry['collection']), 'Collector/child launcher failed despite valid report')
            if entry['mode'] == 'traced':
                entry['parser'] = execute(['dotnet', str(parser), str(directory / 'profile.nettrace'),
                                           str(directory / 'normalized.json'), str(directory / 'profile.json')],
                                          ROOT, directory / 'parse.log', timeout=180)
                require(successful(entry['parser']), 'Trace parser rejected this observation')
            else:
                entry['parser'] = {'state': 'not-applicable'}
            verify_binaries(work, arm, provenance)
            check_budget(output)
            entry['state'] = 'completed'
        except Exception as error:
            entry.update(state='failed', error=str(error), traceback=traceback.format_exc())
            raise
        finally:
            entry['files'] = {str(p.relative_to(output)): {'sha256': digest(p), 'bytes': p.stat().st_size}
                              for p in sorted(directory.rglob('*')) if p.is_file()}
            write(output / 'execution-index.json', index)
        time.sleep(1)
    summarize(output)


def describe(points):
    return {'points': points, 'median': statistics.median(points), 'mean': statistics.mean(points),
            'sampleStdev': statistics.stdev(points), 'min': min(points), 'max': max(points)}


def summarize(output):
    index = json.loads((output / 'execution-index.json').read_text())
    provenance = json.loads((output / 'provenance.json').read_text())
    manifest = load_manifest(HERE / 'revisions.json')
    require(provenance['manifest'] == manifest and provenance['plan'] == plan(), 'Provenance differs from pinned plan')
    require(set(provenance['arms']) == set(ARMS) and all(provenance['arms'][arm]['commit'] == manifest[arm] for arm in ARMS), 'Source provenance mismatch')
    require([{k: row[k] for k in plan()[0]} for row in index] == plan(), 'Incomplete or reordered 18-process population')
    rows = {}
    for entry in index:
        require(entry['state'] == 'completed' and successful(entry['collection']) and entry['scenarioValidation']['valid'] is True, 'Incomplete observation')
        require(entry['identity'] == manifest[entry['arm']] and entry['directory'] == 'raw/' + entry['runId'], 'Observation identity/directory mismatch')
        required = {'report.json', 'metadata.json', 'normalized.json', 'collect.log'}
        if entry['mode'] == 'traced':
            required |= {'profile.nettrace', 'profile.json', 'parse.log'}
            require(successful(entry['parser']), 'Parser did not pass')
        require(all(entry['directory'] + '/' + filename in entry['files'] for filename in required), 'Required raw evidence missing from hash index')
        for filename, proof in entry['files'].items():
            require(digest(output / filename) == proof['sha256'], 'Observation hash mismatch')
        directory = output / entry['directory']
        doc = json.loads((directory / 'normalized.json').read_text())
        report = json.loads((directory / 'report.json').read_text())
        metadata = json.loads((directory / 'metadata.json').read_text())
        reconstructed = validate_report(report, metadata, manifest, manifest[entry['arm']], entry['runId'],
                                        entry['collection']['launcherPid'] if entry['mode'] == 'untraced' else None)
        require(doc == reconstructed, 'Normalized metrics differ from original report/metadata')
        if entry['mode'] == 'traced':
            profile = json.loads((directory / 'profile.json').read_text())
            require(profile['schemaVersion'] == 1 and profile['diagnosticOnly'] is True and profile['calibration'] is False and
                    profile['identity'] == entry['identity'] and profile['runId'] == entry['runId'] and
                    profile['processId'] == doc['processId'] and profile['operations'] == doc['operations'] and
                    profile['runtimeVersion'] == doc['runtimeVersion'] and profile['rawEventsLost'] == profile['convertedEventsLost'] == 0,
                    'Parsed profile identity or loss mismatch')
        rows[(entry['block'], entry['arm'], entry['mode'])] = {
            'qps': doc['qps'], 'cpuMicrosecondsPerRpc': doc['processCpuMs'] * 1000 / doc['operations'],
            'allocatedBytesPerRpc': doc['allocatedBytes'] / doc['operations'], 'p99Us': doc['p99Us'],
            'operations': doc['operations'], 'gen0': doc['gen0'], 'gen1': doc['gen1'], 'gen2': doc['gen2']}
    observations, observer, contrasts = {}, {}, {}
    metrics = next(iter(rows.values())).keys()
    def contrast(left, right):
        return {metric: {'absoluteDelta': describe([r[metric] - l[metric] for l, r in zip(left, right)]),
                         'percentDelta': describe([(r[metric] / l[metric] - 1) * 100 for l, r in zip(left, right)])
                         if all(l[metric] != 0 for l in left) else None} for metric in metrics}
    for arm in ARMS:
        for mode in ('untraced', 'traced'):
            observations[f'{arm}/{mode}'] = {metric: describe([rows[(b, arm, mode)][metric] for b in (1, 2, 3)]) for metric in metrics}
        observer[arm] = contrast([rows[(b, arm, 'untraced')] for b in (1, 2, 3)],
                                 [rows[(b, arm, 'traced')] for b in (1, 2, 3)])
    for mode in ('untraced', 'traced'):
        for reference, candidate in (('baseline', 'safe'), ('baseline', 'normalized'), ('safe', 'normalized')):
            contrasts[f'{mode}/{candidate}-versus-{reference}'] = contrast(
                [rows[(b, reference, mode)] for b in (1, 2, 3)], [rows[(b, candidate, mode)] for b in (1, 2, 3)])
    sensitivity = {}
    for candidate in ('safe', 'normalized'):
        sensitivity[candidate] = {}
        for metric in ('qps', 'cpuMicrosecondsPerRpc'):
            points = []
            for block in (1, 2, 3):
                u = rows[(block, candidate, 'untraced')][metric] / rows[(block, 'baseline', 'untraced')][metric] - 1
                t = rows[(block, candidate, 'traced')][metric] / rows[(block, 'baseline', 'traced')][metric] - 1
                reasons = []
                if u == 0: reasons.append('zero-untraced-gap')
                if (t > 0) - (t < 0) != (u > 0) - (u < 0): reasons.append('ranking-sign-reversal')
                if abs(t - u) >= abs(u): reasons.append('differential-tracing-effect-at-least-untraced-gap')
                points.append({'block': block, 'untracedRelativeGap': u, 'tracedRelativeGap': t,
                               'differentialImpact': t - u, 'observerSensitive': bool(reasons), 'reasons': reasons})
            sensitivity[candidate][metric] = points
    result = {'diagnosticOnly': True, 'plannedProcesses': 18, 'completedProcesses': len(index),
              'observerSensitivity': sensitivity,
              'sourceCostAttributionInconclusiveDueToObserver': any(p['observerSensitive'] for candidate in sensitivity.values() for points in candidate.values() for p in points),
              'observations': observations, 'pairedTracingObserverEffects': observer, 'pairedArmContrasts': contrasts,
              'caveats': ['Three Latin blocks; mode order U/T,T/U,U/T leaves a 2:1 residual order imbalance.',
                          'Traced and untraced use identical boundary-instrumented binaries and formal recorder settings.',
                          'All points retained, no retries or selection. Three pairs are descriptive, not confidence intervals or equivalence.',
                          'CPU/allocation cover combined local client/server/harness. Per-stage p99 values are not pooled.',
                          'Managed all-thread samples are not hardware on-CPU samples. No nanosecond cost is derived from sample shares.',
                          'This one diagnostic pass does not replace acceptance timing or authorize source promotion/merge.']}
    write(output / 'observer-controls.json', result)
    (output / 'summary.md').write_text('# PR753 one-pass TCP c1 diagnostic evidence\n\nAll 18 planned processes completed. '
                                      'Per-trace attribution and every paired observer-effect point are retained. '
                                      'This is diagnostic evidence; performance acceptance remains a separate decision.\n\n' +
                                      '\n'.join('- ' + s for s in result['caveats']) + '\n')


def main():
    parser = argparse.ArgumentParser()
    for name in ('manifest', 'output', 'work', 'trace', 'parser', 'calibration'):
        parser.add_argument('--' + name, required=True, type=Path)
    parser.add_argument('--prepare-only', action='store_true')
    args = parser.parse_args()
    output, work = args.output.resolve(), args.work.resolve()
    require(not output.exists() or not any(output.iterdir()), 'Evidence directory must be fresh/empty; prior attempts are never overwritten')
    require(not work.exists() or not any(work.iterdir()), 'Worktree directory must be fresh/empty')
    output.mkdir(parents=True, exist_ok=True)
    status = {'state': 'preparing'}
    try:
        manifest = load_manifest(args.manifest)
        write(output / 'plan.json', plan())
        runtime_env = tuning_environment(os.environ)
        write(output / 'runtime-environment.json', runtime_env)
        require(subprocess.check_output(['dotnet', '--version'], text=True).strip() == manifest['sdkVersion'], 'SDK pin mismatch')
        version = subprocess.check_output([str(args.trace), '--version'], text=True).strip()
        (output / 'dotnet-trace-version.txt').write_text(version + '\n')
        require(version.startswith(manifest['traceToolVersion']), 'dotnet-trace version mismatch')
        if not args.prepare_only:
            require(os.environ.get('GITHUB_ACTIONS') == 'true' and os.environ.get('RUNNER_ENVIRONMENT') == 'github-hosted',
                    'Formal diagnostic pass requires GitHub-hosted runner')
            require(os.cpu_count() == 4 and len(os.sched_getaffinity(0)) == 4, 'Expected natural 4-CPU hosted runner')
        provenance = prepare(output, work, manifest)
        if not args.prepare_only:
            processes = subprocess.check_output(['ps', '-eo', 'pid,comm,args'], text=True)
            (output / 'processes-before-calibration.txt').write_text(processes)
            require(not re.search(r'SharpLink\.(?:LoadTest|StreamLoadTest|Chaos|Benchmarks)(?:\.dll)?(?: |$)', processes),
                    'Competing SharpLink process found')
            status['state'] = 'calibrating'
            write(output / 'run-status.json', status)
            calibrate(output, manifest, args.trace.resolve(), args.parser.resolve(), args.calibration.resolve())
            status['state'] = 'profiling'
            write(output / 'run-status.json', status)
            measure(output, work, manifest, provenance, args.trace.resolve(), args.parser.resolve())
        status['state'] = 'prepared' if args.prepare_only else 'completed'
    except Exception as error:
        status.update(state='failed', error=str(error), traceback=traceback.format_exc())
        raise
    finally:
        status['finishedUtc'] = time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime())
        write(output / 'run-status.json', status)


if __name__ == '__main__':
    main()
