#!/usr/bin/env python3
"""Frozen-source package forwarding-hop validation. No RPC or artifact upload."""
import hashlib
import importlib.util
import json
import os
import re
import uuid
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import time
import xml.etree.ElementTree as ET
sys.dont_write_bytecode = True
HERE = Path(__file__).resolve().parent
EXPECTED = json.loads((HERE / 'expected.json').read_text())


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def load(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def git(root, *args):
    return subprocess.check_output(['git', '-c', 'core.hooksPath=/dev/null', *args], cwd=root,
                                   text=True, stderr=subprocess.PIPE, timeout=30).strip()


def check_source(root):
    if git(root, 'rev-parse', 'HEAD') != EXPECTED['sourceCommit']:
        raise ValueError('Source HEAD differs from frozen candidate')
    if git(root, 'rev-parse', 'HEAD:src') != EXPECTED['sourceTree']:
        raise ValueError('Production source tree differs from frozen candidate')
    if git(root, 'status', '--porcelain'):
        raise ValueError('Source checkout must be clean')
    for path, sha in EXPECTED['helpers'].items():
        if digest(root / path) != sha:
            raise ValueError('Frozen helper mismatch: ' + path)
    forwarding = (root / 'eng/public-api/3.0.0/SharpLink.Sdk.forwarders.txt').read_text().splitlines()
    if forwarding != [name + ' -> SharpLink.Abstractions' for name in EXPECTED['forwardedNames']]:
        raise ValueError('Expected forwarding names differ from frozen baseline')


def check_graph(root, names):
    allowed = {(root / 'src' / n / (n + '.csproj')).resolve() for n in names}
    for path in allowed:
        for ref in ET.parse(path).findall('.//ProjectReference'):
            if (path.parent / ref.attrib['Include'].replace('\\', '/')).resolve() not in allowed:
                raise ValueError('Unexpected non-production project reference in ' + path.name)


def cleanup_classification(outcome):
    messages = outcome.get('cleanupErrors', [])
    owned = sum(message == 'Owned processes remained after command exit/deadline' for message in messages)
    remaining = outcome.get('remainingOwned')
    return dict(ownedProcessesAfterExitCount=owned, otherCleanupErrorCount=len(messages) - owned,
                remainingOwnedCount=len(remaining) if isinstance(remaining, dict) else None)


def read_json(output, errors):
    if errors.read_bytes() or output.stat().st_size > 24000:
        raise ValueError('Unexpected probe output')
    return json.loads(output.read_text())


def validate_self_tests(summary):
    if set(summary) != {'schemaVersion','mode','success','code','passed','evidenceScope','tests'}:
        raise ValueError('Unexpected self-test schema')
    if summary['schemaVersion'] != 1 or summary['mode'] != 'synthetic-self-test' or summary['success'] is not True or summary['code'] != 'Pass' or summary['evidenceScope'] != 'ManufacturedMetadataFixturesNotHistoricalConsumer':
        raise ValueError('Self-test did not pass')
    expected = json.loads((HERE / 'probe/self-test-expected.json').read_text())
    tests = summary['tests']
    if type(summary['passed']) is not int or not isinstance(tests,list) or len(tests) != len(expected) or summary['passed'] != len(expected):
        raise ValueError('Self-test evidence count mismatch')
    actual = {}
    for row in tests:
        if set(row) != {'name','expectedCode','passed'} or row['passed'] is not True or row['name'] in actual:
            raise ValueError('Unexpected self-test evidence')
        actual[row['name']] = row['expectedCode']
    if actual != expected:
        raise ValueError('Self-test exact name/code manifest mismatch')


def validate_probe(summary, hashes):
    expected = dict(schemaVersion=1,mode='packaged-sdk-forwarding',success=True,code='Pass',
        sourceCommit=EXPECTED['sourceCommit'],evidenceScope='ExplicitSdkQualifiedForwardingOnly',
        historical203AbiProven=False,ordinaryPackageReferenceConsumerProven=False,expectedForwarderCount=20)
    if set(summary) != set(expected) | {'sdk','abstractions','runtimeVersion','runtime'}:
        raise ValueError('Unexpected probe schema')
    for key,value in expected.items():
        if type(summary[key]) is not type(value) or summary[key] != value:
            raise ValueError('Probe assertion mismatch')
    if not isinstance(summary['runtimeVersion'],str) or not re.fullmatch(r'10\.0\.[0-9]+',summary['runtimeVersion']):
        raise ValueError('Unexpected probe runtime')
    for key,name in [('sdk','SharpLink.Sdk'),('abstractions','SharpLink.Abstractions')]:
        row=summary[key]
        if set(row) != {'sha256','fullIdentity','mvid','informationalVersion'} or row['sha256'] != hashes[name] or row['fullIdentity'] != name+', Version=3.0.0.0, Culture=neutral, PublicKeyToken=null' or row['informationalVersion'] != '3.0.0+'+EXPECTED['sourceCommit'] or str(uuid.UUID(row['mvid'])) != row['mvid'] or uuid.UUID(row['mvid']).int == 0:
            raise ValueError('Probe assembly identity mismatch')
    runtime=summary['runtime']
    if set(runtime) != {'metadataExact20','freshBoundedCollectibleContext','loadedOriginsVerified','contextAssemblyCount','types'} or any(runtime[k] is not True for k in ['metadataExact20','freshBoundedCollectibleContext','loadedOriginsVerified']) or type(runtime['contextAssemblyCount']) is not int or runtime['contextAssemblyCount'] != 2:
        raise ValueError('Probe load boundary assertion mismatch')
    names=set()
    for row in runtime['types']:
        if set(row) != {'name','assemblyGetTypeSameAsDirect','getForwardedTypesSameAsDirect','sdkQualifiedTypeGetTypeSameAsDirect'} or any(row[k] is not True for k in row if k != 'name') or row['name'] in names:
            raise ValueError('Probe runtime type assertion mismatch')
        names.add(row['name'])
    if names != set(EXPECTED['forwardedNames']) or len(runtime['types']) != 20:
        raise ValueError('Probe type name set mismatch')


def main():
    if len(sys.argv) != 2 or sys.platform != 'linux':
        raise ValueError('Usage: run.py <clean frozen source checkout> (Linux only)')
    source = Path(sys.argv[1]).resolve()
    tools_root = HERE.parents[1]
    tool_commit = git(tools_root, 'rev-parse', 'HEAD')
    if tool_commit != os.environ.get('GITHUB_SHA') or git(tools_root, 'status', '--porcelain'):
        raise ValueError('Tools must be the clean exact triggering checkout')
    check_source(source)
    if any((source / 'src').rglob('bin')) or any((source / 'src').rglob('obj')) or (source / 'artifacts/nuget').exists():
        raise ValueError('Producer outputs must start empty')
    smoke = load('frozen_smoke', source / 'eng/run-default-package-smoke.py')
    compare = load('frozen_compare', source / 'eng/compare-published-api.py')
    check_graph(source, EXPECTED['projects'])
    print('PACKAGE_FORWARDERS_PREFLIGHT ' + json.dumps(dict(sourceCommit=EXPECTED['sourceCommit'],
          sourceTree=EXPECTED['sourceTree'], toolsCommit=tool_commit, helpers=EXPECTED['helpers'],
          expectedSha256=digest(HERE / 'expected.json'), runnerSha256=digest(Path(__file__))), sort_keys=True), flush=True)
    smoke._process.enable_subreaper()
    work = Path(tempfile.mkdtemp(prefix='sharplink-forwarders-', dir=os.environ.get('RUNNER_TEMP')))
    logs = work / 'logs'; logs.mkdir()
    env = smoke.environment(work / 'state')
    deadline = time.monotonic() + 25 * 60
    def run(stage, command, cwd=source, selected_env=env, seconds=420):
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            raise TimeoutError('Finite package forwarding validation deadline reached')
        print('stage ' + stage, flush=True)
        try:
            return smoke._process.run_owned(stage, [str(x) for x in command], cwd, selected_env,
                                            logs, min(seconds, remaining))
        except BaseException as error:
            outcome = getattr(error, 'outcome', {})
            print('PACKAGE_FORWARDERS_COMMAND_FAIL ' + json.dumps(dict(
                stage=stage, category=type(error).__name__, exitCode=outcome.get('exitCode'),
                timedOut=outcome.get('timedOut'),
                executionErrorCount=len(outcome.get('errors', [])),
                cleanupErrorCount=len(outcome.get('cleanupErrors', [])),
                reportingErrorCount=len(outcome.get('reportingErrors', [])),
                cleanup=cleanup_classification(outcome)), sort_keys=True), flush=True)
            raise
    identity = smoke.sdk_identity(run, source, env, logs)
    if identity['selectedSdkVersion'] != '10.0.114':
        raise ValueError('Unexpected SDK selected for frozen package build')
    config = work / 'NuGet.Config'
    config.write_text('<configuration><packageSources><clear/><add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3"/></packageSources></configuration>\n')
    serial = ['-m:1', '-nr:false']
    for name in EXPECTED['projects']:
        run('restore-' + name, ['dotnet', 'restore', smoke.project(Path('src'), name), '--configfile', config,
            '--disable-parallel', '-v', 'minimal', '-p:Configuration=Release', '-p:RestoreIgnoreFailedSources=false', *serial])
    for name in EXPECTED['projects']:
        run('build-' + name, ['dotnet', 'build', smoke.project(Path('src'), name), '--no-restore',
            '-c', 'Release', '-v', 'minimal', '-p:UseSharedCompilation=false', *serial])
    feed = source / 'artifacts/nuget'; feed.mkdir(parents=True)
    for name in EXPECTED['packages']:
        run('pack-' + name, ['dotnet', 'pack', smoke.project(Path('src'), name), '--no-build', '--no-restore',
            '-c', 'Release', '-o', feed, '-v', 'minimal', *serial])
    filenames = {n + '.3.0.0.' + ext for n in EXPECTED['packages'] for ext in ('nupkg', 'snupkg')}
    if {p.name for p in feed.iterdir()} != filenames:
        raise ValueError('Expected exactly two newly built package/symbol pairs')
    for name in EXPECTED['packages']:
        run('verify-package-' + name, ['python3', source / 'eng/verify-package-identities.py',
            feed / (name + '.3.0.0.nupkg'), feed / (name + '.3.0.0.snupkg'),
            name, '3.0.0', EXPECTED['sourceCommit']], seconds=60)
    assemblies = work / 'assemblies'; assemblies.mkdir()
    records = [compare.extract_package(feed / (n + '.3.0.0.nupkg'), n, '3.0.0', assemblies,
               'frozen-local-build', EXPECTED['sourceCommit']) for n in EXPECTED['packages']]
    probe_root = work / 'probe'
    shutil.copytree(HERE / 'probe', probe_root, ignore=shutil.ignore_patterns('bin', 'obj', '__pycache__', '.cache', '.test-output', '*.log', '*results*.json'))
    (probe_root / 'global.json').write_text(json.dumps(dict(sdk=dict(version='10.0.114', rollForward='disable', allowPrerelease=False))) + '\n')
    def probe_run(stage, command, cwd, selected_env, seconds):
        return run('probe-' + stage, command, cwd, selected_env, seconds)
    probe_identity = smoke.sdk_identity(probe_run, probe_root, env, logs)
    if probe_identity['selectedSdkVersion'] != '10.0.114':
        raise ValueError('Unexpected probe SDK')
    project = probe_root / 'ForwarderProbe.csproj'
    run('probe-restore', ['dotnet', 'restore', project, '--configfile', config, '--disable-parallel', *serial], probe_root)
    run('probe-build', ['dotnet', 'build', project, '--no-restore', '-c', 'Release',
        '-p:UseSharedCompilation=false', *serial], probe_root)
    binary = probe_root / 'bin/Release/net10.0/ForwarderProbe.dll'
    output, errors = run('probe-self-tests', ['dotnet', binary, '--self-test'], probe_root, seconds=120)
    self_tests = read_json(output, errors)
    validate_self_tests(self_tests)
    paths = {name: assemblies / (name + '.dll') for name in EXPECTED['packages']}
    hashes = {name:digest(path) for name,path in paths.items()}
    output, errors = run('probe-shipping-forwarders', ['dotnet', binary,
        '--sdk', paths['SharpLink.Sdk'], '--sdk-sha256', hashes['SharpLink.Sdk'],
        '--abstractions', paths['SharpLink.Abstractions'], '--abstractions-sha256', hashes['SharpLink.Abstractions']], probe_root, seconds=60)
    probe = read_json(output, errors)
    validate_probe(probe, hashes)
    check_source(source)
    summary = dict(sourceCommit=EXPECTED['sourceCommit'], sourceTree=EXPECTED['sourceTree'], toolsCommit=tool_commit,
        producerSdk=identity, probeSdk=probe_identity, packages=records, probeSelfTests=self_tests,
        probe=probe, probeBinarySha256=digest(binary),
        qualification='Two actual-package explicit forwarding-hop identities only; not old consumer ABI, NuGet closure, RPC, Trim/AOT or security approval')
    encoded = json.dumps(summary, sort_keys=True)
    if len(encoded) > 24000:
        raise ValueError('Summary exceeds bound')
    (work / 'summary.json').write_text(encoded + '\n')
    print('PACKAGE_FORWARDERS_SUMMARY ' + encoded, flush=True)
    print('PACKAGE_FORWARDERS_PASS packages=2 forwarded_types=20 rpc_processes=0', flush=True)

if __name__ == '__main__':
    try:
        main()
    except BaseException as error:
        print('PACKAGE_FORWARDERS_FAIL category=' + type(error).__name__ + ' detail=retained-locally', flush=True)
        sys.exit(1)
