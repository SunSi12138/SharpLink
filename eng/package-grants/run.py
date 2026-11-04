#!/usr/bin/env python3
"""Frozen-source producer-only package metadata validation. No RPC or artifact upload."""
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import time
import xml.etree.ElementTree as ET
from zipfile import ZipFile
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
                                   text=True, timeout=30).strip()


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


def check_graph(root, names):
    allowed = {(root / 'src' / n / (n + '.csproj')).resolve() for n in names}
    for path in allowed:
        for ref in ET.parse(path).findall('.//ProjectReference'):
            if (path.parent / ref.attrib['Include'].replace('\\', '/')).resolve() not in allowed:
                raise ValueError('Unexpected non-production project reference in ' + path.name)


def extract_analyzer(package, target):
    expected = 'analyzers/dotnet/cs/SharpLink.Generator.dll'
    with ZipFile(package) as archive:
        paths = [p for p in archive.namelist()
                 if p.lower().startswith('analyzers/') and p.lower().endswith('.dll')]
        if paths != [expected]:
            raise ValueError('Expected exactly one unambiguous SDK analyzer DLL')
        target.write_bytes(archive.read(expected))
    return dict(package='SharpLink.Sdk', assembly_path=expected, assembly_sha256=digest(target))


def validate_inventory(inventory, paths):
    # Filled against reader's stable schema; every file must bind to one frozen identity.
    if inventory.get('SchemaVersion') != 1:
        raise ValueError('Unsupported reader schema')
    rows = inventory['Assemblies']
    if len(rows) != 10 or {row['Identity']['Name'] for row in rows} != set(EXPECTED['assemblies']):
        raise ValueError('Expected exactly ten assembly identities')
    for row in rows:
        name = row['Identity']['Name']
        if row['FileName'] != name + '.dll' or row['Sha256'] != digest(paths[name]):
            raise ValueError('Reader SHA mismatch: ' + name)
        if row['Identity']['Version'] != '3.0.0.0' or row['InformationalVersion'] != '3.0.0+' + EXPECTED['sourceCommit']:
            raise ValueError('Assembly version/source mismatch: ' + name)
        if row['IsReferenceAssembly'] is not False:
            raise ValueError('Implementation/analyzer marked reference assembly: ' + name)
        for field, value in EXPECTED['assemblies'][name].items():
            actual = row[field[0].upper() + field[1:]]
            if not isinstance(actual, list) or any(not isinstance(v, str) for v in actual) or sorted(set(actual)) != sorted(set(value)):
                raise ValueError('Assembly grant string set differs from frozen source: ' + name + '/' + field)
        for attribute in row.get('RecognizedAttributes', []):
            if attribute['TypeName'] == 'System.Runtime.CompilerServices.InternalsVisibleToAttribute' and attribute['AllInternalsVisible'] is not None:
                raise ValueError('Unexpected explicit grant modifier: ' + name)
    return rows


