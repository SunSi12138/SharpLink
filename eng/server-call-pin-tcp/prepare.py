#!/usr/bin/env python3
"""Compile each correctness graph once, freeze its bytes, then build only TCP fixtures.

No products are packed or rebuilt by fixture preparation. Never starts tests/TCP.
Core-only correctness and dual-framework TCP execution are distinct load domains.
"""
import argparse
import os
from pathlib import Path
import re
import shutil
import subprocess
import xml.etree.ElementTree as ET
from common import strict_json, ARMS, CONTRACT, PRODUCTS, ROOT, digest, inventory, schedule, source_inventory, write

PRIVATE_DEPENDENCIES = ('Microsoft.Extensions.DependencyInjection.Abstractions',
                        'Microsoft.Extensions.DependencyInjection',
                        'Microsoft.Extensions.Logging.Abstractions', 'System.Threading.RateLimiting')
CLOSURE = tuple('SharpLink.' + name + '.dll' for name in PRODUCTS) + tuple(name + '.dll' for name in PRIVATE_DEPENDENCIES)


def capture(command, cwd=None):
    return subprocess.check_output(list(map(str, command)), cwd=cwd, text=True, timeout=120).strip()


def run(command, log, cwd=None):
    with log.open('w') as stream:
        subprocess.run(list(map(str, command)), cwd=cwd, stdout=stream, stderr=subprocess.STDOUT,
                       check=True, timeout=900)


def build_environment(output):
    # Set every writable location before even invoking the SDK host.
    names = {'DOTNET_CLI_HOME': 'cli-home', 'NUGET_HTTP_CACHE_PATH': 'http-cache',
             'NUGET_SCRATCH': 'scratch', 'XDG_CACHE_HOME': 'xdg-cache', 'XDG_DATA_HOME': 'xdg-data'}
    for key, name in names.items():
        path = output / name
        path.mkdir(parents=True, exist_ok=True)
        os.environ[key] = str(path)
    os.environ.update(DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1', DOTNET_CLI_TELEMETRY_OPTOUT='1',
                      DOTNET_PROCESSOR_COUNT='2')


def verify_source(source, arm):
    if arm not in ARMS:
        raise ValueError('Unknown product arm')
    if capture(['git', 'rev-parse', 'HEAD'], source) != CONTRACT[f'{arm}Source']:
        raise ValueError(f'{arm} source commit differs')
    if capture(['git', 'rev-parse', 'HEAD^{tree}'], source) != CONTRACT[f'{arm}Tree']:
        raise ValueError(f'{arm} source tree differs')
    if capture(['git', 'status', '--porcelain'], source):
        raise ValueError(f'{arm} source must be pristine')


def identity(arm):
    if arm not in ARMS:
        raise ValueError('Unknown fixture arm')
    value = {'Arm': arm, 'SourceTree': CONTRACT[f'{arm}Tree'],
             'ExpectedProductCommit': CONTRACT[f'{arm}Source']}
    if any(not re.fullmatch('[0-9a-f]{40}', value[key]) for key in ('SourceTree', 'ExpectedProductCommit')):
        raise ValueError('Invalid frozen source identity')
    return value


def identity_properties(arm):
    value = identity(arm)
    return ['-p:Arm=' + arm.upper(), '-p:SourceTree=' + value['SourceTree'],
            '-p:ProductCommit=' + value['ExpectedProductCommit']]


def verify_identity_source(fixture):
    # There is one source of identity: protocol -> explicit build properties.
    texts = [p.read_text() for p in fixture.rglob('*') if p.is_file() and p.suffix in ('.props', '.targets', '.csproj')]
    roots = [ET.fromstring(text) for text in texts]
    if any(list(root.iter('SourceTree')) or list(root.iter('ProductCommit')) for root in roots):
        raise ValueError('Fixture must not duplicate source tree/commit constants')
    metadata = [node for root in roots for node in root.iter('AssemblyMetadata')]
    expected = {'Arm': "$([System.String]::Copy('$(Arm)').ToLowerInvariant())", 'SourceTree': '$(SourceTree)', 'ExpectedProductCommit': '$(ProductCommit)'}
    if len(metadata) != 3 or {node.get('Include'): node.get('Value') for node in metadata} != expected:
        raise ValueError('Fixture metadata must bind explicit identity properties')
    errors = [node.get('Condition', '') for root in roots for node in root.iter('Error')]
    if not all(any("Regex]::IsMatch('$(" + name + ")', '\\A[0-9a-f]{40}\\z')" in condition for condition in errors)
               for name in ('SourceTree', 'ProductCommit')):
        raise ValueError('Fixture missing identity property failure guards')


