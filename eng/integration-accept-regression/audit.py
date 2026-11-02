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
from classify import require, read_json, unique_object

CATALOG = read_json(Path(__file__).with_name('SOURCE.json'))
NAMES = tuple(CATALOG['projects'])
PROJECTS = {name:value['path'] for name,value in CATALOG['projects'].items()}
ENTRY = CATALOG['entryProject']


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()



@dataclass(frozen=True)
class MetadataDiagnostics:
    category: str
    relative_path: str | None
    file_sha256: str | None
    line: int | None
    column: int | None
    position: int | None

    def public(self):
        result={'category':self.category,'fileSHA256':self.file_sha256,
                'line':self.line,'column':self.column,'position':self.position,
                'coordinateUnit':'decoded-text-codepoints-after-UTF8-BOM-removal'}
        if self.relative_path is not None: result['sdkRelativePath']=self.relative_path
        return result


def metadata_json(text, kind):
    """Official per-type JSON extensions; all other JSON rules remain strict.

    Equal-length whitespace replacement preserves original decoded-text error
    positions. Strings/escapes are not rewritten. No regex comment removal.
    """
    require(kind in ('workload-set','install-state'), 'Unknown workload metadata category')
    masked=list(text);i=0;tokens=[]
    while i<len(text):
        ch=text[i]
        if ch in ' \t\r\n': i+=1;continue
        if ch=='"':
            start=i;i+=1
            while i<len(text):
                if text[i]=='\\': i+=2;continue
                if text[i]=='"':i+=1;break
                i+=1
            else: raise json.JSONDecodeError('Unterminated string',text,start)
            tokens.append(('value',start));continue
        if ch=='/' and i+1<len(text) and text[i+1] in '/*':
            start=i
            if kind!='workload-set':
                raise json.JSONDecodeError('Comments not permitted for install-state metadata',text,start)
            if text[i+1]=='/':
                i+=2
                while i<len(text) and text[i] not in '\r\n':i+=1
            else:
                end=text.find('*/',i+2)
                if end<0:raise json.JSONDecodeError('Unterminated comment',text,start)
                i=end+2
            for offset in range(start,i):
                if masked[offset] not in '\r\n':masked[offset]=' '
            continue
        if ch in '{}[],:':
            tokens.append((ch,i));i+=1;continue
        # Preserve any primitive/malformed lexeme for the strict JSON decoder.
        start=i
        while i<len(text) and text[i] not in ' \t\r\n{}[],:":/':i+=1
        if i==start:i+=1
        tokens.append(('value',start))
    for index,(token,position) in enumerate(tokens):
        if (token==',' and index>0 and index+1<len(tokens)
                and tokens[index-1][0] in ('value',']','}') and tokens[index+1][0] in (']','}')):
            masked[position]=' '
    try:
        return json.loads(''.join(masked),object_pairs_hook=unique_object,
                          parse_constant=lambda _: (_ for _ in ()).throw(ValueError('Nonfinite JSON number')))
    except json.JSONDecodeError as error:
        # Keep the original decoded document in the private exception, not the
        # transformed text. Equal-length masking keeps line/column/pos exact.
        raise json.JSONDecodeError(error.msg,text,error.pos) from error


def metadata_diagnostic(path,sdk_root,kind,raw,error):
    relative=path.relative_to(sdk_root).as_posix()
    safe=relative if len(relative)<=512 and re.fullmatch(r'[A-Za-z0-9_./+ @(),=-]+',relative) else None
    decoded=isinstance(error,json.JSONDecodeError)
    return MetadataDiagnostics(kind,safe,hashlib.sha256(raw).hexdigest() if raw is not None else None,
                               error.lineno if decoded else None,error.colno if decoded else None,
                               error.pos if decoded else None)


