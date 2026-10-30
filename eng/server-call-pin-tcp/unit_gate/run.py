#!/usr/bin/env python3
"""Both arms: one full ordinary execution plus one actual dynamic-retirement gate.

Consumes prepared canonical outputs. Never builds, mutates runtimeconfigs, reruns,
replaces a failure, uploads raw data, or silently excludes ordinary UIDs.
"""
from __future__ import annotations
import argparse
import json
import os
from pathlib import Path
import signal
import subprocess
import sys
import time

if __package__ in (None, ''):
    sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
from common import ARMS, CONTRACT, ROOT, inventory, source_inventory
from unit_gate.provenance import read_corpus, verify_source
from unit_gate.record import expected_record, validate_record, graph_digest, DYNAMIC_UID, CLOSURE_NAMES
from prepare import correctness_graph_inventory
from unit_gate.temp_paths import create_short_temp
from unit_gate.diagnostics import emit_diagnostics
from unit_gate.postgate_diagnostics import PostGateDiagnostics, emit_stage_failure
from unit_gate.scope import verify_explicit_sources
from unit_gate.validate import (GateError, discovery_uids, read_report, require,
                                strict_json, uid_digest, validate_run, check_summary)

TOOL = Path(__file__).resolve().parent
PROJECT = 'test/SharpLink.UnitTests/SharpLink.UnitTests.csproj'
ASSEMBLY = 'test/SharpLink.UnitTests/bin/Release/net10.0/SharpLink.UnitTests.dll'
INTEGRATION = 'test/SharpLink.IntegrationTests/bin/Release/net10.0/SharpLink.IntegrationTests.dll'
RUNTIME = '10.0.14'


class UnsafeProcessGroup(GateError):
    pass


def group_alive(pid):
    listing = subprocess.check_output(['ps', '-eo', 'pgid=,stat='], text=True)
    return any(int(parts[0]) == pid and not parts[1].startswith('Z')
               for line in listing.splitlines() if len(parts := line.split()) == 2)


def execute(command, cwd, env, timeout, output):
    with output.open('xb') as stream:
        process = subprocess.Popen(list(map(str, command)), cwd=cwd, env=env, stdout=stream,
            stderr=subprocess.STDOUT, start_new_session=True)
        try:
            code, timed_out = process.wait(timeout=timeout), False
        except subprocess.TimeoutExpired:
            os.killpg(process.pid, signal.SIGKILL)
            code, timed_out = process.wait(), True
        leaked = group_alive(process.pid)
        if leaked:
            os.killpg(process.pid, signal.SIGKILL)
            until = time.monotonic() + 5
            while group_alive(process.pid) and time.monotonic() < until:
                time.sleep(.05)
        quiescent = not group_alive(process.pid)
    output.with_suffix(output.suffix + '.status.json').write_text(json.dumps(
        {'exit': code, 'timed_out': timed_out, 'descendantLeak': leaked,
         'processGroupQuiescent': quiescent, 'command': list(map(str, command))}, indent=2))
    if not quiescent:
        raise UnsafeProcessGroup('test process group remains unsafe')
    return (code if not leaked else -1), timed_out


def host_inventory(runtime_host: Path, root, env, output):
    require(runtime_host.is_absolute() and runtime_host.is_file() and runtime_host.resolve() == runtime_host,
            'missing absolute canonical runtime host')
    checks = [('--list-sdks', ''), ('--list-runtimes',
        f'Microsoft.NETCore.App {RUNTIME} [{runtime_host.parent / "shared/Microsoft.NETCore.App"}]')]
    for index, (option, expected) in enumerate(checks):
        log = output / f'host-{index}.log'
        code, timed = execute([runtime_host, option], root, env, 30, log)
        require(not timed and code == 0 and log.read_text().strip() == expected,
                'correctness host must be private Core-only runtime14')


def private_test_environment(env, host: Path, short_temp: Path):
    value = {k: v for k, v in env.items() if not k.startswith(
        ('DOTNET_', 'COMPlus_', 'GITHUB_', 'TUNIT_', 'TESTINGPLATFORM_', 'MSBUILD'))}
    value.update(CI='true', DOTNET_ROOT=str(host.parent), DOTNET_ROOT_X64=str(host.parent),
        DOTNET_HOST_PATH=str(host), DOTNET_ROLL_FORWARD='Disable', DOTNET_MULTILEVEL_LOOKUP='0',
        DOTNET_PROCESSOR_COUNT='4', PATH=str(host.parent) + os.pathsep + env.get('PATH', ''),
        TMPDIR=str(short_temp), TESTINGPLATFORM_TELEMETRY_OPTOUT='1',
        DOTNET_CLI_TELEMETRY_OPTOUT='1', PYTHONDONTWRITEBYTECODE='1')
    return value