def pin_runtime_config(path, frameworks):
    data = strict_json(path.read_text())
    options = data['runtimeOptions']
    actual = options.get('frameworks', [options.get('framework')])
    if len(actual) != len(frameworks) or {f['name'] for f in actual} != set(frameworks):
        raise ValueError('Unexpected runtime framework domain')
    for framework in actual:
        framework['version'] = CONTRACT['runtime']
    options['rollForward'] = 'Disable'
    write(path, data)


def pin_runtime(destination):
    pin_runtime_config(destination / 'PhysicalTcpProbe.runtimeconfig.json',
                       ('Microsoft.NETCore.App', 'Microsoft.AspNetCore.App'))


def build_correctness(arm, source, dotnet, dependency_cache, output):
    """One solution graph: MSBuild schedules each shared product/generator node once."""
    solution = output / (arm + '-correctness.slnx')
    tree = ET.Element('Solution')
    # Include the transitive projects too: otherwise solution configuration mapping
    # silently defaults unlisted dependencies to Debug despite -c Release.
    pending = [source / 'test' / name / (name + '.csproj')
               for name in ('SharpLink.UnitTests', 'SharpLink.IntegrationTests')]
    projects = set()
    while pending:
        project = pending.pop().resolve(strict=True)
        if project in projects:
            continue
        if not project.is_relative_to(source.resolve()):
            raise ValueError('Correctness project escaped frozen source')
        projects.add(project)
        for reference in ET.parse(project).getroot().iter('ProjectReference'):
            value = reference.get('Include', '').replace('\\', '/')
            if not value or '$(' in value or '*' in value:
                raise ValueError('Unrecognized correctness project reference')
            pending.append(project.parent / value)
    for project in sorted(projects):
        ET.SubElement(tree, 'Project', Path=str(project))
    ET.ElementTree(tree).write(solution, encoding='unicode')
    command = [dotnet, 'build', solution, '-c', 'Release', '-m:1', '-nr:false',
               '--disable-build-servers', '-p:UseSharedCompilation=false',
               f'-p:RestorePackagesPath={dependency_cache}',
               f'-p:RepositoryCommit={CONTRACT[arm + "Source"]}',
               f'-p:SourceRevisionId={CONTRACT[arm + "Source"]}', '-v', 'minimal']
    for project in projects:
        if (project.parent / 'bin').exists() or (project.parent / 'obj').exists():
            raise ValueError('Correctness build requires a fresh graph, never reuse or rebuild products')
    audit = strict_json(capture([dotnet, 'msbuild', source / 'test/SharpLink.UnitTests/SharpLink.UnitTests.csproj',
        '-p:Configuration=Release', '-getProperty:NuGetAudit,NuGetAuditMode,TreatWarningsAsErrors', '-nologo'], source))
    if audit != {'Properties': {'NuGetAudit': 'true', 'NuGetAuditMode': 'all', 'TreatWarningsAsErrors': 'true'}}:
        raise ValueError('Default product audit/warning policy weakened')
    write(output / (arm + '-default-policy.json'), audit)
    write(output / (arm + '-correctness-build-command.json'), list(map(str, command)))
    run(command, output / (arm + '-correctness-build.log'), source)
    unit = source / 'test/SharpLink.UnitTests/bin/Release/net10.0'
    integration = source / 'test/SharpLink.IntegrationTests/bin/Release/net10.0'
    for folder, name in ((unit, 'SharpLink.UnitTests'), (integration, 'SharpLink.IntegrationTests'),
                         (unit / 'allocation-probe', 'SharpLink.ReadOwnershipAllocationProbe')):
        pin_runtime_config(folder / (name + '.runtimeconfig.json'), ('Microsoft.NETCore.App',))
    return unit, integration


def verify_tested_dependency_graph(source, unit, integration):
    records = {}
    for label, folder, assembly in (('unit', unit, 'SharpLink.UnitTests'),
                                    ('integration', integration, 'SharpLink.IntegrationTests')):
        document = strict_json((folder / (assembly + '.deps.json')).read_text())
        target = document.get('runtimeTarget', {}).get('name')
        if target != '.NETCoreApp,Version=v10.0':
            raise ValueError('Correctness dependency target differs')
        graph = document['targets'][target]
        assets = strict_json((source / 'test' / assembly / 'obj/project.assets.json').read_text())
        folders = list(assets.get('packageFolders', {}))
        if len(folders) != 1:
            raise ValueError('Ambiguous correctness package cache')
        selected = {}
        for name in CLOSURE:
            simple = name[:-4]
            product = simple.startswith('SharpLink.')
            key = simple + ('/3.0.0' if product else '/10.0.12')
            node = graph.get(key)
            asset = name if product else 'lib/net10.0/' + name
            library = document.get('libraries', {}).get(key, {})
            if (not isinstance(node, dict) or set(node.get('runtime', {})) != {asset}
                    or library.get('type') != ('project' if product else 'package')):
                raise ValueError('Tested exact dependency graph differs: ' + name)
            if not product:
                path = Path(folders[0]) / simple.lower() / '10.0.12' / asset
                if digest(path) != digest(folder / name):
                    raise ValueError('Tested support DLL differs from resolved package12: ' + name)
            selected[key] = {'runtimeAsset': asset, 'library': library}
        records[label] = selected
    if records['unit'] != records['integration']:
        raise ValueError('Correctness dependency graphs differ')
    return records['unit']