def read_workload_metadata(path,sdk_root,kind):
    require(kind in ('workload-set','install-state'), 'Unknown workload metadata category')
    require(path.is_relative_to(sdk_root) and '..' not in path.relative_to(sdk_root).parts,
            'Workload metadata outside known SDK container')
    raw=None
    try:
        # No-follow traversal ensures metadata diagnostics never read a symlink
        # target or arbitrary external file. Source callers have sealed selectors.
        fd=os.open(sdk_root,os.O_RDONLY|os.O_DIRECTORY|os.O_NOFOLLOW)
        try:
            parts=path.relative_to(sdk_root).parts
            require(parts, 'Workload metadata path is not a file')
            for part in parts[:-1]:
                next_fd=os.open(part,os.O_RDONLY|os.O_DIRECTORY|os.O_NOFOLLOW,dir_fd=fd)
                os.close(fd);fd=next_fd
            content_fd=os.open(parts[-1],os.O_RDONLY|os.O_NOFOLLOW|os.O_NONBLOCK,dir_fd=fd)
            try:
                require(stat.S_ISREG(os.fstat(content_fd).st_mode), 'Workload metadata is not regular')
                with os.fdopen(content_fd,'rb',closefd=False) as stream:raw=stream.read()
            finally:os.close(content_fd)
        finally:os.close(fd)
        # Existing UTF8/UTF8-BOM support only. Unknown encoding fails closed;
        # UnicodeDecodeError has no fabricated decoded-text coordinates.
        return metadata_json(raw.decode('utf-8-sig'),kind)
    except BaseException as original:
        try:original.metadata_diagnostics=metadata_diagnostic(path,sdk_root,kind,raw,original)
        except BaseException as reporting_error:
            raise BaseExceptionGroup('Metadata read failure and diagnostic failure',[original,reporting_error])
        raise


def static_graph(root):
    from prepare_accept import blob_bytes
    for path,pin in CATALOG['sourcePins'].items():
        file=root/path
        require(file.is_file() and not file.is_symlink(),'Missing/symlink original graph input')
        if path=='Directory.Packages.props':
            require(blob_bytes(file.read_bytes())==CATALOG['centralAfterGitBlob'],'Originalaccept TUnit upgrade identity changed')
        else:require(digest(file)==pin['sha256'],'Original fixture/project/import bytes changed')
    for path in CATALOG['absent']:require(not (root/path).exists() and not (root/path).is_symlink(),'Unexpected original import selector')
    edges={}
    for name,path in PROJECTS.items():
        project=root/path;refs=[]
        for item in ET.parse(project).findall('.//ProjectReference'):
            target=(project.parent/item.attrib['Include'].replace('\\','/')).resolve()
            require(target.is_relative_to(root),'Project reference escapes exact source')
            refs.append({'path':target.relative_to(root).as_posix(),
                         'attributes':{k:v for k,v in item.attrib.items() if k!='Include'}})
        require(refs==CATALOG['projects'][name]['references'],'Original19 static reference attributes changed')
        edges[name]=refs
    require(len(edges)==19,'Wrong original Integration project closure')
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


def restore_reference_projection(edges):
    # NuGet GetRestoreProjectReferencesTask emits only missing/true
    # ReferenceOutputAssembly; build traversal remains the full pinned 19 graph.
    result=[]
    for edge in edges:
        output=edge['attributes'].get('ReferenceOutputAssembly','true')
        require(output.lower() in ('true','false'),'Unsupported pinned ReferenceOutputAssembly')
        if output.lower()=='true':result.append(edge)
    return result


@dataclass(frozen=True)
class ProjectEdgeDiagnostics:
    project_name: str
    expected: tuple
    optional: tuple
    actual: tuple

    def public(self):
        project=CATALOG['projects'][self.project_name]
        known={v['path']:name for name,v in CATALOG['projects'].items()}
        def safe(path):
            # No FS access and no uncontrolled filename/path disclosure.
            if path in known:
                row=CATALOG['projects'][known[path]]
                return {'project':known[path],'relative':path,'sourceSHA256':row['sha256']}
            return {'category':'unknown-project-relative','pathSHA256':hashlib.sha256(path.encode()).hexdigest()}
        missing=sorted(set(self.expected)-set(self.actual))
        extra=sorted(set(self.actual)-set(self.expected)-set(self.optional))
        return {'project':self.project_name,'relative':project['path'],'sourceSHA256':project['sha256'],
                'expectedCount':len(self.expected),'optionalCount':len(self.optional),'actualCount':len(self.actual),
                'missingCount':len(missing),'extraCount':len(extra),
                'missing':[safe(x) for x in missing[:16]],'extra':[safe(x) for x in extra[:16]],
                'truncated':len(missing)>16 or len(extra)>16}


def require_project_edges(name,mandatory,optional,refs):
    try:
        require(mandatory<=refs<=mandatory|optional,'Actual original project edges outside precise conditional graph')
    except ValueError as original:
        try:original.project_edges=ProjectEdgeDiagnostics(name,tuple(sorted(mandatory)),tuple(sorted(optional)),tuple(sorted(refs)))
        except BaseException as diagnostic:
            raise BaseExceptionGroup('Original edge rejection and diagnostic failure',[original,diagnostic])
        raise


