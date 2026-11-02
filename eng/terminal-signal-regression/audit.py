"""Read-only static graph, actual restored assets/imports and copied runtime checks."""
import base64
import hashlib
import json
import os
import stat
from dataclasses import dataclass
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



@dataclass(frozen=True)
class ImportDiagnostics:
    # Constructed only at the original rejection; never changes acceptance.
    candidate_count: int
    rejected_count: int
    categories: dict
    rejected: tuple

    def public(self):
        return {'candidateCount': self.candidate_count, 'rejectedCount': self.rejected_count,
                'categoryCounts': self.categories, 'rejected': list(self.rejected),
                'truncated': self.rejected_count > len(self.rejected)}


def origin_candidates(text):
    # Deliberately preserve the old broad candidate rule for this diagnostic run.
    for comment in re.findall(r'<!--(.*?)-->', text, re.S):
        for line in comment.splitlines():
            line = line.strip()
            if line.startswith('/') and line.endswith(('.props', '.targets', '.csproj', '.proj')):
                yield Path(line), comment


def header_role(comment, candidate):
    # Diagnostic only: exact MSBuild 18.0.35 Preprocessor delimiters/positions.
    lines = comment.strip().splitlines()
    if len(lines) < 3 or lines[0] != '=' * 140 or lines[-1] != '=' * 140:
        return 'other-comment'
    middle = lines[1:-1]
    if len(middle)==1 and Path(middle[0])==candidate: return 'root'
    if len(middle)==3 and middle[:2]==['  </Import>', ''] and Path(middle[-1])==candidate: return 'import-close'
    if (len(middle) in (3, 4) and middle[0].startswith('  <Import Project="')
            and middle[0].endswith('>') and middle[-2]=='' and Path(middle[-1])==candidate
            and (len(middle) == 3 or middle[1].startswith('  This import was added implicitly because '))):
        return 'import-open'
    return 'other-comment'



def header_candidates(text):
    # Only the emitted root/open/close comment grammar defines origin records.
    # Arbitrary source comments containing absolute example paths are not imports.
    for comment in re.findall(r'<!--(.*?)-->',text,re.S):
        lines=comment.strip().splitlines()
        if len(lines)<3 or lines[0]!='='*140 or lines[-1]!='='*140: continue
        candidate=Path(lines[-2])
        if candidate.is_absolute() and header_role(comment,candidate)!='other-comment':
            yield candidate,comment


def origin_class(candidate, root, packages, sdk_root, sdk_pins):
    # Lexical normalization does not follow external symlinks or read external files.
    normalized = Path(os.path.normpath(str(candidate)))
    for category, base in (('source', root), ('cache', packages), ('sdk-container-unsealed', sdk_root)):
        if normalized.is_relative_to(base):
            if category == 'sdk-container-unsealed' and str(normalized) in sdk_pins:
                category = 'sdk-sealed'
            return category, base, normalized
    return 'outside-known-roots', None, normalized


