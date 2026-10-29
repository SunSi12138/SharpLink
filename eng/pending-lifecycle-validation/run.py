#!/usr/bin/env python3
"""Nine isolated correctness scenarios, fixed product and private runtime, no uploads.

The canonical validate-pending-lifecycle.py is unchanged. This adapter retains its
scenario ordering, atomic phase witness and whole-process watchdog, but executes
an already-built assembly on the private runtime, never `dotnet run`.
"""
from __future__ import annotations

import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import re
import signal
import subprocess
import sys
import tempfile
import time

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent / 'static-stream-unit-validation'))
from validate import GateError, discovery_uids, read_report, require, strict_json, validate_run
from scope import explicit_workers, verify_explicit_sources

spec = importlib.util.spec_from_file_location('reviewed_unit_runner', HERE.parent / 'static-stream-unit-validation/run.py')
unit = importlib.util.module_from_spec(spec)
spec.loader.exec_module(unit)

SOURCE_SHA = '3d76a3a8b55a37e24c62a073fe588cff3d3867be'
SOURCE_TREE = '1c24da3af090acd4d6cdbfb005a9e3bd95304e3a'
BRANCH = 'refs/heads/validation/pending-lifecycle-no-upload-20261029'
REQUEST = 'eng/pending-lifecycle-validation/request.json'
TOOL_FILES = sorted(['eng/pending-lifecycle-validation/run.py',
                     'eng/pending-lifecycle-validation/test_validation.py',
                     '.github/workflows/pending-lifecycle-no-upload.yml'])
PHASES = (
    ('pending-controls', ('no-listener', 'metric-control')),
    ('pending-deadline', ('deadline-response', 'deadline-cancel', 'deadline-disconnect')),
    ('pending-disposal', ('metric-minus', 'metric-plus')),
    ('admission-diagnostics', ('logger-control', 'logger-throw')),
)
SCENARIOS = tuple(s for _, scenarios in PHASES for s in scenarios)


def git(root: Path, *args: str) -> str:
    return subprocess.check_output(['git', '-C', str(root), *args], text=True).strip()


def verify_source(root: Path) -> None:
    require(git(root, 'rev-parse', 'HEAD') == SOURCE_SHA, 'source SHA mismatch')
    require(git(root, 'rev-parse', 'HEAD^{tree}') == SOURCE_TREE, 'source tree mismatch')
    require(not git(root, 'status', '--porcelain', '--untracked-files=all'), 'source not clean')


def verify_release(root: Path, release_sha: str, ref: str) -> dict:
    require(ref == BRANCH, 'wrong branch')
    require(re.fullmatch('[0-9a-f]{40}', release_sha) is not None, 'invalid release SHA')
    require(git(root, 'rev-parse', 'HEAD') == release_sha, 'release SHA mismatch')
    require(not git(root, 'status', '--porcelain', '--untracked-files=all'), 'release not clean')
    request = strict_json((root / REQUEST).read_text())
    require(isinstance(request, dict) and set(request) == {'schema_version', 'tools_sha', 'tools_tree'}, 'invalid request')
    require(type(request['schema_version']) is int and request['schema_version'] == 1, 'invalid schema')
    for key in ('tools_sha', 'tools_tree'):
        require(isinstance(request[key], str) and re.fullmatch('[0-9a-f]{40}', request[key]) is not None, 'invalid tool identity')
    parent = request['tools_sha']
    require(git(root, 'show', '-s', '--format=%P', 'HEAD') == parent, 'request parent mismatch')
    require(git(root, 'rev-parse', parent + '^{tree}') == request['tools_tree'], 'tool tree mismatch')
    require(git(root, 'diff', '--name-status', '--no-renames', parent, 'HEAD') == 'A\t' + REQUEST, 'not request-only')
    require(git(root, 'show', '-s', '--format=%P', parent) == SOURCE_SHA, 'tool parent not frozen product')
    require(git(root, 'rev-parse', SOURCE_SHA + '^{tree}') == SOURCE_TREE, 'product ancestry tree mismatch')
    delta = git(root, 'diff', '--name-status', '--no-renames', SOURCE_SHA, parent).splitlines()
    require(delta == sorted('A\t' + f for f in TOOL_FILES), 'tools modify files outside frozen validation scope')
    return request


def worker_uid(scenario: str) -> str:
    require(scenario in SCENARIOS, 'unknown scenario')
    worker = 'AdmissionDiagnosticsValidationProbe' if scenario.startswith('logger-') else 'PendingLifecycleValidationProbe'
    return 'SharpLink.UnitTests.Validation.' + worker + '.1.1.Run.1.1.0'


