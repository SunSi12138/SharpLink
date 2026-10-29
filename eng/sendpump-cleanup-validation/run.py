#!/usr/bin/env python3
"""Baseline-only TUnit status calibration; retain the earlier inconclusive run."""
from __future__ import annotations
import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tempfile

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent / 'static-stream-unit-validation'))
from validate import GateError, discovery_uids, read_report, require, strict_json, uid_digest, unique_uids, validate_run
from scope import explicit_workers, ordinary_uids, verify_explicit_sources
spec = importlib.util.spec_from_file_location('reviewed_unit_runner', HERE.parent / 'static-stream-unit-validation/run.py')
unit = importlib.util.module_from_spec(spec)
spec.loader.exec_module(unit)

BASE_SHA = '3d76a3a8b55a37e24c62a073fe588cff3d3867be'
BASE_TREE = '1c24da3af090acd4d6cdbfb005a9e3bd95304e3a'
SOURCE_TREE = '410fdb911cf7120ccabbbd10c820b0c9a73d9223'
OVERLAY_TREE = '63889de3c423c1429af4a80588e9b0d79ef8d36a'
TEST_PATH = 'test/SharpLink.UnitTests/Runtime/SendPumpCleanupProgressTests.cs'
PRODUCT_PATH = 'src/SharpLink.Runtime/RpcSession.SendPump.cs'
TEST_BLOB = '0573677c1d83b95c2ed75d5e8f0d395babc7d49e'
BRANCH = 'refs/heads/validation/sendpump-baseline-calibration-v2-20261029'
REQUEST = 'eng/sendpump-cleanup-validation/request.json'
TOOL_FILES = sorted(['.github/workflows/sendpump-cleanup-no-upload.yml',
                     'eng/sendpump-cleanup-validation/run.py', 'eng/sendpump-cleanup-validation/test_validation.py',
                     'eng/sendpump-cleanup-validation/new-uids.json'])
NEW_UIDS = unique_uids(strict_json((HERE / 'new-uids.json').read_text()), 'new regression manifest')
BASELINE_UID = 'SharpLink.UnitTests.Runtime.SendPumpCleanupProgressTests.1.1.ThrowingBatchReturnMustNotReleaseRerentedOwnerOrAbandonEitherQueue.1.1.0'
BASELINE_ASSERTION = ('SENDPUMP_STALE_SLOT_CAUSAL_V1: completed cleanup retried a retired batch slot '
                      'and abandoned later batch and queued owners.')
TCP_CASES = [(condition, termination) for condition in ('static-none', 'static-cancellable', 'dynamic-none', 'static-deadline')
             for termination in ('early-break', 'explicit-dispose', 'consumer-throw')]
TCP_CASES += [('static-enumerator-only', 'enumerator-cancel'), ('static-none-held', 'explicit-dispose')]


def git(root: Path, *args: str) -> str:
    return subprocess.check_output(['git', '-C', str(root), *args], text=True).strip()


