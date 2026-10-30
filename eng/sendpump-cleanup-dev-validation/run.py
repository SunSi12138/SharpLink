#!/usr/bin/env python3
"""One ordinary-only diagnostic on frozen dev cleanup; no baseline/focused rerun."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import stat
import sys
import tempfile
import allocation
import host
from diagnostics import Diagnostics
from provenance import SOURCE_SHA, SOURCE_TREE, verify_release, verify_source
from scope import explicit_workers, ordinary_uids, verify_explicit_sources
from validate import GateError, discovery_uids, read_report, require, uid_digest, validate_run
HERE = Path(__file__).resolve().parent
RAW_SHA256 = 'b1ed36e5939aedfeb5f97598dbb38481682082addb4e640d1345c3e0873c2514'
ORDINARY_SHA256 = 'd4ed0e528f0f76a629cba2ecc9d1e4f2640eac4fa13cf88100d69ed4eb0288b3'

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


def frozen_census(source, discovery):
    raw = discovery_uids(discovery)
    require(len(raw) == 2199 and uid_digest(raw) == RAW_SHA256, 'fresh raw census differs from run40984965648')
    workers = explicit_workers(HERE)
    require(len(workers) == 5, 'explicit-worker census differs')
    verify_explicit_sources(source, discovery, workers)
    ordinary = ordinary_uids(raw, workers)
    require(len(ordinary) == 2194 and uid_digest(ordinary) == ORDINARY_SHA256, 'ordinary corpus differs from frozen digest')
    allocation.verify_discovery(discovery)
    return ordinary


def run(args):
    output, source, release = args.output, args.source_root, args.release_root
    require(all(p.is_absolute() for p in (output, source, release)), 'absolute paths required')
    require(source.resolve(strict=True) == source and release.resolve(strict=True) == release
            and output.parent.resolve(strict=True) == output.parent, 'canonical paths required')
    require(not output.is_relative_to(source) and not output.is_relative_to(release), 'reports must be outside sources/tools')
    output.mkdir(mode=0o700)
    diag = Diagnostics()
    identity = dict(sourceSHA=SOURCE_SHA, sourceTree=SOURCE_TREE, priorRun=40984965648,
        expectedRawSha256=RAW_SHA256, expectedOrdinarySha256=ORDINARY_SHA256, expectedOrdinary=2194)
    binaries = None
    try:
        diag.stage('REQUEST')
        identity['request'] = verify_release(release, os.environ.get('GITHUB_SHA', ''), os.environ.get('GITHUB_REF', ''))
        require(os.environ.get('GITHUB_RUN_ATTEMPT') == '1', 'attempt must be one')
        diag.stage('SOURCE'); verify_source(source, 'candidate'); allocation.verify_source(source)
        env = {k: v for k, v in os.environ.items() if not k.startswith(('TUNIT_', 'TESTINGPLATFORM_', 'DOTNET_', 'COMPlus_', 'MSBUILD'))}
        sdk = env.get('SDK_DOTNET', '')
        env.update(DOTNET_ROOT=str(Path(sdk).parent), DOTNET_ROOT_X64=str(Path(sdk).parent), DOTNET_HOST_PATH=sdk,
            DOTNET_PROCESSOR_COUNT='4', DOTNET_CLI_TELEMETRY_OPTOUT='1', TESTINGPLATFORM_TELEMETRY_OPTOUT='1',
            PYTHONDONTWRITEBYTECODE='1')
        diag.stage('HOSTS'); sdk, runtime = host.verify_hosts(source, env)
        env['TMPDIR'] = tempfile.mkdtemp(prefix='slc-', dir='/tmp')
        identity['shortTempRetained'] = env['TMPDIR']
        diag.stage('BUILD')
        code, timed = host.execute([sdk, 'build', host.PROJECT, '-c', 'Release', '-m:1', '-nr:false',
            '--disable-build-servers', '-p:UseSharedCompilation=false', '-v', 'minimal'], source, env, 900, output / 'build.log')
        diag.emit(exit=code, timedOut=timed)
        require(not timed and code == 0, 'normal Release unit build failed')
        assembly = source / host.ASSEMBLY
        diag.stage('BINARY_IDENTITY'); binaries = hashes(assembly.parent)
        require(assembly.name in binaries, 'missing unit assembly')
        identity['dllSha256'] = binaries
        discovery_output = output / 'discovery'; discovery_output.mkdir(mode=0o700)
        discovery_env = test_environment(env, runtime, discovery_output)
        diag.stage('DISCOVERY_PROCESS')
        code, timed = host.execute([runtime, str(assembly), '--list-tests', 'json'], source, discovery_env,
                                   180, discovery_output / 'discovery.json')
        diag.emit(exit=code, timedOut=timed)
        require(not timed and code == 0, 'fresh discovery failed')
        diag.stage('DISCOVERY_CENSUS')
        ordinary = frozen_census(source, read_report(discovery_output / 'discovery.json'))
        diag.emit(rawCount=2199, ordinaryCount=2194, explicitWorkersNotExecuted=5,
                  rawSha256=RAW_SHA256, ordinarySha256=uid_digest(ordinary))
        require(hashes(assembly.parent) == binaries, 'discovery changed DLL identity')
        case = output / 'ordinary'; case.mkdir(mode=0o700)
        test_env = test_environment(env, runtime, case)
        diag.stage('EXECUTION_PROCESS')
        code, timed = host.execute(command(runtime, assembly, case), source, test_env, 1260, case / 'execution.log')
        diag.emit(exit=code, timedOut=timed)
        diag.stage('RESULT_REPORT'); report = final_report(case)
        diag.observe_report(report, ordinary, code, timed)
        diag.stage('RESULT_RUNTIME'); host.require_test_runtime(report)
        summary = validate_run(ordinary, report, code, timed, on_stage=diag.stage)
        diag.emit(**summary)
        diag.stage('ALLOCATION_DEV_RESULT'); allocation.verify_result(ordinary, report)
        diag.emit(executed=2, passed=2, inProcess=True, childProbeRequired=False)
    except Exception as error:
        diag.failure(error)
    # Read-only final integrity checks preserve failures; they never rerun tests.
    for stage_name, action in (
        ('FINAL_DLL_IDENTITY', lambda: require(binaries is not None and hashes((source / host.ASSEMBLY).parent) == binaries,
                                             'final DLL identity missing or changed')),
        ('FINAL_SOURCE', lambda: (verify_source(source, 'candidate'), allocation.verify_source(source))),
        ('FINAL_REQUEST', lambda: verify_release(release, os.environ.get('GITHUB_SHA', ''), os.environ.get('GITHUB_REF', '')))):
        try: diag.stage(stage_name); action()
        except Exception as error: diag.failure(error)
    diag.stage('COMPLETE'); diag.emit(passed=not diag.failures)
    diag.save(output / 'diagnostic.json', identity)
    print('DEV_ORDINARY_DIAGNOSTIC_' + ('FAIL' if diag.failures else 'PASS'), flush=True)
    return 1 if diag.failures else 0


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--release-root', type=Path, required=True)
    parser.add_argument('--source-root', type=Path)
    parser.add_argument('--output', type=Path)
    parser.add_argument('--provenance-only', action='store_true')
    args = parser.parse_args()
    if args.provenance_only:
        verify_release(args.release_root.resolve(), os.environ.get('GITHUB_SHA', ''), os.environ.get('GITHUB_REF', ''))
        require(os.environ.get('GITHUB_RUN_ATTEMPT') == '1', 'attempt must be one')
        print('ORDINARY_PROVENANCE_PASS source=' + SOURCE_SHA)
        return 0
    require(args.source_root is not None and args.output is not None, 'missing source/output')
    return run(args)


if __name__ == '__main__':
    try: sys.exit(main())
    except Exception as error:
        print('ORDINARY_PREFLIGHT_FAIL type=' + type(error).__name__, flush=True)
        sys.exit(1)
