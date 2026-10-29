#!/usr/bin/env python3
"""Build, fresh-discover, and execute the complete exact-UID unit census.

Only ordinary build output and bounded summaries reach CI logs. Discovery,
JSON/HTML (including traces), stdout/stderr and any raw outputs stay in a unique
runner-temp directory, are never uploaded, and are removed on every exit.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import signal
import subprocess
import tempfile

from scope import explicit_workers, ordinary_uids, verify_explicit_sources
from provenance import read_corpus, verify_release, verify_source
from validate import GateError, discovery_uids, read_report, require, strict_json, uid_digest, validate_run

PROJECT = 'test/SharpLink.UnitTests/SharpLink.UnitTests.csproj'
ASSEMBLY = 'test/SharpLink.UnitTests/bin/Release/net10.0/SharpLink.UnitTests.dll'
RUNTIME_VERSION = '10.0.14'


def execute(command: list[str], cwd: Path, env: dict, timeout: int, output: Path | None = None) -> tuple[int, bool]:
    # Kill the entire process group on timeout, not just the parent dotnet host.
    stream = output.open('wb') if output else None
    try:
        process = subprocess.Popen(command, cwd=cwd, env=env, stdout=stream,
                                   stderr=subprocess.STDOUT if stream else None,
                                   start_new_session=True)
        try:
            return process.wait(timeout=timeout), False
        except subprocess.TimeoutExpired:
            os.killpg(process.pid, signal.SIGKILL)
            process.wait()
            return process.returncode, True
    finally:
        if stream:
            stream.close()


def require_pinned_sdk(root: Path, env: dict) -> str:
    sdk = subprocess.check_output([env.get('SDK_DOTNET', 'dotnet'), '--version'], cwd=root, env=env, text=True).strip()
    # Keep the full value for the gate; truncate only its ordinary diagnostic log.
    actual = sdk[:160]
    print('SDK_SELECTION ' + json.dumps({'actual': actual, 'expected': '10.0.102',
          'truncated': len(sdk) > len(actual)}, sort_keys=True), flush=True)
    require(sdk == '10.0.102',
            f'unexpected SDK: actual={json.dumps(actual)} expected=10.0.102')
    return sdk


def verify_hosts(root: Path, env: dict) -> tuple[str, str]:
    sdk_host = env.get('SDK_DOTNET', '')
    runtime_root = Path(env.get('PRIVATE_RUNTIME', ''))
    require(Path(sdk_host).is_absolute() and Path(sdk_host).is_file(), 'missing absolute SDK host')
    require(runtime_root.is_absolute() and (runtime_root / 'dotnet').is_file(), 'missing absolute private runtime host')
    require(Path(sdk_host).parent.resolve() != runtime_root.resolve(), 'build and test hosts must be separate')
    require_pinned_sdk(root, env)
    sdk_root = Path(sdk_host).parent
    sdk_lines = subprocess.check_output([sdk_host, '--list-sdks'], env=env, text=True).splitlines()
    require(sdk_lines == [f'10.0.102 [{sdk_root / "sdk"}]'], 'unexpected build SDK inventory')
    sdk_runtime_lines = subprocess.check_output([sdk_host, '--list-runtimes'], env=env, text=True).splitlines()
    expected_build = [f'{name} 10.0.2 [{sdk_root / "shared" / name}]'
                      for name in ('Microsoft.AspNetCore.App', 'Microsoft.NETCore.App')]
    require(sorted(sdk_runtime_lines) == expected_build, 'unexpected bundled build-runtime inventory')
    test_host = str(runtime_root / 'dotnet')
    require(not subprocess.check_output([test_host, '--list-sdks'], env=env, text=True).strip(),
            'private test runtime must not contain an SDK')
    runtime_lines = subprocess.check_output([test_host, '--list-runtimes'], env=env, text=True).splitlines()
    expected_test = [f'{name} {RUNTIME_VERSION} [{runtime_root / "shared" / name}]'
                     for name in ('Microsoft.AspNetCore.App', 'Microsoft.NETCore.App')]
    require(sorted(runtime_lines) == expected_test, 'unexpected private test-runtime inventory')
    print('HOSTS ' + json.dumps({'build_sdk': '10.0.102', 'sdk_bundled_runtime': '10.0.2',
          'test_coreclr': RUNTIME_VERSION, 'test_aspnetcore': RUNTIME_VERSION,
          'sdk_host': sdk_host, 'test_host': test_host}, sort_keys=True), flush=True)
    return sdk_host, test_host


def private_test_environment(env: dict, test_host: str) -> dict:
    result = {key: value for key, value in env.items() if not key.startswith('GITHUB_')}
    root = str(Path(test_host).parent)
    result.update({'CI': 'true', 'DOTNET_ROOT': root, 'DOTNET_ROOT_X64': root,
                   'DOTNET_HOST_PATH': test_host, 'PATH': root + os.pathsep + env.get('PATH', '')})
    return result


def require_test_runtime(result: dict) -> str:
    actual = result.get('runtimeVersion')
    print('RUNTIME_SELECTION ' + json.dumps({'actual': str(actual)[:80],
          'expected': '.NET ' + RUNTIME_VERSION}, sort_keys=True), flush=True)
    require(actual == '.NET ' + RUNTIME_VERSION,
            f'actual test CoreCLR must be exactly .NET {RUNTIME_VERSION}')
    return actual


def verify_allocation_child_runtimes(report: dict) -> list[dict]:
    scenarios = ('SuccessfulSuspensionsShouldNotAllocateAfterWarmup',
                 'RegisteredConsumerSuspensionsShouldNotAllocateAfterWarmup')
    expected = {f'SharpLink.UnitTests.Runtime.ReadOwnershipPipeReaderLifetimeTests.1.1.{scenario}'
                '(System.Threading.CancellationToken).1.1.0': scenario for scenario in scenarios}
    marker = 'READ_OWNERSHIP_ALLOCATION_VERIFIED|'
    records = {}
    for group in report.get('groups', []):
        for test in group.get('tests', []):
            output = test.get('output', '')
            require(isinstance(output, str), 'invalid child output field')
            for line in output.splitlines():
                if not line.startswith(marker):
                    continue
                uid = test.get('id')
                require(uid in expected and uid not in records, 'unexpected or duplicate child-runtime marker')
                require(test.get('status') == 'passed', 'child-runtime witness test did not pass')
                record = strict_json(line[len(marker):])
                require(isinstance(record, dict) and record.get('scenario') == expected[uid],
                        'child-runtime scenario mismatch')
                require(record.get('runtime') == RUNTIME_VERSION and record.get('architecture') == 'X64'
                        and type(record.get('executed')) is int and record['executed'] == 1
                        and record.get('passed') is True, 'allocation child runtime/execution mismatch')
                records[uid] = {key: record[key] for key in ('scenario', 'runtime', 'architecture', 'executed')}
    require(set(records) == set(expected), 'missing required allocation child-runtime witness')
    return [records[uid] for uid in sorted(records)]


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument('--release-root', type=Path)
    parser.add_argument('--verify-hosts-only', action='store_true')
    parser.add_argument('--source-root', required=True, type=Path)
    args = parser.parse_args()
    root = args.source_root.resolve()
    if args.verify_hosts_only:
        verify_hosts(root, dict(os.environ))
        return
    require(args.release_root is not None, '--release-root is required for validation')
    request = verify_release(args.release_root.resolve(), os.environ.get('GITHUB_SHA', ''),
                             os.environ.get('GITHUB_REF', ''))
    verify_source(root, request)
    print(f"SOURCE sha={request['source_sha']} tree={request['source_tree']}", flush=True)
    env = {key: value for key, value in os.environ.items()
           if not key.startswith(('TUNIT_', 'TESTINGPLATFORM_'))}
    env.update({'DOTNET_PROCESSOR_COUNT': '4', 'DOTNET_CLI_TELEMETRY_OPTOUT': '1',
                'TESTINGPLATFORM_TELEMETRY_OPTOUT': '1', 'PYTHONDONTWRITEBYTECODE': '1'})
    sdk_host, test_host = verify_hosts(root, env)
    sdk = '10.0.102'
    with tempfile.TemporaryDirectory(prefix='static-stream-unit-', dir=env.get('RUNNER_TEMP')) as temporary:
        temp = Path(temporary)
        code, timed_out = execute([sdk_host, 'build', PROJECT, '-c', 'Release', '-m:1',
                                  '-nr:false', '--disable-build-servers', '-p:UseSharedCompilation=false',
                                  '-v', 'minimal'], root, env, 900)
        require(not timed_out and code == 0, f'build failed: exit={code} timeout={timed_out}')
        assembly_dir = (root / ASSEMBLY).parent
        assembly_names = ('SharpLink.UnitTests.dll', 'SharpLink.Client.dll',
                          'SharpLink.Runtime.dll', 'SharpLink.Server.dll')
        hashes = {name: hashlib.sha256((assembly_dir / name).read_bytes()).hexdigest()
                  for name in assembly_names}
        # Stop any TUnit GitHub reporter from publishing raw failures/trace payloads
        # through job summaries or workflow annotations. Files remain local only.
        test_env = private_test_environment(env, test_host)
        discovery_file = temp / 'discovery.json'
        code, timed_out = execute([test_host, str(root / ASSEMBLY), '--list-tests', 'json'],
                                  root, test_env, 180, discovery_file)
        require(not timed_out and code == 0, f'discovery failed: exit={code} timeout={timed_out}')
        discovery = strict_json(discovery_file.read_text(encoding='utf-8-sig'))
        discovered = discovery_uids(discovery)
        require(discovered == read_corpus(root, request), 'fresh discovery differs from reviewed UID corpus')
        require(uid_digest(discovered) == request['raw_uid_sha256'], 'fresh discovery digest mismatch')
        workers = explicit_workers(Path(__file__).parent)
        verify_explicit_sources(root, discovery, workers)
        ordinary = ordinary_uids(discovered, workers)
        print(f"CENSUS raw_discovered={len(discovered)} raw_uid_sha256={uid_digest(discovered)} "
              f"ordinary={len(ordinary)} ordinary_uid_sha256={uid_digest(ordinary)} "
              f"explicit_workers_not_executed={len(workers)}", flush=True)
        report = temp / 'results.html'
        code, timed_out = execute([
            test_host, str(root / ASSEMBLY), '--maximum-parallel-tests', '4',
            '--timeout', '20m', '--minimum-expected-tests', str(len(ordinary)),
            '--zero-tests-policy', 'strict', '--no-ansi', '--progress', 'off',
            '--show-stdout', 'None', '--show-stderr', 'None', '--show-test-results', 'failed,skipped',
            '--report-html', '--report-html-filename', str(report),
            '--results-directory', str(temp / 'results'),
        ], root, test_env, 1260, temp / 'run.log')
        require(not timed_out, f'test process timeout: exit={code}')
        json_reports = list(temp.glob('*.tunit-report.json'))
        require(len(json_reports) == 1, f'missing or ambiguous final JSON report: exit={code}')
        result = read_report(json_reports[0])
        runtime = require_test_runtime(result)
        require(hashes == {name: hashlib.sha256((assembly_dir / name).read_bytes()).hexdigest()
                           for name in assembly_names}, 'test assembly changed during execution')
        print('IDENTITY ' + json.dumps({'sdk': sdk, 'actual_coreclr': runtime,
              'assembly_sha256': hashes}, sort_keys=True), flush=True)
        summary = validate_run(ordinary, result, code, timed_out)
        children = verify_allocation_child_runtimes(result)
        print('ALLOCATION_CHILD_RUNTIME ' + json.dumps(children, sort_keys=True), flush=True)
        print('UNIT GATE PASS ' + json.dumps(summary, sort_keys=True), flush=True)
        # No tracked changes may be produced by the runner or tests.
        verify_source(root, request)


if __name__ == '__main__':
    try:
        main()
    except (GateError, OSError, ValueError, subprocess.SubprocessError) as error:
        # Avoid traceback dumps and never dump input payloads or raw process logs.
        raise SystemExit(f'UNIT GATE FAIL: {error}') from None