def verify_release(root: Path, release_sha: str, ref: str) -> dict:
    require(ref == BRANCH and re.fullmatch('[0-9a-f]{40}', release_sha) is not None, 'wrong release ref/SHA')
    require(git(root, 'rev-parse', 'HEAD') == release_sha, 'release SHA mismatch')
    require(not git(root, 'status', '--porcelain', '--untracked-files=all'), 'release not clean')
    request = strict_json((root / REQUEST).read_text())
    require(isinstance(request, dict) and set(request) == {'schema_version', 'source_sha', 'baseline_sha', 'tools_sha', 'tools_tree'}, 'invalid request')
    require(type(request['schema_version']) is int and request['schema_version'] == 1, 'wrong request schema')
    for key in ('source_sha', 'baseline_sha', 'tools_sha', 'tools_tree'):
        require(isinstance(request[key], str) and re.fullmatch('[0-9a-f]{40}', request[key]) is not None, 'invalid request identity')
    source, tools = request['source_sha'], request['tools_sha']
    require(git(root, 'show', '-s', '--format=%P', 'HEAD') == tools, 'request parent mismatch')
    require(git(root, 'rev-parse', tools + '^{tree}') == request['tools_tree'], 'tools tree mismatch')
    require(git(root, 'diff', '--name-status', '--no-renames', tools, 'HEAD') == 'A\t' + REQUEST, 'not request-only')
    require(git(root, 'show', '-s', '--format=%P', tools) == source, 'tool parent mismatch')
    require(git(root, 'rev-parse', source + '^{tree}') == SOURCE_TREE, 'unreviewed source tree')
    require(git(root, 'show', '-s', '--format=%P', source) == BASE_SHA, 'source parent mismatch')
    require(git(root, 'rev-parse', BASE_SHA + '^{tree}') == BASE_TREE, 'base product tree mismatch')
    require(git(root, 'diff', '--name-status', '--no-renames', BASE_SHA, source).splitlines()
            == ['M\t' + PRODUCT_PATH, 'A\t' + TEST_PATH], 'wrong source diff')
    require(git(root, 'rev-parse', source + ':' + TEST_PATH) == TEST_BLOB, 'regression test bytes mismatch')
    require(git(root, 'diff', '--name-status', '--no-renames', source, tools).splitlines()
            == sorted('A\t' + path for path in TOOL_FILES), 'wrong tool scope')
    return request


def verify_source(root: Path, sha: str, baseline: bool) -> None:
    require(git(root, 'rev-parse', 'HEAD') == sha, 'execution source SHA mismatch')
    require(git(root, 'rev-parse', 'HEAD^{tree}') == (OVERLAY_TREE if baseline else SOURCE_TREE), 'execution source tree mismatch')
    require(not git(root, 'status', '--porcelain', '--untracked-files=all'), 'execution source not clean')
    require(git(root, 'show', '-s', '--format=%P', 'HEAD') == BASE_SHA, 'execution source parent mismatch')
    require(git(root, 'rev-parse', BASE_SHA + '^{tree}') == BASE_TREE, 'execution base tree mismatch')
    changes = ([] if baseline else ['M\t' + PRODUCT_PATH]) + ['A\t' + TEST_PATH]
    require(git(root, 'diff', '--name-status', '--no-renames', BASE_SHA, 'HEAD').splitlines() == changes, 'execution source scope mismatch')
    require(git(root, 'rev-parse', 'HEAD:' + TEST_PATH) == TEST_BLOB, 'baseline/candidate test bytes differ')


def census(root: Path, discovery: dict) -> list[str]:
    original = unique_uids(strict_json((root / 'eng/static-stream-unit-validation/expected-uids.json').read_text()), 'original corpus')
    require(len(original) == 2310 and len(NEW_UIDS) == 6 and not set(original).intersection(NEW_UIDS), 'manifest counts mismatch')
    raw = discovery_uids(discovery)
    require(raw == sorted(original + NEW_UIDS), 'fresh raw UID census differs from reviewed old+new manifest')
    workers = explicit_workers(root / 'eng/static-stream-unit-validation')
    require(len(workers) == 5, 'explicit-worker count changed')
    verify_explicit_sources(root, discovery, workers)
    ordinary = ordinary_uids(raw, workers)
    require(len(raw) == 2316 and len(ordinary) == 2311, 'ordinary/raw count mismatch')
    new_tests = [t for t in discovery['tests'] if t['uid'] in NEW_UIDS]
    require(len(new_tests) == 6 and all(t.get('type', {}).get('namespace') == 'SharpLink.UnitTests.Runtime'
            and t.get('type', {}).get('typeName') == 'SendPumpCleanupProgressTests'
            and t.get('location', {}).get('file', '').replace('\\', '/').endswith('/' + TEST_PATH)
            for t in new_tests), 'new UID source mapping mismatch')
    print('CENSUS raw=2316 ordinary=2311 new=6 explicit_workers_not_executed=5 raw_sha256=' + uid_digest(raw)
          + ' ordinary_sha256=' + uid_digest(ordinary), flush=True)
    return ordinary


