"""Point-in-time original Integration graph and official archive/output byte audit.
Extracted from reviewed Linux audit; no expanded-import or full SDK seal claim.
"""
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
CATALOG=read_json(Path(__file__).with_name('ACCEPT-SOURCE.json'))
NAMES=tuple(CATALOG['projects'])
PROJECTS={name:value['path'] for name,value in CATALOG['projects'].items()}
ENTRY=CATALOG['entryProject']


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def static_graph(root):
    from candidate import blob as blob_bytes
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
    expected={(Path(path).parent/'obj/project.assets.json').as_posix() for path in PROJECTS.values()}
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
class RidAssetDiagnostics:
    package: str
    relative: str
    restored_member: bool
    deps: dict
    restored: dict | None

    def public(self):
        def hashed(value):
            return hashlib.sha256(json.dumps(value,sort_keys=True,separators=(',',':'),allow_nan=False).encode()).hexdigest()
        known_keys={'rid','assetType','assemblyVersion','fileVersion'}
        left=set(self.deps);right=set(self.restored or {})
        def keys(values):
            return [{'key':key} if key in known_keys else {'unknownKeySHA256':hashed(key)} for key in sorted(values)[:16]]
        parts=self.relative.split('/')
        safe_relative=(self.restored_member and len(self.relative)<=512 and len(parts)>=4 and parts[0]=='runtimes'
                       and all(re.fullmatch(r'[A-Za-z0-9_.+-]+',x) and x not in ('.','..') for x in parts))
        output={'packageIdentitySHA256':hashed(self.package),'relativeSHA256':hashed(self.relative),
                'restoredMember':self.restored_member,'depsKeyCount':len(left),'assetsKeyCount':len(right),
                'depsKeys':keys(left),'assetsKeys':keys(right),'keysTruncated':len(left)>16 or len(right)>16,
                'depsMetadataSHA256':hashed(self.deps),'assetsMetadataSHA256':hashed(self.restored),
                'commonKnownFieldsEqual':{key:self.deps[key]==self.restored[key] for key in sorted(left&right&known_keys)}}
        # Package identity already came from audited official Integration assets.
        if len(self.package)<=256 and re.fullmatch(r'[A-Za-z0-9_.+-]+/[A-Za-z0-9_.+-]+',self.package):output['package']=self.package
        if safe_relative:output['knownPackageRelative']=self.relative
        return output


def sdk_derived_version(value):
    # Version.ToString canonical four UInt16 components. These are descriptive
    # SDK output fields, NOT an independent Python extraction from the PE file.
    return (isinstance(value,str) and re.fullmatch(r'(?:0|[1-9][0-9]{0,4})(?:\.(?:0|[1-9][0-9]{0,4})){3}',value) is not None
            and all(int(part)<=65535 for part in value.split('.')))


def rid_metadata_matches(relative,entry,restored_targets):
    if relative not in restored_targets:return False
    restored=restored_targets[relative]
    if type(entry) is not dict or type(restored) is not dict:return False
    identity_keys={'rid','assetType'};version_keys={'fileVersion','assemblyVersion'}
    if set(restored)!=identity_keys or not identity_keys<=set(entry)<=identity_keys|version_keys:return False
    if any(type(entry[key]) is not str or entry[key]!=restored[key] for key in identity_keys):return False
    if any(not sdk_derived_version(entry[key]) for key in version_keys&set(entry)):return False
    if 'assemblyVersion' in entry and ('fileVersion' not in entry or Path(relative).suffix.lower() not in ('.dll','.exe','.winmd')):return False
    return True


def require_rid_metadata(identity,relative,entry,restored_targets):
    try:
        require(rid_metadata_matches(relative,entry,restored_targets), 'Unbound RID-specific runtime asset')
    except ValueError as original:
        try:
            original.rid_asset=RidAssetDiagnostics(identity,relative,relative in restored_targets,entry,restored_targets.get(relative))
        except BaseException as diagnostic:
            raise BaseExceptionGroup('Original RID rejection and diagnostic failure',[original,diagnostic])
        raise


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
    known_dlls=set();known_rid_files=set();version_fields_checked=0
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
            require_rid_metadata(identity,relative,entry,source_targets[identity].get('runtimeTargets',{}))
            version_fields_checked+=sum(key in entry for key in ('fileVersion','assemblyVersion'))
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
            'ridVersionMetadata':{'policy':'SDK-derived descriptor schema only; not independent PE version extraction','validatedFields':version_fields_checked},
            'assetOnlyPackages':sorted(set(expected_packages)-package_ids),'runtimeProjects':sorted(project_ids),'frameworkRequest':'10.0.0'}