def validate_runtime_config(path: Path):
    options = strict_json(path.read_text()).get('runtimeOptions')
    require(isinstance(options, dict) and options.get('rollForward') == 'Disable', 'invalid correctness runtime config')
    frameworks = options.get('frameworks', [options.get('framework')])
    require(frameworks == [{'name': 'Microsoft.NETCore.App', 'version': RUNTIME}],
            'correctness config must pin Core-only14')


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


def tool_identity():
    root = ROOT.parents[1]
    revision = subprocess.check_output(['git', '-C', str(root), 'rev-parse', 'HEAD'], text=True).strip()
    require(not subprocess.check_output(['git', '-C', str(root), 'status', '--porcelain',
        '--untracked-files=all'], text=True).strip(), 'unit tooling checkout is dirty')
    return revision


def check_prepared(manifest, tool_revision):
    require(manifest['contract'] == CONTRACT and manifest['toolRevision'] == tool_revision and
            manifest['toolSourceHashes'] == source_inventory(ROOT) and
            manifest['metadataPreflight'] == 'passed', 'prepared tool/metadata contract differs')
    require(set(manifest['arms']) == set(ARMS), 'prepared arm census differs')
    for arm in ARMS:
        data = manifest['arms'][arm]
        require(data['source']['commit'] == CONTRACT[arm + 'Source'] and
                data['source']['tree'] == CONTRACT[arm + 'Tree'], 'prepared arm identity differs')
        source = Path(data['source']['path'])
        require(source.is_absolute() and source.resolve(strict=True) == source, 'noncanonical source path')
        correctness = data['correctness']
        require(correctness_graph_inventory(source) == correctness['graphSnapshot'], 'prepared full correctness graph changed')
        for key, relative, snapshot in [('unitOutput', ASSEMBLY, 'unitSnapshot'),
                                      ('integrationOutput', INTEGRATION, 'integrationSnapshot')]:
            folder = Path(correctness[key])
            require(folder == (source / relative).parent and folder.resolve(strict=True) == folder,
                    'correctness output escaped canonical source')
            require(not any(p.is_symlink() for p in folder.rglob('*')), 'symlink in correctness output')
            require(inventory(folder) == correctness[snapshot], 'prepared correctness snapshot changed')
            require(set(data['closure']) == CLOSURE_NAMES and all(
                correctness[snapshot].get(name) == sha for name, sha in data['closure'].items()),
                'correctness frozen closure differs')
        for relative in (ASSEMBLY.replace('.dll', '.runtimeconfig.json'),
                'test/SharpLink.UnitTests/bin/Release/net10.0/allocation-probe/SharpLink.ReadOwnershipAllocationProbe.runtimeconfig.json',
                INTEGRATION.replace('.dll', '.runtimeconfig.json')):
            validate_runtime_config(source / relative)


def report_from(output):
    reports = list(output.glob('*.tunit-report.json'))
    require(len(reports) == 1, 'missing or ambiguous final report')
    return read_report(reports[0])


def test_command(host, assembly, output, count, uid=None):
    command = [host, assembly]
    if uid is not None:
        command += ['--filter-uid', uid]
    return command + ['--maximum-parallel-tests', '1' if uid else '4', '--timeout', '20m',
        '--minimum-expected-tests', str(count), '--zero-tests-policy', 'strict', '--no-ansi', '--progress', 'off',
        '--show-stdout', 'None', '--show-stderr', 'None', '--show-test-results', 'failed,skipped',
        '--report-html', '--report-html-filename', output / 'results.html', '--results-directory', output / 'results']


