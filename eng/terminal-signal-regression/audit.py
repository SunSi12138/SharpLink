"""Read-only static graph, actual restored assets/imports and copied runtime checks."""
import base64
import hashlib
import json
from pathlib import Path
import re
import xml.etree.ElementTree as ET
from zipfile import ZipFile
from classify import require, read_json

NAMES = ('SharpLink.Abstractions', 'SharpLink.Runtime', 'SharpLink.Server', 'SharpLink.TerminalSignal.Focused')
PROJECTS = {name: ('test/' if name.endswith('Focused') else 'src/') + name + '/' + name + '.csproj' for name in NAMES}


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def static_graph(root):
    edges = {}
    allowed = {(root / path).resolve(): name for name, path in PROJECTS.items()}
    for name, path in PROJECTS.items():
        p = root / path
        tree = ET.parse(p)
        refs = [(p.parent / ref.attrib['Include'].replace('\\', '/')).resolve() for ref in tree.findall('.//ProjectReference')]
        require(all(ref in allowed for ref in refs), 'Unexpected project graph edge')
        edges[name] = sorted(allowed[ref] for ref in refs)
    require(edges == {'SharpLink.Abstractions': [], 'SharpLink.Runtime': ['SharpLink.Abstractions'],
                      'SharpLink.Server': ['SharpLink.Abstractions', 'SharpLink.Runtime'],
                      'SharpLink.TerminalSignal.Focused': ['SharpLink.Abstractions', 'SharpLink.Runtime', 'SharpLink.Server']},
            'Wrong static four-project graph')
    focused = ET.parse(root / PROJECTS[NAMES[-1]])
    require(sorted(x.get('Include') for x in focused.findall('.//Compile')) == ['FocusedGlobalUsings.cs', 'ServerTerminalSignalObservationTests.cs'], 'Wrong focused compile set')
    require(focused.findtext('.//EnableDefaultCompileItems') == 'false' and focused.findtext('.//AssemblyName') == 'SharpLink.UnitTests', 'Wrong focused assembly/compile policy')
    require(focused.find(".//TestingPlatformBuilderHook[@Remove='98058041-B5B6-4A75-9834-58E6DF796A22']") is not None, 'Focused hook removal missing')
    return edges


def hash512(value):
    require(isinstance(value, str), 'Missing SHA512 string')
    decoded = base64.b64decode(value, validate=True)
    require(len(decoded) == 64, 'Wrong SHA512 length')
    return decoded


def content_hash_matches(assets_hash, metadata_hash):
    require(isinstance(assets_hash, str), 'Missing assets canonical hash')
    require(hash512(assets_hash.removeprefix('sha512-')) == hash512(metadata_hash), 'Assets/canonical metadata mismatch')


def cache_audit(packages):
    archives = sorted(packages.glob('*/*/*.nupkg'))
    require(archives, 'Official package cache is empty')
    identities = {}
    folders = {archive.parent for archive in archives}
    for path in packages.rglob('*'):
        require(not path.is_symlink(), 'Symlink anywhere in official cache')
        require(path.is_file() or path.is_dir(), 'Special file in official cache')
        require(any(path.is_relative_to(folder) or (path.is_dir() and folder.is_relative_to(path)) for folder in folders), 'Orphan cache file/directory without archive')
    for archive in archives:
        folder = archive.parent
        identifier, version = folder.parent.name, folder.name
        raw = archive.read_bytes()
        sidecar = archive.with_suffix('.nupkg.sha512')
        require(hash512(sidecar.read_text().strip()) == hashlib.sha512(raw).digest(), 'Raw archive/sidecar SHA512 mismatch')
        metadata = read_json(folder / '.nupkg.metadata')
        require(metadata.get('source') == 'https://api.nuget.org/v3/index.json', 'Package was not restored from sole official source')
        canonical = metadata.get('contentHash')
        hash512(canonical)  # Shape only: canonical signed-content hash is not raw ZIP hash.
        with ZipFile(archive) as zipped:
            names = {x.lower(): x for x in zipped.namelist() if not x.endswith('/')}
            require(len(names) == len([x for x in zipped.namelist() if not x.endswith('/')]), 'Archive duplicate/case alias')
            nuspec = [x for x in names if x.endswith('.nuspec') and '/' not in x]
            require(len(nuspec) == 1, 'Package nuspec missing/duplicate')
            tree = ET.fromstring(zipped.read(names[nuspec[0]]))
            fields = {e.tag.split('}')[-1]: e.text for e in tree.iter() if e.tag.split('}')[-1] in ('id', 'version')}
            require(fields.get('id', '').lower() == identifier and fields.get('version', '').lower() == version, 'Package nuspec identity mismatch')
            for file in folder.rglob('*'):
                require(not file.is_symlink(), 'Symlink in official cache')
                if not file.is_file() or file in (archive, sidecar, folder / '.nupkg.metadata'):
                    continue
                rel = file.relative_to(folder).as_posix().lower()
                require(rel in names, 'Installed file absent from official archive')
                require(file.read_bytes() == zipped.read(names[rel]), 'Installed bytes differ from official archive')
        identities[identifier + '/' + version] = {'archiveSHA256':digest(archive), 'archiveSHA512':base64.b64encode(hashlib.sha512(raw).digest()).decode(), 'canonicalContentHash':canonical}
    return identities