def hashes(directory: Path) -> dict:
    return {p.name: hashlib.sha256(p.read_bytes()).hexdigest() for p in sorted(directory.glob('*.dll'))}


def command(host: str, assembly: Path, output: Path, uid: str | None = None) -> list[str]:
    result = [host, str(assembly), '--maximum-parallel-tests', '1' if uid else '4',
              '--minimum-expected-tests', '1' if uid else '2311', '--zero-tests-policy', 'strict',
              '--no-ansi', '--progress', 'off', '--show-stdout', 'None', '--show-stderr', 'None',
              '--show-test-results', 'failed,skipped', '--report-html',
              '--report-html-filename', str(output / 'result.html'), '--results-directory', str(output / 'results')]
    return result + (['--filter-uid', uid] if uid else ['--timeout', '20m'])


def final_report(output: Path) -> dict:
    reports = list(output.glob('*.tunit-report.json'))
    require(len(reports) == 1, 'missing or ambiguous final TUnit report')
    return read_report(reports[0])


def bounded_scalar(value: object, limit: int = 160) -> object:
    if type(value) is int:
        return value if abs(value) < 1_000_000_000 else '<int-out-of-range>'
    if value is None or type(value) is bool:
        return value
    if isinstance(value, str):
        return value[:limit]
    return '<' + type(value).__name__ + '>'


def baseline_observation(report: dict) -> dict:
    """Only bounded identity/classification fields, never arbitrary failure text."""
    groups = report.get('groups')
    tests = groups[0].get('tests') if isinstance(groups, list) and len(groups) == 1 and isinstance(groups[0], dict) else None
    test = tests[0] if isinstance(tests, list) and len(tests) == 1 and isinstance(tests[0], dict) else {}
    error = test.get('exception')
    message = error.get('message') if isinstance(error, dict) else None
    marker = ('exact-reviewed-causal-marker' if message == BASELINE_ASSERTION else
              'former-generic-aggregate-assertion' if message == 'every distinct owner-cleanup failure must remain observable'
              else 'missing-exception' if not isinstance(error, dict) else 'unreviewed-assertion')
    attempts = test.get('attempts')
    return dict(uid=bounded_scalar(test.get('id'), 220), status=bounded_scalar(test.get('status'), 32),
                retry_present='retryAttempt' in test, retry=bounded_scalar(test.get('retryAttempt'), 32),
                attempts_type=type(attempts).__name__, attempts_count=len(attempts) if isinstance(attempts, list) else None,
                exception_type=bounded_scalar(error.get('type'), 120) if isinstance(error, dict) else None,
                inner_exception_present=bool(error.get('innerException')) if isinstance(error, dict) else False,
                marker=marker)