def validate_integration(report, code, timed):
    require(not timed and code == 0, 'dynamic correctness process failed')
    require(type(report.get('schemaVersion')) is int and report['schemaVersion'] == 1 and
            report.get('assemblyName') == 'SharpLink.IntegrationTests' and
            report.get('tunitVersion') == '1.73.19.0' and
            report.get('runtimeVersion') == '.NET ' + RUNTIME, 'dynamic report identity')
    generation = report.get('publicationGeneration')
    require(isinstance(generation, str) and len(generation) == 32 and
            all(c in '0123456789abcdef' for c in generation), 'dynamic generation')
    groups = report.get('groups')
    require(isinstance(groups, list) and groups, 'dynamic groups')
    tests = []
    for group in groups:
        require(isinstance(group, dict) and isinstance(group.get('tests'), list) and
                all(isinstance(t, dict) for t in group['tests']), 'dynamic group shape')
        check_summary(group.get('summary'), len(group['tests']), 'dynamic group')
        tests.extend(group['tests'])
    require(len(tests) == 1 and tests[0].get('id') == DYNAMIC_UID and tests[0].get('status') == 'passed',
            'exact dynamic module-retirement execution')
    check_summary(report.get('summary'), 1, 'dynamic summary')


def run_arm(arm, data, runtime, output, env, record):
    postgate = PostGateDiagnostics(arm)
    source = Path(data['source']['path'])
    verify_source(source, output / 'source.log', arm)
    require(correctness_graph_inventory(source) == data['correctness']['graphSnapshot'], 'pre-gate correctness graph changed')
    raw, ordinary, workers = read_corpus(TOOL, arm)
    short_temp = create_short_temp(output)
    environment = private_test_environment(env, runtime, short_temp)
    assembly = source / ASSEMBLY
    code, timed = execute([runtime, assembly, '--list-tests', 'json'], source, environment, 180, output / 'discovery.json')
    require(not timed and code == 0, 'fresh ordinary discovery failed')
    discovery = read_report(output / 'discovery.json')
    require(discovery_uids(discovery) == raw, 'fresh corpus differs from frozen exact UIDs')
    verify_explicit_sources(source, discovery, workers)
    print(f'UNIT_CENSUS arm={arm} raw={len(raw)} ordinary={len(ordinary)} excludedExplicit=5', flush=True)
    code, timed = execute(test_command(runtime, assembly, output, len(ordinary)), source,
                          environment, 1260, output / 'execution.log')
    report = report_from(output)
    emit_diagnostics(report, ordinary, code, timed)
    postgate.report('ORDINARY_RUNTIME', report, len(ordinary), code, timed)
    require_runtime(report)
    validate_run(ordinary, report, code, timed, on_stage=lambda stage: postgate.validation(stage, report, len(ordinary), code, timed))
    postgate.children(report)
    record['allocationChildren'] = allocation_children(report)
    postgate.stage('ORDINARY_SNAPSHOT')
    postgate.snapshot('ORDINARY_SNAPSHOT', data['correctness']['unitSnapshot'], inventory(assembly.parent))
    require(inventory(assembly.parent) == data['correctness']['unitSnapshot'], 'ordinary output bytes changed')
    record['assemblySha256'] = inventory(assembly.parent)
    postgate.stage('ORDINARY_GRAPH')
    postgate.snapshot('ORDINARY_GRAPH', data['correctness']['graphSnapshot'], correctness_graph_inventory(source))
    require(correctness_graph_inventory(source) == data['correctness']['graphSnapshot'], 'ordinary changed full correctness graph')
    dynamic = output / 'dynamic-module-retirement'
    dynamic.mkdir()
    integration = source / INTEGRATION
    postgate.stage('DYNAMIC_DISCOVERY_PROCESS')
    code, timed = execute([runtime, integration, '--list-tests', 'json'], source, environment,
                          180, dynamic / 'discovery.json')
    postgate.process('DYNAMIC_DISCOVERY_EXIT', code, timed)
    require(not timed and code == 0, 'fresh dynamic discovery failed')
    postgate.stage('DYNAMIC_DISCOVERY_REPORT')
    discovery = read_report(dynamic / 'discovery.json')
    postgate.discovery(discovery, DYNAMIC_UID)
    require(DYNAMIC_UID in discovery_uids(discovery), 'missing exact dynamic UID')
    selected = [t for t in discovery['tests'] if t['uid'] == DYNAMIC_UID]
    postgate.mapping(selected)
    require(len(selected) == 1 and selected[0].get('location', {}).get('file', '').replace('\\', '/').endswith(
        '/test/SharpLink.IntegrationTests/RuntimeAssemblyIntegrationTests.PhysicalOwnership.cs'), 'dynamic UID source mapping')
    postgate.stage('DYNAMIC_EXECUTION_PROCESS')
    code, timed = execute(test_command(runtime, integration, dynamic, 1, DYNAMIC_UID), source,
                          environment, 180, dynamic / 'execution.log')
    postgate.process('DYNAMIC_EXECUTION_EXIT', code, timed)
    postgate.stage('DYNAMIC_RESULT_REPORT')
    postgate.report('DYNAMIC_RESULT', report_from(dynamic), 1, code, timed)
    validate_integration(report_from(dynamic), code, timed)
    postgate.stage('DYNAMIC_SNAPSHOT')
    postgate.snapshot('DYNAMIC_SNAPSHOT', data['correctness']['integrationSnapshot'], inventory(integration.parent))
    require(inventory(integration.parent) == data['correctness']['integrationSnapshot'], 'integration output bytes changed')
    record['integrationAssemblySha256'] = inventory(integration.parent)
    record['frozenClosure'] = data['closure']
    postgate.stage('DYNAMIC_GRAPH')
    graph = correctness_graph_inventory(source)
    postgate.snapshot('DYNAMIC_GRAPH', data['correctness']['graphSnapshot'], graph)
    require(graph == data['correctness']['graphSnapshot'], 'dynamic changed full correctness graph')
    record['correctnessGraphSha256'] = graph_digest(graph)
    postgate.stage('FINAL_SOURCE_IDENTITY')
    verify_source(source, output / 'source.log', arm)
    postgate.stage('ARM_POSTGATE_PASS')
    print(f'UNIT_ARM_PASS arm={arm} ordinary={len(ordinary)} actualDynamicRetirement=1 CoreOnly14=true', flush=True)


