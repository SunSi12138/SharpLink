#!/usr/bin/env python3
"""Execute only the eleven reviewed TLS integration tests; no raw output uploads."""
from __future__ import annotations

import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent / 'static-stream-unit-validation'))
from validate import GateError, check_summary, discovery_uids, read_report, require, strict_json, unique_uids
spec = importlib.util.spec_from_file_location('reviewed_unit_runner', HERE.parent / 'static-stream-unit-validation/run.py')
unit = importlib.util.module_from_spec(spec)
spec.loader.exec_module(unit)

BASE_SHA = '3d76a3a8b55a37e24c62a073fe588cff3d3867be'
BASE_TREE = '1c24da3af090acd4d6cdbfb005a9e3bd95304e3a'
# This constant is the independently reviewed test-only source tree. Never derive
# the expected tree from a candidate/request or substitute an arbitrary branch.
SOURCE_TREE = 'e091a9601775b97cf3d46d5c692ff7571095f354'
SOURCE_PATH = 'test/SharpLink.IntegrationTests/TlsTransportIntegrationTests.cs'
BRANCH = 'refs/heads/validation/tls-security-no-upload-20261029'
REQUEST = 'eng/tls-security-validation/request.json'
TOOL_FILES = sorted(['.github/workflows/tls-security-no-upload.yml',
                     'eng/tls-security-validation/run.py', 'eng/tls-security-validation/test_validation.py'])
PROJECT = 'test/SharpLink.IntegrationTests/SharpLink.IntegrationTests.csproj'
ASSEMBLY = 'test/SharpLink.IntegrationTests/bin/Release/net10.0/SharpLink.IntegrationTests.dll'
METHODS = (
    'RuntimeMultiClusterAddAndReplaceShouldPreserveTlsAndAuthenticationConfiguration',
    'TlsShouldProtectAllRpcShapesAndReconnect',
    'TlsShouldRejectWrongHostname',
    'TlsShouldRejectUntrustedCertificateByDefault',
    'TlsShouldRejectExpiredCertificate',
    'MutualTlsShouldRejectMissingAndAcceptExpectedClientCertificate',
    'MutualTlsShouldRejectPresentedUnexpectedClientCertificateAndRecoverWithExpectedCertificate',
    'CertificateValidationShouldRejectMissingCertificateRegardlessOfPlatformErrors',
    'MutualTlsShouldPreserveClientCertificateContext',
    'TlsHandshakeShouldHonorIndependentTimeout',
    'StaticTlsEndpointsShouldUseEndpointAuthorityAndIsolateFailure',
)
UIDS = {name: 'SharpLink.IntegrationTests.TlsTransportIntegrationTests.1.1.' + name + '.1.1.0'
        for name in METHODS}


def git(root: Path, *args: str) -> str:
    return subprocess.check_output(['git', '-C', str(root), *args], text=True).strip()


def verify_source(root: Path, source_sha: str) -> None:
    require(re.fullmatch('[0-9a-f]{40}', source_sha) is not None, 'invalid source SHA')
    require(git(root, 'rev-parse', 'HEAD') == source_sha, 'source SHA mismatch')
    require(git(root, 'rev-parse', 'HEAD^{tree}') == SOURCE_TREE, 'reviewed source tree mismatch')
    require(not git(root, 'status', '--porcelain', '--untracked-files=all'), 'source not clean')
    require(git(root, 'show', '-s', '--format=%P', 'HEAD') == BASE_SHA, 'source parent mismatch')
    require(git(root, 'rev-parse', BASE_SHA + '^{tree}') == BASE_TREE, 'product tree mismatch')
    require(git(root, 'diff', '--name-status', '--no-renames', BASE_SHA, 'HEAD') == 'M\t' + SOURCE_PATH,
            'source changes more than the reviewed TLS test file')