def validate_baseline(report: dict, code: int, timed_out: bool) -> None:
    # A timeout/crash/any-error is not causal proof. The frozen test emits this
    # marker only after completed cleanup, explicit non-timeout prerequisites and
    # the exact duplicate/abandoned-owner census. Generic assertions cannot pass.
    # Bind one exact UID, complete counters and runtime before accepting it.
    require(not timed_out and code in (1, 2), 'baseline did not terminate with an ordinary failing-test exit')
    require(type(report.get('schemaVersion')) is int and report['schemaVersion'] == 1, 'wrong baseline report schema')
    require(report.get('assemblyName') == 'SharpLink.UnitTests' and report.get('tunitVersion') == '1.73.19.0', 'wrong baseline assembly/TUnit')
    require(report.get('runtimeVersion') == '.NET 10.0.14', 'wrong baseline runtime')
    require(isinstance(report.get('publicationGeneration'), str)
            and re.fullmatch('[0-9a-f]{32}', report['publicationGeneration']) is not None, 'missing final baseline generation')
    expected = dict(total=1, passed=0, failed=1, skipped=0, cancelled=0, timedOut=0, flaky=0)
    require(report.get('summary') == expected and all(type(v) is int for v in report['summary'].values()), 'baseline counters not exactly one failed test')
    groups = report.get('groups')
    require(isinstance(groups, list) and len(groups) == 1 and groups[0].get('summary') == expected, 'baseline group counters mismatch')
    tests = groups[0].get('tests')
    require(isinstance(tests, list) and len(tests) == 1, 'baseline execution count mismatch')
    test = tests[0]
    # Pinned TUnit 1.73.19 source ff2dc5186b05eddc8f6aea03a18784df3aa6e737:
    # a plain InvalidOperationException is Unknown -> ErrorTestNodeStateProperty
    # -> report status "error", while summary.failed increments. Not "failed".
    require(test.get('id') == BASELINE_UID and test.get('status') == 'error'
            and test.get('retryAttempt', 0) == 0 and not test.get('attempts'), 'baseline wrong UID/status/retry')
    error = test.get('exception')
    require(isinstance(error, dict), 'baseline missing assertion exception')
    require(error.get('type') in ('InvalidOperationException', 'System.InvalidOperationException'),
            'baseline wrong exception type (not the reviewed assertion type)')
    # Never copy arbitrary exception text into CI; keep the full report local.
    classification = ('former-generic-aggregate-assertion' if error.get('message') ==
                      'every distinct owner-cleanup failure must remain observable' else 'unreviewed-assertion')
    require(error.get('message') == BASELINE_ASSERTION, 'baseline wrong assertion: classification=' + classification
            + ' expected=' + BASELINE_ASSERTION)
    require(not error.get('innerException'), 'baseline unexpected inner exception')
    require(isinstance(error.get('stackTrace'), str) and 'ThrowingBatchReturnMustNotReleaseRerentedOwnerOrAbandonEitherQueue'
            in error['stackTrace'], 'baseline failure did not originate in the target regression')


def validate_tcp(log: str, code: int, timed_out: bool) -> None:
    require(not timed_out and code == 0, 'TCP14 process failed or timed out')
    cases = [line.removeprefix('NEW_CORRECTNESS_CASE ') for line in log.splitlines() if line.startswith('NEW_CORRECTNESS_CASE ')]
    require(cases == [c + '/' + t for c, t in TCP_CASES], 'TCP14 case sequence mismatch')
    rows = [strict_json(line) for line in log.splitlines() if line.startswith('{')]
    rows = [r for r in rows if isinstance(r, dict) and r.get('suite') == 'new-source-serverstream-cleanup']
    require([(r.get('condition'), r.get('termination')) for r in rows] == TCP_CASES, 'TCP14 result census mismatch')
    require(all(r.get('passed') is True and r.get('failure') is None and r.get('teardownFailures') == []
            and r.get('expectedRuntimeVersion') == '10.0.14' and r.get('runtimeVersion') == '10.0.14'
            and r.get('frameworkDescription') == '.NET 10.0.14' and r.get('historicalReplay') is False for r in rows),
            'TCP14 case/runtime/teardown failure')
    require('NEW_CORRECTNESS_RESULT cases=14 failures=[]' in log.splitlines(), 'TCP14 final marker missing')