def correctness_graph_inventory(source):
    # Dynamic correctness loads plugin binaries from sibling project outputs too.
    files = sorted(path for path in source.rglob('*') if path.is_file()
                   and 'bin' in path.relative_to(source).parts and 'Release' in path.relative_to(source).parts)
    if any(path.is_symlink() for path in files):
        raise ValueError('Symlink in correctness graph')
    return {str(path.relative_to(source)): digest(path) for path in files}


def freeze_closure(arm, source, unit, integration, output):
    dependency_graph = verify_tested_dependency_graph(source, unit, integration)
    directory = output / (arm + '-libraries')
    directory.mkdir()
    hashes = {}
    for name in CLOSURE:
        src = unit / name
        if not src.is_file() or digest(src) != digest(integration / name):
            raise ValueError('Correctness output closure differs: ' + name)
        if name.startswith('SharpLink.'):
            project = name[:-4]
            if digest(source / 'src' / project / 'bin/Release/net10.0' / name) != digest(src):
                raise ValueError('Product output differs from tested copies: ' + name)
        shutil.copy2(src, directory / name)
        hashes[name] = digest(src)
    generator = source / 'src/SharpLink.Generator/bin/Release/netstandard2.0/SharpLink.Generator.dll'
    shutil.copy2(generator, directory / generator.name)
    manifest = {name: {'dllSha256': hashes['SharpLink.' + name + '.dll']} for name in PRODUCTS}
    manifest['Sdk']['generatorSha256'] = digest(generator)
    manifest.update(dependencyGraph=dependency_graph, closure=hashes, dependencies={name: hashes[name + '.dll'] for name in PRIVATE_DEPENDENCIES},
                    source={'commit': CONTRACT[arm + 'Source'], 'tree': CONTRACT[arm + 'Tree'], 'path': str(source)},
                    correctness={'unitOutput': str(unit), 'integrationOutput': str(integration),
                                 'unitSnapshot': inventory(unit), 'integrationSnapshot': inventory(integration),
                                 'graphSnapshot': correctness_graph_inventory(source)})
    # Preserve dependency graph evidence; no claim these package12 bytes load in TCP's AspNet14 domain.
    shutil.copy2(unit / 'SharpLink.UnitTests.deps.json', directory / 'tested-unit.deps.json')
    shutil.copy2(integration / 'SharpLink.IntegrationTests.deps.json', directory / 'tested-integration.deps.json')
    write(directory / 'closure.json', manifest)
    return directory, manifest


def verify_references(build, directory, manifest):
    for project in ('module', 'probe'):
        files = list((build / project / 'obj').rglob('references.txt'))
        if len(files) != 1:
            raise ValueError('Compiler references absent/ambiguous')
        references = [Path(line) for line in files[0].read_text().splitlines() if line]
        for name, expected in manifest['closure'].items():
            matches = [path for path in references if path.name == name]
            if len(matches) != 1 or matches[0].resolve() != (directory / name).resolve() or digest(matches[0]) != expected:
                raise ValueError('Compiler reference not frozen own-arm bytes: ' + name)
    files = list((build / 'module' / 'obj').rglob('analyzers.txt'))
    if len(files) != 1:
        raise ValueError('Generator inventory absent/ambiguous')
    generators = [Path(line) for line in files[0].read_text().splitlines() if line.endswith('/SharpLink.Generator.dll')]
    if (len(generators) != 1 or generators[0].resolve() != (directory / 'SharpLink.Generator.dll').resolve()
            or digest(generators[0]) != manifest['Sdk']['generatorSha256']):
        raise ValueError('Generator is not frozen own-arm bytes')


def verify_embedded(binary, arm, reader, dotnet):
    actual = strict_json(capture([dotnet, reader, binary]))
    if actual != identity(arm):
        raise ValueError('Actual DLL embedded identity differs')
    return actual