def restored(root, packages):
    edges=static_graph(root)
    expected={str(Path(path).parent/'obj/project.assets.json') for path in PROJECTS.values()}
    found={p.relative_to(root).as_posix() for p in root.rglob('project.assets.json')}
    require(found==expected,'Actual assets differ from original19 project closure')
    cache=cache_audit(packages);documents={};actual_edges={};all_packages={}
    # Identity is per exact project path, never a global restore-name lookup:
    # Dynamic/Replacement intentionally share AssemblyName/RestoreProjectName.
    restored_names={};project_library_paths={name:set() for name in PROJECTS}
    for name,path in PROJECTS.items():
        document=read_json(root/Path(path).parent/'obj/project.assets.json')
        actual=document['project']['restore']
        require(Path(actual['projectPath']).resolve()==(root/path).resolve(),'Wrong original restored project identity')
        require(actual.get('projectName')==CATALOG['projects'][name]['restoreName'],'Wrong actual RestoreProjectName')
        restored_names[(root/path).resolve()]=actual['projectName']
    for name,path in PROJECTS.items():
        project=root/path;value=read_json(project.parent/'obj/project.assets.json');documents[name]=value
        require(not value.get('logs'),'Original project restore reported warning/error')
        restore=value['project']['restore'];framework=CATALOG['projects'][name]['framework']
        require(Path(restore['projectPath']).resolve()==project.resolve(),'Wrong original restored project identity')
        require(set(restore['sources'])=={'https://api.nuget.org/v3/index.json'},'Unexpected original restore source')
        require({Path(x).resolve() for x in value['packageFolders']}=={packages.resolve()},'Unexpected original package folder')
        require(set(restore['frameworks'])=={framework},'Wrong original restored framework')
        refs={Path(x['projectPath']).resolve().relative_to(root).as_posix() for x in restore['frameworks'][framework].get('projectReferences',{}).values()}
        projected=restore_reference_projection(edges[name])
        mandatory={x['path'] for x in projected if 'Condition' not in x['attributes']}
        optional={x['path'] for x in projected if x['attributes'].get('Condition')=="'$(PublishAot)' != 'true'"}
        require_project_edges(name,mandatory,optional,refs)
        actual_edges[name]=sorted(refs)
        for identity,library in value['libraries'].items():
            identifier,version=identity.rsplit('/',1)
            if library['type']=='package':
                require(identity.lower() in cache,'Original asset outside audited official cache')
                require(Path(library['path']).as_posix()==identity.lower(),'Original package asset path identity mismatch')
                content_hash_matches(library['sha512'],cache[identity.lower()]['canonicalContentHash'])
                if identifier=='TUnit' or identifier.startswith('TUnit.'):
                    require(version=='1.78.0','Wrong originalaccept actual TUnit package')
                all_packages[identity]=cache[identity.lower()]
            elif library['type']=='project':
                paths=[]
                for field in ('path','msbuildProject'):
                    relative=library.get(field)
                    require(isinstance(relative,str) and relative and not Path(relative).is_absolute() and '\\' not in relative,
                            'Project library lacks a supported relative project path')
                    paths.append((project.parent/relative).resolve())
                require(paths[0]==paths[1] and paths[0] in restored_names,'Project library path/msbuildProject identity mismatch')
                require(identifier==restored_names[paths[0]] and version=='2.0.4','Unpinned actual project restore identity')
                project_library_paths[name].add(paths[0].relative_to(root.resolve()).as_posix())
            else:require(False,'Unexpected original asset library kind')
    names_by_path={path:name for name,path in PROJECTS.items()}
    for name,library_paths in project_library_paths.items():
        reachable=set();pending=list(actual_edges[name])
        while pending:
            path=pending.pop()
            if path in reachable:continue
            reachable.add(path);pending.extend(actual_edges[names_by_path[path]])
        require(library_paths<=reachable,'Project library is outside actual restored reference closure')
    target=documents[ENTRY]
    require(set(target['targets'])=={'net10.0'},'Wrong original Integration asset target')
    active=sorted(key for key,item in target['libraries'].items() if item['type']=='package')
    require('TUnit/1.78.0' in active and 'Microsoft.Testing.Platform/2.5.2' in active,'Originalaccept version-specific engine/MTP absent')
    return {'packages':active,'projectEdges':actual_edges,'cache':cache,'allProjectPackages':sorted(all_packages)}



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


