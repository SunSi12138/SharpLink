#!/usr/bin/env python3
"""One ordinary hosted full-unit run; no benchmarks, filtering, retries or edits."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
import traceback

from census import inspect_build, inspect_census, inspect_discovery, require
from verify_request import CANDIDATE, CANDIDATE_TREE, ROOT, REQUEST

LOCAL_PRODUCT_COMMIT = 'e9f81d9a543b2f3368b54ba5d5c3a5068b5d5fdc'
PROJECT = 'test/SharpLink.UnitTests/SharpLink.UnitTests.csproj'
DLL = 'test/SharpLink.UnitTests/bin/Release/net10.0/SharpLink.UnitTests.dll'


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--product', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    source, output = args.product.resolve(), args.output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    status = {'passed': False, 'phase': 'source-identity'}

    def save(name, value):
        (output / name).write_text(json.dumps(value, indent=2) + '\n')

    def git(*argv):
        return subprocess.check_output(['git', *argv], cwd=source, text=True).strip()

    def run(name, argv, timeout=900):
        save(name + '-command.json', argv)
        with (output / (name + '.log')).open('w') as log:
            result = subprocess.run(argv, cwd=source, stdout=log, stderr=subprocess.STDOUT, text=True, timeout=timeout, check=False)
        save(name + '-exit.json', {'exitCode': result.returncode})
        return result.returncode

    try:
        identity = {'candidate': git('rev-parse', 'HEAD'), 'candidateTree': git('rev-parse', 'HEAD^{tree}'),
                    'localProductProvenance': LOCAL_PRODUCT_COMMIT, 'requestCommit': os.environ.get('GITHUB_SHA'),
                    'request': json.loads((ROOT / REQUEST).read_text()),
                    'toolCommit': subprocess.check_output(['git', 'rev-parse', 'HEAD^'], cwd=ROOT, text=True).strip(),
                    'runId': os.environ.get('GITHUB_RUN_ID'), 'attempt': os.environ.get('GITHUB_RUN_ATTEMPT'),
                    'runnerOS': os.environ.get('RUNNER_OS'), 'runnerArch': os.environ.get('RUNNER_ARCH'),
                    'imageOS': os.environ.get('ImageOS'), 'imageVersion': os.environ.get('ImageVersion')}
        save('source-identities.json', identity)
        require((identity['candidate'], identity['candidateTree']) == (CANDIDATE, CANDIDATE_TREE), 'Product commit/tree mismatch')
        require(not git('status', '--porcelain'), 'Product checkout must be initially clean')
        require(source != ROOT and not source.is_relative_to(ROOT), 'Product must be a distinct checkout')
        require(json.loads((source / 'global.json').read_text())['sdk']['version'] == '10.0.102', 'Product SDK pin changed')
        require(run('sdk-version', ['dotnet', '--version']) == 0, 'SDK check failed')
        require((output / 'sdk-version.log').read_text().strip() == '10.0.102', 'Resolved SDK not exactly 10.0.102')
        require(run('dotnet-info', ['dotnet', '--info']) == 0, 'SDK info failed')
        status['phase'] = 'build'
        code = run('build', ['dotnet', 'build', PROJECT, '-c', 'Release', '-v', 'minimal', '-m:1',
                             '--disable-build-servers', '/nodeReuse:false', '/p:UseSharedCompilation=false'])
        require(code == 0, 'Build failed; see build.log')
        inspect_build((output / 'build.log').read_text())
        binaries = {str(p.relative_to(source)): hashlib.sha256(p.read_bytes()).hexdigest()
                    for p in sorted((source / DLL).parent.glob('SharpLink*.dll'))}
        require(DLL in binaries, 'Missing unit test binary')
        save('build-stamp.json', {'candidate': CANDIDATE, 'tree': CANDIDATE_TREE, 'sdk': '10.0.102',
                                 'configuration': 'Release', 'warnings': 0, 'binariesSha256': binaries})
        status['phase'] = 'discovery'
        require(run('discovery', ['dotnet', str(source / DLL), '--list-tests', 'json', '--no-ansi', '--disable-logo']) == 0,
                'Discovery failed')
        discovery = json.loads((output / 'discovery.log').read_text())
        save('discovered-tests.json', discovery)
        expected = inspect_discovery(discovery)
        save('expected-executed-uids.json', sorted(expected))
        status['phase'] = 'full-unit-tests'
        results = output / 'results'
        results.mkdir()
        code = run('full-unit-tests', ['dotnet', str(source / DLL), '--no-ansi', '--disable-logo', '--progress', 'off',
                                      '--maximum-parallel-tests', '8', '--timeout', '10m',
                                      '--results-directory', str(results), '--report-trx',
                                      '--report-trx-filename', 'full-unit.trx', '--report-html'])
        # Inspect even a failed test process: preserve raw reports and explicit failure.
        status['testExitCode'] = code
        status['phase'] = 'exact-census'
        reports = list(results.glob('*.tunit-report.json'))
        require(len(reports) == 1, 'Expected one TUnit per-case JSON report')
        report = json.loads(reports[0].read_text())
        save('raw-outcome-summary.json', report['summary'])
        save('executed-tests.json', [t for group in report['groups'] for t in group['tests']])
        census = inspect_census(discovery, report, (output / 'full-unit-tests.log').read_text(),
                                (results / 'full-unit.trx').read_text())
        save('census.json', census)
        require(code == 0, 'Test process failed despite report census')
        require(not git('status', '--porcelain'), 'Product source changed during validation')
        status.update(passed=True, phase='complete')
    except Exception as error:
        status['error'] = str(error)
        (output / 'failure.txt').write_text(traceback.format_exc())
        print(traceback.format_exc(), file=sys.stderr)
    finally:
        save('status.json', status)
    return 0 if status['passed'] else 1


if __name__ == '__main__':
    sys.exit(main())