def verify_product_metadata(arm, directory, reader, dotnet):
    result = {}
    for name in (*PRODUCTS, 'Generator'):
        file = directory / ('SharpLink.' + name + '.dll')
        actual = strict_json(capture([dotnet, reader, '--assembly', file]))
        if (set(actual) != {'assemblyName', 'mvid', 'informationalVersion'}
                or actual['assemblyName'] != 'SharpLink.' + name
                or actual['informationalVersion'] != CONTRACT[arm + 'Version'] + '+' + CONTRACT[arm + 'Source']
                or not re.fullmatch('[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}', actual['mvid'])):
            raise ValueError('Actual product/generator source identity differs: ' + name)
        result[name] = actual
    return result


def build_fixtures(arm, directory, manifest, dotnet, reader, output):
    manifest['productMetadata'] = verify_product_metadata(arm, directory, reader, dotnet)
    build = output / (arm + '-build')
    shutil.copytree(ROOT / 'fixture', build, ignore=shutil.ignore_patterns('bin', 'obj', '__pycache__'))
    (build / 'Directory.Packages.props').write_text('<Project />\n')
    props = [f'-p:LibraryRoot={directory}', f'-p:GeneratorPath={directory / "SharpLink.Generator.dll"}', *identity_properties(arm)]
    commands = []
    for name in ('module', 'probe'):
        project = build / name / (name + '.csproj')
        evaluated = strict_json(capture([dotnet, 'msbuild', project, *props,
                                        '-getProperty:Arm,SourceTree,ProductCommit,NuGetAudit,NuGetAuditMode,TreatWarningsAsErrors', '-nologo'], build))
        expected = {'Arm': arm.upper(), 'SourceTree': identity(arm)['SourceTree'], 'ProductCommit': identity(arm)['ExpectedProductCommit'],
                    'NuGetAudit': 'true', 'NuGetAuditMode': 'all', 'TreatWarningsAsErrors': 'true'}
        if evaluated != {'Properties': expected}:
            raise ValueError('Evaluated fixture properties differ')
        write(output / (arm + '-' + name + '-properties.json'), evaluated)
        command = [dotnet, 'build', project, '-c', 'Release', '-m:1', '-nr:false', '--disable-build-servers',
                   '-p:UseSharedCompilation=false', *props]
        commands.append(list(map(str, command)))
        run(command, output / (arm + '-' + name + '.log'), build)
        generated = list((build / name / 'obj').rglob('*.AssemblyInfo.cs'))
        if len(generated) != 1:
            raise ValueError('Generated AssemblyInfo absent/ambiguous')
        rows = re.findall(r'\[assembly:\s*System\.Reflection\.AssemblyMetadata(?:Attribute)?\("([^"\\]*)",\s*"([^"\\]*)"\)\]', generated[0].read_text())
        if len(rows) != 3 or dict(rows) != identity(arm):
            raise ValueError('Generated assembly identity differs')
    write(output / (arm + '-fixture-build-commands.json'), commands)
    verify_references(build, directory, manifest)
    destination = output / arm
    shutil.copytree(build / 'probe/bin/Release/net10.0', destination)
    shutil.copy2(build / 'module/bin/Release/net10.0/ScalarStreamModule.dll', destination)
    pin_runtime(destination)
    for name, expected in manifest['closure'].items():
        if digest(destination / name) != expected or digest(directory / name) != expected:
            raise ValueError('Immutable closure drift: ' + name)
    manifest['fixtureMetadata'] = {name: verify_embedded(destination / file, arm, reader, dotnet)
                                  for name, file in (('probe', 'PhysicalTcpProbe.dll'), ('module', 'ScalarStreamModule.dll'))}
    manifest['snapshot'] = inventory(destination)
    manifest['generated'] = inventory(build / 'module/obj/generated')
    if not manifest['generated']:
        raise ValueError('Own generator output absent')


