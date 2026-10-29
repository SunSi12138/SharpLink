#!/usr/bin/env python3
"""One fresh candidate full ordinary-unit execution; retain all raw data locally.

Build and discovery are not test executions. No retries, failure exclusions,
uploads, job annotations, raw stdout forwarding, or temporary-directory deletion.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import signal
import subprocess
import sys

if __package__ in (None, ''):
    sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
from unit_gate.provenance import SOURCE, TREE, read_corpus, verify_source
from unit_gate.record import expected_record, validate_record
from unit_gate.scope import verify_explicit_sources
from unit_gate.validate import (GateError, discovery_uids, read_report, require,
                                strict_json, uid_digest, validate_run)

TOOL = Path(__file__).resolve().parent
PROJECT = 'test/SharpLink.UnitTests/SharpLink.UnitTests.csproj'
ASSEMBLY = 'test/SharpLink.UnitTests/bin/Release/net10.0/SharpLink.UnitTests.dll'
RUNTIME = '10.0.14'


def execute(command, cwd, env, timeout, output):
    with output.open('wb') as stream:
        process = subprocess.Popen(command, cwd=cwd, env=env, stdout=stream,
            stderr=subprocess.STDOUT, start_new_session=True)
        try:
            code, timed_out = process.wait(timeout=timeout), False
        except subprocess.TimeoutExpired:
            os.killpg(process.pid, signal.SIGKILL)
            code, timed_out = process.wait(), True
    output.with_suffix(output.suffix + '.status.json').write_text(json.dumps(
        {'exit': code, 'timed_out': timed_out, 'command': command}, indent=2))
    return code, timed_out


def host_inventory(sdk_host: Path, runtime_host: Path, root, env, output):
    require(sdk_host.is_absolute() and sdk_host.is_file() and runtime_host.is_absolute()
            and runtime_host.is_file(), 'missing absolute host')
    require(sdk_host.parent.resolve() != runtime_host.parent.resolve(), 'hosts must be separate')
    checks = [
        (sdk_host, '--version', '10.0.102'),
        (sdk_host, '--list-sdks', f'10.0.102 [{sdk_host.parent / "sdk"}]'),
        (sdk_host, '--list-runtimes', '\n'.join(f'{name} 10.0.2 [{sdk_host.parent / "shared" / name}]'
            for name in ('Microsoft.AspNetCore.App', 'Microsoft.NETCore.App'))),
        (runtime_host, '--list-sdks', ''),
        (runtime_host, '--list-runtimes', '\n'.join(f'{name} {RUNTIME} [{runtime_host.parent / "shared" / name}]'
            for name in ('Microsoft.AspNetCore.App', 'Microsoft.NETCore.App'))),
    ]
    for index, (host, option, expected) in enumerate(checks):
        log = output / f'host-{index}.log'
        code, timed = execute([str(host), option], root, env, 30, log)
        require(not timed and code == 0, 'host inventory command failed')
        actual = log.read_text().strip()
        require(actual == expected, 'host inventory mismatch')


def private_test_environment(env, host: Path, output: Path):
    value = {k: v for k, v in env.items()
             if not k.startswith(('GITHUB_', 'TUNIT_', 'TESTINGPLATFORM_'))}
    value.update(CI='true', DOTNET_ROOT=str(host.parent), DOTNET_ROOT_X64=str(host.parent),
        DOTNET_HOST_PATH=str(host), DOTNET_ROLL_FORWARD='Disable',
        PATH=str(host.parent) + os.pathsep + env.get('PATH', ''),
        TMPDIR=str(output / 'tmp'), TESTINGPLATFORM_TELEMETRY_OPTOUT='1')
    return value


def pin_runtime_config(path: Path):
    document = strict_json(path.read_text())
    options = document.get('runtimeOptions')
    require(isinstance(options, dict), 'invalid runtime config')
    frameworks = options.get('frameworks', [options.get('framework')])
    require(isinstance(frameworks, list) and frameworks and all(isinstance(v, dict) for v in frameworks),
            'missing framework config')
    require({v.get('name') for v in frameworks} <= {'Microsoft.NETCore.App', 'Microsoft.AspNetCore.App'}
            and any(v.get('name') == 'Microsoft.NETCore.App' for v in frameworks), 'unexpected framework config')
    for framework in frameworks:
        framework['version'] = RUNTIME
    options['rollForward'] = 'Disable'
    path.write_text(json.dumps(document, indent=2) + '\n')


def require_runtime(report):
    require(report.get('runtimeVersion') == '.NET ' + RUNTIME, 'actual ordinary runtime mismatch')


def allocation_children(report):
    scenarios = ('SuccessfulSuspensionsShouldNotAllocateAfterWarmup',
                 'RegisteredConsumerSuspensionsShouldNotAllocateAfterWarmup')
    expected = {f'SharpLink.UnitTests.Runtime.ReadOwnershipPipeReaderLifetimeTests.1.1.{name}'
                '(System.Threading.CancellationToken).1.1.0': name for name in scenarios}
    records = {}
    marker = 'READ_OWNERSHIP_ALLOCATION_VERIFIED|'
    for group in report.get('groups', []):
        for test in group.get('tests', []):
            output = test.get('output', '')
            require(isinstance(output, str), 'invalid child output')
            for line in output.splitlines():
                if not line.startswith(marker):
                    continue
                uid = test.get('id')
                require(uid in expected and uid not in records and test.get('status') == 'passed',
                        'wrong or duplicate child witness')
                record = strict_json(line[len(marker):])
                require(isinstance(record, dict) and record.get('scenario') == expected[uid]
                    and record.get('runtime') == RUNTIME and record.get('architecture') == 'X64'
                    and type(record.get('executed')) is int and record['executed'] == 1
                    and record.get('passed') is True, 'allocation child runtime/execution mismatch')
                records[uid] = record
    require(set(records) == set(expected), 'missing allocation child witness')
    return [{key: records[uid][key] for key in ('scenario', 'runtime', 'architecture', 'executed', 'passed')}
            for uid in sorted(records)]


def inventory(folder: Path):
    return {str(p.relative_to(folder)): hashlib.sha256(p.read_bytes()).hexdigest()
            for p in sorted(folder.rglob('*')) if p.is_file() and
            (p.suffix == '.dll' or p.name.endswith(('.runtimeconfig.json', '.deps.json')))}


def tool_identity():
    root = TOOL.parents[2]
    revision = subprocess.check_output(['git', '-C', str(root), 'rev-parse', 'HEAD'],
        text=True, stderr=subprocess.STDOUT).strip()
    require(not subprocess.check_output(['git', '-C', str(root), 'status', '--porcelain',
        '--untracked-files=all'], text=True, stderr=subprocess.STDOUT).strip(), 'unit tooling checkout is dirty')
    return revision


def run(args):
    source, output = args.source.resolve(), args.output.resolve()
    require(not output.exists() and not output.is_relative_to(source), 'output must be new and outside source')
    output.mkdir(parents=True)
    phase = 'SOURCE'
    try:
        verify_source(source, output / 'source.log')
        tool_revision = tool_identity()
        raw, ordinary, workers = read_corpus(TOOL)
        print(f'UNIT_SOURCE sha={SOURCE} tree={TREE}', flush=True)
        env = {k: v for k, v in os.environ.items()
               if not k.startswith(('TUNIT_', 'TESTINGPLATFORM_', 'GITHUB_'))}
        for name in ('cli-home', 'http-cache', 'scratch', 'tmp'):
            (output / name).mkdir()
        env.update(DOTNET_CLI_HOME=str(output / 'cli-home'), NUGET_HTTP_CACHE_PATH=str(output / 'http-cache'),
            NUGET_SCRATCH=str(output / 'scratch'), TMPDIR=str(output / 'tmp'),
            NUGET_PACKAGES=env.get('NUGET_PACKAGES', str(output / 'packages')),
            DOTNET_PROCESSOR_COUNT='4', DOTNET_CLI_TELEMETRY_OPTOUT='1',
            TESTINGPLATFORM_TELEMETRY_OPTOUT='1', PYTHONDONTWRITEBYTECODE='1',
            DOTNET_ROOT=str(args.dotnet.parent), DOTNET_ROOT_X64=str(args.dotnet.parent),
            DOTNET_HOST_PATH=str(args.dotnet))
        phase = 'HOSTS'
        host_inventory(args.dotnet, args.runtime_dotnet, source, env, output)
        phase = 'BUILD'
        code, timed = execute([str(args.dotnet), 'build', PROJECT, '-c', 'Release', '-m:1', '-nr:false',
            '--disable-build-servers', '-p:UseSharedCompilation=false', '-v', 'minimal'],
            source, env, 900, output / 'build.log')
        require(not timed and code == 0, 'candidate build failed')
        assembly = source / ASSEMBLY
        phase = 'RUNTIME_CONFIG'
        for path in (assembly.with_suffix('.runtimeconfig.json'),
                     assembly.parent / 'allocation-probe/SharpLink.ReadOwnershipAllocationProbe.runtimeconfig.json'):
            require(path.is_file(), 'required runtime config missing')
            pin_runtime_config(path)
        hashes = inventory(assembly.parent)
        require(all(name in hashes for name in ('SharpLink.UnitTests.dll', 'SharpLink.Server.dll',
            'SharpLink.Runtime.dll', 'SharpLink.Client.dll', 'allocation-probe/SharpLink.ReadOwnershipAllocationProbe.dll')),
            'built execution closure incomplete')
        test_env = private_test_environment(env, args.runtime_dotnet, output)
        phase = 'DISCOVERY'
        code, timed = execute([str(args.runtime_dotnet), str(assembly), '--list-tests', 'json'],
            source, test_env, 180, output / 'discovery.json')
        require(not timed and code == 0, 'fresh discovery failed')
        discovery = strict_json((output / 'discovery.json').read_text(encoding='utf-8-sig'))
        discovered = discovery_uids(discovery)
        require(discovered == raw and uid_digest(discovered) == uid_digest(raw), 'fresh corpus differs from frozen exact UIDs')
        verify_explicit_sources(source, discovery, workers)
        census = {'raw_discovered': len(raw), 'raw_uid_sha256': uid_digest(raw),
            'ordinary': len(ordinary), 'ordinary_uid_sha256': uid_digest(ordinary),
            'explicit_uids_not_executed': len(workers), 'explicit_worker_classes': 4}
        print('UNIT_CENSUS ' + json.dumps(census, sort_keys=True), flush=True)
        phase = 'EXECUTION'
        code, timed = execute([str(args.runtime_dotnet), str(assembly), '--maximum-parallel-tests', '4',
            '--timeout', '20m', '--minimum-expected-tests', str(len(ordinary)), '--zero-tests-policy', 'strict',
            '--no-ansi', '--progress', 'off', '--show-stdout', 'None', '--show-stderr', 'None',
            '--show-test-results', 'failed,skipped', '--report-html', '--report-html-filename', str(output / 'results.html'),
            '--results-directory', str(output / 'results')], source, test_env, 1260, output / 'execution.log')
        require(not timed, 'ordinary test process timed out')
        phase = 'RESULT'
        reports = list(output.glob('*.tunit-report.json'))
        require(len(reports) == 1, 'missing or ambiguous final report')
        report = read_report(reports[0])
        require_runtime(report)
        result = validate_run(ordinary, report, code, timed)
        witnesses = allocation_children(report)
        require(hashes == inventory(assembly.parent), 'test execution closure changed')
        verify_source(source, output / 'source.log')
        require(tool_identity() == tool_revision, 'unit tooling changed during gate')
        summary = {'status': 'passed', 'source': SOURCE, 'tree': TREE, 'sdk': '10.0.102',
            'runtime': RUNTIME, 'census': census, 'result': result, 'allocation_children': witnesses,
            'assembly_sha256': hashes}
        (output / 'summary.json').write_text(json.dumps(summary, indent=2) + '\n')
        gate_record = expected_record(tool_revision)
        gate_record.update(assemblySha256=hashes, allocationChildren=witnesses,
            executedCount=result['executed'], executedUidSha256=result['uid_sha256'], passedCount=result['passed'])
        validate_record(gate_record)
        # This consumable success marker is absent for every failure/timeout/partial run.
        (output / 'unit-gate.json').write_text(json.dumps(gate_record, indent=2) + '\n')
        print('UNIT_GATE_PASS ' + json.dumps({'ordinary': len(ordinary), 'executed': result['executed'],
            'passed': result['passed'], 'failed': 0, 'skipped': 0, 'cancelled': 0,
            'runtime': RUNTIME, 'allocation_children': len(witnesses)}, sort_keys=True), flush=True)
        return 0
    except (GateError, OSError, ValueError, subprocess.SubprocessError) as error:
        (output / 'failure.json').write_text(json.dumps({'reason': phase, 'exception_type': type(error).__name__,
            'detail': str(error)}, indent=2) + '\n')
        print(f'UNIT_GATE_FAIL reason={phase}', flush=True)
        return 1


def main():
    parser = argparse.ArgumentParser()
    for name in ('source', 'dotnet', 'runtime-dotnet', 'output'):
        parser.add_argument('--' + name, required=True, type=Path)
    args = parser.parse_args()
    try:
        return run(args)
    except (GateError, OSError, ValueError):
        print('UNIT_GATE_FAIL reason=OUTPUT', flush=True)
        return 1


if __name__ == '__main__':
    raise SystemExit(main())