def origin_detail(candidate, comment, root, packages, sdk_root, sdk_pins):
    category, base, normalized = origin_class(candidate, root, packages, sdk_root, sdk_pins)
    row = {'category': category, 'headerRole': header_role(comment, candidate),
           'pathSHA256': hashlib.sha256(str(candidate).encode()).hexdigest(),
           'normalizedSpellingDiffers': str(normalized) != str(candidate),
           'normalizedExactSealedMember': str(normalized) in sdk_pins,
           'exists': None, 'regular': None, 'symlink': None}
    if base is None: return row  # No stat/open/read of any unknown external path.
    relative = normalized.relative_to(base).as_posix()
    # Only bounded conventional relative paths are public, never absolute roots.
    if len(relative) <= 512 and re.fullmatch(r'[A-Za-z0-9_./+ @(),=-]+', relative):
        row['relativePath'] = relative
    # Traverse beneath the trusted container with no-follow dirfds. A symlink,
    # special node, missing parent, or race never causes an external content read.
    fd = os.open(base, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
    try:
        parts = normalized.relative_to(base).parts
        for index, part in enumerate(parts):
            try: info = os.stat(part, dir_fd=fd, follow_symlinks=False)
            except FileNotFoundError:
                row.update(exists=False, regular=False, symlink=False); return row
            if stat.S_ISLNK(info.st_mode):
                row.update(exists=True, regular=False, symlink=True); return row
            if index < len(parts) - 1:
                if not stat.S_ISDIR(info.st_mode):
                    row.update(exists=False, regular=False, symlink=False); return row
                next_fd = os.open(part, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW, dir_fd=fd)
                os.close(fd); fd = next_fd
                continue
            regular = stat.S_ISREG(info.st_mode)
            row.update(exists=True, regular=regular, symlink=False)
            if regular and info.st_size <= 16 * 1024 * 1024:
                content_fd = os.open(part, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK, dir_fd=fd)
                try:
                    actual = os.fstat(content_fd)
                    if (not stat.S_ISREG(actual.st_mode) or
                            (actual.st_dev, actual.st_ino, actual.st_size) != (info.st_dev, info.st_ino, info.st_size)):
                        raise ValueError('Diagnostic file identity changed')
                    with os.fdopen(content_fd, 'rb', closefd=False) as stream:
                        raw = stream.read(16 * 1024 * 1024 + 1)
                    if len(raw) != info.st_size: raise ValueError('Diagnostic file size changed')
                    row['fileSHA256'] = hashlib.sha256(raw).hexdigest()
                finally: os.close(content_fd)
    finally: os.close(fd)
    return row


def import_diagnostics(candidates, root, packages, sdk_root, sdk_pins, exact_pins=None):
    categories = {}; rejected = []; count = 0
    for candidate, comment in candidates:
        category, _, _ = origin_class(candidate, root, packages, sdk_root, sdk_pins)
        categories[category] = categories.get(category, 0) + 1
        if exact_pins is None:
            rejected_here=not (any(candidate.is_relative_to(base) for base in (root, packages)) or str(candidate) in sdk_pins)
        else:
            try:
                actual=canonical_origin(candidate,(root,packages,sdk_root),exact_pins)
                rejected_here=digest(actual)!=exact_pins[str(actual)][0]
            except (ValueError,OSError): rejected_here=True
        if rejected_here:
            count += 1
            if len(rejected) < 16:
                rejected.append(origin_detail(candidate, comment, root, packages, sdk_root, sdk_pins))
    return ImportDiagnostics(len(candidates), count, categories, tuple(rejected))



def canonical_origin(candidate, containers, pins):
    """Canonical file identity inside its original container, no symlink traversal.

    Normal '..' within a container is allowed, but cannot escape it. Every
    spelling component is checked before collapsing, so link/.. is not a bypass.
    MSBuild's '--' -> '__' comment encoding is reversed only by an unambiguous
    match to the exact sealed file table, never by a global string replacement.
    """
    raw=str(candidate)
    matches=[base for base in containers if candidate.is_relative_to(base)]
    require(matches, 'Import outside exact source/official cache/sealed selected SDK')
    base=max(matches,key=lambda p:len(p.parts))
    normalized=str(Path(os.path.normpath(raw)))
    require(Path(normalized).is_relative_to(base), 'Import path escaped its input container')
    aliases=[Path(path) for path in pins if path.replace('--','__')==normalized]
    if normalized in pins and Path(normalized) not in aliases: aliases.append(Path(normalized))
    require(len(aliases)==1, 'Import origin missing/ambiguous in exact sealed inputs')
    actual=aliases[0]
    require(actual.is_relative_to(base), 'Import encoded identity escaped container')
    encoded_ancestors={str(p).replace('--','__') for p in (actual,*actual.parents) if p.is_relative_to(base)}
    stack=[]
    for index,part in enumerate(candidate.relative_to(base).parts):
        if part=='.': continue
        if part=='..':
            require(stack, 'Import path escaped its input container')
            stack.pop();continue
        stack.append(part)
        current=base.joinpath(*stack)
        try: info=current.lstat()
        except FileNotFoundError:
            require('__' in str(current) and str(current) in encoded_ancestors, 'Import spelling parent unavailable')
            continue
        require(not stat.S_ISLNK(info.st_mode), 'Import origin contains symlink')
        require(stat.S_ISREG(info.st_mode) or stat.S_ISDIR(info.st_mode), 'Import origin contains special node')
        if index<len(candidate.relative_to(base).parts)-1:
            require(stat.S_ISDIR(info.st_mode), 'Import origin parent is not directory')
    current=base
    for part in actual.relative_to(base).parts:
        current=current/part;info=current.lstat()
        require(not stat.S_ISLNK(info.st_mode), 'Import origin contains symlink')
        require(stat.S_ISREG(info.st_mode) if current==actual else stat.S_ISDIR(info.st_mode), 'Import origin is not regular file/directory')
    return actual


def expanded(path, root, packages, sdk_pins, diagnostic_sdk_root=None, source_pins=None, cache_pins=None):
    text = path.read_text(encoding='utf-8-sig')
    xml = ET.fromstring(text)
    require(not any(e.tag.split('}')[-1] == 'Import' for e in xml.iter()), 'Unexpanded MSBuild Import')
    require(source_pins is not None and cache_pins is not None and diagnostic_sdk_root is not None, 'Exact import input seals missing')
    pins={**source_pins,**cache_pins,**sdk_pins}
    candidates = list(header_candidates(text))
    origins = {}
    for candidate, comment in candidates:
        try:
            actual=canonical_origin(candidate,(root,packages,diagnostic_sdk_root),pins)
            require(digest(actual)==pins[str(actual)][0], 'Expanded origin differs from exact sealed input')
        except (ValueError,OSError) as original:
            try:
                original.origin_diagnostics = import_diagnostics(candidates, root, packages, diagnostic_sdk_root, sdk_pins,exact_pins=pins)
            except BaseException as diagnostic_error:
                raise BaseExceptionGroup('Import rejection and diagnostic failure', [original, diagnostic_error])
            raise
        origins[str(actual)] = pins[str(actual)][0]
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