def run(args):
    prepared, output = args.prepared.resolve(strict=True), args.output.resolve()
    require(not output.exists() and not output.is_relative_to(prepared), 'output must be new and outside preparation')
    output.mkdir(parents=True)
    try:
        revision = tool_identity()
        manifest = read_report(prepared / 'prepared.json')
        check_prepared(manifest, revision)
        require(all(not output.is_relative_to(Path(manifest['arms'][arm]['source']['path'])) for arm in ARMS),
                'gate output must be outside both sources')
        runtime = args.runtime_dotnet.resolve(strict=True)
        env = private_test_environment(os.environ, runtime, create_short_temp(output))
        host_inventory(runtime, prepared, env, output)
        value, failures = expected_record(revision), []
        for arm in ARMS:
            arm_output = output / arm
            arm_output.mkdir()
            try:
                run_arm(arm, manifest['arms'][arm], runtime, arm_output, env, value['arms'][arm])
            except UnsafeProcessGroup:
                raise
            except (GateError, OSError, ValueError, TypeError, KeyError, subprocess.SubprocessError) as error:
                emit_stage_failure(arm, type(error).__name__)
                failures.append({'arm': arm, 'error': str(error), 'type': type(error).__name__})
                (arm_output / 'failure.json').write_text(json.dumps(failures[-1], indent=2) + '\n')
                print(f'UNIT_ARM_FAIL arm={arm} reason={type(error).__name__}', flush=True)
        require(not failures, 'one or more ordinary/dynamic arm gates failed')
        postgate = PostGateDiagnostics('both')
        postgate.stage('FINAL_PREPARED_SNAPSHOTS')
        check_prepared(manifest, revision)
        postgate.stage('FINAL_TOOL_IDENTITY')
        require(tool_identity() == revision, 'unit tooling changed during execution')
        postgate.stage('FINAL_RECORD_BINDING')
        validate_record(value, revision, manifest)
        (output / 'unit-gate.json').write_text(json.dumps(value, indent=2) + '\n')
        postgate.stage('COMPLETE_POSTGATE_PASS')
        print('UNIT_GATE_PASS bothArms=true actualDynamicRetirementEachArm=true CoreOnly14=true', flush=True)
        return 0
    except (GateError, OSError, ValueError, TypeError, KeyError, subprocess.SubprocessError) as error:
        emit_stage_failure('both', type(error).__name__)
        (output / 'failure.json').write_text(json.dumps({'type': type(error).__name__, 'detail': str(error)}, indent=2) + '\n')
        print('UNIT_GATE_FAIL reason=' + type(error).__name__, flush=True)
        return 1


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ('prepared', 'runtime-dotnet', 'output'):
        parser.add_argument('--' + name, required=True, type=Path)
    args = parser.parse_args()
    try:
        return run(args)
    except (GateError, OSError, ValueError):
        print('UNIT_GATE_FAIL reason=OUTPUT', flush=True)
        return 1


if __name__ == '__main__':
    raise SystemExit(main())
