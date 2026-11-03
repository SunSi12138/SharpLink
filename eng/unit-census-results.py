"""Release ordinary Unit metadata census. No SDK calls and no test execution.

Observed UID rows are not test results; initial source eligibility is not the
execution dependency closure. Private values never become public names.
"""
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import stat


def load(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    value = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(value)
    return value


legacy = load('census_reviewed_helpers', Path(__file__).with_name('route59-results.py'))


class Rejected(Exception):
    def __init__(self, code):
        super().__init__(code)
        self.code = code


def require(ok, code):
    if not ok:
        raise Rejected(code)


def digest(value):
    return hashlib.sha256(json.dumps(value, sort_keys=True, separators=(',', ':'),
                                     ensure_ascii=True, allow_nan=False).encode()).hexdigest()


def stat_identity(info):
    return (info.st_dev, info.st_ino, info.st_mode, info.st_nlink, info.st_size, info.st_mtime_ns, info.st_ctime_ns)


def stable_read(path):
    """No-follow directory descriptors plus before/after file AND path identity."""
    path=Path(path).absolute();require('..' not in path.parts,'UC_READ_PATH')
    descriptors=[];anchors=[]
    try:
        fd=os.open('/',os.O_RDONLY|os.O_DIRECTORY|os.O_NOFOLLOW|os.O_CLOEXEC);descriptors.append(fd)
        for part in path.parts[1:-1]:
            before=os.stat(part,dir_fd=fd,follow_symlinks=False)
            require(stat.S_ISDIR(before.st_mode),'UC_READ_PARENT')
            child=os.open(part,os.O_RDONLY|os.O_DIRECTORY|os.O_NOFOLLOW|os.O_CLOEXEC,dir_fd=fd)
            descriptors.append(child)
            require(stat_identity(before)==stat_identity(os.fstat(child)),'UC_READ_PARENT_RACE')
            anchors.append((fd,part,child,before));fd=child
        name=path.name;before=os.stat(name,dir_fd=fd,follow_symlinks=False)
        require(stat.S_ISREG(before.st_mode) and before.st_nlink==1,'UC_FILE_KIND')
        file_fd=os.open(name,os.O_RDONLY|os.O_NOFOLLOW|os.O_CLOEXEC,dir_fd=fd);descriptors.append(file_fd)
        require(stat_identity(before)==stat_identity(os.fstat(file_fd)),'UC_READ_REPLACED')
        chunks=[]
        while True:
            data=os.read(file_fd,1024*1024)
            if not data:break
            chunks.append(data)
        after=os.fstat(file_fd)
        require(stat_identity(before)==stat_identity(after)==stat_identity(os.stat(name,dir_fd=fd,follow_symlinks=False)),'UC_READ_CHANGED')
        for parent,name,child,initial in anchors:
            require(stat_identity(initial)[:3]==stat_identity(os.fstat(child))[:3]==stat_identity(os.stat(name,dir_fd=parent,follow_symlinks=False))[:3],'UC_READ_PARENT_CHANGED')
        data=b''.join(chunks)
        require(len(data)==before.st_size,'UC_READ_LENGTH')
        return data,[before.st_dev,before.st_ino,stat.S_IMODE(before.st_mode),before.st_size,before.st_mtime_ns,before.st_ctime_ns,hashlib.sha256(data).hexdigest()]
    finally:
        for fd in reversed(descriptors):os.close(fd)


def sha(path):
    return stable_read(path)[1][-1]


def read_json(path):
    data,_=stable_read(path);require(len(data)<=16*1024*1024,'UC_JSON_SIZE')
    return json.loads(data.decode('utf-8-sig'),object_pairs_hook=legacy.common.unique_object,
        parse_constant=lambda _:(_ for _ in ()).throw(Rejected('UC_JSON_NUMBER')))


def file_identity(path):
    return stable_read(path)[1]


def enumeration(root):
    require(stat.S_ISDIR(root.lstat().st_mode),'UC_DIRECTORY')
    result={}
    for path in [root,*sorted(root.rglob('*'))]:
        info=path.lstat();require(stat.S_ISDIR(info.st_mode) or stat.S_ISREG(info.st_mode),'UC_NODE_KIND')
        require(stat.S_ISDIR(info.st_mode) or info.st_nlink==1,'UC_NODE_ALIAS')
        result[path.relative_to(root).as_posix()]=stat_identity(info)
    return result


def snapshot(root):
    """Exact members including empty dirs; reject mutation during whole scan."""
    before=enumeration(root);result={}
    for name,info in before.items():
        path=root/name
        if stat.S_ISDIR(info[2]):result[name]=['directory',info[0],info[1],stat.S_IMODE(info[2])]
        else:
            identity=file_identity(path)
            require((identity[0],identity[1],identity[2],identity[3],identity[4],identity[5])==(info[0],info[1],stat.S_IMODE(info[2]),info[4],info[5],info[6]),'UC_SCAN_FILE_CHANGED')
            result[name]=['file',*identity]
    require(enumeration(root)==before,'UC_SCAN_CHANGED')
    return result


def verify_snapshot(root, expected):
    require(snapshot(root) == expected, 'UC_SEAL')


def write_json(directory, name, value):
    require(name.isascii() and name.replace('-', '').replace('.', '').isalnum(), 'UC_PRIVATE_NAME')
    fd = os.open(directory, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
    try:
        payload = (json.dumps(value, ensure_ascii=True, allow_nan=False, indent=2)+'\n').encode()
        out = os.open(name, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600, dir_fd=fd)
        with os.fdopen(out, 'wb') as file:
            file.write(payload); file.flush(); os.fsync(file.fileno())
        os.fsync(fd)
    finally:
        os.close(fd)


def method_key(row):
    return (row['source'], row['namespace'], row['className'], row['methodName'])


def classify(document, catalog, configuration='Release'):
    require(type(document) is dict and set(document) == {'schemaVersion', 'tests'} and
            type(document['schemaVersion']) is int and document['schemaVersion'] == 1 and
            type(document['tests']) is list, 'UC_SCHEMA')
    active = [m for m in catalog['methods'] if configuration in m['configurations']]
    expected = {method_key(m): m for m in active}
    require(len(expected) == len(active) and expected, 'UC_CATALOG_METHODS')
    counts = {key: 0 for key in expected}
    rows = {}; explicit = eligible = unknown = 0
    for row in document['tests']:
        require(type(row) is dict and set(row) <= {'uid', 'displayName', 'type', 'location', 'traits', 'properties'}, 'UC_ROW')
        uid = row.get('uid')
        require(type(uid) is str and 0 < len(uid) <= 8192 and not any(x in uid for x in '\0\r\n') and uid not in rows, 'UC_UID')
        require(type(row.get('displayName')) is str and len(row['displayName']) <= 32768, 'UC_DISPLAY')
        meta = row.get('type'); location = row.get('location')
        require(type(meta) is dict and type(location) is dict, 'UC_METADATA')
        require(meta.get('assemblyFullName') == catalog['assemblyFullName'], 'UC_ASSEMBLY')
        file = location.get('file')
        require(type(file) is str and file.startswith('/_/') and '\\' not in file and
                Path(file).as_posix() == file and '..' not in Path(file).parts, 'UC_SOURCE_PATH')
        name = meta.get('typeName'); ns = meta.get('namespace')
        require(type(ns) is str and type(name) is str and type(meta.get('methodName')) is str, 'UC_TYPE')
        short = name[len(ns)+1:] if name.startswith(ns+'.') else name
        key = (file[3:], ns, short, meta['methodName'])
        require(key in expected, 'UC_FOREIGN_METHOD')
        method = expected[key]
        require(type(meta.get('methodArity')) is int and meta['methodArity'] == method['arity'] and
                meta.get('parameterTypeFullNames') == method['parameters'] and
                meta.get('returnTypeFullName') == method['return'], 'UC_SIGNATURE')
        require(type(location.get('lineStart')) is int and method['attributeLine'] <= location['lineStart'] <= method['signatureLine'] and
                type(location.get('lineEnd')) is int and location['lineEnd'] == method['endLine'], 'UC_SPAN')
        for field in ('traits', 'properties'):
            if field in row:
                require(type(row[field]) is list and all(type(p) is dict and set(p)=={'key','value'} and
                        type(p['key']) is str and type(p['value']) is str for p in row[field]), 'UC_PROPERTIES')
        counts[key] += 1
        eligibility = method['initialEligibility']
        require(eligibility in ('explicit', 'ordinary', 'unknown'), 'UC_ELIGIBILITY')
        explicit += eligibility == 'explicit'; eligible += eligibility == 'ordinary'; unknown += eligibility == 'unknown'
        rows[uid] = {'native': json.loads(json.dumps(row, ensure_ascii=True, allow_nan=False)), 'sourceMethod': list(key), 'initialEligibility': eligibility}
    require(all(counts.values()), 'UC_MISSING_METHOD')
    cardinality_unknown = 0
    for key, method in expected.items():
        count = method['expectedCases']
        if count is None:
            cardinality_unknown += 1
        else:
            require(type(count) is int and count > 0 and counts[key] == count, 'UC_CARDINALITY')
    canonical = [rows[uid] for uid in sorted(rows)]
    source_counts = {}
    method_counts = []
    for key, count in sorted(counts.items()):
        source_counts[key[0]] = source_counts.get(key[0], 0) + count
        method_counts.append({'source': key[0], 'namespace': key[1], 'className': key[2], 'methodName': key[3], 'count': count})
    return {'rows': canonical, 'summary': {'observed': len(rows), 'unique': len(rows),
            'sourceExplicit': explicit, 'sourceInitialOrdinarySelectionEligible': eligible,
            'sourceEligibilityUnknown': unknown, 'unknownCardinalityMethods': cardinality_unknown,
            'runtimeSkipped': 'notObserved', 'resultEligibility': 'notObserved', 'executionDependencyClosure': 'notObserved',
            'sourceCounts': source_counts, 'methodCounts': method_counts, 'canonicalLedgerSHA256': digest(canonical)}}


def stable(first, second):
    require(first['rows'] == second['rows'], 'UC_UNSTABLE')
    require(not first['summary']['sourceEligibilityUnknown'] and not first['summary']['unknownCardinalityMethods'], 'UC_INCOMPLETE')
    return True


def budget(remaining, requested, commands):
    require(type(commands) is int and 0 <= commands < 64, 'UC_COMMAND_CAP')
    reserve = 16 * (64 - commands)
    require(remaining > reserve, 'UC_BUDGET')
    return min(requested, remaining-reserve)


def verify_catalog(root, catalog):
    for name, expected in catalog['sourcePins'].items():
        path = root / name
        require(not path.is_symlink() and path.is_file() and sha(path) == expected, 'UC_SOURCE_PIN')
    require(catalog['configuration'] == 'Release' and len(catalog['projects']) == 14 and
            len(set(catalog['projects'])) == 14, 'UC_GRAPH')
    actual = {p.relative_to(root).as_posix() for p in (root/'test/SharpLink.UnitTests').rglob('*.cs') if 'obj' not in p.parts and 'bin' not in p.parts}
    require(actual == set(catalog['unitSources']), 'UC_SOURCE_INVENTORY')
    for m in catalog['methods']:
        require(m['source'] in catalog['sourcePins'], 'UC_METHOD_SOURCE')


def generated_tokens(text):
    import re
    text=text.removeprefix('\ufeff');tokens=[];i=0
    pattern=re.compile(r'\s+|//[^\n]*|/\*.*?\*/|[A-Za-z_][A-Za-z_0-9]*|::|=>|[.{}()\[\],;<>:=]',re.S)
    while i<len(text):
        match=pattern.match(text,i);require(match is not None,'UC_GENERATED_SYNTAX')
        token=match[0];i=match.end()
        if token.isspace() or token.startswith(('//','/*')):continue
        tokens.append(token)
    return tokens


def expected_entrypoint():
    return """namespace SharpLink.UnitTests {
    [global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
    internal static class MicrosoftTestingPlatformApplication {
      public static async global::System.Threading.Tasks.Task<int> RunAsync(string[] args) {
        global::Microsoft.Testing.Platform.Builder.ITestApplicationBuilder builder = await global::Microsoft.Testing.Platform.Builder.TestApplication.CreateBuilderAsync(args);
        global::SharpLink.UnitTests.SelfRegisteredExtensions.AddSelfRegisteredExtensions(builder, args);
        using (global::Microsoft.Testing.Platform.Builder.ITestApplication app = await builder.BuildAsync()) { return await app.RunAsync(); }
      }
    }
    [global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
    internal sealed class MicrosoftTestingPlatformEntryPoint {
      public static global::System.Threading.Tasks.Task<int> Main(string[] args) => MicrosoftTestingPlatformApplication.RunAsync(args);
    }
    }"""


def expected_registration(hooks):
    calls='\n'.join('global::'+h['TypeFullName']+'.AddExtensions(builder, args);' for h in hooks)
    return """namespace SharpLink.UnitTests {
    [global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
    [global::Microsoft.CodeAnalysis.Embedded]
    internal static class SelfRegisteredExtensions {
      public static void AddSelfRegisteredExtensions(this global::Microsoft.Testing.Platform.Builder.ITestApplicationBuilder builder, string[] args) {
    """+calls+"""
      }
    }
    }
    namespace Microsoft.CodeAnalysis {
      internal sealed partial class EmbeddedAttribute : global::System.Attribute { }
    }"""


def configuration_neutral(value):
    require(type(value) is dict,'UC_CONFIG_SCHEMA')
    # Flattened JSON keys are case-insensitive configuration paths. Reject both
    # conventional sections and colon/__ spellings, never inspect only one key.
    def visit(node,prefix=''):
        if type(node) is dict:
            for key,child in node.items():
                normalized=(prefix+key).replace('__',':').casefold()
                if normalized.split(':')[0] in ('commandlineoptions','commandlineoptiondefaults'):
                    require(child in ({},[]), 'UC_CONFIG_OPTIONS')
                visit(child,normalized+':')
        elif type(node) is list:
            for index,child in enumerate(node):visit(child,prefix+str(index)+':')
    visit(value)


def discovery_inputs(argv, environment):
    require(len(argv)==4 and argv[2:]==['--list-tests','json'] and all(type(x) is str and not x.startswith('@') for x in argv),'UC_DISCOVERY_ARGV')
    require(Path(argv[0]).is_absolute() and Path(argv[1]).is_absolute(),'UC_DISCOVERY_EXECUTABLE')
    for key in environment:
        normalized=key.replace('__',':').casefold()
        require(normalized.split(':')[0] not in ('commandlineoptions','commandlineoptiondefaults'),'UC_ENV_OPTIONS')


def filter_qualification(root, packages, metadata, catalog, environment=None, imports=None):
    """Bind exact official source/binary registration proof to actual build.

    No CLI filter alone is NOT authority: generated entrypoint, complete hooks,
    neutral effective configuration and byte-selected registration chain are.
    """
    proof=catalog.get('providerProof')
    require(type(proof) is dict and proof.get('reviewed') is True,'UC_PROVIDER_UNPROVEN')
    require(metadata['Properties']['RootNamespace']=='SharpLink.UnitTests' and
            metadata['Properties']['TestingPlatformHostFactory']=='','UC_PROVIDER_ENTRYPOINT')
    require(metadata['Properties']['Language']=='C#' and all(metadata['Properties'][name].lower()=='true' for name in
            ('GenerateTestingPlatformEntryPoint','GenerateTestingPlatformApplicationHelper','GenerateSelfRegisteredExtensions')),'UC_PROVIDER_GENERATION')
    require(metadata['Items'].get('TestingPlatformCommandLineOptionDefault',[])==[],'UC_CONFIG_DEFAULTS')
    for key in (environment or {}):
        normalized=key.replace('__',':').casefold()
        require(normalized.split(':')[0] not in ('commandlineoptions','commandlineoptiondefaults'),'UC_ENV_OPTIONS')
    hooks=[{key:h[key] for key in ('Identity','DisplayName','TypeFullName')} for h in metadata['Items'].get('TestingPlatformBuilderHook',[])]
    require(len(hooks)==4 and len({h['Identity'] for h in hooks})==4 and
            sorted(hooks,key=lambda h:h['Identity'])==sorted(proof['hooks'],key=lambda h:h['Identity']),'UC_PROVIDER_HOOKS')
    # Actual generated calls close evaluation-vs-target mutation, including the
    # TUnit HTML hook substitution. No unreviewed hook or hosted entrypoint.
    obj=root/'test/SharpLink.UnitTests/obj/Release/net10.0'
    generated={'MicrosoftTestingPlatformEntryPoint.cs':expected_entrypoint(),
               'SelfRegisteredExtensions.cs':expected_registration(hooks)}
    compiled=[Path(item['FullPath']) for item in metadata['Items']['Compile']]
    require(len(compiled)==len(set(compiled)), 'UC_COMPILED_DUPLICATE')
    for name,expected in generated.items():
        path=obj/name;file_identity(path)
        require(path in compiled,'UC_GENERATED_NOT_COMPILED')
        require(generated_tokens(stable_read(path)[0].decode('utf-8-sig'))==generated_tokens(expected),'UC_GENERATED_CALLS')
    verified=[]
    for item in proof['files']:
        if 'basename' in item:
            container=packages/item['packageRoot']
            require(not Path(item['packageRoot']).is_absolute() and '..' not in Path(item['packageRoot']).parts,'UC_PROVIDER_PATH')
            paths=list(container.rglob(item['basename']))
            hashes={file_identity(path)[-1] for path in paths}
            require(paths and hashes<=set(item['allowedSHA256']) and set(item['requiredSHA256'])<=hashes,'UC_PROVIDER_BYTES')
            require(imports is not None,'UC_PROVIDER_IMPORTS')
            used={file_identity(path)[-1] for path in paths if path in imports}
            require(set(item['requiredSHA256'])<=used,'UC_PROVIDER_IMPORTS')
            verified.extend(sorted(hashes))
        else:
            path=packages/item['path']
            require(not Path(item['path']).is_absolute() and '..' not in Path(item['path']).parts,'UC_PROVIDER_PATH')
            require(file_identity(path)[-1]==item['sha256'],'UC_PROVIDER_BYTES')
            if item.get('mustBeImported'):
                require(imports is not None and path in imports,'UC_PROVIDER_IMPORTS')
            verified.append(item['sha256'])
    require(verified and proof.get('effectiveDiscoveryFilter')=='non-narrowing','UC_PROVIDER_UNPROVEN')
    output=root/'test/SharpLink.UnitTests/bin/Release/net10.0'
    require(file_identity(output/'Microsoft.Testing.Extensions.CodeCoverage.dll')[-1]==proof['codeCoverageStaticIL']['assemblySHA256'],'UC_PROVIDER_SELECTED_ASSET')
    config=output/'SharpLink.UnitTests.testconfig.json'
    if config.exists() or config.is_symlink():
        file_identity(config);configuration_neutral(read_json(config))
    # argv is constructed by the runner with exactly --list-tests json. No @
    # response/config/dynamic option; official initial parser returns before any
    # DynamicExtensionLoader manifest search. Output/config identity is sealed.
    return {'unfilteredQualified':True,'proofSHA256':digest(proof)}


def package_folder_identity(folders, restored_path, packages, expected_identity):
    require(type(folders) is dict and len(folders)==1,'UC_PACKAGE_FOLDERS')
    for spelling in [restored_path,*folders]:
        require(type(spelling) is str,'UC_PACKAGE_FOLDERS')
        candidate=Path(spelling)
        require(candidate.is_absolute() and '..' not in candidate.parts and candidate==packages and
                candidate.resolve()==candidate and not candidate.is_symlink(),'UC_PACKAGE_FOLDERS')
        require(legacy.directory_identity_strict(candidate)==expected_identity,'UC_PACKAGE_DIRECTORY_ID')


def dependencies(root, packages, catalog, package_identity=None):
    """Bind actual Unit14 assets, official archive extraction and runtime bytes."""
    import base64
    import xml.etree.ElementTree as ET
    from zipfile import ZipFile
    from io import BytesIO
    expected = {str(Path(p).parent/'obj/project.assets.json') for p in catalog['projects']}
    require({p.relative_to(root).as_posix() for p in root.rglob('project.assets.json')} == expected, 'UC_ASSETS_GRAPH')
    archives = {}; documents = {}
    for archive in sorted(packages.glob('*/*/*.nupkg')):
        folder = archive.parent
        meta = read_json(folder/'.nupkg.metadata')
        require(meta.get('source') == 'https://api.nuget.org/v3/index.json', 'UC_PACKAGE_ORIGIN')
        require(base64.b64decode(stable_read(archive.with_suffix('.nupkg.sha512'))[0].decode().strip(), validate=True) ==
                hashlib.sha512(stable_read(archive)[0]).digest(), 'UC_ARCHIVE_HASH')
        with ZipFile(BytesIO(stable_read(archive)[0])) as zipped:
            entries = [n for n in zipped.namelist() if not n.endswith('/')]
            names = {n.lower(): n for n in entries}
            require(len(names) == len(entries) and all(not Path(n).is_absolute() and '..' not in Path(n).parts and '\\' not in n for n in names), 'UC_ARCHIVE_NAMES')
            nuspec = [n for n in names if n.endswith('.nuspec') and '/' not in n]
            require(len(nuspec)==1, 'UC_NUSPEC')
            fields = {e.tag.split('}')[-1]: e.text for e in ET.fromstring(zipped.read(names[nuspec[0]])).iter() if e.tag.split('}')[-1] in ('id','version')}
            require(fields['id'].lower()==folder.parent.name and fields['version'].lower()==folder.name, 'UC_PACKAGE_ID')
            expected_commit=catalog.get('providerProof',{}).get('packageCommits',{}).get(folder.relative_to(packages).as_posix())
            if expected_commit is not None:
                repos=[e for e in ET.fromstring(zipped.read(names[nuspec[0]])).iter() if e.tag.split('}')[-1]=='repository']
                require(len(repos)==1 and repos[0].attrib.get('commit')==expected_commit,'UC_PACKAGE_COMMIT')
            for path in folder.rglob('*'):
                if path.is_dir():
                    require(not path.is_symlink(), 'UC_PACKAGE_KIND'); continue
                file_identity(path)
                if path in (archive, archive.with_suffix('.nupkg.sha512'), folder/'.nupkg.metadata'):
                    continue
                key = path.relative_to(folder).as_posix().lower()
                require(key in names and stable_read(path)[0]==zipped.read(names[key]), 'UC_PACKAGE_EXTRACTION')
        archives[folder.relative_to(packages).as_posix()] = meta
    for path in packages.rglob('*'):
        require(any(path.is_relative_to(packages/n) or path.is_dir() and (packages/n).is_relative_to(path) for n in archives), 'UC_ORPHAN_PACKAGE')
    for name in sorted(expected):
        doc = read_json(root/name); documents[name]=doc
        restore=doc['project']['restore']
        require(set(restore['sources'])=={'https://api.nuget.org/v3/index.json'},'UC_RESTORE_ORIGIN')
        package_folder_identity(doc['packageFolders'],restore['packagesPath'],packages,package_identity)
        project = root/Path(name).parent.parent/(Path(name).parent.parent.name+'.csproj')
        require(Path(restore['projectPath']) in {root/p for p in catalog['projects']} and
                Path(restore['projectPath']).parent == (root/name).parent.parent, 'UC_RESTORE_PROJECT')
        for identity, library in doc['libraries'].items():
            if library['type']=='package':
                key=library['path'];require(key in archives, 'UC_PACKAGE_MISSING')
                require(library['sha512'].removeprefix('sha512-')==archives[key]['contentHash'].removeprefix('sha512-'), 'UC_PACKAGE_CANONICAL_HASH')
                if identity.startswith('TUnit/') or identity.startswith('TUnit.'):
                    require(identity.rsplit('/',1)[1]=='1.73.19', 'UC_TUNIT_VERSION')
    unit=documents[str(Path(catalog['project']).parent/'obj/project.assets.json')]
    require('TUnit/1.73.19' in unit['libraries'] and 'Microsoft.Testing.Platform/2.5.1' in unit['libraries'], 'UC_FRAMEWORK_VERSION')
    return unit


def runtime_identity(root, packages, catalog, assets, inventory_facts=None):
    output=root/Path(catalog['project']).parent/'bin/Release/net10.0'
    deps=read_json(output/'SharpLink.UnitTests.deps.json')
    target=deps['targets'][deps['runtimeTarget']['name']]
    restored=assets['targets']['net10.0']; known={'SharpLink.UnitTests.dll'};resource_destinations=set()
    for identity, item in target.items():
        library=deps['libraries'][identity]
        if library['type']=='package':
            require(identity in restored and identity in assets['libraries'], 'UC_DEPS_PACKAGE')
            folder=packages/assets['libraries'][identity]['path']
            for kind in ('runtime','native','runtimeTargets'):
                for relative, entry in item.get(kind,{}).items():
                    require(not Path(relative).is_absolute() and '..' not in Path(relative).parts and '\\' not in relative and relative in restored[identity].get(kind,{}), 'UC_DEPS_ASSET')
                    if Path(relative).name=='_._':continue
                    if kind=='runtimeTargets':
                        expected=restored[identity][kind][relative]
                        require(entry.get('rid')==expected.get('rid') and entry.get('assetType')==expected.get('assetType') and
                                set(entry)<=set(expected)|{'fileVersion','assemblyVersion'}, 'UC_DEPS_RID')
                        dest=relative
                    else:dest=Path(relative).name
                    require(dest not in resource_destinations,'UC_RESOURCE_COLLISION')
                    require(file_identity(output/dest)[-1]==file_identity(folder/relative)[-1], 'UC_RUNTIME_BYTES')
                    known.add(dest)
            # SDK ResolvePackageAssets copies restore `resource` assets into
            # locale/basename; DependencyContextBuilder writes deps `resources`.
            # Require exact declarations and bytes, never accept a DLL glob.
            for relative,entry in item.get('resources',{}).items():
                relative_path=Path(relative)
                require(not relative_path.is_absolute() and '..' not in relative_path.parts and
                        '\\' not in relative and relative_path.name.endswith('.resources.dll'),'UC_RESOURCE_PATH')
                expected=restored[identity].get('resource',{}).get(relative)
                require(type(entry) is dict and set(entry)=={'locale'} and entry==expected,'UC_RESOURCE_METADATA')
                locale=entry['locale']
                import re
                require(type(locale) is str and re.fullmatch(r'[A-Za-z0-9]+(?:-[A-Za-z0-9]+)*',locale) is not None,'UC_RESOURCE_LOCALE')
                dest=locale+'/'+relative_path.name
                require(dest not in known,'UC_RESOURCE_COLLISION')
                require(file_identity(output/dest)[-1]==file_identity(folder/relative)[-1],'UC_RESOURCE_BYTES')
                known.add(dest);resource_destinations.add(dest)
        elif library['type']=='project':
            for relative in item.get('runtime',{}):
                require(not Path(relative).is_absolute() and '..' not in Path(relative).parts and '\\' not in relative, 'UC_DEPS_PROJECT_PATH')
                require(relative not in resource_destinations,'UC_RESOURCE_COLLISION')
                if relative=='SharpLink.UnitTests.dll':continue
                matches=[root/Path(p).parent/'bin/Release/net10.0'/relative for p in catalog['projects']]
                matches=[p for p in matches if p.exists()]
                require(matches and all(sha(p)==sha(output/relative) for p in matches), 'UC_RUNTIME_PROJECT')
                known.add(relative)
        else:require(False, 'UC_DEPS_KIND')
    actual_dlls={p.relative_to(output).as_posix() for p in output.rglob('*.dll')}
    expected_dlls={p for p in known if p.endswith('.dll')}
    if inventory_facts is not None:
        inventory_facts.update({'actualDllCount':len(actual_dlls),'expectedDllCount':len(expected_dlls),
            'missingCount':len(expected_dlls-actual_dlls),'extraCount':len(actual_dlls-expected_dlls),
            'actualPathsSHA256':digest(sorted(actual_dlls)),'expectedPathsSHA256':digest(sorted(expected_dlls)),
            'missingPathsSHA256':digest(sorted(expected_dlls-actual_dlls)),'extraPathsSHA256':digest(sorted(actual_dlls-expected_dlls))})
    require(actual_dlls==expected_dlls, 'UC_RUNTIME_INVENTORY')
    config=read_json(output/'SharpLink.UnitTests.runtimeconfig.json')['runtimeOptions']
    require(config['tfm']=='net10.0' and config['framework']=={'name':'Microsoft.NETCore.App','version':'10.0.0'}, 'UC_RUNTIME_CONFIG')
    return output


def import_origins(path, source, packages, sdk, known):
    """Exact reviewed MSBuild preprocessor header grammar, not arbitrary comments.

    Input paths are resolved only against the sealed source/package/SDK table;
    '--' comment escaping is reversed by a unique known-file match.
    """
    import re
    import xml.etree.ElementTree as ET
    text=stable_read(path)[0].decode('utf-8-sig')
    require(not any(e.tag.split('}')[-1]=='Import' for e in ET.fromstring(text).iter()),'UC_UNEXPANDED_IMPORT')
    found={}
    for comment in re.findall(r'<!--(.*?)-->',text,re.S):
        lines=comment.strip().splitlines()
        if len(lines)<3 or lines[0]!='='*140 or lines[-1]!='='*140:continue
        middle=lines[1:-1];candidate=Path(middle[-1])
        valid=(len(middle)==1 or len(middle)==3 and middle[:2]==['  </Import>',''] or
               len(middle) in (3,4) and middle[0].startswith('  <Import Project="') and middle[0].endswith('>') and
               middle[-2]=='' and (len(middle)==3 or middle[1].startswith('  This import was added implicitly because ')))
        if not valid or not candidate.is_absolute():continue
        containers=[base for base in (source,packages,sdk) if candidate.is_relative_to(base)]
        require(containers,'UC_IMPORT_CONTAINER');base=max(containers,key=lambda p:len(p.parts))
        normalized=os.path.normpath(str(candidate));require(Path(normalized).is_relative_to(base),'UC_IMPORT_ESCAPE')
        matches=[p for p in known if str(p).replace('--','__')==normalized or str(p)==normalized]
        require(len(matches)==1,'UC_IMPORT_IDENTITY');actual=matches[0]
        require(actual.is_relative_to(base),'UC_IMPORT_ESCAPE')
        # Entire known containers were previously traversed with lstat. Check the
        # actual ancestry again, including every pre-collapse spelling component.
        current=base;stack=[]
        for part in candidate.relative_to(base).parts:
            if part=='..':
                require(stack,'UC_IMPORT_ESCAPE');stack.pop();continue
            if part=='.':continue
            stack.append(part);spelled=base.joinpath(*stack)
            if spelled.exists() or spelled.is_symlink():
                mode=spelled.lstat().st_mode;require(stat.S_ISDIR(mode) or stat.S_ISREG(mode),'UC_IMPORT_KIND')
            else:
                require(str(spelled) in {str(p).replace('--','__') for p in (actual,*actual.parents)},'UC_IMPORT_SPELLING')
        for part in actual.relative_to(base).parts:
            current=current/part;mode=current.lstat().st_mode
            require(stat.S_ISREG(mode) if current==actual else stat.S_ISDIR(mode),'UC_IMPORT_KIND')
        require(file_identity(actual)==known[actual],'UC_IMPORT_CHANGED');found[actual]=known[actual]
    require(found and source/'test/SharpLink.UnitTests/SharpLink.UnitTests.csproj' in found and any('Microsoft.NET.Sdk' in str(p) for p in found),'UC_IMPORT_MISSING')
    return found


def project_metadata(root, name, value, catalog):
    expected=catalog['projectMetadata'][name];props=value['Properties']
    require(props['MSBuildProjectFullPath']==str(root/name) and props['Configuration']=='Release' and
            props.get('PublishAot','').lower() in ('','false') and props['TargetFramework']==expected['targetFramework'] and props['AssemblyName']==expected['assemblyName'] and
            props['TargetPath']==str(root/expected['output']),'UC_PROJECT_PROPERTIES')
    refs=[Path(item['FullPath']) for item in value['Items'].get('ProjectReference',[])]
    require(len(refs)==len(set(refs)) and set(refs)=={root/p for p in expected['references']},'UC_GRAPH_EDGES')
    constants={part for part in props['DefineConstants'].split(';') if part}
    standard={'NET','TRACE','RELEASE','NETSTANDARD','NETSTANDARD2_0','SHARPLINK_GENERATOR'}|{'NETSTANDARD'+v+'_OR_GREATER' for v in ('1_0','1_1','1_2','1_3','1_4','1_5','1_6','2_0')}
    core={'NET','TRACE','RELEASE','NETCOREAPP','NET10_0'}|{'NET'+str(v)+'_0_OR_GREATER' for v in range(5,11)}|{'NETCOREAPP'+v+'_OR_GREATER' for v in ('1_0','1_1','2_0','2_1','2_2','3_0','3_1')}
    require(set(expected['requiredConstants'])<=constants<= (standard if expected['targetFramework']=='netstandard2.0' else core),'UC_RELEASE_CONSTANTS')
    return {'constants':sorted(constants),'references':sorted(expected['references']),'targetFramework':expected['targetFramework']}


# Exact official TUnit.Core 1.73.19 package source, not a generated test body.
# https://github.com/thomhurst/TUnit/blob/ff2dc5186b05eddc8f6aea03a18784df3aa6e737/src/TUnit.Core/TUnit.Core.props
TUNIT_CORE_ARCHIVE_SHA256='d74daa05bb1467612e48435228ae3e7e77fc3f62eaeb8c5755c116620b1c55ec'
TUNIT_CORE_PROPS_SHA256='caa0fc63c20cc5b05a0857090b76273edd67f941f72e460ba1aefc2918ca4961'
TUNIT_CORE_NAMESPACE_SHA256='d05ade924c6f7728fe7bfa9eaae17da7963f63d3d3ed0b4f091b45a9fac66002'
TUNIT_CORE_SOURCE_MEMBER='buildTransitive/net10.0/TUnit.Core.GeneratedNamespace.cs'
TUNIT_CORE_PROPS_MEMBER='buildTransitive/net10.0/TUnit.Core.props'


def package_compile_proof():
    return {'package':'tunit.core/1.73.19','archiveSHA256':TUNIT_CORE_ARCHIVE_SHA256,
            'sourceMember':TUNIT_CORE_SOURCE_MEMBER,'sourceSHA256':TUNIT_CORE_NAMESPACE_SHA256,
            'propsMember':TUNIT_CORE_PROPS_MEMBER,'propsSHA256':TUNIT_CORE_PROPS_SHA256,
            'sourceCommit':'ff2dc5186b05eddc8f6aea03a18784df3aa6e737'}


def package_compile_inputs(root, packages, metadata, imports=None):
    """The one official package source must be exact, unique and first.

    Pre-build evaluation binds official archive members and actual Compile input;
    before Build the expanded import set must also prove the matching props used.
    Whole package extraction and all ancestors are independently sealed by caller.
    """
    from zipfile import ZipFile
    from io import BytesIO
    folder=packages/'tunit.core/1.73.19'
    source=folder/TUNIT_CORE_SOURCE_MEMBER;props=folder/TUNIT_CORE_PROPS_MEMBER
    archive=folder/'tunit.core.1.73.19.nupkg'
    paths=[Path(item['FullPath']) for item in metadata['Items']['Compile']]
    require(paths and paths[0]==source and len(paths)==len(set(paths)) and
            {path for path in paths if not path.is_relative_to(root)}=={source},'UC_PACKAGE_COMPILE_SET')
    raw=stable_read(archive)[0]
    require(hashlib.sha256(raw).hexdigest()==TUNIT_CORE_ARCHIVE_SHA256,'UC_PACKAGE_COMPILE_BYTES')
    with ZipFile(BytesIO(raw)) as zipped:
        for member,path,sha in ((TUNIT_CORE_SOURCE_MEMBER,source,TUNIT_CORE_NAMESPACE_SHA256),
                                (TUNIT_CORE_PROPS_MEMBER,props,TUNIT_CORE_PROPS_SHA256)):
            require(zipped.namelist().count(member)==1,'UC_PACKAGE_COMPILE_BYTES')
            data=stable_read(path)[0]
            require(data==zipped.read(member) and hashlib.sha256(data).hexdigest()==sha,'UC_PACKAGE_COMPILE_BYTES')
    if imports is not None:require(props in imports,'UC_PACKAGE_COMPILE_IMPORT')
    return {source:'package:tunit.core/1.73.19/'+TUNIT_CORE_SOURCE_MEMBER}


GLOBAL_USINGS_PATH='test/SharpLink.UnitTests/obj/Release/net10.0/SharpLink.UnitTests.GlobalUsings.g.cs'
GLOBAL_USINGS_TEXT="""global using TUnit.Assertions;
global using TUnit.Assertions.Extensions;
global using TUnit.Core;
global using static TUnit.Core.HookType;
"""


def global_using_proof():
    return {'sdkTarget':'sdk/10.0.114/Sdks/Microsoft.NET.Sdk/targets/Microsoft.NET.GenerateGlobalUsings.targets',
            'sdkTargetSHA256':'ecde6efe033ff19f05aeaedef96a1e34ee7595c48c68b4bf9299fedb413049bb',
            'packageFiles':{
                'tunit.core/1.73.19/buildTransitive/net10.0/TUnit.Core.targets':'f1627607a5cbf99f1b64efb0bab78a0f206eb99d284958ea86b0a10a41acca3b',
                'tunit.assertions/1.73.19/buildTransitive/net10.0/TUnit.Assertions.props':'0d7a4d33688e9a7a1db77cbff3531471dd3949e8928874636850c883dbabca35',
                'tunit.assertions/1.73.19/buildTransitive/net10.0/TUnit.Assertions.targets':'4819cec7dc7d11ed36323ba07cdda7a6466f679507096257905457f75fe5765c'},
            'generatedSource':GLOBAL_USINGS_PATH}


def global_using_rows(metadata):
    rows=metadata['Items'].get('Using',[]);actual=[]
    for row in rows:
        static=row.get('Static','').lower();alias=row.get('Alias','')
        require(static in ('','false','true') and alias=='','UC_GLOBAL_USINGS_ITEMS')
        actual.append((row['Identity'],static=='true'))
    require(sorted(actual)==[('TUnit.Assertions',False),('TUnit.Assertions.Extensions',False),
                             ('TUnit.Core',False),('TUnit.Core.HookType',True)],'UC_GLOBAL_USINGS_ITEMS')


def global_using_qualification(root, packages, sdkroot, metadata, imports, catalog):
    proof=global_using_proof()
    require(catalog.get('globalUsingProof')==proof,'UC_GLOBAL_USINGS_PROOF')
    global_using_rows(metadata)
    files={sdkroot/proof['sdkTarget']:proof['sdkTargetSHA256']}
    files.update({packages/name:sha for name,sha in proof['packageFiles'].items()})
    for path,expected in files.items():
        require(path in imports and file_identity(path)[-1]==expected,'UC_GLOBAL_USINGS_PROOF')
    path=root/GLOBAL_USINGS_PATH
    require(sum(Path(row['FullPath'])==path for row in metadata['Items']['Compile'])==1,'UC_GLOBAL_USINGS_SOURCE')
    require(generated_tokens(stable_read(path)[0].decode('utf-8-sig'))==generated_tokens(GLOBAL_USINGS_TEXT),'UC_GLOBAL_USINGS_SOURCE')


MTP_ASSEMBLY_MARKER='[assembly: System.Reflection.AssemblyMetadata("Microsoft.Testing.Platform.Application", "true")]'


def assembly_metadata_proof():
    return {'sdkTarget':'sdk/10.0.114/Sdks/Microsoft.NET.Sdk/targets/Microsoft.NET.GenerateAssemblyInfo.targets',
            'sdkTargetSHA256':'aed5db2d53cb2517edb7cae09dfd543b177ae733d5331536bd044d772bad18e8',
            'packageTarget':'microsoft.testing.platform/2.5.1/buildMultiTargeting/Microsoft.Testing.Platform.targets',
            'packageTargetSHA256':'00eb786984026f49c3b086450d3b2cc06368293f1c2b3518c4188dd024ad99a4'}


def assembly_metadata_qualification(packages, sdkroot, metadata, imports, catalog):
    proof=assembly_metadata_proof()
    require(catalog.get('assemblyMetadataProof')==proof,'UC_ASSEMBLY_METADATA_PROOF')
    rows=metadata['Items'].get('AssemblyMetadata',[])
    require(len(rows)==1 and rows[0].get('Identity')=='Microsoft.Testing.Platform.Application' and
            rows[0].get('Value')=='true','UC_ASSEMBLY_METADATA_ITEMS')
    for path,expected in ((sdkroot/proof['sdkTarget'],proof['sdkTargetSHA256']),
                          (packages/proof['packageTarget'],proof['packageTargetSHA256'])):
        require(path in imports and file_identity(path)[-1]==expected,'UC_ASSEMBLY_METADATA_PROOF')


def compiled_inputs(root, value, catalog, package_sources=None):
    """Complete post-Build MSBuild Compile item ledger, not only bootstrap files.

    This is not an inventory of Roslyn internal source-generator syntax trees.
    Those remain bound by the original source/analyzer/package/output identities.
    """
    paths=[Path(item['FullPath']) for item in value['Items']['Compile']]
    package_sources=package_sources or {}
    expected={root/p for p in catalog['unitSources']+catalog['linkedSources']+catalog['generatedCompile']}|set(package_sources)
    require(len(paths)==len(set(paths)) and set(paths)==expected,'UC_POST_COMPILE_SET')
    rows={}
    for path in paths:
        require(path.is_absolute() and '..' not in path.parts and (path.is_relative_to(root) or path in package_sources),'UC_COMPILE_PATH')
        key=package_sources[path] if path in package_sources else path.relative_to(root).as_posix()
        rows[key]=file_identity(path)
    # SDK assembly metadata and the separately checked GlobalUsings source are
    # the only non-bootstrap generated Compile. Exact metadata is checked below;
    # any unknown generator file (even beside valid entrypoints) is rejected.
    attrs=root/'test/SharpLink.UnitTests/obj/Release/net10.0/.NETCoreApp,Version=v10.0.AssemblyAttributes.cs'
    text=stable_read(attrs)[0].decode('utf-8-sig')
    import re
    compact=re.sub(r'\s+','',re.sub(r'//[^\n]*','',text))
    require(compact=='usingSystem;usingSystem.Reflection;[assembly:global::System.Runtime.Versioning.TargetFrameworkAttribute(".NETCoreApp,Version=v10.0",FrameworkDisplayName=".NET10.0")]', 'UC_FRAMEWORK_ATTRIBUTES')
    info=root/'test/SharpLink.UnitTests/obj/Release/net10.0/SharpLink.UnitTests.AssemblyInfo.cs'
    text=stable_read(info)[0].decode('utf-8-sig')
    # This file may contain SDK-selected informational/source revision metadata,
    # but cannot introduce executable types/methods or arbitrary attributes.
    lines=[line.strip() for line in text.splitlines() if line.strip() and not line.lstrip().startswith('//')]
    allowed={'AssemblyCompanyAttribute','AssemblyConfigurationAttribute','AssemblyCopyrightAttribute','AssemblyDescriptionAttribute','AssemblyFileVersionAttribute','AssemblyInformationalVersionAttribute','AssemblyProductAttribute','AssemblyTitleAttribute','AssemblyVersionAttribute','AssemblyMetadataAttribute'}
    require(lines and all(line in ('using System;','using System.Reflection;',MTP_ASSEMBLY_MARKER) or
        (match:=re.fullmatch(r'\[assembly: System\.Reflection\.(\w+)\(("(?:[^"\\]|\\.)*"(?:, "(?:[^"\\]|\\.)*")*)\)\]',line)) is not None and match[1] in allowed for line in lines),'UC_ASSEMBLY_INFO')
    if 'assemblyMetadataProof' in catalog:
        require(lines.count(MTP_ASSEMBLY_MARKER)==1,'UC_ASSEMBLY_METADATA_ITEMS')
    require('[assembly: System.Reflection.AssemblyConfigurationAttribute("Release")]' in lines and
            '[assembly: System.Reflection.AssemblyVersionAttribute("3.0.0.0")]' in lines,'UC_ASSEMBLY_ID')
    return rows


PUBLIC_REJECTION_CODES = frozenset(('UC_RESOURCE_PATH', 'UC_RESOURCE_METADATA', 'UC_RESOURCE_LOCALE', 'UC_RESOURCE_COLLISION', 'UC_RESOURCE_BYTES', 'UC_ASSEMBLY_METADATA_PROOF', 'UC_ASSEMBLY_METADATA_ITEMS', 'UC_GLOBAL_USINGS_ITEMS', 'UC_GLOBAL_USINGS_PROOF', 'UC_GLOBAL_USINGS_SOURCE', 'UC_PACKAGE_COMPILE_SET', 'UC_PACKAGE_COMPILE_BYTES', 'UC_PACKAGE_COMPILE_IMPORT', 'UC_ANCESTOR_OVERRIDE', 'UC_ARCHIVE_HASH', 'UC_ARCHIVE_NAMES', 'UC_ARGUMENTS', 'UC_ASSEMBLY', 'UC_ASSEMBLY_ID', 'UC_ASSEMBLY_INFO', 'UC_ASSETS_GRAPH', 'UC_BUDGET', 'UC_CARDINALITY', 'UC_CATALOG_METHODS', 'UC_CLONE_BYTES', 'UC_CLONE_HEAD', 'UC_COMMAND_CAP', 'UC_COMPILED_DUPLICATE', 'UC_COMPILE_FOREIGN', 'UC_COMPILE_PATH', 'UC_COMPILE_SET', 'UC_COMPLETED_LOG_MUTATION', 'UC_CONFIG_DEFAULTS', 'UC_CONFIG_OPTIONS', 'UC_CONFIG_SCHEMA', 'UC_DEPS_ASSET', 'UC_DEPS_KIND', 'UC_DEPS_PACKAGE', 'UC_DEPS_PROJECT_PATH', 'UC_DEPS_RID', 'UC_DIRECTORY', 'UC_DIRTY', 'UC_DISCOVERY_ARGV', 'UC_DISCOVERY_EXECUTABLE', 'UC_DISPLAY', 'UC_ELIGIBILITY', 'UC_EMIT_BUDGET', 'UC_ENV_OPTIONS', 'UC_EVIDENCE_MEMBERS', 'UC_FAILED', 'UC_FILE_KIND', 'UC_FINAL_BUDGET', 'UC_FOREIGN_METHOD', 'UC_FRAMEWORK_ATTRIBUTES', 'UC_FRAMEWORK_VERSION', 'UC_GENERATED_CALLS', 'UC_GENERATED_NOT_COMPILED', 'UC_GENERATED_SYNTAX', 'UC_GIT_ID', 'UC_GRAPH', 'UC_GRAPH_EDGES', 'UC_IMPORT_CHANGED', 'UC_IMPORT_CONTAINER', 'UC_IMPORT_ESCAPE', 'UC_IMPORT_FOREIGN', 'UC_IMPORT_IDENTITY', 'UC_IMPORT_KIND', 'UC_IMPORT_MISSING', 'UC_IMPORT_PATH', 'UC_IMPORT_SPELLING', 'UC_INCOMPLETE', 'UC_INPUT_CHANGED', 'UC_JSON_NUMBER', 'UC_JSON_SIZE', 'UC_METADATA', 'UC_METHOD_SOURCE', 'UC_MISSING_METHOD', 'UC_NODE_ALIAS', 'UC_NODE_KIND', 'UC_NUSPEC', 'UC_ORPHAN_PACKAGE', 'UC_OS', 'UC_PACKAGE_CANONICAL_HASH', 'UC_PACKAGE_COMMIT', 'UC_PACKAGE_DIRECTORY_ID', 'UC_PACKAGE_EXTRACTION', 'UC_PACKAGE_FOLDERS', 'UC_PACKAGE_ID', 'UC_PACKAGE_KIND', 'UC_PACKAGE_MISSING', 'UC_PACKAGE_ORIGIN', 'UC_POST_COMPILE_SET', 'UC_PRIVATE_NAME', 'UC_PROJECT_PROPERTIES', 'UC_PROJECT_REFERENCE', 'UC_PROPERTIES', 'UC_PROVIDER_BYTES', 'UC_PROVIDER_ENTRYPOINT', 'UC_PROVIDER_GENERATION', 'UC_PROVIDER_HOOKS', 'UC_PROVIDER_IMPORTS', 'UC_PROVIDER_PATH', 'UC_PROVIDER_SELECTED_ASSET', 'UC_PROVIDER_UNPROVEN', 'UC_READ_CHANGED', 'UC_READ_LENGTH', 'UC_READ_PARENT', 'UC_READ_PARENT_CHANGED', 'UC_READ_PARENT_RACE', 'UC_READ_PATH', 'UC_READ_REPLACED', 'UC_RELEASE_CONSTANTS', 'UC_RESTORE_ORIGIN', 'UC_RESTORE_PROJECT', 'UC_ROW', 'UC_RUNTIME_BYTES', 'UC_RUNTIME_CONFIG', 'UC_RUNTIME_INVENTORY', 'UC_RUNTIME_LOCATION', 'UC_RUNTIME_PROJECT', 'UC_RUNTIME_VERSION', 'UC_SCAN_CHANGED', 'UC_SCAN_FILE_CHANGED', 'UC_SCHEMA', 'UC_SDK_MISSING', 'UC_SDK_VERSION', 'UC_SEAL', 'UC_SIGNATURE', 'UC_SOURCE_INVENTORY', 'UC_SOURCE_MEMBERS', 'UC_SOURCE_PATH', 'UC_SOURCE_PIN', 'UC_SPAN', 'UC_SUMMARY_BUDGET', 'UC_TOTAL_BUDGET', 'UC_TRACKED', 'UC_TUNIT_VERSION', 'UC_TYPE', 'UC_UID', 'UC_UNEXPANDED_IMPORT', 'UC_UNSTABLE'))


def public_rejections(errors):
    """Only finite reviewed codes and counts; private graph stays authoritative."""
    queue=list(errors);seen=set();codes=set();unknown=0
    while queue:
        error=queue.pop()
        if not isinstance(error,BaseException) or id(error) in seen:continue
        seen.add(id(error))
        if type(error) is Rejected and type(error.code) is str and error.code in PUBLIC_REJECTION_CODES:
            codes.add(error.code)
        elif not isinstance(error,BaseExceptionGroup):unknown+=1
        if isinstance(error,BaseExceptionGroup):queue.extend(error.exceptions)
        if error.__cause__ is not None:queue.append(error.__cause__)
        if error.__context__ is not None:queue.append(error.__context__)
    return {"codes":sorted(codes),"uniqueExceptionNodes":len(seen),"unclassifiedExceptionNodes":unknown}


def metadata_facts(root, name, value, catalog):
    """Diagnostic only. Never validate/relax input or export arbitrary names.

    Returned labels are literals or the already pinned original project path.
    Values from native metadata appear only as booleans, cardinalities or hashes.
    """
    require(name in catalog['projectMetadata'],'UC_GRAPH')
    properties=value.get('Properties',{}) if type(value) is dict else {}
    items=value.get('Items',{}) if type(value) is dict else {}
    properties=properties if type(properties) is dict else {}
    items=items if type(items) is dict else {}
    expected=catalog['projectMetadata'][name]
    expected_properties={'MSBuildProjectFullPath':str(root/name),'Configuration':'Release',
        'TargetFramework':expected['targetFramework'],'AssemblyName':expected['assemblyName'],
        'TargetPath':str(root/expected['output'])}
    facts={'knownProject':name,'properties':{key:{'present':key in properties,'equal':properties.get(key)==target,
        'actualSHA256':digest(properties.get(key))} for key,target in expected_properties.items()}}
    constants=properties.get('DefineConstants')
    actual_constants={part for part in constants.split(';') if part} if type(constants) is str else set()
    required=set(expected['requiredConstants'])
    facts['constants']={'string':type(constants) is str,'count':len(actual_constants),
        'requiredMissingCount':len(required-actual_constants),'hasDebug': 'DEBUG' in actual_constants,
        'actualSHA256':digest(constants),'sortedSetSHA256':digest(sorted(actual_constants))}
    facts['publishAot']={'present':'PublishAot' in properties,'normal':type(properties.get('PublishAot')) is str and properties['PublishAot'].lower() in ('','false'), 'actualSHA256':digest(properties.get('PublishAot'))}
    for kind,expect in [('ProjectReference',{str(root/p) for p in expected['references']}),
                         ('Compile',{str(root/p) for p in catalog['unitSources']+catalog['linkedSources']} if name==catalog['project'] else None)]:
        rows=items.get(kind)
        paths=[row.get('FullPath') for row in rows if type(row) is dict] if type(rows) is list else []
        strings=[p for p in paths if type(p) is str];actual=set(strings)
        fact={'list':type(rows) is list,'rowCount':len(rows) if type(rows) is list else None,
            'pathStringCount':len(strings),'uniquePathCount':len(actual),'duplicatePathCount':len(strings)-len(actual),
            'actualPathsSHA256':digest(sorted(actual))}
        if expect is not None:fact.update(expectedCount=len(expect),missingCount=len(expect-actual),extraCount=len(actual-expect))
        if kind=='Compile' and name==catalog['project']:
            generated={str(root/p):p for p in catalog['generatedCompile']}
            package=str(root.parent/'packages/tunit.core/1.73.19'/TUNIT_CORE_SOURCE_MEMBER)
            fact['knownGeneratedPresent']={label:path in actual for path,label in generated.items()}
            fact['knownPackageSourcePresent']=package in actual
            fact['unknownExtraCount']=len(actual-expect-set(generated)-{package})
        facts[kind]=fact
    imports=properties.get('MSBuildAllProjects')
    facts['imports']={'string':type(imports) is str,'count':len([p for p in imports.split(';') if p]) if type(imports) is str else None,'actualSHA256':digest(imports)}
    return facts