def built(root,packages,expected_packages):
    output=root/Path(PROJECTS[ENTRY]).parent/'bin/Release/net10.0'
    assets=read_json(root/Path(PROJECTS[ENTRY]).parent/'obj/project.assets.json')
    deps=read_json(output/(ENTRY+'.deps.json'));libs=deps['libraries']
    package_ids={key for key,value in libs.items() if value['type']=='package'}
    project_ids={key for key,value in libs.items() if value['type']=='project'}
    require(package_ids<=set(expected_packages),'Runtime package outside original restored Integration assets')
    require({'TUnit/1.78.0','TUnit.Engine/1.78.0','Microsoft.Testing.Platform/2.5.2'}<=package_ids,'Missing required originalaccept runtime packages')
    require(project_ids=={name+'/2.0.4' for name in CATALOG['runtimeProjects']},'Wrong original production runtime ProjectReferences')
    require(set(libs)==package_ids|project_ids,'Unknown built dependency library kind')
    targets=deps['targets'][deps['runtimeTarget']['name']]
    source_targets=assets['targets']['net10.0'];checked=0
    known_dlls=set();known_rid_files=set()
    for identity in package_ids:
        library=assets['libraries'][identity];folder=packages/library['path']
        require(libs[identity].get('path')==library['path'],'Runtime package path differs from actual assets')
        for kind in ('runtime','native'):
            for relative in targets[identity].get(kind,{}):
                require(relative in source_targets[identity].get(kind,{}),'Runtime asset absent from exact restore target')
                if relative.endswith('/_._') or relative=='_._':continue
                installed=folder/relative;copied=output/Path(relative).name
                require(copied.is_file() and digest(copied)==digest(installed),'Original runtime copied bytes differ from official asset')
                if copied.suffix=='.dll':known_dlls.add(copied.name)
                checked+=1
        # SDK WriteRuntimeTargets preserves the asset directory in both
        # DestinationSubDirectory and DestinationSubPath for copy-local output.
        for relative,entry in targets[identity].get('runtimeTargets',{}).items():
            require(relative in source_targets[identity].get('runtimeTargets',{}) and entry==source_targets[identity]['runtimeTargets'][relative],
                    'Unbound RID-specific runtime asset')
            parts=relative.split('/')
            require(len(parts)>=4 and parts[0]=='runtimes' and all(x not in ('','..','.') for x in parts)
                    and '\\' not in relative and entry.get('rid')==parts[1] and entry.get('assetType') in ('runtime','native'),
                    'Unsupported RID-specific copy destination')
            if parts[-1]=='_._':continue
            copied=output/relative;installed=folder/relative
            for base in (output,folder):
                current=base
                for index,part in enumerate(parts):
                    current=current/part;mode=current.lstat().st_mode
                    require(stat.S_ISREG(mode) if index==len(parts)-1 else stat.S_ISDIR(mode),'RID-specific asset is symlink/special')
            require(digest(copied)==digest(installed),'RID-specific copied bytes differ from official asset')
            require(relative not in known_rid_files,'Duplicate RID-specific copy destination')
            known_rid_files.add(relative);checked+=1
    actual_rid_files=set()
    if (output/'runtimes').exists() or (output/'runtimes').is_symlink():
        require(stat.S_ISDIR((output/'runtimes').lstat().st_mode),'RID output root is not an ordinary directory')
        for file in (output/'runtimes').rglob('*'):
            mode=file.lstat().st_mode
            require(stat.S_ISREG(mode) or stat.S_ISDIR(mode),'RID output contains symlink/special')
            if stat.S_ISREG(mode):actual_rid_files.add(file.relative_to(output).as_posix())
    require(actual_rid_files==known_rid_files,'Unmapped extra/missing RID output file')
    for name in CATALOG['runtimeProjects']:
        actual=root/Path(PROJECTS[name]).parent/'bin/Release/net10.0'/(name+'.dll')
        require(digest(output/(name+'.dll'))==digest(actual),'Original production assembly copy differs from project output')
        known_dlls.add(name+'.dll')
    require({p.name for p in output.glob('*.dll')}==known_dlls,'Unmapped extra/missing original runtime root DLL')
    config=read_json(output/(ENTRY+'.runtimeconfig.json'))
    require(config['runtimeOptions']['tfm']=='net10.0' and config['runtimeOptions']['framework']=={'name':'Microsoft.NETCore.App','version':'10.0.0'},'Wrong originalaccept runtime request')
    return {'binary':digest(output/(ENTRY+'.dll')),'officialRuntimeAssets':checked,'runtimePackages':sorted(package_ids),
            'assetOnlyPackages':sorted(set(expected_packages)-package_ids),'runtimeProjects':sorted(project_ids),'frameworkRequest':'10.0.0'}
