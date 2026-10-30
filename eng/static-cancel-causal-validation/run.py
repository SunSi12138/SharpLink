#!/usr/bin/env python3
"""Frozen baseline flags6 + TCP2, candidate TCP2. Never reruns a full suite."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import tempfile
from validate import require, strict_json, read_report, discovery_uids, unique_uids
import hosts

HERE = Path(__file__).resolve().parent
PINS = strict_json((HERE / 'pins.json').read_text())
FLAGS = 'SharpLink.UnitTests.Client.SharpLinkClientStreamingRequestFlagsTests.1.1.RequestShouldAdvertiseCancellationForOwnedStreams(SharpLink.Abstractions.RpcMethodKind, System.Boolean, System.Boolean, System.Boolean).'
TCP = 'SharpLink.IntegrationTests.StaticServerStreamCancellationCausalValidationTests.1.1.EarlyTerminationHasCompletedCancellationCausalWitness(System.Boolean).'
FLAGS_UIDS = [FLAGS + str(i) + '.1.0' for i in range(1, 7)]
TCP_UIDS = [TCP + str(i) + '.1.0' for i in range(1, 3)]
FLAGS_ERROR = 'stream ownership, not only a caller token or deadline, must enable wire cancellation'
NEGATIVE = 'STATIC_NONE_CANCEL_CAUSAL_V1: published static request was not cancellable; consumer completed but producer stayed pending for 10000ms; explicit release joined physical cleanup without overlap.'
RECORD = 'STATIC_CANCEL_CAUSAL_RECORD|'


def git(root, *args):
    return subprocess.check_output(['git', '-C', str(root), *args], text=True).strip()


def verify_release(root, sha, ref, pins=PINS):
    require(ref == pins['branch'] and re.fullmatch('[0-9a-f]{40}', sha), 'wrong release ref/SHA')
    require(git(root, 'rev-parse', 'HEAD') == sha, 'release HEAD mismatch')
    require(not git(root, 'status', '--porcelain', '--untracked-files=all'), 'release not clean')
    request = strict_json((root / pins['request']).read_text())
    require(isinstance(request, dict) and set(request) == {'schema', 'tools_sha', 'tools_tree', 'baseline_sha', 'candidate_sha'}, 'wrong request shape')
    require(type(request['schema']) is int and request['schema'] == 1, 'wrong schema')
    require(all(isinstance(request[k], str) and re.fullmatch('[0-9a-f]{40}', request[k]) for k in request if k != 'schema'), 'invalid request identity')
    tools = request['tools_sha']
    require(git(root, 'show', '-s', '--format=%P', sha) == tools, 'request parent not tools')
    require(git(root, 'rev-parse', tools + '^{tree}') == request['tools_tree'], 'tools tree mismatch')
    require(git(root, 'diff', '--name-status', '--no-renames', tools, sha) == 'A\t' + pins['request'], 'not request-only')
    source = pins['arms']['candidate']['sha']
    require(git(root, 'show', '-s', '--format=%P', tools) == source, 'tools parent mismatch')
    # Check the tools COMMIT, not request HEAD; request is intentionally outside the tool manifest.
    require(git(root, 'diff', '--name-status', '--no-renames', source, tools).splitlines() ==
            sorted('A\t' + p for p in pins['tool_files']), 'unexpected tool files')
    for arm in ('baseline', 'candidate'):
        require(request[arm + '_sha'] == pins['arms'][arm]['sha'], 'wrong source pin')
    return request


def verify_source(root, arm, pins=PINS):
    pin = pins['arms'][arm]
    require(git(root, 'rev-parse', 'HEAD') == pin['sha'], 'source SHA mismatch')
    require(git(root, 'rev-parse', 'HEAD^{tree}') == pin['tree'], 'source tree mismatch')
    require(not git(root, 'status', '--porcelain', '--untracked-files=all'), 'source not clean')
    require(git(root, 'show', '-s', '--format=%P', 'HEAD') == pin['parent'], 'source parent mismatch')
    require(git(root, 'rev-parse', pin['parent'] + '^{tree}') == pin['parent_tree'], 'parent tree mismatch')
    require(git(root, 'diff', '--name-status', '--no-renames', pin['parent'], 'HEAD').splitlines() ==
            sorted('A\t' + p for p in pin['added_paths']), 'not fixture-only overlay')
    for p, blob in pin['fixtures'].items():
        require(git(root, 'rev-parse', 'HEAD:' + p) == blob, 'fixture bytes mismatch')


def counters(summary, failed):
    expected = dict(total=1, passed=0 if failed else 1, failed=1 if failed else 0, skipped=0, cancelled=0, timedOut=0, flaky=0)
    require(isinstance(summary, dict) and summary == expected and all(type(v) is int for v in summary.values()), 'wrong exact counters')


def causal_record(record, row, baseline):
    keys = {'schema', 'earlyBreak', 'runtime', 'producerAwaitRegistered', 'consumerCompleted', 'initialCancellable',
            'observationMilliseconds', 'observationCompleted', 'elapsedMilliseconds', 'sameConnection',
            'releaseWasWithheld', 'teardownComplete', 'before', 'after', 'verdict'}
    require(isinstance(record, dict) and set(record) == keys, 'wrong causal record shape')
    require(type(record['schema']) is int and record['schema'] == 1, 'wrong causal schema')
    require(type(record['earlyBreak']) is bool and record['earlyBreak'] == (row == 2), 'wrong TCP termination')
    require(record['runtime'] == '10.0.14', 'wrong causal runtime')
    require(all(record[k] is True for k in ['producerAwaitRegistered', 'consumerCompleted', 'sameConnection', 'releaseWasWithheld', 'teardownComplete']), 'missing causal prerequisites')
    require(record['initialCancellable'] is (not baseline) and record['observationCompleted'] is (not baseline), 'wrong observed cancellation')
    require(type(record['observationMilliseconds']) is int and record['observationMilliseconds'] == 10000, 'changed observation budget')
    require(type(record['elapsedMilliseconds']) in (int, float) and record['elapsedMilliseconds'] >= 0, 'invalid elapsed time')
    if baseline:
        require(record['elapsedMilliseconds'] >= 10000, 'baseline did not observe the complete fixed window')
    before = dict(cancellation=0 if baseline else 1, moveCompleted=0 if baseline else 1, disposed=0 if baseline else 1, overlap=0)
    after = dict(cancellation=0 if baseline else 1, moveCompleted=1, disposed=1, overlap=0)
    for key, expected in [('before', before), ('after', after)]:
        require(isinstance(record[key], dict) and record[key] == expected and all(type(v) is int for v in record[key].values()), 'wrong physical witness')
    require(record['verdict'] == ('uncancellable-baseline' if baseline else 'cancelled-candidate'), 'wrong causal verdict')


def validate_report(report, uid, phase, code, timed_out):
    require(phase in ('baseline-flags', 'baseline-tcp', 'candidate-tcp'), 'unknown validation phase')
    baseline = phase == 'baseline-tcp'
    flag_failure = phase == 'baseline-flags' and uid == FLAGS_UIDS[0]
    failed = baseline or flag_failure
    require(not timed_out and code in ((1, 2) if failed else (0,)), 'process exit/timeout is not expected test result')
    require(type(report.get('schemaVersion')) is int and report['schemaVersion'] == 1, 'wrong report schema')
    assembly = 'SharpLink.UnitTests' if phase == 'baseline-flags' else 'SharpLink.IntegrationTests'
    require(report.get('assemblyName') == assembly and report.get('tunitVersion') == '1.73.19.0' and
            report.get('runtimeVersion') == '.NET 10.0.14', 'wrong report identity')
    require(isinstance(report.get('publicationGeneration'), str) and re.fullmatch('[0-9a-f]{32}', report['publicationGeneration']), 'not a final report')
    counters(report.get('summary'), failed)
    groups = report.get('groups')
    require(isinstance(groups, list) and len(groups) == 1, 'wrong groups')
    counters(groups[0].get('summary'), failed)
    tests = groups[0].get('tests')
    require(isinstance(tests, list) and len(tests) == 1, 'wrong result count')
    test = tests[0]
    require(test.get('id') == uid and test.get('status') == ('failed' if failed else 'passed') and
            type(test.get('retryAttempt', 0)) is int and test.get('retryAttempt', 0) == 0 and not test.get('attempts'), 'wrong UID/status/retry')
    if failed:
        error = test.get('exception')
        require(isinstance(error, dict) and not error.get('innerException'), 'missing/plain expected assertion')
        require(error.get('message') == (FLAGS_ERROR if flag_failure else NEGATIVE), 'generic failure is not causal evidence')
        require(error.get('type') in (('Exception', 'System.Exception') if flag_failure else ('InvalidOperationException', 'System.InvalidOperationException')), 'wrong assertion type')
        stack = error.get('stackTrace')
        require(isinstance(stack, str) and (('EnsureRequestFlags' in stack and 'RequestShouldAdvertiseCancellationForOwnedStreams' in stack) if flag_failure
                else 'EarlyTerminationHasCompletedCancellationCausalWitness' in stack), 'wrong assertion origin')
    if phase != 'baseline-flags':
        output = test.get('output')
        require(isinstance(output, str), 'missing probe output')
        rows = [strict_json(line[len(RECORD):]) for line in output.splitlines() if line.startswith(RECORD)]
        require(len(rows) == 1, 'missing or duplicate causal record')
        causal_record(rows[0], TCP_UIDS.index(uid) + 1, baseline)


def execute_recorded(command, root, env, timeout, log):
    outcome = dict(argv=command, cwd=str(root), timeout_seconds=timeout)
    unsafe = False
    try:
        code, timed_out = hosts.execute(command, root, env, timeout, log)
        outcome.update(exit_code=code, timed_out=timed_out)
        return code, timed_out
    except hosts.UnsafeProcessGroup as error:
        unsafe = True
        outcome.update(unsafe_process_group=repr(error))
        raise
    except Exception as error:
        outcome.update(execution_error=repr(error))
        raise
    finally:
        try:
            with log.with_name(log.name + '.outcome.json').open('x') as record:
                record.write(json.dumps(outcome, indent=2) + '\n')
        except Exception as error:
            if unsafe:
                raise hosts.UnsafeProcessGroup('unsafe process group; outer outcome could not be retained') from error
            raise


def attempt_case(action, phase, uid, stages):
    try:
        action()
        stages.append(dict(phase=phase, uid=uid, passed=True)); print('CASE PASS phase=' + phase + ' uid=' + uid, flush=True)
    except hosts.UnsafeProcessGroup as error:
        stages.append(dict(phase=phase, uid=uid, passed=False, unsafe_process_group=True, error=repr(error)))
        raise
    except Exception as error:
        stages.append(dict(phase=phase, uid=uid, passed=False, error=repr(error)))
        print('CASE FAIL phase=' + phase + ' uid=' + uid, flush=True)


def run_case(root, assembly, uid, phase, env, host, out):
    out.mkdir()
    command = [host, str(assembly), '--filter-uid', uid, '--maximum-parallel-tests', '1', '--minimum-expected-tests', '1',
               '--zero-tests-policy', 'strict', '--no-ansi', '--progress', 'off', '--show-stdout', 'None', '--show-stderr', 'None',
               '--show-test-results', 'failed,skipped', '--report-html', '--report-html-filename', str(out / 'result.html'),
               '--results-directory', str(out / 'results')]
    (out / 'argv.json').write_text(json.dumps(command) + '\n')
    code, timed_out = execute_recorded(command, root, env, 180, out / 'worker.log')
    reports = list(out.glob('*.tunit-report.json'))
    require(len(reports) == 1, 'missing/ambiguous final report')
    validate_report(read_report(reports[0]), uid, phase, code, timed_out)


def dlls(assembly):
    return {p.name: hashlib.sha256(p.read_bytes()).hexdigest() for p in sorted(assembly.parent.glob('*.dll'))}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--release-root', type=Path, required=True)
    parser.add_argument('--baseline-root', type=Path)
    parser.add_argument('--candidate-root', type=Path)
    parser.add_argument('--provenance-only', action='store_true')
    args = parser.parse_args()
    request = verify_release(args.release_root.resolve(), os.environ.get('GITHUB_SHA', ''), os.environ.get('GITHUB_REF', ''))
    if args.provenance_only:
        if os.environ.get('GITHUB_OUTPUT'):
            with open(os.environ['GITHUB_OUTPUT'], 'a') as f:
                for arm in ('baseline', 'candidate'): f.write(arm + '_sha=' + request[arm + '_sha'] + '\n')
        print('STATIC CANCEL PROVENANCE PASS'); return 0
    require(args.baseline_root and args.candidate_root, 'both source roots required')
    roots = {'baseline': args.baseline_root.resolve(), 'candidate': args.candidate_root.resolve()}
    for arm, root in roots.items(): verify_source(root, arm)
    env = {k: v for k, v in os.environ.items() if not k.startswith(('TUNIT_', 'TESTINGPLATFORM_'))}
    env.update(DOTNET_PROCESSOR_COUNT='4', DOTNET_CLI_TELEMETRY_OPTOUT='1', TESTINGPLATFORM_TELEMETRY_OPTOUT='1')
    sdk, host = hosts.verify_hosts(roots['baseline'], env)
    test_env = hosts.private_test_environment(env, host)
    output = Path(tempfile.mkdtemp(prefix='static-cancel-causal-', dir=env.get('RUNNER_TEMP')))
    stages = []
    try:
        for phase, arm, project, uids in [('baseline-flags', 'baseline', 'SharpLink.UnitTests', FLAGS_UIDS),
                                         ('baseline-tcp', 'baseline', 'SharpLink.IntegrationTests', TCP_UIDS),
                                         ('candidate-tcp', 'candidate', 'SharpLink.IntegrationTests', TCP_UIDS)]:
            root = roots[arm]; directory = output / phase; directory.mkdir()
            try:
                code, timed_out = execute_recorded([sdk, 'build', f'test/{project}/{project}.csproj', '-c', 'Release', '-m:1', '-nr:false',
                        '--disable-build-servers', '-p:UseSharedCompilation=false', '-v', 'minimal'], root, env, 900, directory / 'build.log')
                require(not timed_out and code == 0, 'build failed')
                assembly = root / f'test/{project}/bin/Release/net10.0/{project}.dll'
                identity = dlls(assembly)
                code, timed_out = execute_recorded([host, str(assembly), '--list-tests', 'json'], root, test_env, 180, directory / 'discovery.json')
                require(not timed_out and code == 0, 'discovery failed')
                discovery = read_report(directory / 'discovery.json'); actual = discovery_uids(discovery)
                prefix = FLAGS if phase == 'baseline-flags' else TCP
                require(sorted(u for u in actual if u.startswith(prefix)) == sorted(uids), 'focused discovery case set changed')
                expected_path = 'test/SharpLink.UnitTests/Client/SharpLinkClientStreamingRequestFlagsTests.cs' if phase == 'baseline-flags' else 'test/SharpLink.IntegrationTests/StaticServerStreamCancellationCausalValidationTests.cs'
                for test in discovery['tests']:
                    if test['uid'] in uids:
                        require(test.get('location', {}).get('file', '').replace('\\', '/').endswith('/' + expected_path), 'UID source mismatch')
                (directory / 'identity.json').write_text(json.dumps(dict(source=PINS['arms'][arm], dlls=identity, uids=uids), indent=2) + '\n')
                for index, uid in enumerate(uids):
                    attempt_case(lambda: run_case(root, assembly, uid, phase, test_env, host, directory / str(index)),
                                 phase, uid, stages)
                require(identity == dlls(assembly), 'binaries changed during run')
                verify_source(root, arm)
            except hosts.UnsafeProcessGroup as error:
                stages.append(dict(phase=phase, passed=False, unsafe_process_group=True, error=repr(error)))
                (directory / 'failure.json').write_text(json.dumps(dict(error=repr(error), unsafe_process_group=True)) + '\n')
                raise
            except Exception as error:
                stages.append(dict(phase=phase, passed=False, error=repr(error)))
                (directory / 'failure.json').write_text(json.dumps(dict(error=repr(error))) + '\n')
                print('PHASE FAIL phase=' + phase, flush=True)
    finally:
        (output / 'summary.json').write_text(json.dumps(dict(request=request, stages=stages), indent=2) + '\n')
    require(len(stages) == 10 and all(x['passed'] for x in stages), 'one or more fixed stages failed')
    for arm, root in roots.items(): verify_source(root, arm)
    verify_release(args.release_root.resolve(), os.environ.get('GITHUB_SHA', ''), os.environ.get('GITHUB_REF', ''))
    print('STATIC CANCEL CAUSAL PASS baseline_flags=6 baseline_tcp=2 candidate_tcp=2 runtime=10.0.14')
    return 0


if __name__ == '__main__':
    try: raise SystemExit(main())
    except Exception as error:
        print('STATIC CANCEL GATE FAIL type=' + type(error).__name__)
        raise SystemExit(1)