def verify_release(root: Path, release_sha: str, ref: str) -> dict:
    require(ref == BRANCH, 'wrong validation branch')
    require(re.fullmatch('[0-9a-f]{40}', release_sha) is not None, 'invalid release SHA')
    require(git(root, 'rev-parse', 'HEAD') == release_sha, 'release SHA mismatch')
    require(not git(root, 'status', '--porcelain', '--untracked-files=all'), 'release not clean')
    request = strict_json((root / REQUEST).read_text())
    require(isinstance(request, dict) and set(request) == {'schema_version', 'source_sha', 'tools_sha', 'tools_tree'}, 'invalid request')
    require(type(request['schema_version']) is int and request['schema_version'] == 1, 'invalid schema')
    for key in ('source_sha', 'tools_sha', 'tools_tree'):
        require(isinstance(request[key], str) and re.fullmatch('[0-9a-f]{40}', request[key]) is not None, 'invalid requested identity')
    source, tools = request['source_sha'], request['tools_sha']
    require(git(root, 'show', '-s', '--format=%P', 'HEAD') == tools, 'request parent mismatch')
    require(git(root, 'rev-parse', tools + '^{tree}') == request['tools_tree'], 'reviewed tools tree mismatch')
    require(git(root, 'diff', '--name-status', '--no-renames', tools, 'HEAD') == 'A\t' + REQUEST, 'not request-only')
    require(git(root, 'show', '-s', '--format=%P', tools) == source, 'tool/source parent mismatch')
    require(git(root, 'rev-parse', source + '^{tree}') == SOURCE_TREE, 'unreviewed requested source tree')
    require(git(root, 'show', '-s', '--format=%P', source) == BASE_SHA, 'source/product parent mismatch')
    require(git(root, 'rev-parse', BASE_SHA + '^{tree}') == BASE_TREE, 'product tree mismatch')
    require(git(root, 'diff', '--name-status', '--no-renames', BASE_SHA, source) == 'M\t' + SOURCE_PATH,
            'unreviewed product source change')
    require(git(root, 'diff', '--name-status', '--no-renames', source, tools).splitlines()
            == sorted('A\t' + path for path in TOOL_FILES), 'unreviewed tool scope')
    return request


def validate_discovery(document: dict) -> None:
    discovery_uids(document)  # Reject malformed schema and duplicate UIDs globally.
    selected = [t for t in document['tests'] if t.get('type', {}).get('namespace') == 'SharpLink.IntegrationTests'
                and t.get('type', {}).get('typeName') == 'TlsTransportIntegrationTests']
    require(sorted(t['uid'] for t in selected) == sorted(UIDS.values()), 'TLS discovery differs from all eleven reviewed tests')
    for test in selected:
        require(test.get('type', {}).get('methodName') in METHODS
                and test['uid'] == UIDS[test['type']['methodName']], 'TLS method/UID mismatch')
        require(test.get('location', {}).get('file', '').replace('\\', '/').endswith('/' + SOURCE_PATH),
                'TLS source location mismatch')


def validate_result(name: str, report: dict, code: int, timed_out: bool) -> None:
    require(not timed_out and code == 0, 'test process failure or watchdog timeout')
    require(type(report.get('schemaVersion')) is int and report['schemaVersion'] == 1, 'unsupported test report schema')
    require(report.get('assemblyName') == 'SharpLink.IntegrationTests', 'wrong test assembly')
    require(report.get('tunitVersion') == '1.73.19.0', 'wrong TUnit version')
    require(report.get('runtimeVersion') == '.NET 10.0.14', 'wrong test runtime')
    generation = report.get('publicationGeneration')
    require(isinstance(generation, str) and re.fullmatch('[0-9a-f]{32}', generation) is not None, 'missing final report generation')
    groups = report.get('groups')
    require(isinstance(groups, list) and groups, 'missing result groups')
    tests = []
    for group in groups:
        require(isinstance(group, dict) and isinstance(group.get('tests'), list), 'invalid result group')
        require(all(isinstance(t, dict) for t in group['tests']), 'invalid result tests')
        tests.extend(group['tests'])
        check_summary(group.get('summary'), len(group['tests']), 'group')
    require(unique_uids([t.get('id') for t in tests], 'executed tests') == [UIDS[name]], 'wrong/missing/duplicate executed UID')
    require(all(t.get('status') == 'passed' for t in tests), 'test failed or skipped')
    check_summary(report.get('summary'), 1, 'run')


def test_command(host: str, assembly: Path, name: str, output: Path) -> list[str]:
    return [host, str(assembly), '--filter-uid', UIDS[name], '--maximum-parallel-tests', '1',
            '--minimum-expected-tests', '1', '--zero-tests-policy', 'strict',
            '--no-ansi', '--progress', 'off', '--show-stdout', 'None', '--show-stderr', 'None',
            '--show-test-results', 'failed,skipped', '--report-html',
            '--report-html-filename', str(output / 'result.html'), '--results-directory', str(output / 'results')]


def hashes(assembly: Path) -> dict:
    return {p.name: hashlib.sha256(p.read_bytes()).hexdigest() for p in sorted(assembly.parent.glob('*.dll'))}


