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


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument('--release-root', required=True, type=Path)
    parser.add_argument('--source-root', required=True, type=Path)
    args = parser.parse_args()
    root = args.source_root.resolve()
    request = verify_release(args.release_root.resolve(), os.environ.get('GITHUB_SHA', ''),
                             os.environ.get('GITHUB_REF', ''))
    verify_source(root, request)
    print(f"SOURCE sha={request['source_sha']} tree={request['source_tree']}", flush=True)
    env = {key: value for key, value in os.environ.items()
           if not key.startswith(('TUNIT_', 'TESTINGPLATFORM_'))}
    env.update({'DOTNET_PROCESSOR_COUNT': '4', 'DOTNET_CLI_TELEMETRY_OPTOUT': '1',
                'TESTINGPLATFORM_TELEMETRY_OPTOUT': '1', 'PYTHONDONTWRITEBYTECODE': '1'})
    sdk = subprocess.check_output(['dotnet', '--version'], cwd=root, env=env, text=True).strip()
    require(sdk == '10.0.102', 'unexpected SDK (must be 10.0.102)')
    with tempfile.TemporaryDirectory(prefix='static-stream-unit-', dir=env.get('RUNNER_TEMP')) as temporary:
        temp = Path(temporary)
        code, timed_out = execute(['dotnet', 'build', PROJECT, '-c', 'Release', '-m:1',
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
        test_env = {key: value for key, value in env.items() if not key.startswith('GITHUB_')}
        test_env['CI'] = 'true'
        discovery_file = temp / 'discovery.json'
        code, timed_out = execute(['dotnet', str(root / ASSEMBLY), '--list-tests', 'json'],
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
            'dotnet', str(root / ASSEMBLY), '--maximum-parallel-tests', '4',
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
        runtime = result.get('runtimeVersion')
        require(isinstance(runtime, str) and runtime.startswith('.NET 10.0.')
                and len(runtime) < 80 and not any(c in runtime for c in '\r\n'),
                'missing or unexpected actual CoreCLR runtime identity')
        require(hashes == {name: hashlib.sha256((assembly_dir / name).read_bytes()).hexdigest()
                           for name in assembly_names}, 'test assembly changed during execution')
        print('IDENTITY ' + json.dumps({'sdk': sdk, 'actual_coreclr': runtime,
              'assembly_sha256': hashes}, sort_keys=True), flush=True)
        summary = validate_run(ordinary, result, code, timed_out)
        print('UNIT GATE PASS ' + json.dumps(summary, sort_keys=True), flush=True)
        # No tracked changes may be produced by the runner or tests.
        verify_source(root, request)


if __name__ == '__main__':
    try:
        main()
    except (GateError, OSError, ValueError, subprocess.SubprocessError) as error:
        # Avoid traceback dumps and never dump input payloads or raw process logs.
        raise SystemExit(f'UNIT GATE FAIL: {error}') from None