def main():
    if len(sys.argv) != 2 or sys.platform != 'linux':
        raise ValueError('Usage: run.py <clean frozen source checkout> (Linux only)')
    source = Path(sys.argv[1]).resolve()
    tools_root = HERE.parents[1]
    tool_commit = git(tools_root, 'rev-parse', 'HEAD')
    if tool_commit != os.environ.get('GITHUB_SHA') or git(tools_root, 'status', '--porcelain'):
        raise ValueError('Tools must be the clean exact triggering checkout')
    check_source(source)
    smoke = load('frozen_smoke', source / 'eng/run-default-package-smoke.py')
    compare = load('frozen_compare', source / 'eng/compare-published-api.py')
    check_graph(source, smoke.PRODUCTION)
    if set(smoke.PRODUCTION) != set(EXPECTED['assemblies']):
        raise ValueError('Unexpected production project set')
    print('PACKAGE_GRANTS_PREFLIGHT ' + json.dumps(dict(sourceCommit=EXPECTED['sourceCommit'],
          sourceTree=EXPECTED['sourceTree'], toolsCommit=tool_commit, helpers=EXPECTED['helpers'],
          expectedSha256=digest(HERE / 'expected.json'), runnerSha256=digest(Path(__file__))), sort_keys=True), flush=True)
    smoke._process.enable_subreaper()
    work = Path(tempfile.mkdtemp(prefix='sharplink-grants-', dir=os.environ.get('RUNNER_TEMP')))
    logs = work / 'logs'; logs.mkdir()
    env = smoke.environment(work / 'state')
    deadline = time.monotonic() + 25 * 60
    def run(stage, command, cwd=source, selected_env=env, seconds=420):
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            raise TimeoutError('Finite package grant validation deadline reached')
        print('stage ' + stage, flush=True)
        try:
            return smoke._process.run_owned(stage, [str(x) for x in command], cwd, selected_env,
                                            logs, min(seconds, remaining))
        except BaseException as error:
            smoke.raise_with_diagnostics(error, logs, stage)
    identity = smoke.sdk_identity(run, source, env, logs)
    if identity['selectedSdkVersion'] != '10.0.114':
        raise ValueError('Unexpected SDK selected for frozen package build')
    config = work / 'NuGet.Config'
    config.write_text('<configuration><packageSources><clear/><add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3"/></packageSources></configuration>\n')
    serial = ['-m:1', '-nr:false']
    for name in smoke.PRODUCTION:
        run('restore-' + name, ['dotnet', 'restore', smoke.project(Path('src'), name), '--configfile', config,
            '--disable-parallel', '-v', 'minimal', '-p:Configuration=Release', '-p:RestoreIgnoreFailedSources=false', *serial])
    for name in smoke.PRODUCTION:
        run('build-' + name, ['dotnet', 'build', smoke.project(Path('src'), name), '--no-restore',
            '-c', 'Release', '-v', 'minimal', '-p:UseSharedCompilation=false', *serial])
    feed = source / 'artifacts/nuget'; feed.mkdir(parents=True)
    for name in smoke.PRODUCTS:
        run('pack-' + name, ['dotnet', 'pack', smoke.project(Path('src'), name), '--no-build', '--no-restore',
            '-c', 'Release', '-o', feed, '-v', 'minimal', *serial])
    filenames = {n + '.3.0.0.' + ext for n in smoke.PRODUCTS for ext in ('nupkg', 'snupkg')}
    if {p.name for p in feed.iterdir()} != filenames:
        raise ValueError('Expected exactly nine newly built package/symbol pairs')
    run('verify-packages', ['bash', 'eng/verify-packages.sh', feed, '3.0.0'], seconds=60)
    assemblies = work / 'assemblies'; assemblies.mkdir()
    records = [compare.extract_package(feed / (n + '.3.0.0.nupkg'), n, '3.0.0', assemblies,
               'frozen-local-build', EXPECTED['sourceCommit']) for n in smoke.PRODUCTS]
    analyzer = extract_analyzer(feed / 'SharpLink.Sdk.3.0.0.nupkg', assemblies / 'SharpLink.Generator.dll')
    reader_root = work / 'reader'
    shutil.copytree(HERE / 'reader', reader_root, ignore=shutil.ignore_patterns('bin', 'obj', '__pycache__', '.cache', '.test-output', 'test-results.json'))
    project = reader_root / 'SharpLink.AssemblyGrants/SharpLink.AssemblyGrants.csproj'
    run('reader-self-tests', ['bash', reader_root / 'test.sh'], reader_root, seconds=180)
    self_tests = json.loads((reader_root / 'test-results.json').read_text())
    if self_tests.get('Passed') != 42 or len(self_tests.get('Checks', [])) != 42:
        raise ValueError('Reader self-test evidence incomplete')
    print('reader self-tests=42 passed', flush=True)
    run('reader-restore', ['dotnet', 'restore', project, '--configfile', config, '--disable-parallel', *serial], reader_root)
    run('reader-build', ['dotnet', 'build', project, '--no-restore', '-c', 'Release',
        '-p:UseSharedCompilation=false', *serial], reader_root)
    paths = {n: assemblies / (n + '.dll') for n in EXPECTED['assemblies']}
    output, errors = run('reader-inventory', ['dotnet', project.parent / 'bin/Release/net10.0/SharpLink.AssemblyGrants.dll',
                                             *[paths[n] for n in sorted(paths)]], reader_root, seconds=60)
    if errors.read_bytes():
        raise ValueError('Reader emitted unexpected stderr')
    inventory = json.loads(output.read_text())
    rows = validate_inventory(inventory, paths)
    check_source(source)
    summary = dict(sourceCommit=EXPECTED['sourceCommit'], sourceTree=EXPECTED['sourceTree'],
                   toolsCommit=tool_commit, readerSelfTests=42, sdk=identity, packages=records, analyzer=analyzer, assemblies=rows,
                   packageHashes={p.name:digest(p) for p in sorted(feed.iterdir())},
                   qualification='Metadata inventory only; no RPC/consumer, no API/ABI/security approval, no Trim/AOT/performance claim')
    (work / 'summary.json').write_text(json.dumps(summary, indent=2) + '\n')
    # Fixed ten assemblies; no raw trace/log/report upload. Compact retained identity evidence.
    encoded = json.dumps(summary, sort_keys=True)
    if len(encoded) > 120000:
        raise ValueError('Identity summary exceeds reviewed bound; retain local only')
    print('PACKAGE_GRANTS_SUMMARY ' + encoded, flush=True)
    print('PACKAGE_GRANTS_PASS packages=9 assemblies=10 rpc_processes=0', flush=True)


if __name__ == '__main__':
    try:
        main()
    except BaseException as error:
        print('PACKAGE_GRANTS_FAIL ' + type(error).__name__ + ': ' + str(error)[:1800], flush=True)
        sys.exit(1)
