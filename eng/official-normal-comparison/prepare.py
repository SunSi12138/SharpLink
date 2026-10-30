#!/usr/bin/env python3
"""Build-only preparation. Never starts the fixture, RPC, cold gates, or timing."""
import argparse
import json
import os
from pathlib import Path
import shutil
import subprocess
import zipfile
import xml.etree.ElementTree as ET
from common import strict_json, ARMS, CONTRACT, PRODUCTS, ROOT, digest, inventory, schedule, source_inventory, write


def capture(command, cwd=None):
    return subprocess.check_output(list(map(str, command)), cwd=cwd, text=True).strip()


def run(command, log, cwd=None):
    with log.open('w') as stream:
        subprocess.run(list(map(str, command)), cwd=cwd, stdout=stream, stderr=subprocess.STDOUT, check=True, timeout=900)


def package_identity(path, name, version, source):
    with zipfile.ZipFile(path) as z:
        nuspec = ET.fromstring(z.read(next(n for n in z.namelist() if n.endswith('.nuspec'))))
        fields = {e.tag.rsplit('}', 1)[-1]: e for e in nuspec.iter()}
        if fields['id'].text != f'SharpLink.{name}' or fields['version'].text != version:
            raise ValueError(f'Wrong package identity: {path}')
        if fields['repository'].get('commit') != source:
            raise ValueError(f'Wrong package source commit: {path}')
        return z.read(f'lib/net10.0/SharpLink.{name}.dll'), (z.read('analyzers/dotnet/cs/SharpLink.Generator.dll') if name == 'Sdk' else None)


def materialize(packages, destination, version, source, provenance=None):
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
        if generator:
            gp = destination / 'SharpLink.Generator.dll'
            gp.write_bytes(generator)
            entry['generatorSha256'] = digest(gp)
        if provenance:
            prior = provenance['packages'][f'SharpLink.{name}']
            for new, old in [('packageSha256', 'package_sha256'), ('dllSha256', 'dll_sha256')] + ([('generatorSha256', 'generator_sha256')] if generator else []):
                if entry[new] != prior[old]:
                    raise ValueError(f'Published cached provenance mismatch {name}/{old}')
        records[name] = entry
    return records


