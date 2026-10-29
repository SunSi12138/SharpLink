#!/usr/bin/env python3
"""Build two independent exact-source package arms. Never starts a probe or test."""
import argparse
import os
from pathlib import Path
import shutil
import subprocess
import zipfile
import xml.etree.ElementTree as ET
from common import strict_json, ARMS, CONTRACT, PRODUCTS, ROOT, digest, inventory, schedule, source_inventory, write

PRIVATE_DEPENDENCIES = ('Microsoft.Extensions.DependencyInjection.Abstractions',
                        'Microsoft.Extensions.DependencyInjection',
                        'Microsoft.Extensions.Logging.Abstractions', 'System.Threading.RateLimiting')


def capture(command, cwd=None):
    return subprocess.check_output(list(map(str, command)), cwd=cwd, text=True).strip()


def run(command, log, cwd=None):
    with log.open('w') as stream:
        subprocess.run(list(map(str, command)), cwd=cwd, stdout=stream, stderr=subprocess.STDOUT,
                       check=True, timeout=900)


def verify_source(source, arm):
    if arm not in ARMS:
        raise ValueError('Unknown product arm')
    if capture(['git', 'rev-parse', 'HEAD'], source) != CONTRACT[f'{arm}Source']:
        raise ValueError(f'{arm} source commit differs')
    if capture(['git', 'rev-parse', 'HEAD^{tree}'], source) != CONTRACT[f'{arm}Tree']:
        raise ValueError(f'{arm} source tree differs')
    if capture(['git', 'status', '--porcelain'], source):
        raise ValueError(f'{arm} source must be pristine')


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
        framework['version'] = CONTRACT['runtime']
    options['rollForward'] = 'Disable'
    write(path, data)


