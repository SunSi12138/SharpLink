#!/usr/bin/env python3
"""Build-only single-source DiagnosticOnly preparation; never starts fixture/collector/tests."""
import argparse
import os
from pathlib import Path
import shutil
import subprocess
import zipfile
import xml.etree.ElementTree as ET
from common import ROOT, SOURCE, TREE, CONTRACT, strict_json, digest, inventory, source_inventory, write

PRODUCTS = ('Abstractions', 'Client', 'Runtime', 'Sdk', 'Server')
PRIVATE_DEPENDENCIES = ('Microsoft.Extensions.DependencyInjection.Abstractions',
    'Microsoft.Extensions.DependencyInjection', 'Microsoft.Extensions.Logging.Abstractions', 'System.Threading.RateLimiting')


def capture(command, cwd=None):
    return subprocess.check_output(list(map(str, command)), cwd=cwd, text=True).strip()


def run(command, log, cwd=None):
    with log.open('w') as stream:
        subprocess.run(list(map(str, command)), cwd=cwd, stdout=stream, stderr=subprocess.STDOUT, check=True, timeout=900)


def verify_source(source):
    if capture(['git', 'rev-parse', 'HEAD'], source) != SOURCE:
        raise ValueError('Baseline source commit differs')
    if capture(['git', 'rev-parse', 'HEAD^{tree}'], source) != TREE:
        raise ValueError('Baseline source tree differs')
    if capture(['git', 'status', '--porcelain'], source):
        raise ValueError('Baseline source must be pristine')


def verify_contract():
    expected = dict(source=SOURCE, tree=TREE, purpose='DiagnosticOnly', runtime='10.0.14', sdk='10.0.102',
                    api=5, mode='static-intercepted', itemCount=100, workers=1, poolSize=1, warmupSeconds=3, workSeconds=20)
    if any(CONTRACT.get(key) != value or type(CONTRACT.get(key)) is not type(value) for key, value in expected.items()) or CONTRACT.get('variants') != ['control', 'trace']:
        raise ValueError('Diagnostic contract differs from fixed single-baseline workload')


def package_identity(path, name, version, source):
    with zipfile.ZipFile(path) as z:
        specs = [n for n in z.namelist() if n.endswith('.nuspec')]
        if len(specs) != 1:
            raise ValueError('Expected exactly one package nuspec')
        nuspec = ET.fromstring(z.read(specs[0]))
        fields = {e.tag.rsplit('}', 1)[-1]: e for e in nuspec.iter()}
        if fields['id'].text != f'SharpLink.{name}' or fields['version'].text != version:
            raise ValueError(f'Wrong package identity: {path}')
        if fields['repository'].get('commit') != source:
            raise ValueError(f'Wrong package source commit: {path}')
        return z.read(f'lib/net10.0/SharpLink.{name}.dll'), (z.read('analyzers/dotnet/cs/SharpLink.Generator.dll') if name == 'Sdk' else None)


def materialize(packages, destination, version, source):
    destination.mkdir()
    records = {}
    for name in PRODUCTS:
        matches = list(packages.rglob(f'sharplink.{name.lower()}.{version}.nupkg')) + list(packages.glob(f'SharpLink.{name}.{version}.nupkg'))
        matches = list(dict.fromkeys(matches))
        if len(matches) != 1:
            raise ValueError(f'Expected one {name} {version} package, got {matches}')
        package = matches[0]
        dll, generator = package_identity(package, name, version, source)
        path = destination / f'SharpLink.{name}.dll'
        path.write_bytes(dll)
        entry = {'package': str(package), 'packageSha256': digest(package), 'dllSha256': digest(path)}
        if generator is not None:
            gp = destination / 'SharpLink.Generator.dll'
            gp.write_bytes(generator)
            entry['generatorSha256'] = digest(gp)
        records[name] = entry
    return records


def pin_runtime(destination):
    path = destination / 'PhysicalTcpProbe.runtimeconfig.json'
    data = strict_json(path.read_text())
    options = data['runtimeOptions']
    frameworks = options.get('frameworks', [options.get('framework')])
    if len(frameworks) != 2 or {f['name'] for f in frameworks} != {'Microsoft.NETCore.App', 'Microsoft.AspNetCore.App'}:
        raise ValueError('Expected both private runtime frameworks')
    for framework in frameworks:
        framework['version'] = '10.0.14'
    options['rollForward'] = 'Disable'
    write(path, data)