def pin_runtime(destination):
    p = destination / 'PhysicalTcpProbe.runtimeconfig.json'
    data = strict_json(p.read_text())
    options = data['runtimeOptions']
    frameworks = options.get('frameworks', [options.get('framework')])
    if len(frameworks) != 2 or {f['name'] for f in frameworks} != {'Microsoft.NETCore.App', 'Microsoft.AspNetCore.App'}:
        raise ValueError('Expected both private runtime frameworks')
    for f in frameworks:
        f['version'] = CONTRACT['runtime']
    options['rollForward'] = 'Disable'
    write(p, data)


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--official-cache', type=Path, required=True)
    p.add_argument('--official-provenance', type=Path, required=True)
    p.add_argument('--dev-source', type=Path, required=True)
    p.add_argument('--dependency-cache', type=Path, required=True)
    p.add_argument('--dotnet', type=Path, required=True)
    p.add_argument('--output', type=Path, required=True)
    a = p.parse_args()
    tool_sha = capture(['git', 'rev-parse', 'HEAD'], ROOT)
    if capture(['git', 'status', '--porcelain'], ROOT):
        raise ValueError('Tools must be frozen clean commit before preparation')
    tool_inventory = source_inventory(ROOT)
    fixture_source = ROOT / 'fixture'
    fixture_inventory = source_inventory(fixture_source)
    source = a.dev_source.resolve(strict=True)
    dotnet = a.dotnet.resolve(strict=True)
    if capture([dotnet, '--version'], ROOT) != CONTRACT['sdk']:
        raise ValueError('SDK must actually be 10.0.102')
    if capture(['git', 'rev-parse', 'HEAD'], source) != CONTRACT['devSource'] or capture(['git', 'status', '--porcelain'], source):
        raise ValueError('Dev product must be pristine exact candidate8ae9; tools use a separate checkout')
    if capture(['git', 'rev-parse', 'HEAD^{tree}'], source) != CONTRACT['devTree']:
        raise ValueError('Candidate tree identity differs')
    output = a.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    cli_home = output / 'cli-home'
    cli_home.mkdir()
    os.environ.update(DOTNET_CLI_HOME=str(cli_home), DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1', DOTNET_CLI_TELEMETRY_OPTOUT='1', DOTNET_PROCESSOR_COUNT='2',
                      NUGET_HTTP_CACHE_PATH=str(output / 'http-cache'), NUGET_SCRATCH=str(output / 'scratch'))
    write(output / 'contract.json', CONTRACT)
    write(output / 'schedule.json', schedule())
    write(output / 'tool-source-hashes.json', tool_inventory)
    provenance = strict_json(a.official_provenance.read_text())
    if (provenance['version'], provenance['source_commit']) != ('2.0.3', CONTRACT['officialSource']):
        raise ValueError('Wrong retained published provenance')
    shutil.copy2(a.official_provenance, output / 'published.provenance.json')
    # Export the exact product tree; never build modified tool checkout as product.
    archive = output / 'dev-source.tar'
    run(['git', 'archive', '--format=tar', '-o', archive, CONTRACT['devSource']], output / 'archive.log', source)
    dev = output / 'dev-source'
    dev.mkdir()
    run(['tar', '-xf', archive, '-C', dev], output / 'extract.log')
    packages = output / 'dev-packages'
    packages.mkdir()
    config = output / 'NuGet.offline.config'
    config.write_text('<configuration><packageSources><clear /></packageSources></configuration>\n')
    flags = ['-c', 'Release', '-m:1', '-nr:false', '-p:UseSharedCompilation=false',
             f'-p:RestorePackagesPath={a.dependency_cache.resolve(strict=True)}', f'-p:RestoreConfigFile={config}',
             '-p:NuGetAudit=false', f'-p:RepositoryCommit={CONTRACT["devSource"]}',
             f'-p:SourceRevisionId={CONTRACT["devSource"]}']
    for name in PRODUCTS:
        run([dotnet, 'pack', dev / f'src/SharpLink.{name}/SharpLink.{name}.csproj', *flags, '-o', packages,
             f'-bl:{output / ("pack-" + name + ".binlog")}'], output / f'pack-{name}.log', dev)
    manifests = {}
    for arm in ARMS:
        directory = output / f'{arm}-libraries'
        manifests[arm] = materialize(a.official_cache.resolve(strict=True) if arm == 'official' else packages,
                                    directory, CONTRACT[f'{arm}Version'], CONTRACT[f'{arm}Source'],
                                    provenance if arm == 'official' else None)
        # Same dependency servicing closure in both arms, taken from existing cache.
        deps = {}
        for name in ('Microsoft.Extensions.DependencyInjection.Abstractions', 'Microsoft.Extensions.DependencyInjection',
                     'Microsoft.Extensions.Logging.Abstractions', 'System.Threading.RateLimiting'):
            dependency = a.dependency_cache / name.lower() / '10.0.12/lib/net10.0' / (name + '.dll')
            shutil.copy2(dependency, directory)
            deps[name] = digest(dependency)
        manifests[arm]['dependencies'] = deps
        build = output / f'{arm}-build'
        shutil.copytree(fixture_source, build, ignore=shutil.ignore_patterns('bin', 'obj'))
        for filename in ('Directory.Packages.props',):
            (build / filename).write_text('<Project />\n')
        for name in ('module', 'probe'):
            command = [dotnet, 'build', build / name / f'{name}.csproj', '-c', 'Release', '-m:1', '-nr:false',
                       '-p:UseSharedCompilation=false', f'-p:LibraryRoot={directory}',
                       f'-p:GeneratorPath={directory / "SharpLink.Generator.dll"}', f'-p:Arm={arm.upper()}',
                       f'-p:RestoreConfigFile={config}', f'-bl:{output / (arm + "-" + name + ".binlog")}']
            run(command, output / f'{arm}-{name}.log', build)
        destination = output / arm
        shutil.copytree(build / 'probe/bin/Release/net10.0', destination)
        shutil.copy2(build / 'module/bin/Release/net10.0/ScalarStreamModule.dll', destination)
        pin_runtime(destination)
        manifests[arm]['snapshot'] = inventory(destination)
        manifests[arm]['generated'] = inventory(build / 'module/obj/generated')
        if not manifests[arm]['generated']:
            raise ValueError('Own arm generator output missing')
        # Require exact product assemblies from own packages; never swap across arms.
        for name in PRODUCTS:
            if digest(destination / f'SharpLink.{name}.dll') != manifests[arm][name]['dllSha256']:
                raise ValueError(f'Wrong output DLL: {arm}/{name}')
        for name, expected_hash in deps.items():
            if digest(destination / (name + '.dll')) != expected_hash:
                raise ValueError(f'Private dependency snapshot drift {arm}/{name}')
    if capture(['git', 'rev-parse', 'HEAD'], ROOT) != tool_sha or capture(['git', 'status', '--porcelain'], ROOT) or source_inventory(ROOT) != tool_inventory:
        raise ValueError('Tool source changed during preparation')
    if source_inventory(fixture_source) != fixture_inventory:
        raise ValueError('Selected fixture source changed during preparation')
    if manifests['official']['dependencies'] != manifests['dev']['dependencies']:
        raise ValueError('Non-product dependency drift')
    write(output / 'prepared.json', dict(schema=1, sdk=CONTRACT['sdk'], contract=CONTRACT, arms=manifests,
                                        toolRevision=tool_sha, toolSourceHashes=tool_inventory, fixturePurpose='normal-comparison', fixtureSourceHashes=fixture_inventory, status='build-only'))
    print(f'BUILD_ONLY_PREPARED {output}; cold and timing NOT RUN')


if __name__ == '__main__':
    main()