def tcp_gate(root: Path, sdk: str, host: str, env: dict, output: Path, unit_identity: dict) -> None:
    target = root / 'eng/static-stream-cleanup-regression'
    for name in ('module', 'probe'):
        code, timed_out = unit.execute([sdk, 'build', str(target / name / (name + '.csproj')), '-c', 'Release',
                '-m:1', '-nr:false', '-p:UseSharedCompilation=false'], root, env, 900, output / ('tcp-build-' + name + '.log'))
        require(not timed_out and code == 0, 'TCP project build failed')
    directory = target / 'probe/bin/Release/net10.0'
    shutil.copy2(target / 'module/bin/Release/net10.0/StaticStreamCleanupModule.dll', directory / 'StaticStreamCleanupModule.dll')
    identity = hashes(directory)
    require({'StaticStreamCleanupProbe.dll', 'StaticStreamCleanupModule.dll'}.issubset(identity), 'TCP assemblies missing')
    for name in ('SharpLink.Runtime.dll', 'SharpLink.Server.dll', 'SharpLink.Client.dll'):
        require(identity.get(name) == unit_identity.get(name), 'TCP and unit production DLL identity differs')
    (output / 'tcp-identity.json').write_text(json.dumps(identity, indent=2) + '\n')
    code, timed_out = unit.execute([sys.executable, str(target / 'run.py'), '--extended', '--no-build', '--dotnet', sdk,
                '--runtime-dotnet', host, '--runtime-version', '10.0.14'], root, env, 1500, output / 'tcp.log')
    require(identity == hashes(directory), 'TCP assemblies changed during execution')
    validate_tcp((output / 'tcp.log').read_text(), code, timed_out)
    print('TCP14 PASS cases=14 runtime=10.0.14', flush=True)