def fixture_inputs(root):
    return {str(path.relative_to(root)): digest(path) for path in sorted(root.rglob('*')) if path.is_file() and
            not {'bin', 'obj', '__pycache__'} & set(path.relative_to(root).parts)}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--baseline-source', type=Path, required=True)
    parser.add_argument('--dependency-cache', type=Path, required=True)
    parser.add_argument('--dotnet', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    verify_contract()
    tool_sha = capture(['git', 'rev-parse', 'HEAD'], ROOT)
    if capture(['git', 'status', '--porcelain'], ROOT):
        raise ValueError('Freeze clean tools before complete preparation')
    tool_inventory = source_inventory()
    fixture = ROOT / 'fixture'
    fixture_inventory = fixture_inputs(fixture)
    source = args.baseline_source.resolve(strict=True)
    verify_source(source)
    dotnet = args.dotnet.resolve(strict=True)
    if capture([dotnet, '--version'], ROOT) != '10.0.102':
        raise ValueError('SDK must actually be 10.0.102')
    cache = args.dependency_cache.resolve(strict=True)
    output = args.output.resolve(); output.mkdir(parents=True, exist_ok=False)
    cli = output / 'cli-home'; cli.mkdir()
    os.environ.update(DOTNET_CLI_HOME=str(cli), DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1',
        DOTNET_CLI_TELEMETRY_OPTOUT='1', DOTNET_PROCESSOR_COUNT='2',
        NUGET_HTTP_CACHE_PATH=str(output / 'http-cache'), NUGET_SCRATCH=str(output / 'scratch'))
    write(output / 'contract.json', CONTRACT)
    write(output / 'tool-source-hashes.json', tool_inventory)
    archive = output / 'baseline-source.tar'
    run(['git', 'archive', '--format=tar', '-o', archive, SOURCE], output / 'archive.log', source)
    exported = output / 'baseline-source'; exported.mkdir()
    run(['tar', '-xf', archive, '-C', exported], output / 'extract.log')
    packages = output / 'baseline-packages'; packages.mkdir()
    config = output / 'NuGet.offline.config'
    config.write_text('<configuration><packageSources><clear /></packageSources></configuration>\n')
    flags = ['-c', 'Release', '-m:1', '-nr:false', '-p:UseSharedCompilation=false',
        f'-p:RestorePackagesPath={cache}', f'-p:RestoreConfigFile={config}', '-p:NuGetAudit=false',
        f'-p:RepositoryCommit={SOURCE}', f'-p:SourceRevisionId={SOURCE}']
    for name in PRODUCTS:
        run([dotnet, 'pack', exported / f'src/SharpLink.{name}/SharpLink.{name}.csproj', *flags,
             '-o', packages, f'-bl:{output / ("pack-" + name + ".binlog")}'], output / f'pack-{name}.log', exported)
    libraries = output / 'baseline-libraries'
    arm = materialize(packages, libraries, '3.0.0', SOURCE)
    dependencies = {}
    for name in PRIVATE_DEPENDENCIES:
        dependency = cache / name.lower() / '10.0.12/lib/net10.0' / (name + '.dll')
        shutil.copy2(dependency, libraries)
        dependencies[name] = digest(dependency)
    arm['dependencies'] = dependencies
    build = output / 'baseline-build'
    shutil.copytree(fixture, build, ignore=shutil.ignore_patterns('bin', 'obj', '__pycache__'))
    (build / 'Directory.Packages.props').write_text('<Project />\n')
    commands = []
    for name in ('module', 'probe'):
        command = [dotnet, 'build', build / name / f'{name}.csproj', '-c', 'Release', '-m:1', '-nr:false',
            '-p:UseSharedCompilation=false', f'-p:LibraryRoot={libraries}',
            f'-p:GeneratorPath={libraries / "SharpLink.Generator.dll"}', '-p:Arm=BASELINE',
            f'-p:RestoreConfigFile={config}', f'-bl:{output / (name + ".binlog")}']
        commands.append(list(map(str, command))); write(output / 'fixture-commands.json', commands)
        run(command, output / f'{name}.log', build)
    destination = output / 'baseline'
    shutil.copytree(build / 'probe/bin/Release/net10.0', destination)
    shutil.copy2(build / 'module/bin/Release/net10.0/ScalarStreamModule.dll', destination)
    pin_runtime(destination)
    arm['snapshot'] = inventory(destination)
    arm['generated'] = inventory(build / 'module/obj/generated')
    if not arm['generated']:
        raise ValueError('Own baseline Generator output missing')
    for name in PRODUCTS:
        if digest(destination / f'SharpLink.{name}.dll') != arm[name]['dllSha256']:
            raise ValueError('Own baseline product DLL drift')
    if digest(libraries / 'SharpLink.Generator.dll') != arm['Sdk']['generatorSha256']:
        raise ValueError('Own baseline Generator drift')
    for name, sha in dependencies.items():
        if digest(destination / (name + '.dll')) != sha:
            raise ValueError('Private dependency build-input drift')
    verify_source(source)
    if (capture(['git', 'rev-parse', 'HEAD'], ROOT) != tool_sha or
        capture(['git', 'status', '--porcelain'], ROOT) or source_inventory() != tool_inventory or
        fixture_inputs(fixture) != fixture_inventory):
        raise ValueError('Frozen tool or fixture source changed during preparation')
    write(output / 'prepared.json', dict(schema=1, sdk='10.0.102', contract=CONTRACT, arms={'baseline': arm},
        sourceRevision=SOURCE, sourceTree=TREE, toolRevision=tool_sha, toolSourceHashes=tool_inventory,
        fixturePurpose='DiagnosticOnly', fixtureSourceHashes=fixture_inventory, status='build-only'))
    print('DIAGNOSTIC_BUILD_ONLY_PREPARED; no fixture, collector or tests executed')


if __name__ == '__main__':
    main()