def restored(root, packages):
    edges = static_graph(root)
    all_assets = {}
    for name, relative in PROJECTS.items():
        p = root / relative
        a = read_json(p.parent / 'obj/project.assets.json')
        require(not a.get('logs'), 'NuGet restore reported warning/error')
        restore = a['project']['restore']
        require(Path(restore['projectPath']).resolve() == p.resolve(), 'Wrong restored project identity')
        require(set(restore['sources']) == {'https://api.nuget.org/v3/index.json'}, 'Unexpected NuGet source')
        require({Path(x).resolve() for x in a['packageFolders']} == {packages.resolve()}, 'Unexpected package folder')
        frameworks = restore['frameworks']
        require(set(frameworks) == {'net10.0'}, 'Unexpected restored framework')
        actual_refs = sorted(Path(v['projectPath']).stem for v in frameworks['net10.0'].get('projectReferences', {}).values())
        require(actual_refs == edges[name], 'Actual restored project graph differs')
        all_assets[name] = a
    focused = all_assets[NAMES[-1]]
    libs = focused['libraries']
    active_packages = {name: item for name, item in libs.items() if item['type'] == 'package'}
    active_projects = {name: item for name, item in libs.items() if item['type'] == 'project'}
    require(set(active_projects) == {x+'/2.0.4' for x in NAMES[:3]}, 'Production package substituted for ProjectReference')
    require('TUnit/1.73.19' in active_packages and all(key.split('/')[1] == '1.73.19' for key in active_packages if key.split('/')[0].lower().startswith('tunit')), 'Wrong TUnit version')
    require('Microsoft.Testing.Platform/2.5.1' in active_packages, 'Wrong MTP version')
    cache = cache_audit(packages)
    for identity, library in active_packages.items():
        path = packages / library['path']
        archive = path / (path.parent.name+'.'+path.name+'.nupkg')
        require(identity.lower() in cache, 'Active package missing from audited cache')
        content_hash_matches(library['sha512'], cache[identity.lower()]['canonicalContentHash'])
    return {'packages': sorted(active_packages), 'projectEdges': edges, 'cache': cache}


def expanded(path, root, packages, sdk_pins):
    text = path.read_text(encoding='utf-8-sig')
    xml = ET.fromstring(text)
    require(not any(e.tag.split('}')[-1] == 'Import' for e in xml.iter()), 'Unexpanded MSBuild Import')
    origins = {}
    for comment in re.findall(r'<!--(.*?)-->', text, re.S):
        for line in comment.splitlines():
            line = line.strip()
            if not line.startswith('/') or not line.endswith(('.props', '.targets', '.csproj', '.proj')):
                continue
            candidate = Path(line)
            require(any(candidate.is_relative_to(base) for base in (root, packages)) or str(candidate) in sdk_pins, 'Import outside exact source/official cache/sealed selected SDK')
            require(not candidate.is_symlink() and candidate.is_file(), 'Import origin unavailable')
            current_digest = digest(candidate)
            if str(candidate) in sdk_pins:
                require(current_digest == sdk_pins[str(candidate)][0], 'Expanded SDK origin differs from sealed input')
            origins[str(candidate)] = current_digest
    require(origins and any('Microsoft.NET.Sdk' in p for p in origins), 'Actual import origins missing')
    return origins


def built(root, packages, expected_packages):
    output = root / 'test/SharpLink.TerminalSignal.Focused/bin/Release/net10.0'
    deps = read_json(output / 'SharpLink.UnitTests.deps.json')
    libs = deps['libraries']
    require(sorted(k for k,v in libs.items() if v['type'] == 'package') == expected_packages, 'Built package graph differs from assets')
    require({k for k,v in libs.items() if v['type']=='project'} == {x+'/2.0.4' for x in (*NAMES[:3], 'SharpLink.UnitTests')}, 'Built production project identity mismatch')
    targets = deps['targets'][deps['runtimeTarget']['name']]
    checked = 0
    for identity in expected_packages:
        package_dir = packages / libs[identity]['path']
        for relative in targets[identity].get('runtime', {}):
            if relative.endswith('/_._') or relative == '_._':
                continue
            installed, copied = package_dir / relative, output / Path(relative).name
            require(copied.is_file() and digest(copied) == digest(installed), 'Runtime DLL differs from audited official cache')
            checked += 1
    for name in NAMES[:3]:
        require(digest(output / (name+'.dll')) == digest(root / 'src' / name / 'bin/Release/net10.0' / (name+'.dll')), 'Wrong production DLL copy')
    config = read_json(output / 'SharpLink.UnitTests.runtimeconfig.json')
    require(config['runtimeOptions']['tfm'] == 'net10.0', 'Wrong runtime target')
    require(config['runtimeOptions']['framework']['name'] == 'Microsoft.NETCore.App' and config['runtimeOptions']['framework']['version'] == '10.0.0', 'Wrong runtime framework request')
    return {'officialRuntimeAssets': checked, 'binary': digest(output / 'SharpLink.UnitTests.dll'), 'frameworkRequest': '10.0.0'}
