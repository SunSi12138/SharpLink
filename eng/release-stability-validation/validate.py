#!/usr/bin/env python3
"""Bind a single reviewed request to the frozen release candidate; no product execution."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess

SOURCE = 'e8fcef3ddb93d76457b690dadc279e7a8f91b47d'
TREE = '23074b603a369fe339b9958dec2da53a04b4bfa8'
REQUEST = 'eng/release-stability-validation/request.json'
TOOLS = {
    '.github/workflows/release-stability-validation.yml',
    'eng/release-stability-validation/validate.py',
    'eng/release-stability-validation/test_validation.py',
    'eng/release-stability-validation/README.md',
    'eng/release-stability-validation/dotnet.py',
}


def git(root, *args):
    return subprocess.check_output(['git', '-C', str(root), *args], text=True).strip()


def validate(root, source, request_sha):
    request = json.loads((root / REQUEST).read_text())
    if set(request) != {'source_sha', 'source_tree', 'tools_sha'}:
        raise ValueError('Request keys differ from the reviewed schema')
    if request['source_sha'] != SOURCE or request['source_tree'] != TREE:
        raise ValueError('Request changes the frozen candidate')
    if git(root, 'rev-parse', 'HEAD') != request_sha:
        raise ValueError('Request checkout does not match event SHA')
    if git(root, 'show', '-s', '--format=%P', 'HEAD') != request['tools_sha']:
        raise ValueError('Request must have exactly the reviewed tools HEAD as parent')
    if git(root, 'diff', '--name-only', 'HEAD^', 'HEAD').splitlines() != [REQUEST]:
        raise ValueError('Request commit must change only request.json')
    if git(root, 'ls-tree', '-r', '--name-only', 'HEAD^', '--', REQUEST):
        raise ValueError('Only the first request is accepted')
    changed = set(git(root, 'diff', '--name-only', SOURCE, 'HEAD^').splitlines())
    if changed != TOOLS:
        raise ValueError(f'Tools must differ from candidate only by reviewed files: {changed}')
    if git(source, 'rev-parse', 'HEAD') != SOURCE or git(source, 'rev-parse', 'HEAD^{tree}') != TREE:
        raise ValueError('Actual source checkout differs from frozen candidate')
    if git(source, 'status', '--porcelain', '--untracked-files=no'):
        raise ValueError('Candidate tracked worktree is dirty')
    return {'request_sha': request_sha, **request,
            'actual_source_sha': git(source, 'rev-parse', 'HEAD'),
            'actual_source_tree': git(source, 'rev-parse', 'HEAD^{tree}'),
            'run_id': os.environ.get('GITHUB_RUN_ID'),
            'run_attempt': os.environ.get('GITHUB_RUN_ATTEMPT'),
            'six_hour_memory_window': 'not covered by a ten-minute release soak'}


# Binary input roots are derived from the selected tests' PluginBundle loader and
# project references. The selected nine cases load only DynamicServices; the
# remaining plugin roots below are built dependencies, not claimed executions.
PRODUCT_DLLS = ['SharpLink.Abstractions.dll', 'SharpLink.Runtime.dll',
                'SharpLink.Client.dll', 'SharpLink.Server.dll', 'SharpLink.Sdk.dll']
PLUGIN_DLLS = ['SharpLink.DynamicPlugin.Contracts.dll', 'SharpLink.Sdk.dll',
               'SharpLink.Abstractions.dll', 'SharpLink.Serializer.SharpPack.dll']
METRICS = {'sharplink.connections.active', 'sharplink.calls.active',
           'sharplink.requests.pending', 'sharplink.streams.active', 'sharplink.send.queue.bytes'}
METHODS = ['OneHundredClientReplacementsShouldLeaveOneReusableRegistration',
           'MultiClusterReplacementShouldPublishCoordinatorRoutesBeforeOldDrainAndAfterCallerCancellation',
           'Api4DynamicStreamExitShouldReleaseItsCollectibleContext',
           'RejectedApi4DynamicRegistrationShouldReleaseItsCollectibleContext',
           'CollectibleContextShouldUnloadAfterFrameworkReferencesAreReleased']
MODES = 'normal,cancellation-before-first,cancellation-mid-stream,consumer-break,service-exception'


def required_files(kind):
    project = 'SharpLink.IntegrationTests' if kind == 'abi' else 'SharpLink.ChaosTests'
    roots = {project: [*PRODUCT_DLLS, project + '.dll', project + '.deps.json', project + '.runtimeconfig.json']}
    if kind == 'abi':
        roots[project].extend(['SharpLink.Serializer.SharpPack.dll', 'SharpPack.Core.dll'])
        for name in ['DynamicServices', 'ReplacementServices']:
            roots['SharpLink.' + name] = [*PLUGIN_DLLS, 'SharpLink.DynamicPlugin.Services.dll',
                                         'SharpLink.DynamicPlugin.Services.deps.json']
        for name in ['DynamicContracts', 'ReplacementContracts']:
            roots['SharpLink.' + name] = [*PLUGIN_DLLS, 'SharpLink.DynamicPlugin.Contracts.deps.json']
    return {f'test/{name}/bin/Release/net10.0': files for name, files in roots.items()}


def inventory(source, kind='tcp'):
    result, missing = {}, []
    for directory, required in required_files(kind).items():
        base = source / directory
        for name in required:
            path = base / name
            if not path.is_file() or path.stat().st_size == 0:
                missing.append(str(path.relative_to(source)))
        if base.exists():
            for path in sorted(base.rglob('*')):
                if path.is_file() and (path.suffix == '.dll' or path.name.endswith(('.runtimeconfig.json', '.deps.json'))):
                    if path.is_symlink() or not path.resolve().is_relative_to(source.resolve()):
                        raise ValueError(f'Binary input escapes source checkout: {path}')
                    result[str(path.relative_to(source))] = hashlib.sha256(path.read_bytes()).hexdigest()
    if missing:
        raise ValueError(f'Missing or empty required binary inputs: {missing}')
    if not result:
        raise ValueError('Empty binary inventory')
    return result


def assert_same(before, after):
    if not before or before != after:
        raise ValueError('Executed binary input set or bytes changed')


def nonempty(path):
    if not path.is_file() or path.stat().st_size == 0:
        raise ValueError(f'Required evidence is missing or empty: {path}')
    return path.read_text()


def source_identity(source):
    if git(source, 'rev-parse', 'HEAD') != SOURCE or git(source, 'rev-parse', 'HEAD^{tree}') != TREE:
        raise ValueError('Source identity changed')
    if git(source, 'status', '--porcelain', '--untracked-files=no'):
        raise ValueError('Tracked source changed')
    return {'source_sha': SOURCE, 'source_tree': TREE}


def verify_chaos(report, kind):
    import datetime
    import math
    if not (report['Status'] == 'Passed' and report['Phase'] == 'Completed'
            and report['IsFinal'] is True and report['ExitCode'] == 0
            and report['Commit'] == SOURCE and report['WorkingTreeDirty'] is False):
        raise ValueError('Chaos report is not a clean successful final result for source')
    expected = {'DurationSeconds': 600, 'CheckpointIntervalSeconds': 120,
                'RestartIntervalSeconds': 60, 'Concurrency': 32,
                'Transport': 'Tcp' if kind == 'tcp' else 'SharedMemory',
                'DumpOnFailure': True, 'StopOnUnexpectedFailure': True}
    if any(report[key] != value for key, value in expected.items()):
        raise ValueError('Chaos report options differ from original release gate')
    start = datetime.datetime.fromisoformat(report['StartedUtc'].replace('Z', '+00:00'))
    end = datetime.datetime.fromisoformat(report['TimestampUtc'].replace('Z', '+00:00'))
    # Original timer begins before warmup; ActualElapsedSeconds begins after it.
    # Do not silently impose a different 600s workload clock on the original runner.
    if (end - start).total_seconds() < 600 or not math.isfinite(report['ActualElapsedSeconds']) or report['ActualElapsedSeconds'] <= 0:
        raise ValueError('Chaos elapsed-time evidence is incomplete')
    if report['UnexpectedFailures'] != 0 or report['UnobservedTaskExceptions'] != 0:
        raise ValueError('Chaos failure counters are nonzero')
    if report['TerminalFailure'] is not None or report['ClientErrors'] or report['ServerErrors']:
        raise ValueError('Chaos terminal/log errors are not empty')
    if any(report[key] <= 0 for key in ['Success', 'ExpectedFailures', 'RestartCount']):
        raise ValueError('Original chaos coverage is incomplete')
    operations = {'Unary', 'ServerStreamingEarlyBreak', 'ClientStreaming', 'Cancellation', 'OneWay', 'DuplexStreaming'}
    if set(report['OperationAttempts']) != operations or any(value <= 0 for value in report['OperationAttempts'].values()):
        raise ValueError('Original chaos operation coverage is incomplete')
    drain = report['Drain']
    if drain['Drained'] is not True:
        raise ValueError('Chaos final resources did not drain')
    for metrics in [report['FinalMetrics'], drain['Metrics']]:
        if set(metrics) != METRICS or any(value != 0 for value in metrics.values()):
            raise ValueError('Chaos required final metrics are not zero')
    if report['ActiveCallBreakdown'] or drain['ActiveCallBreakdown']:
        raise ValueError('Chaos final active call breakdown is not empty')
    if not report['Runtime'] or not report['MemorySamples'] or not report['ServerStops']:
        raise ValueError('Chaos runtime/final resource samples are missing')
    if report['ServerStops'][-1]['Reason'] != 'FinalStop' or report['ServerStops'][-1]['ActiveCallsAfterStop'] != 0:
        raise ValueError('Chaos final server-stop evidence is incomplete')
    return {'actual_runtime': report['Runtime'], 'actual_elapsed_seconds': report['ActualElapsedSeconds'],
            'timer_elapsed_seconds': (end - start).total_seconds(),
            'six_hour_memory_window': 'not covered', 'final_metrics': report['FinalMetrics']}


def parse_summary(text):
    rows = [line.split('=', 1) for line in text.splitlines() if line]
    if any(len(row) != 2 for row in rows) or len({row[0] for row in rows}) != len(rows):
        raise ValueError('Malformed or duplicate ABI summary')
    return dict(rows)


def abi_uids(discovery):
    groups = [[test['uid'] for test in discovery['tests'] if test['type']['methodName'] == method]
              for method in METHODS]
    if [len(group) for group in groups] != [1, 1, 5, 1, 1]:
        raise ValueError('ABI selected discovery identities are incomplete')
    result = [uid for group in groups for uid in group]
    if any(not isinstance(uid, str) or not uid for uid in result) or len(set(result)) != 9:
        raise ValueError('ABI selected UIDs are empty or duplicated')
    return result


def verify_abi(summary, discovery, records):
    rounds = int(summary['rounds'])
    if summary['commit'] != SOURCE or int(summary['duration_seconds']) < 600 or rounds < 1:
        raise ValueError('ABI source/duration/full-round coverage is incomplete')
    expected = {'test_processes': rounds * 9, 'client_registration_replacements': rounds * 100,
                'route_publication_before_old_drain': rounds, 'api4_stream_exits': rounds * 5,
                'registration_rejections': rounds, 'framework_reference_unloads': rounds}
    if any(int(summary[key]) != value for key, value in expected.items()) or summary['api4_stream_exit_modes'] != MODES:
        raise ValueError('ABI final full-round counts are inconsistent')
    selected = abi_uids(discovery)
    test_records = [row for row in records if row['kind'] == 'test']
    if [row['uid'] for row in test_records] != selected * rounds:
        raise ValueError('Actual ABI invocations do not prove every complete final round')
    if any(row['exit_code'] != 0 or not row.get('actual_runtime') for row in test_records):
        raise ValueError('ABI actual test runtime or successful exit evidence missing')
    return {'duration_seconds': int(summary['duration_seconds']), 'rounds': rounds,
            'test_processes': len(test_records), 'actual_runtimes': sorted({row['actual_runtime'] for row in test_records}),
            'resource_release_evidence': 'all original registration/replacement/ALC release assertions passed in every recorded test process'}


def verify_test_log(text, process_count):
    import re
    text = re.sub(r'\x1b\[[0-9;]*m', '', text)
    for key, expected in [('total', 1), ('succeeded', 1), ('skipped', 0)]:
        values = [int(value) for value in re.findall(r'\b' + key + r':\s*(\d+)\b', text)]
        if values != [expected] * process_count:
            raise ValueError(f'ABI test log does not prove one successful unskipped case per process: {key}={values}')
    failed = [int(value) for value in re.findall(r'\bfailed:\s*(\d+)\b', text)]
    if any(failed) or len(failed) > process_count:
        raise ValueError('ABI test log contains a failed or ambiguous result')


def complete(source, kind, evidence):
    source_identity(source)
    before = json.loads(nonempty(evidence / 'built-before.json'))
    after = inventory(source, kind)
    assert_same(before['files'], after)
    records = [json.loads(line) for line in nonempty(evidence / 'commands.jsonl').splitlines()]
    if not records or records[0]['kind'] != 'build' or records[0]['exit_code'] != 0 or any(row['exit_code'] != 0 or row.get('tool_error') for row in records):
        raise ValueError('Command ledger is not a complete successful run')
    nonempty(evidence / 'dotnet-info.txt')
    from dotnet import classify, actual_test_runtime
    config = json.loads(nonempty(evidence / 'host-config.json'))
    if config['source'] != str(source.resolve()) or config['kind'] != kind:
        raise ValueError('Recorded dotnet configuration identifies another source/job')
    if before.get('source_sha') != SOURCE or before.get('source_tree') != TREE:
        raise ValueError('Pre-run binary manifest identifies another source')
    manifest_sha = hashlib.sha256((evidence / 'built-before.json').read_bytes()).hexdigest()
    for index, row in enumerate(records):
        classified, uid = classify(row['argv'], config)
        if (row['sequence'] != index or row['kind'] != classified or row['uid'] != uid
                or row['cwd'] != str(source.resolve()) or row['dotnet'] != config['dotnet']):
            raise ValueError('Command ledger identity/argv/order mismatch')
        if row['kind'] in {'discovery', 'test', 'chaos'} and row.get('binary_manifest_sha256') != manifest_sha:
            raise ValueError('Actual invocation is not bound to the pre-run binary manifest')
        if row['kind'] == 'test':
            if row.get('host_trace') != f'host-{index:05d}.log':
                raise ValueError('Actual test host trace identity is missing')
            runtime = actual_test_runtime(evidence / row['host_trace'], source.resolve())
            if row.get('actual_runtime') != runtime:
                raise ValueError('Actual test runtime disagrees with raw host evidence')
    if kind == 'abi':
        output = source / 'artifacts/p3-generated-abi/dynamic-module-soak'
        test_log = nonempty(output / 'test.log')
        if [row['kind'] for row in records[:2]] != ['build', 'discovery'] or any(row['kind'] != 'test' for row in records[2:]):
            raise ValueError('ABI command sequence is incomplete')
        result = verify_abi(parse_summary(nonempty(output / 'summary.txt')),
                            json.loads(nonempty(output / 'tests.json')), records)
        verify_test_log(test_log, result['test_processes'])
    else:
        if [row['kind'] for row in records] != ['build', 'chaos']:
            raise ValueError('Chaos command sequence is incomplete')
        result = verify_chaos(json.loads(nonempty(source / f'artifacts/chaos/release-10m-{kind}.json')), kind)
    return {**source_identity(source), 'binary_file_count': len(after), 'verified': True, **result}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('mode', choices=['validate', 'inventory', 'complete', 'setup'])
    parser.add_argument('--source', type=Path, default=Path('source'))
    parser.add_argument('--kind', choices=['tcp', 'sharedmemory', 'abi'], default='tcp')
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    args.output.parent.mkdir(parents=True, exist_ok=True)
    try:
        if args.mode == 'validate':
            data = validate(Path.cwd(), args.source, os.environ['GITHUB_SHA'])
        elif args.mode == 'setup':
            import shutil
            source_identity(args.source)
            original = shutil.which('dotnet')
            if original is None:
                raise ValueError('Original dotnet is not on PATH')
            original = str(Path(original).resolve())
            data = {'dotnet': original, 'dotnet_sha256': hashlib.sha256(Path(original).read_bytes()).hexdigest(),
                    'source': str(args.source.resolve()), 'kind': args.kind,
                    'evidence': str(args.output.parent.resolve()), 'original_path': os.environ['PATH']}
            shim = Path(os.environ['RUNNER_TEMP']) / 'stability-dotnet'
            shim.mkdir(exist_ok=False)
            (shim / 'dotnet').symlink_to(Path(__file__).resolve().with_name('dotnet.py'))
            with open(os.environ['GITHUB_ENV'], 'a') as handle:
                handle.write(f'STABILITY_CONFIG={args.output.resolve()}\n')
            with open(os.environ['GITHUB_PATH'], 'a') as handle:
                handle.write(f'{shim}\n')
        elif args.mode == 'complete':
            data = complete(args.source, args.kind, args.source / 'artifacts/stability')
        else:
            data = {**source_identity(args.source), 'files': inventory(args.source, args.kind)}
        args.output.write_text(json.dumps(data, indent=2) + '\n')
        print(json.dumps(data, indent=2))
    except Exception as error:
        args.output.write_text(json.dumps({'verified': False, 'tool_error': str(error)}, indent=2) + '\n')
        raise


if __name__ == '__main__':
    main()