def bounded_error(error: Exception) -> str:
    return type(error).__name__ + (':' + str(error).replace('\n', ' ')[:400] if isinstance(error, GateError) else '')


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument('--release-root', required=True, type=Path)
    parser.add_argument('--source-root', type=Path)
    parser.add_argument('--phase', choices=('baseline',))
    parser.add_argument('--provenance-only', action='store_true')
    args = parser.parse_args()
    request = verify_release(args.release_root.resolve(), os.environ.get('GITHUB_SHA', ''), os.environ.get('GITHUB_REF', ''))
    if args.provenance_only:
        if os.environ.get('GITHUB_OUTPUT'):
            with open(os.environ['GITHUB_OUTPUT'], 'a') as output:
                output.write('source_sha=' + request['source_sha'] + '\nbaseline_sha=' + request['baseline_sha'] + '\n')
        print('SENDPUMP PROVENANCE PASS source=' + request['source_sha'] + ' source_tree=' + SOURCE_TREE
              + ' baseline_overlay=' + request['baseline_sha'] + ' baseline_tree=' + OVERLAY_TREE)
        return 0
    require(args.source_root is not None and args.phase is not None, 'missing execution source or phase')
    baseline = args.phase == 'baseline'
    root = args.source_root.resolve()
    sha = request['baseline_sha' if baseline else 'source_sha']
    verify_source(root, sha, baseline)
    env = {k: v for k, v in os.environ.items() if not k.startswith(('TUNIT_', 'TESTINGPLATFORM_'))}
    env.update(DOTNET_PROCESSOR_COUNT='4', DOTNET_CLI_TELEMETRY_OPTOUT='1', TESTINGPLATFORM_TELEMETRY_OPTOUT='1', PYTHONDONTWRITEBYTECODE='1')
    sdk, host = unit.verify_hosts(root, env)
    test_env = unit.private_test_environment(env, host)
    output = Path(tempfile.mkdtemp(prefix='sendpump-' + args.phase + '-', dir=env.get('RUNNER_TEMP')))
    stages = []
    try:
        code, timed_out = unit.execute([sdk, 'build', unit.PROJECT, '-c', 'Release', '-m:1', '-nr:false',
                     '--disable-build-servers', '-p:UseSharedCompilation=false', '-v', 'minimal'], root, env, 900, output / 'build.log')
        require(not timed_out and code == 0, 'unit build failed')
        assembly = root / unit.ASSEMBLY
        identity = hashes(assembly.parent)
        require(assembly.name in identity, 'unit assembly missing')
        code, timed_out = unit.execute([host, str(assembly), '--list-tests', 'json'], root, test_env, 180, output / 'discovery.json')
        require(not timed_out and code == 0, 'unit discovery failed')
        ordinary = census(root, read_report(output / 'discovery.json'))
        (output / 'identity.json').write_text(json.dumps(dict(request=request, phase=args.phase, dll_sha256=identity,
                     ordinary_uid_sha256=uid_digest(ordinary)), indent=2) + '\n')
        if baseline:
            code, timed_out = unit.execute(command(host, assembly, output, BASELINE_UID), root, test_env, 120, output / 'baseline.log')
            report = final_report(output)
            print('BASELINE CALIBRATION V2 OBSERVATION ' + json.dumps(baseline_observation(report), ensure_ascii=True, sort_keys=True), flush=True)
            validate_baseline(report, code, timed_out)
            print('BASELINE CAUSAL EXPECTED FAILURE uid=' + BASELINE_UID + ' assertion=' + BASELINE_ASSERTION, flush=True)
            stages.append(dict(stage='baseline-causal', passed=True, expected_assertion=BASELINE_ASSERTION))
        else:
            # All independent candidate stages continue after a test failure. A
            # failed/unfinished phase never changes the subsequent pass criterion.
            for index, uid in enumerate(NEW_UIDS):
                case = output / ('focused-' + str(index)); case.mkdir()
                try:
                    code, timed_out = unit.execute(command(host, assembly, case, uid), root, test_env, 120, case / 'worker.log')
                    report = final_report(case); unit.require_test_runtime(report)
                    validate_run([uid], report, code, timed_out)
                    stages.append(dict(stage=uid, passed=True)); print('FOCUSED PASS uid=' + uid, flush=True)
                except Exception as error:
                    stages.append(dict(stage=uid, passed=False, detail=str(error)))
                    print('FOCUSED FAIL uid=' + uid + ' error=' + bounded_error(error), flush=True)
            case = output / 'ordinary'; case.mkdir()
            try:
                code, timed_out = unit.execute(command(host, assembly, case), root, test_env, 1260, case / 'worker.log')
                report = final_report(case); unit.require_test_runtime(report)
                summary = validate_run(ordinary, report, code, timed_out)
                children = unit.verify_allocation_child_runtimes(report)
                print('ALLOCATION_CHILD_RUNTIME ' + json.dumps(children, sort_keys=True), flush=True)
                stages.append(dict(stage='ordinary', passed=True, result=summary))
                print('ORDINARY UNIT PASS ' + json.dumps(summary, sort_keys=True), flush=True)
            except Exception as error:
                stages.append(dict(stage='ordinary', passed=False, detail=str(error)))
                print('ORDINARY UNIT FAIL error=' + bounded_error(error), flush=True)
            try:
                require(identity == hashes(assembly.parent), 'unit DLL identity changed before TCP stage')
                tcp_gate(root, sdk, host, env, output, identity)
                stages.append(dict(stage='tcp14', passed=True))
            except Exception as error:
                stages.append(dict(stage='tcp14', passed=False, detail=str(error)))
                print('TCP14 FAIL error=' + bounded_error(error), flush=True)
        require(identity == hashes(assembly.parent), 'unit DLL identity changed')
        verify_source(root, sha, baseline)
        require(len(stages) == (1 if baseline else 8) and all(s['passed'] for s in stages), 'one or more required stages failed')
        print('SENDPUMP ' + args.phase.upper() + ' GATE PASS source=' + sha + ' runtime=10.0.14', flush=True)
        return 0
    except Exception as error:
        (output / 'failure.json').write_text(json.dumps(dict(type=type(error).__name__, detail=str(error)), indent=2) + '\n')
        print(('BASELINE CAUSAL INCONCLUSIVE' if baseline else 'CANDIDATE GATE FAIL') + ' error=' + bounded_error(error), flush=True)
        return 1
    finally:
        (output / 'summary.json').write_text(json.dumps(dict(phase=args.phase, source_sha=sha, stages=stages), indent=2) + '\n')


if __name__ == '__main__':
    try: sys.exit(main())
    except Exception as error:
        print('SENDPUMP PREFLIGHT FAIL error=' + bounded_error(error), flush=True)
        sys.exit(1)