def pack_arm(arm, source, dotnet, dependency_cache, output, config):
    """Each arm owns a separate archive, source export and all five package outputs."""
    verify_source(source, arm)
    archive = output / f'{arm}-source.tar'
    run(['git', 'archive', '--format=tar', '-o', archive, CONTRACT[f'{arm}Source']],
        output / f'{arm}-archive.log', source)
    exported = output / f'{arm}-source'
    exported.mkdir()
    run(['tar', '-xf', archive, '-C', exported], output / f'{arm}-extract.log')
    packages = output / f'{arm}-packages'
    packages.mkdir()
    flags = ['-c', 'Release', '-m:1', '-nr:false', '-p:UseSharedCompilation=false',
             f'-p:RestorePackagesPath={dependency_cache}', f'-p:RestoreConfigFile={config}',
             '-p:NuGetAudit=false', f'-p:RepositoryCommit={CONTRACT[f"{arm}Source"]}',
             f'-p:SourceRevisionId={CONTRACT[f"{arm}Source"]}']
    commands = []
    for name in PRODUCTS:
        command = [dotnet, 'pack', exported / f'src/SharpLink.{name}/SharpLink.{name}.csproj',
                   *flags, '-o', packages, f'-bl:{output / (arm + "-pack-" + name + ".binlog")}']
        commands.append(list(map(str, command)))
        write(output / f'{arm}-pack-commands.json', commands)
        run(command, output / f'{arm}-pack-{name}.log', exported)
    verify_source(source, arm)
    return packages


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--baseline-source', type=Path, required=True)
    parser.add_argument('--candidate-source', type=Path, required=True)
    parser.add_argument('--dependency-cache', type=Path, required=True)
    parser.add_argument('--dotnet', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    tool_sha = capture(['git', 'rev-parse', 'HEAD'], ROOT)
    if capture(['git', 'status', '--porcelain'], ROOT):
        raise ValueError('Tools must be frozen clean commit before preparation')
    tool_inventory = source_inventory(ROOT)
    fixture_source = ROOT / 'fixture'
    fixture_inventory = source_inventory(fixture_source)
    sources = {arm: getattr(args, f'{arm}_source').resolve(strict=True) for arm in ARMS}
    if len(set(sources.values())) != len(ARMS):
        raise ValueError('Each arm requires its own exact-source checkout')
    for arm, source in sources.items():
        verify_source(source, arm)
    dotnet = args.dotnet.resolve(strict=True)
    if capture([dotnet, '--version'], ROOT) != CONTRACT['sdk']:
        raise ValueError('SDK must actually be 10.0.102')
    dependency_cache = args.dependency_cache.resolve(strict=True)
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    cli_home = output / 'cli-home'; cli_home.mkdir()
    os.environ.update(DOTNET_CLI_HOME=str(cli_home), DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1',
                      DOTNET_CLI_TELEMETRY_OPTOUT='1', DOTNET_PROCESSOR_COUNT='2',
                      NUGET_HTTP_CACHE_PATH=str(output / 'http-cache'), NUGET_SCRATCH=str(output / 'scratch'))
    write(output / 'contract.json', CONTRACT)
    write(output / 'schedule.json', schedule())
    write(output / 'tool-source-hashes.json', tool_inventory)
    config = output / 'NuGet.offline.config'
    config.write_text('<configuration><packageSources><clear /></packageSources></configuration>\n')
    manifests = {}
    for arm in ARMS:
        packages = pack_arm(arm, sources[arm], dotnet, dependency_cache, output, config)
        directory = output / f'{arm}-libraries'
        manifests[arm] = materialize(packages, directory, CONTRACT[f'{arm}Version'], CONTRACT[f'{arm}Source'])
        dependencies = {}
        for name in PRIVATE_DEPENDENCIES:
            dependency = dependency_cache / name.lower() / '10.0.12/lib/net10.0' / (name + '.dll')
            shutil.copy2(dependency, directory)
            dependencies[name] = digest(dependency)
        manifests[arm]['dependencies'] = dependencies
        build = output / f'{arm}-build'
        shutil.copytree(fixture_source, build, ignore=shutil.ignore_patterns('bin', 'obj'))
        (build / 'Directory.Packages.props').write_text('<Project />\n')
        commands = []
        for name in ('module', 'probe'):
            command = [dotnet, 'build', build / name / f'{name}.csproj', '-c', 'Release', '-m:1', '-nr:false',
                       '-p:UseSharedCompilation=false', f'-p:LibraryRoot={directory}',
                       f'-p:GeneratorPath={directory / "SharpLink.Generator.dll"}', f'-p:Arm={arm.upper()}',
                       f'-p:RestoreConfigFile={config}', f'-bl:{output / (arm + "-" + name + ".binlog")}']
            commands.append(list(map(str, command)))
            write(output / f'{arm}-fixture-build-commands.json', commands)
            run(command, output / f'{arm}-{name}.log', build)
        destination = output / arm
        shutil.copytree(build / 'probe/bin/Release/net10.0', destination)
        shutil.copy2(build / 'module/bin/Release/net10.0/ScalarStreamModule.dll', destination)
        pin_runtime(destination)
        manifests[arm]['snapshot'] = inventory(destination)
        manifests[arm]['generated'] = inventory(build / 'module/obj/generated')
        if not manifests[arm]['generated']:
            raise ValueError(f'Own arm generator output missing: {arm}')
        for name in PRODUCTS:
            if digest(destination / f'SharpLink.{name}.dll') != manifests[arm][name]['dllSha256']:
                raise ValueError(f'Wrong output DLL: {arm}/{name}')
        if digest(directory / 'SharpLink.Generator.dll') != manifests[arm]['Sdk']['generatorSha256']:
            raise ValueError(f'Generator changed during build: {arm}')
        for name, expected_hash in dependencies.items():
            if digest(destination / (name + '.dll')) != expected_hash:
                raise ValueError(f'Private dependency snapshot drift {arm}/{name}')
    if capture(['git', 'rev-parse', 'HEAD'], ROOT) != tool_sha or capture(['git', 'status', '--porcelain'], ROOT) or source_inventory(ROOT) != tool_inventory:
        raise ValueError('Tool source changed during preparation')
    if source_inventory(fixture_source) != fixture_inventory:
        raise ValueError('Fixture source changed during preparation')
    for arm, source in sources.items():
        verify_source(source, arm)
    if manifests['baseline']['dependencies'] != manifests['candidate']['dependencies']:
        raise ValueError('Non-product dependency drift')
    write(output / 'prepared.json', dict(schema=1, sdk=CONTRACT['sdk'], contract=CONTRACT, arms=manifests,
          toolRevision=tool_sha, toolSourceHashes=tool_inventory, fixturePurpose='encoded-page-comparison',
          fixtureSourceHashes=fixture_inventory, status='build-only'))
    print('BUILD_ONLY_PREPARED arms=baseline,candidate; cold and timing NOT RUN')


if __name__ == '__main__':
    main()