def bounded_error(error: Exception) -> str:
    # GateError messages are our fixed validation reasons and numeric counters,
    # never raw test output, exceptions, certificate data, or traces.
    return type(error).__name__ + (':' + str(error).replace('\n', ' ')[:200] if isinstance(error, GateError) else '')


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument('--release-root', required=True, type=Path)
    parser.add_argument('--source-root', type=Path)
    parser.add_argument('--provenance-only', action='store_true')
    args = parser.parse_args()
    request = verify_release(args.release_root.resolve(), os.environ.get('GITHUB_SHA', ''), os.environ.get('GITHUB_REF', ''))
    if args.source_root is not None:
        verify_source(args.source_root.resolve(), request['source_sha'])
    if args.provenance_only:
        print('TLS PROVENANCE PASS source=' + request['source_sha'] + ' tree=' + SOURCE_TREE + ' tools=' + request['tools_sha'])
        if os.environ.get('GITHUB_OUTPUT'):
            with open(os.environ['GITHUB_OUTPUT'], 'a') as output:
                output.write('source_sha=' + request['source_sha'] + '\n')
        return 0
    require(args.source_root is not None, 'missing source checkout')
    root = args.source_root.resolve()
    env = {k: v for k, v in os.environ.items() if not k.startswith(('TUNIT_', 'TESTINGPLATFORM_'))}
    env.update(DOTNET_PROCESSOR_COUNT='4', DOTNET_CLI_TELEMETRY_OPTOUT='1',
               TESTINGPLATFORM_TELEMETRY_OPTOUT='1', PYTHONDONTWRITEBYTECODE='1')
    sdk, host = unit.verify_hosts(root, env)
    output = Path(tempfile.mkdtemp(prefix='tls-security-', dir=env.get('RUNNER_TEMP')))
    records = []
    try:
        code, timed_out = unit.execute([sdk, 'build', PROJECT, '-c', 'Release', '-m:1', '-nr:false',
                                      '--disable-build-servers', '-p:UseSharedCompilation=false', '-v', 'minimal'],
                                      root, env, 1200, output / 'build.log')
        require(not timed_out and code == 0, 'build failed')
        assembly = root / ASSEMBLY
        identity = hashes(assembly)
        require(assembly.name in identity, 'test assembly missing')
        test_env = unit.private_test_environment(env, host)
        code, timed_out = unit.execute([host, str(assembly), '--list-tests', 'json'], root, test_env, 180, output / 'discovery.json')
        require(not timed_out and code == 0, 'discovery failed')
        validate_discovery(read_report(output / 'discovery.json'))
        (output / 'identity.json').write_text(json.dumps(dict(request=request, source_tree=SOURCE_TREE,
            sdk='10.0.102', runtime='10.0.14', dll_sha256=identity, expected_uids=UIDS), indent=2) + '\n')
        print('TLS PREPARE PASS source=' + request['source_sha'] + ' sdk=10.0.102 runtime=10.0.14 tests=11', flush=True)
        for name in METHODS:
            case = output / name
            case.mkdir()
            record = dict(name=name, uid=UIDS[name], passed=False)
            try:
                # Entire process, including async disposal/cleanup, must finish.
                code, timed_out = unit.execute(test_command(host, assembly, name, case), root, test_env, 120, case / 'worker.log')
                record.update(exit_code=code, watchdog=timed_out)
                require(not timed_out and code == 0, 'worker failed or watchdog expired')
                reports = list(case.glob('*.tunit-report.json'))
                require(len(reports) == 1, 'missing or ambiguous final test report')
                validate_result(name, read_report(reports[0]), code, timed_out)
                record.update(passed=True, runtime='10.0.14', executed=1)
                print('TLS TEST PASS ' + name + ' executed=1 runtime=10.0.14', flush=True)
            except Exception as error:
                record.update(error_type=type(error).__name__, detail=str(error))
                print('TLS TEST FAIL ' + name + ' error=' + bounded_error(error), flush=True)
            records.append(record)
            (case / 'execution.json').write_text(json.dumps(record, indent=2) + '\n')
        require(identity == hashes(assembly), 'loaded assembly set changed')
        verify_source(root, request['source_sha'])
        require(len(records) == 11 and all(r['passed'] for r in records), 'TLS slice not all passing')
        print('TLS SECURITY GATE PASS tests=11 failed=0 skipped=0 runtime=10.0.14 new_mtls_protocol=TLS1.2', flush=True)
        return 0
    except Exception as error:
        (output / 'failure.json').write_text(json.dumps(dict(type=type(error).__name__, detail=str(error)), indent=2) + '\n')
        print('TLS SECURITY GATE FAIL error=' + bounded_error(error), flush=True)
        return 1
    finally:
        (output / 'summary.json').write_text(json.dumps(dict(request=request, records=records), indent=2) + '\n')


if __name__ == '__main__':
    try:
        sys.exit(main())
    except Exception as error:
        print('TLS SECURITY PREFLIGHT FAIL error=' + bounded_error(error), flush=True)
        sys.exit(1)