def worker_command(host: str, assembly: Path, scenario: str, output: Path) -> list[str]:
    return [host, str(assembly), '--filter-uid', worker_uid(scenario),
            '--maximum-parallel-tests', '1', '--minimum-expected-tests', '1',
            '--zero-tests-policy', 'strict', '--no-ansi', '--progress', 'off',
            '--show-stdout', 'None', '--show-stderr', 'None',
            '--show-test-results', 'failed,skipped', '--report-html',
            '--report-html-filename', str(output / 'result.html'),
            '--results-directory', str(output / 'results')]


def validate_witness(scenario: str, report: dict) -> None:
    require(isinstance(report, dict) and report.get('phase') == 'complete'
            and report.get('scenario') == scenario and report.get('invariant') is True,
            'incomplete or failed scenario invariant')
    def eq(key, expected):
        require(key in report and type(report[key]) is type(expected) and report[key] == expected,
                'scenario witness mismatch: ' + key)
    if scenario.startswith('deadline-'):
        for key in ('sameReference', 'futureBefore', 'futureAfter'):
            eq(key, True)
        eq('firstError', 'DeadlineExceeded')
        eq('oldReason', 'DeadlineExceeded')
        eq('secondError', None)
        eq('active', 0)
        eq('count', 0)
        require(type(report.get('firstId')) is int and type(report.get('secondId')) is int
                and report['firstId'] != report['secondId'], 'request identity was not renewed')
    elif scenario.startswith('logger-'):
        for key in ('policyAcquires', 'policyReports', 'loggerReports'):
            eq(key, 1)
        for key in ('countAfter', 'activeAfter', 'connectionsOpened'):
            eq(key, 0)
        for key in ('completed', 'returned', 'nextSucceeded'):
            eq(key, True)
        eq('escaped', None)
    else:
        eq('escaped', None)
        eq('positiveHits', 0 if scenario == 'no-listener' else 1)
        eq('negativeHits', 0 if scenario in ('no-listener', 'metric-plus') else 1)
        eq('hits', 0 if scenario == 'no-listener' else (1 if scenario == 'metric-plus' else 2))
        eq('active', 0)
        eq('count', 0)
        if scenario == 'metric-plus':
            for key in ('registered', 'ownerRegistered', 'countBefore', 'activeBefore'):
                eq(key, 1)
            eq('operationError', 'ConnectionClosed')
        else:
            eq('operationError', None)
            eq('nextSucceeded', True)
            eq('nextError', None)
            eq('ownerRegisteredBeforeNext', 1)
            eq('countBefore', 0)
            eq('activeBefore', 0)


def validate_worker_report(scenario: str, report: dict, exit_code: int) -> None:
    require(report.get('runtimeVersion') == '.NET 10.0.14', 'worker runtime mismatch')
    validate_run([worker_uid(scenario)], report, exit_code)


def run_probe(root: Path, host: str, assembly: Path, env: dict, scenario: str, output: Path) -> dict:
    output.mkdir()
    result_path = output / 'scenario.json'
    environment = dict(env, SHARPLINK_VALIDATION_SCENARIO=scenario,
                       SHARPLINK_VALIDATION_OUTPUT=str(result_path))
    command = worker_command(host, assembly, scenario, output)
    started = time.monotonic()
    armed_at = None
    timeout_kind = None
    with (output / 'worker.log').open('w') as log:
        process = subprocess.Popen(command, cwd=root, env=environment, stdout=log,
                                   stderr=subprocess.STDOUT, start_new_session=True)
        try:
            while process.poll() is None:
                if result_path.exists():
                    witness = read_report(result_path)
                    if witness.get('phase') == 'dispose-enter' and armed_at is None:
                        armed_at = time.monotonic()
                now = time.monotonic()
                if (armed_at is not None and now - armed_at > 15) or now - started > 120:
                    timeout_kind = 'dispose-15s' if armed_at is not None and now - armed_at > 15 else 'process-120s'
                    os.killpg(process.pid, signal.SIGKILL)
                    process.wait(timeout=10)
                    break
                time.sleep(0.05)  # External watchdog only, never race coordination.
            process.wait(timeout=10)
        finally:
            if process.poll() is None:
                os.killpg(process.pid, signal.SIGKILL)
                process.wait(timeout=10)
    record = {'scenario': scenario, 'uid': worker_uid(scenario), 'exit_code': process.returncode,
              'watchdog': timeout_kind, 'elapsed_seconds': round(time.monotonic() - started, 3)}
    (output / 'execution.json').write_text(json.dumps(record, indent=2) + '\n')
    require(timeout_kind is None, 'worker watchdog expired')
    require(process.returncode == 0, 'worker exited unsuccessfully')
    reports = list(output.glob('*.tunit-report.json'))
    require(len(reports) == 1, 'missing or ambiguous worker runtime/test report')
    test_report = read_report(reports[0])
    validate_worker_report(scenario, test_report, process.returncode)
    validate_witness(scenario, read_report(result_path))
    return dict(record, passed=True, runtime='10.0.14', executed=1)


