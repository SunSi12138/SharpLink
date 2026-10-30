#!/usr/bin/env python3
"""Dev-only cleanup correctness: fresh UID delta, calibrated causal old arm, new arm."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import stat
import sys
import tempfile

import host
from baseline import BASELINE_UID, validate_baseline
from provenance import IDENTITIES, TEST_PATH, verify_release, verify_source
from scope import explicit_workers, ordinary_uids, verify_explicit_sources
from validate import GateError, discovery_uids, read_report, require, strict_json, uid_digest, unique_uids, validate_run

HERE = Path(__file__).resolve().parent
NEW_UIDS = unique_uids(strict_json((HERE / 'new-uids.json').read_text()), 'new cleanup UIDs')


def hashes(directory):
    require(directory.resolve(strict=True) == directory, 'noncanonical binary directory')
    files = sorted(directory.rglob('*.dll'))
    require(files and all(not p.is_symlink() and p.resolve(strict=True) == p
                          and p.is_file() and p.stat().st_nlink == 1 for p in files), 'unsafe DLL identity')
    return {p.relative_to(directory).as_posix(): hashlib.sha256(p.read_bytes()).hexdigest() for p in files}


def test_environment(env, runtime, output):
    require(output.is_absolute() and output.resolve(strict=True) == output, 'noncanonical result directory')
    report_dir = output / 'child-reports'; report_dir.mkdir(mode=0o700)
    require(stat.S_IMODE(report_dir.lstat().st_mode) == 0o700 and not report_dir.is_symlink(), 'unsafe report directory')
    value = host.private_test_environment(env, runtime)
    # MTP 2.5.1 generic env provider normalizes '__' and ignores case. An active
    # report option can outrank platformOptions, so remove all generic aliases.
    value = {k: v for k, v in value.items() if not k.replace('__', ':').lower().startswith(
        ('platformoptions:', 'commandlineoptions:', 'commandlineoptiondefaults:'))}
    value.update(platformOptions__resultDirectory=str(report_dir), TUNIT_DISABLE_ARTIFACT_UPLOAD='true')
    return value


def census(root, discovery, original=None):
    raw = discovery_uids(discovery)
    workers = explicit_workers(HERE)
    require(len(workers) == 5, 'explicit worker scope differs')
    verify_explicit_sources(root, discovery, workers)
    ordinary = ordinary_uids(raw, workers)
    require(len(NEW_UIDS) == 6, 'new fixture census differs')
    if original is None:
        require(len(raw) == 2193 and len(ordinary) == 2188 and not set(raw).intersection(NEW_UIDS),
                'original dev census differs')
    else:
        require(len(original) == 2193 and not set(original).intersection(NEW_UIDS), 'invalid original corpus')
        require(raw == sorted(original + NEW_UIDS), 'fresh UID delta is not exactly the six additions')
        require(len(raw) == 2199 and len(ordinary) == 2194, 'cleanup census differs')
        new = [t for t in discovery['tests'] if t['uid'] in NEW_UIDS]
        require(len(new) == 6 and all(t.get('type', {}).get('namespace') == 'SharpLink.UnitTests.Runtime'
                and t.get('type', {}).get('typeName') == 'SendPumpCleanupProgressTests'
                and t.get('location', {}).get('file', '').replace('\\', '/').endswith('/' + TEST_PATH)
                for t in new), 'new UID source mapping mismatch')
    print('CENSUS ' + json.dumps(dict(raw=len(raw), ordinary=len(ordinary), explicitWorkersNotExecuted=5,
        rawSha256=uid_digest(raw), ordinarySha256=uid_digest(ordinary)), sort_keys=True), flush=True)
    return raw, ordinary


def command(runtime, assembly, output, uid=None):
    result = [runtime, str(assembly), '--maximum-parallel-tests', '1' if uid else '4',
              '--minimum-expected-tests', '1' if uid else '2194', '--zero-tests-policy', 'strict',
              '--no-ansi', '--progress', 'off', '--show-stdout', 'None', '--show-stderr', 'None',
              '--show-test-results', 'failed,skipped', '--report-html',
              '--report-html-filename', str(output / 'result.html'), '--results-directory', str(output / 'results')]
    return result + (['--filter-uid', uid] if uid else ['--timeout', '20m'])


def final_report(output):
    reports = list(output.glob('*.tunit-report.json'))
    require(len(reports) == 1, 'missing or ambiguous TUnit report')
    return read_report(reports[0])


def prepare(root, phase, sdk, runtime, env, output, original=None):
    verify_source(root, phase)
    code, timed = host.execute([sdk, 'build', host.PROJECT, '-c', 'Release', '-m:1', '-nr:false',
        '--disable-build-servers', '-p:UseSharedCompilation=false', '-v', 'minimal'], root, env, 900, output / 'build.log')
    require(not timed and code == 0, 'normal Release unit build failed')
    assembly = root / host.ASSEMBLY
    identity = hashes(assembly.parent)
    require(assembly.name in identity, 'missing unit assembly')
    test_env = test_environment(env, runtime, output)
    code, timed = host.execute([runtime, str(assembly), '--list-tests', 'json'], root, test_env, 180, output / 'discovery.json')
    require(not timed and code == 0, 'fresh discovery failed')
    raw, ordinary = census(root, read_report(output / 'discovery.json'), original)
    require(hashes(assembly.parent) == identity, 'discovery changed DLL identity')
    (output / 'identity.json').write_text(json.dumps(dict(source=IDENTITIES[phase], dllSha256=identity,
        rawUidSha256=uid_digest(raw), ordinaryUidSha256=uid_digest(ordinary)), indent=2) + '\n')
    return assembly, identity, raw, ordinary


def stage(name, output, stages, action):
    print('CLEANUP_STAGE ' + name, flush=True)
    try:
        result = action()
        stages.append(dict(stage=name, passed=True))
        print('CLEANUP_PASS ' + name, flush=True)
        return result
    except host.UnsafeProcessGroup:
        # Never advance to another source/test while descendants may still run.
        raise
    except Exception as error:
        stages.append(dict(stage=name, passed=False, errorType=type(error).__name__))
        (output / (name + '-failure.json')).write_text(json.dumps(dict(type=type(error).__name__, detail=str(error)), indent=2) + '\n')
        print('CLEANUP_FAIL ' + name + ' type=' + type(error).__name__, flush=True)
        return None


def execute_case(root, assembly, identity, runtime, env, output, uid, ordinary=None, baseline=False):
    output.mkdir(mode=0o700)
    test_env = test_environment(env, runtime, output)
    require(hashes(assembly.parent) == identity, 'DLL identity changed before execution')
    code, timed = host.execute(command(runtime, assembly, output, uid), root, test_env,
                               120 if uid else 1260, output / 'execution.log')
    report = final_report(output)
    if baseline:
        validate_baseline(report, code, timed)
    else:
        host.require_test_runtime(report)
        summary = validate_run([uid] if uid else ordinary, report, code, timed)
        if uid is None:
            host.verify_allocation_child_runtimes(report)
        print('RESULT ' + json.dumps(summary, sort_keys=True), flush=True)
    require(hashes(assembly.parent) == identity, 'DLL identity changed after execution')


def execute_all(roots, sdk, runtime, env, output, stages):
    prepared = {}
    original = None
    for phase in ('original', 'baseline', 'candidate'):
        folder = output / phase; folder.mkdir(mode=0o700)
        if phase != 'original' and original is None:
            stages.append(dict(stage=phase + '-prepare', passed=False, blockedBy='original-prepare'))
            continue
        value = stage(phase + '-prepare', folder, stages,
            lambda: prepare(roots[phase], phase, sdk, runtime, env, folder, original))
        if value is None: continue
        prepared[phase] = value
        if phase == 'original': original = value[2]
        elif phase == 'baseline':
            stage('baseline-causal', folder, stages, lambda: execute_case(roots[phase], value[0], value[1], runtime,
                env, folder / 'causal', BASELINE_UID, baseline=True))
        else:
            for index, uid in enumerate(NEW_UIDS):
                stage('focused-' + str(index), folder, stages, lambda: execute_case(roots[phase], value[0], value[1], runtime,
                    env, folder / ('focused-' + str(index)), uid))
            stage('ordinary', folder, stages, lambda: execute_case(roots[phase], value[0], value[1], runtime,
                env, folder / 'ordinary', None, ordinary=value[3]))
    # Repeat identity/source checks for every prepared arm even after a failed case.
    for phase, value in prepared.items():
        def verify():
            require(hashes(value[0].parent) == value[1], 'final DLL identity changed')
            verify_source(roots[phase], phase)
        stage(phase + '-final', output / phase, stages, verify)
    require(len(stages) == 14 and all(s['passed'] for s in stages), 'one or more cleanup stages failed or blocked')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--release-root', type=Path, required=True)
    for phase in IDENTITIES: parser.add_argument('--' + phase + '-root', type=Path)
    parser.add_argument('--provenance-only', action='store_true')
    args = parser.parse_args()
    verify_release(args.release_root.resolve(), os.environ.get('GITHUB_SHA', ''), os.environ.get('GITHUB_REF', ''))
    require(os.environ.get('GITHUB_RUN_ATTEMPT') == '1', 'only one retained attempt is authorized')
    if args.provenance_only:
        print('CLEANUP_PROVENANCE_PASS source=' + IDENTITIES['candidate'][0] + ' baseline=' + IDENTITIES['baseline'][0])
        return 0
    roots = {phase: getattr(args, phase + '_root') for phase in IDENTITIES}
    require(all(path is not None and path.is_absolute() and path.resolve(strict=True) == path for path in roots.values()),
            'all source roots must be absolute/canonical')
    require(len(set(roots.values())) == 3 and all(not a.is_relative_to(b) for a in roots.values() for b in roots.values() if a != b),
            'source roots overlap')
    env = {k: v for k, v in os.environ.items() if not k.startswith(('TUNIT_', 'TESTINGPLATFORM_', 'DOTNET_', 'COMPlus_', 'MSBUILD'))}
    sdk = env.get('SDK_DOTNET', '')
    env.update(DOTNET_ROOT=str(Path(sdk).parent), DOTNET_ROOT_X64=str(Path(sdk).parent), DOTNET_HOST_PATH=sdk,
        DOTNET_PROCESSOR_COUNT='4', DOTNET_CLI_TELEMETRY_OPTOUT='1', TESTINGPLATFORM_TELEMETRY_OPTOUT='1',
        PYTHONDONTWRITEBYTECODE='1')
    sdk, runtime = host.verify_hosts(roots['candidate'], env)
    # Short private per-run TMPDIR avoids changing existing filesystem-UDS tests.
    output = Path(tempfile.mkdtemp(prefix='slc-', dir='/tmp'))
    require(all(not output.is_relative_to(root) for root in roots.values()), 'output inside source')
    env['TMPDIR'] = str(output)
    stages = []
    try:
        execute_all(roots, sdk, runtime, env, output, stages)
        verify_release(args.release_root.resolve(), os.environ.get('GITHUB_SHA', ''), os.environ.get('GITHUB_REF', ''))
        print('DEV_CLEANUP_GATE_PASS ordinary=2194 focused=6 causalBaseline=1 explicitWorkersNotExecuted=5', flush=True)
        return 0
    except Exception as error:
        (output / 'failure.json').write_text(json.dumps(dict(type=type(error).__name__, detail=str(error)), indent=2) + '\n')
        print('DEV_CLEANUP_GATE_FAIL type=' + type(error).__name__, flush=True)
        return 1
    finally:
        (output / 'summary.json').write_text(json.dumps(dict(sources=IDENTITIES, stages=stages), indent=2) + '\n')


if __name__ == '__main__':
    try: sys.exit(main())
    except Exception as error:
        print('DEV_CLEANUP_PREFLIGHT_FAIL type=' + type(error).__name__, flush=True)
        sys.exit(1)