def verify_prepared(prepared, dotnet):
    manifest = strict_json((prepared / 'prepared.json').read_text())
    if manifest.get('contract') != CONTRACT or manifest.get('metadataPreflight') != 'passed':
        raise ValueError('Prepared identity contract differs')
    reader = Path(manifest['identityReader'])
    if inventory(reader.parent) != manifest['identityReaderSnapshot']:
        raise ValueError('Metadata-only inspector changed')
    for arm in ARMS:
        record = manifest['arms'][arm]
        if record['source']['commit'] != identity(arm)['ExpectedProductCommit'] or record['source']['tree'] != identity(arm)['SourceTree']:
            raise ValueError('Prepared source identity differs')
        if inventory(prepared / arm) != record['snapshot']:
            raise ValueError('Fixture immutable snapshot changed')
        for name, file in (('probe', 'PhysicalTcpProbe.dll'), ('module', 'ScalarStreamModule.dll')):
            if verify_embedded(prepared / arm / file, arm, reader, dotnet) != record['fixtureMetadata'][name]:
                raise ValueError('Compiled identity witness differs')
        if verify_product_metadata(arm, prepared / (arm + '-libraries'), reader, dotnet) != record['productMetadata']:
            raise ValueError('Product metadata changed')
        for name, expected in record['closure'].items():
            if digest(prepared / arm / name) != expected or digest(prepared / (arm + '-libraries') / name) != expected:
                raise ValueError('Product closure changed')
        if correctness_graph_inventory(Path(record['source']['path'])) != record['correctness']['graphSnapshot']:
            raise ValueError('Correctness plugin/product graph changed')
        if digest(prepared / (arm + '-libraries') / 'SharpLink.Generator.dll') != record['Sdk']['generatorSha256']:
            raise ValueError('Frozen generator changed')
        for kind in ('unit', 'integration'):
            if inventory(Path(record['correctness'][kind + 'Output'])) != record['correctness'][kind + 'Snapshot']:
                raise ValueError('Correctness immutable snapshot changed')
    return manifest


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ('baseline-source', 'candidate-source', 'dependency-cache', 'dotnet', 'output'):
        parser.add_argument('--' + name, type=Path, required=True)
    args = parser.parse_args()
    tool_sha = capture(['git', 'rev-parse', 'HEAD'], ROOT)
    if capture(['git', 'status', '--porcelain'], ROOT):
        raise ValueError('Tools must be frozen clean commit before preparation')
    tool_inventory = source_inventory(ROOT)
    verify_identity_source(ROOT / 'fixture')
    sources = {arm: getattr(args, arm + '_source').resolve(strict=True) for arm in ARMS}
    if len(set(sources.values())) != len(ARMS):
        raise ValueError('Each arm requires independent source checkout')
    for arm, source in sources.items():
        verify_source(source, arm)
    output = args.output.resolve(); output.mkdir(parents=True, exist_ok=False)
    build_environment(output)
    dotnet = args.dotnet.resolve(strict=True)
    if capture([dotnet, '--version'], ROOT) != CONTRACT['sdk']:
        raise ValueError('Actual SDK differs')
    cache = args.dependency_cache.resolve(strict=True)
    write(output / 'contract.json', CONTRACT); write(output / 'schedule.json', schedule())
    reader_source = output / 'identity-reader'
    shutil.copytree(ROOT / 'identity-reader', reader_source, ignore=shutil.ignore_patterns('bin', 'obj'))
    run([dotnet, 'build', reader_source / 'Identity.csproj', '-c', 'Release', '-m:1', '-nr:false',
         '--disable-build-servers', '-p:UseSharedCompilation=false'], output / 'identity-reader-build.log', reader_source)
    reader = reader_source / 'bin/Release/net10.0/Identity.dll'
    manifests = {}
    for arm, source in sources.items():
        unit, integration = build_correctness(arm, source, dotnet, cache, output)
        directory, manifests[arm] = freeze_closure(arm, source, unit, integration, output)
        build_fixtures(arm, directory, manifests[arm], dotnet, reader, output)
        if inventory(unit) != manifests[arm]['correctness']['unitSnapshot'] or inventory(integration) != manifests[arm]['correctness']['integrationSnapshot']:
            raise ValueError('Fixture build mutated correctness binaries')
        verify_source(source, arm)
    if manifests['baseline']['dependencies'] != manifests['candidate']['dependencies']:
        raise ValueError('Non-product dependency drift')
    if capture(['git', 'rev-parse', 'HEAD'], ROOT) != tool_sha or capture(['git', 'status', '--porcelain'], ROOT) or source_inventory(ROOT) != tool_inventory:
        raise ValueError('Tool source changed during preparation')
    write(output / 'prepared.json', dict(schema=2, sdk=CONTRACT['sdk'], contract=CONTRACT, arms=manifests,
        toolRevision=tool_sha, toolSourceHashes=tool_inventory, fixturePurpose='server-call-pin-tcp',
        fixtureSourceHashes=source_inventory(ROOT / 'fixture'), status='build-only', metadataPreflight='passed',
        identityReader=str(reader), identityReaderSnapshot=inventory(reader.parent)))
    verify_prepared(output, dotnet)
    print('BUILD_ONLY_PREPARED arms=baseline,candidate; actual DLL metadata verified; tests and TCP NOT RUN')


if __name__ == '__main__':
    main()