def hashes(assembly: Path) -> dict:
    return {p.name: hashlib.sha256(p.read_bytes()).hexdigest() for p in sorted(assembly.parent.glob('*.dll'))}


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument('--release-root', type=Path, required=True)
    parser.add_argument('--source-root', type=Path, required=True)
    parser.add_argument('--provenance-only', action='store_true')
    args = parser.parse_args()
    root = args.source_root.resolve()
    request = verify_release(args.release_root.resolve(), os.environ.get('GITHUB_SHA', ''), os.environ.get('GITHUB_REF', ''))
    verify_source(root)
    if args.provenance_only:
        print('PROVENANCE PASS source=' + SOURCE_SHA + ' tree=' + SOURCE_TREE + ' tools=' + request['tools_sha'])
        return 0
    env = {k: v for k, v in os.environ.items() if not k.startswith(('TUNIT_', 'TESTINGPLATFORM_'))}
    env.update(DOTNET_PROCESSOR_COUNT='4', DOTNET_CLI_TELEMETRY_OPTOUT='1',
               TESTINGPLATFORM_TELEMETRY_OPTOUT='1', PYTHONDONTWRITEBYTECODE='1')
    sdk, host = unit.verify_hosts(root, env)
    # Retained only on this ephemeral runner, including failures. No uploader/cache.
    output = Path(tempfile.mkdtemp(prefix='pending-lifecycle-', dir=env.get('RUNNER_TEMP')))
    records = []
    try:
        code, timed_out = unit.execute([sdk, 'build', unit.PROJECT, '-c', 'Release', '-m:1',
                                      '-nr:false', '--disable-build-servers', '-p:UseSharedCompilation=false',
                                      '-v', 'minimal'], root, env, 900, output / 'build.log')
        require(code == 0 and not timed_out, 'worker build failed')
        assembly = root / unit.ASSEMBLY
        identity = hashes(assembly)
        require(assembly.name in identity, 'missing test assembly')
        test_env = unit.private_test_environment(env, host)
        code, timed_out = unit.execute([host, str(assembly), '--list-tests', 'json'], root, test_env,
                                      180, output / 'discovery.json')
        require(code == 0 and not timed_out, 'worker discovery failed')
        discovery = read_report(output / 'discovery.json')
        actual = discovery_uids(discovery)
        expected = strict_json((root / 'eng/static-stream-unit-validation/expected-uids.json').read_text())
        require(actual == expected, 'fresh discovery differs from frozen product corpus')
        workers = explicit_workers(root / 'eng/static-stream-unit-validation')
        verify_explicit_sources(root, discovery, workers)
        require(set(worker_uid(s) for s in SCENARIOS).issubset(actual), 'required worker UID missing')
        provenance = dict(source_sha=SOURCE_SHA, source_tree=SOURCE_TREE, tools=request,
                          sdk='10.0.102', runtime='10.0.14', assembly_sha256=identity,
                          scenarios=SCENARIOS, discovered=len(actual))
        (output / 'provenance.json').write_text(json.dumps(provenance, indent=2) + '\n')
        print('PREPARE PASS source=' + SOURCE_SHA + ' sdk=10.0.102 runtime=10.0.14 workers=2 scenarios=9', flush=True)
        for phase, scenarios in PHASES:
            for scenario in scenarios:
                try:
                    record = run_probe(root, host, assembly, test_env, scenario, output / scenario)
                    print('SCENARIO PASS ' + scenario + ' executed=1 runtime=10.0.14', flush=True)
                except Exception as error:
                    record = dict(scenario=scenario, passed=False, error_type=type(error).__name__, detail=str(error))
                    print('SCENARIO FAIL ' + scenario + ' error=' + type(error).__name__, flush=True)
                records.append(record)
            print('PHASE ' + phase + ' passed=' + str(sum(r['passed'] for r in records if r['scenario'] in scenarios))
                  + '/' + str(len(scenarios)), flush=True)
        require(hashes(assembly) == identity, 'assembly identity changed during execution')
        verify_source(root)
        require(len(records) == 9 and all(r['passed'] for r in records), 'one or more scenarios failed')
        print('PENDING LIFECYCLE GATE PASS scenarios=9 workers=2 runtime=10.0.14', flush=True)
        return 0
    except Exception as error:
        (output / 'failure.json').write_text(json.dumps({'type': type(error).__name__, 'detail': str(error)}, indent=2) + '\n')
        print('PENDING LIFECYCLE GATE FAIL error=' + type(error).__name__, flush=True)
        return 1
    finally:
        (output / 'summary.json').write_text(json.dumps({'source_sha': SOURCE_SHA, 'records': records}, indent=2) + '\n')


if __name__ == '__main__':
    try:
        sys.exit(main())
    except Exception as error:
        print('PENDING LIFECYCLE PREFLIGHT FAIL error=' + type(error).__name__, flush=True)
        sys.exit(1)
