#!/usr/bin/env python3
"""Exact TUnit evidence; official SDK NuGet content hash, never a Python ZIP hash emulation."""
import argparse, base64, hashlib, json, os, shutil, zipfile
from pathlib import Path, PurePosixPath
import xml.etree.ElementTree as ET
import process_host as host
from verify_request import strict_json

ROOT = Path(__file__).resolve().parent
SDK = '10.0.114'
PACKAGE_ID = 'TUnit.Core/1.73.19'
PACKAGE_PATH = 'tunit.core/1.73.19'
NUPKG = 'tunit.core.1.73.19.nupkg'
MEMBER = 'buildTransitive/net10.0/TUnit.Core.GeneratedNamespace.cs'
PROPS = 'buildTransitive/net10.0/TUnit.Core.props'
KEY = 'nuget:' + PACKAGE_ID + '/' + MEMBER
METHOD = 'NuGet.Packaging.PackageArchiveReader.GetContentHash(System.Threading.CancellationToken,System.Func<System.String>)'
EXPECTED_PROJECTS = {'test/' + name + '/' + name + '.csproj' for name in (
    'SharpLink.Generator.Tests', 'SharpLink.IntegrationTests', 'SharpLink.LoadTest.Tests',
    'SharpLink.StreamLoadTest.Tests', 'SharpLink.UnitTests')}
SDK_ASSEMBLIES = ('NuGet.Packaging', 'NuGet.Configuration', 'NuGet.Versioning', 'NuGet.Common',
                  'NuGet.Frameworks', 'Newtonsoft.Json', 'System.Security.Cryptography.Pkcs',
                  'System.Security.Cryptography.ProtectedData')

def regular(path):
    path = Path(path)
    if not path.is_absolute() or path.resolve() != path or not path.is_file() or path.is_symlink():
        raise ValueError('Missing/noncanonical/symlink evidence file: ' + str(path))
    return path

def sha(path):
    with regular(path).open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()

def digest(path):
    path = regular(path)
    size = path.stat().st_size
    if size > 128 * 1024 * 1024:
        raise ValueError('Package exceeds 128 MiB budget')
    with path.open('rb') as stream:
        raw = base64.b64encode(hashlib.file_digest(stream, 'sha512').digest()).decode('ascii')
    return {'rawSha256': sha(path), 'rawSha512': raw, 'bytes': size}

def content_hash(value):
    if not isinstance(value, str):
        raise ValueError('Missing content hash')
    value = value.removeprefix('sha512-')
    if len(base64.b64decode(value, validate=True)) != 64:
        raise ValueError('Invalid content hash length')
    return value

def sdk_identity(sdk_dir):
    sdk_dir = Path(sdk_dir)
    if not sdk_dir.is_absolute() or sdk_dir.resolve() != sdk_dir or sdk_dir.name != SDK or sdk_dir.parent.name != 'sdk':
        raise ValueError('Expected canonical SDK 10.0.114 directory')
    dotnet = regular(sdk_dir.parent.parent / 'dotnet')
    assemblies = {}
    for name in SDK_ASSEMBLIES:
        path = sdk_dir / (name + '.dll')
        if path.exists():
            assemblies[name] = {'path': str(path), 'sha256': sha(path)}
        elif name.startswith('NuGet.'):
            raise ValueError('Missing required SDK assembly: ' + name)
    return {'sdkDirectory': str(sdk_dir), 'sdkVersion': SDK, 'dotnetPath': str(dotnet),
            'dotnetSha256': sha(dotnet), 'assemblies': assemblies}

def build_helper(stage, output, sdk_dir):
    """Hosted-only, pre-init. Caller must stage outside the later product source root."""
    stage, output = Path(stage), Path(output)
    if not stage.is_absolute() or not output.is_absolute() or stage.resolve() != stage or output.resolve() != output:
        raise ValueError('Noncanonical helper directories')
    if stage == output or stage.is_relative_to(output) or output.is_relative_to(stage):
        raise ValueError('Helper sources and output must be disjoint')
    if stage.exists() or output.exists():
        raise ValueError('Helper preparation never overwrites/retries')
    stage.mkdir(); output.mkdir()
    report = {'status': 'INCOMPLETE', 'problems': [], 'sourceDirectory': str(stage), 'outputDirectory': str(output)}
    try:
        report['sdk'] = sdk_identity(sdk_dir)
        report['sourceHashes'] = {}
        for name in ('Program.cs', 'NuGetEvidence.csproj'):
            source = regular(ROOT / 'NuGetEvidence' / name)
            shutil.copyfile(source, stage / name)
            report['sourceHashes'][name] = sha(source)
        generated = {'global.json': json.dumps({'sdk': {'version': SDK, 'rollForward': 'disable', 'allowPrerelease': False}}).encode('utf-8'),
                     'NuGet.Config': b'<configuration><packageSources><clear /></packageSources></configuration>\n'}
        for name, data in generated.items():
            (stage / name).write_bytes(data)
        expected_inputs = dict(report['sourceHashes'], **{name: hashlib.sha256(data).hexdigest() for name, data in generated.items()})
        report['expectedStageInputHashes'] = expected_inputs
        report['stageInputHashesBefore'] = {name: sha(stage / name) for name in expected_inputs}
        if report['stageInputHashesBefore'] != expected_inputs or any(sha(ROOT / 'NuGetEvidence' / name) != value for name, value in report['sourceHashes'].items()):
            raise ValueError('Helper staged inputs differ from reviewed sources/fixed templates before build')
        argv = [report['sdk']['dotnetPath'], 'build', str(stage / 'NuGetEvidence.csproj'), '-c', 'Release',
                '-o', str(output), '--disable-build-servers', '--no-incremental', '-nodeReuse:false', '-p:UseSharedCompilation=false']
        process = host.execute(argv, stage, 120, output / 'process-helper-build.json')
        report['process'] = process
        report['stageInputHashesAfter'] = {name: sha(stage / name) for name in expected_inputs}
        if report['stageInputHashesAfter'] != expected_inputs or any(sha(ROOT / 'NuGetEvidence' / name) != value for name, value in report['sourceHashes'].items()):
            raise ValueError('Helper staged/reviewed inputs changed during build')
        if not host.safe(process) or process.get('exitCode') != 0:
            raise ValueError('Helper build failed or unsafe lifecycle')
        report['files'] = {name: sha(output / name) for name in
                           ('NuGetEvidence.dll', 'NuGetEvidence.deps.json', 'NuGetEvidence.runtimeconfig.json')}
        if sdk_identity(sdk_dir) != report['sdk']:
            raise ValueError('SDK bytes changed during helper build')
        report['helperDll'] = str(output / 'NuGetEvidence.dll')
        report['status'] = 'HELPER_BUILT'
    except Exception as error:
        report['problems'].append(str(error))
    finally:
        host.save(output / 'helper-build.json', report)
    if report['problems']:
        raise ValueError('; '.join(report['problems']))
    return report

def assets_observation(source, project):
    """Whitelist only. Never export sources/credentials/configuration from arbitrary assets fields."""
    relative = project['path']
    p = PurePosixPath(relative)
    if p.is_absolute() or '..' in p.parts:
        raise ValueError('Unsafe project path')
    path = source / p.parent / 'obj/project.assets.json'
    row = {'project': relative, 'assetsPath': str(path.relative_to(source)), 'problems': []}
    try:
        row['assetsSha256'] = sha(path)
        doc = strict_json(path.read_text())
        restore = doc.get('project', {}).get('restore', {})
        if restore.get('projectPath') != str(source / relative) or Path(restore.get('outputPath', '')) != path.parent:
            raise ValueError('Assets project/output identity mismatch')
        expected_tfm = 'netstandard2.0' if relative == 'src/SharpLink.Generator/SharpLink.Generator.csproj' else 'net10.0'
        row['expectedTargetFramework'] = expected_tfm
        if restore.get('originalTargetFrameworks') != [expected_tfm] or set(doc.get('project', {}).get('frameworks', {})) != {expected_tfm}:
            raise ValueError('Assets framework identity mismatch')
        ids = [name for name in doc.get('libraries', {}) if name.split('/')[0].lower() == 'tunit.core']
        row['resolvedTUnitIds'] = ids
        if not ids:
            return row
        if expected_tfm != 'net10.0':
            raise ValueError('TUnit package resolved by unexpected project target framework')
        library = doc['libraries'].get(PACKAGE_ID, {})
        row['library'] = {k: library[k] for k in ('type', 'path', 'sha512', 'contentHash') if k in library}
        row['declaredMembers'] = [name for name in library.get('files', []) if name in (MEMBER, PROPS)]
        row['expectedHashField'] = '/libraries/TUnit.Core~11.73.19/' + ('sha512' if 'sha512' in library else 'contentHash')
        row['expectedHashRaw'] = library.get('sha512', library.get('contentHash'))
        row['expectedHashAlgorithm'] = 'SHA512'
        row['expectedHashSemantics'] = 'NuGet content hash, not whole signed ZIP hash'
        row['selectedBuildAssets'] = {}
        for target, items in doc.get('targets', {}).items():
            if target == 'net10.0' or target.startswith('net10.0/'):
                entry = items.get(PACKAGE_ID, {})
                selected = {kind: [PROPS] for kind in ('build', 'buildTransitive') if PROPS in entry.get(kind, {})}
                if selected:
                    row['selectedBuildAssets'][target] = selected
        row['packageFolders'] = list(doc.get('packageFolders', {}))
        if ids != [PACKAGE_ID] or relative not in EXPECTED_PROJECTS:
            raise ValueError('Unexpected TUnit dependency identity/project')
        if library.get('type') != 'package' or library.get('path') != PACKAGE_PATH:
            raise ValueError('Unexpected package type/path')
        if any(row['declaredMembers'].count(name) != 1 for name in (MEMBER, PROPS)) or not row['selectedBuildAssets']:
            raise ValueError('Missing/ambiguous selected TUnit member/props')
        row['expectedContentHash'] = content_hash(row['expectedHashRaw'])
        if 'contentHash' in library and content_hash(library['contentHash']) != row['expectedContentHash']:
            raise ValueError('Conflicting assets content hashes')
        candidates = []
        for folder in row['packageFolders']:
            root = Path(folder)
            if not root.is_absolute() or root.resolve() != root or '..' in root.parts:
                raise ValueError('Unsafe package folder')
            candidate = root / PACKAGE_PATH / NUPKG
            if candidate.exists():
                candidates.append(str(regular(candidate)))
        row['nupkgCandidates'] = candidates
        if len(candidates) != 1:
            raise ValueError('Missing/ambiguous actual nupkg')
        row['nupkgPath'] = candidates[0]
    except Exception as error:
        row['problems'].append(str(error))
    return row

def package_members(path):
    with zipfile.ZipFile(regular(path)) as archive:
        names = set()
        for item in archive.infolist():
            name = item.filename
            if name in names or '\\' in name or PurePosixPath(name).is_absolute() or '..' in PurePosixPath(name).parts or item.external_attr >> 16 & 0o170000 == 0o120000:
                raise ValueError('Unsafe/duplicate package ZIP member')
            names.add(name)
        data = {}
        for name in (MEMBER, PROPS):
            item = archive.getinfo(name)
            if item.is_dir() or item.file_size > 2 * 1024 * 1024:
                raise ValueError('Unexpected member size/type')
            data[name] = archive.read(item)
        return data

def validate_helper(report, manifest, package, snapshot, sdk_dir):
    if report.get('status') != 'CONTENT_HASH_OBSERVED_NOT_SIGNATURE_VERIFICATION' or report.get('signatureTrust') != 'NOT_VERIFIED' or report.get('method') != METHOD:
        raise ValueError('Official API receipt incomplete/method mismatch')
    if report.get('sdkDirectory') != str(sdk_dir) or report.get('packagePath') != str(snapshot):
        raise ValueError('Official API receipt path mismatch')
    if (report.get('rawSha256'), report.get('rawSha512'), report.get('packageBytes')) != (package['rawSha256'], package['rawSha512'], package['bytes']):
        raise ValueError('Official API receipt raw package mismatch')
    if report.get('contentHashAlgorithm') != 'SHA512':
        raise ValueError('Unexpected official content hash algorithm')
    content_hash(report.get('contentHash'))
    helper = report.get('helper', {})
    if helper.get('path') != manifest['helperDll'] or helper.get('sha256') != manifest['files']['NuGetEvidence.dll']:
        raise ValueError('Loaded helper identity mismatch')
    loaded = report.get('loadedAssemblies', [])
    if not loaded or len({r.get('name') for r in loaded}) != len(loaded):
        raise ValueError('Missing/duplicate loaded NuGet assemblies')
    for row in loaded:
        expected = manifest['sdk']['assemblies'].get(row.get('name'))
        if not expected or row.get('path') != expected['path'] or row.get('sha256') != expected['sha256'] or not row.get('assemblyVersion') or not row.get('informationalVersion') or not row.get('moduleVersionId'):
            raise ValueError('Loaded SDK assembly identity mismatch')
    packaging = [r for r in loaded if r.get('name') == 'NuGet.Packaging']
    if len(packaging) != 1 or report.get('methodModuleVersionId') != packaging[0]['moduleVersionId'] or not isinstance(report.get('methodMetadataToken'), int):
        raise ValueError('Official method module binding missing')

def collect(source, out, sdk_dir, helperDll):
    """Hosted-only after original analysis. Safe observation is not successful validation."""
    source, out, sdk_dir, helperDll = map(Path, (source, out, sdk_dir, helperDll))
    path = out / 'package-evidence.json'
    if path.exists():
        raise ValueError('No package evidence overwrite/retry')
    doc = {'schema': 1, 'status': 'INCOMPLETE', 'observationCompleted': False, 'safeToContinue': False,
           'comparisonProblems': [], 'signatureTrust': 'NOT_VERIFIED', 'references': [], 'packages': [],
           'externalPackageSources': {}, 'problems': [], 'originalHistoricalFailures': 'CQL1-CQL5 remain INCOMPLETE; no historical bytes reconstructed.'}
    observation_ready = False
    host.save(path, doc)
    try:
        if any(not p.is_absolute() or p.resolve() != p for p in (source, out, sdk_dir, helperDll)) or out.is_relative_to(source) or helperDll.is_relative_to(source):
            raise ValueError('Package evidence/helper must be outside canonical product source')
        ledger = strict_json((ROOT / 'coverage-ledger.json').read_text())
        doc['staticLedgerSha256'] = sha(ROOT / 'coverage-ledger.json')
        projects = [p for p in ledger['projects'] if p['scope'] != 'unreached']
        if len(projects) != 67 or len({p['path'] for p in projects}) != 67:
            raise ValueError('Unexpected built project ledger')
        for project in projects:
            observation = assets_observation(source, project)
            if observation.get('resolvedTUnitIds') or observation['problems'] or project['path'] in EXPECTED_PROJECTS:
                doc['references'].append(observation)
                doc['problems'].extend(project['path'] + ': ' + p for p in observation['problems'])
        doc['assetsProjectsInspected'] = len(projects)
        host.save(path, doc) # All five expected values survive any subsequent first mismatch.
        refs = [r for r in doc['references'] if r.get('resolvedTUnitIds')]
        if {r['project'] for r in refs} != EXPECTED_PROJECTS:
            doc['problems'].append('Actual TUnit referring project set differs from frozen five')
        packages = {r['nupkgPath'] for r in refs if r.get('nupkgPath')}
        if len(packages) != 1:
            raise ValueError('Exactly one actual TUnit nupkg required')
        original = Path(next(iter(packages))); directory = out / 'package-evidence'; directory.mkdir()
        snapshot = directory / NUPKG
        package = {'nupkgPath': str(original), 'artifactPath': 'package-evidence/' + NUPKG, 'members': {}, 'problems': []}
        doc['packages'].append(package)
        package.update(digest(original))
        host.save(path, doc)
        shutil.copyfile(regular(original), snapshot)
        if digest(snapshot) != {k: package[k] for k in ('rawSha256', 'rawSha512', 'bytes')} or digest(original) != digest(snapshot):
            raise ValueError('Package changed while preserving original bytes')
        # Preserve both observed materialized files and hashes before official content-hash comparison.
        for member in (MEMBER, PROPS):
            materialized = original.parent / member
            name = PurePosixPath(member).name
            record = {'materializedPath': str(materialized), 'artifactPath': 'package-evidence/' + name}
            package['members'][member] = record
            try:
                regular(materialized)
                if materialized.stat().st_size > 2 * 1024 * 1024:
                    raise ValueError('Materialized member exceeds size budget')
                record['materializedSha256'] = sha(materialized)
                shutil.copyfile(materialized, directory / name)
                if sha(directory / name) != record['materializedSha256']:
                    raise ValueError('Materialized member changed during preservation')
            except Exception as error:
                package['problems'].append(str(error))
        try:
            members = package_members(snapshot)
            for name, data in members.items():
                package['members'][name]['memberSha256'] = hashlib.sha256(data).hexdigest()
                if package['members'][name].get('materializedSha256') != hashlib.sha256(data).hexdigest():
                    raise ValueError('Materialized source/props differs from nupkg member')
            props = members[PROPS]
            if b'<!DOCTYPE' in props.upper() or b'<!ENTITY' in props.upper():
                raise ValueError('DTD/entity in package props')
            root = ET.fromstring(props)
            includes = [x for x in root.iter() if x.tag.rsplit('}', 1)[-1] == 'Compile' and x.get('Include') == '$(MSBuildThisFileDirectory)TUnit.Core.GeneratedNamespace.cs']
            if len(includes) != 1:
                raise ValueError('Missing/ambiguous exact props Compile item')
        except Exception as error:
            package['problems'].append(str(error))
        host.save(path, doc)
        manifest = strict_json(regular(helperDll.parent / 'helper-build.json').read_text())
        doc['helperBuild'] = manifest
        if manifest.get('status') != 'HELPER_BUILT' or manifest.get('helperDll') != str(helperDll) or not host.safe(manifest.get('process', {})) or manifest['process'].get('exitCode') != 0:
            raise ValueError('Missing/suspect helper build receipt')
        if sdk_identity(sdk_dir) != manifest['sdk']:
            raise ValueError('Official SDK changed since pre-init preparation')
        for name, value in manifest['files'].items():
            if name not in ('NuGetEvidence.dll', 'NuGetEvidence.deps.json', 'NuGetEvidence.runtimeconfig.json') or sha(helperDll.parent / name) != value:
                raise ValueError('Helper runtime output changed')
        if set(manifest['files']) != {'NuGetEvidence.dll', 'NuGetEvidence.deps.json', 'NuGetEvidence.runtimeconfig.json'}:
            raise ValueError('Incomplete helper runtime identity')
        argv = [manifest['sdk']['dotnetPath'], str(helperDll), str(sdk_dir), str(snapshot), str(directory / 'official-content-hash.json')]
        process = host.execute(argv, helperDll.parent, 60, directory / 'process-content-hash.json')
        doc['helperProcess'] = process
        official_path = directory / 'official-content-hash.json'
        if official_path.is_file():
            package['official'] = strict_json(regular(official_path).read_text())
        host.save(path, doc)
        if not host.safe(process) or process.get('exitCode') != 0:
            raise ValueError('Official helper failed or unsafe lifecycle')
        if sdk_identity(sdk_dir) != manifest['sdk'] or digest(original) != digest(snapshot) or digest(snapshot) != {k: package[k] for k in ('rawSha256', 'rawSha512', 'bytes')}:
            raise ValueError('SDK/package bytes changed during official observation')
        validate_helper(package.get('official', {}), manifest, package, snapshot, sdk_dir)
        package['contentHash'] = package['official']['contentHash']
        host.save(path, doc) # Actual official value precedes all expected-value comparisons.
        # Only a content-hash comparison mismatch can be observationally complete while invalid.
        # Earlier structural/member/assets errors, or subsequent mutation, must block later work.
        observation_ready = not doc['problems'] and not package['problems']
        for reference in refs:
            reference['contentHashMatches'] = reference.get('expectedContentHash') == package['contentHash']
            if not reference['contentHashMatches']:
                problem = reference['project'] + ': official NuGet content hash differs from assets'
                doc['comparisonProblems'].append(problem)
                doc['problems'].append(problem)
            if sha(source / reference['assetsPath']) != reference['assetsSha256']:
                observation_ready = False
                doc['problems'].append(reference['project'] + ': assets changed during package observation')
        for member, observation in package['members'].items():
            if observation.get('materializedSha256') != sha(original.parent / member):
                observation_ready = False
                doc['problems'].append('Materialized package member changed during official observation: ' + member)
        if package['problems']:
            doc['problems'].extend(package['problems'])
        if not doc['problems']:
            binding = {'canonicalSourceId': KEY, 'kind': 'external-package-source', 'packageId': 'TUnit.Core', 'packageVersion': '1.73.19',
                       'packagePath': PACKAGE_PATH, 'member': MEMBER, 'memberSha256': package['members'][MEMBER]['memberSha256'],
                       'materializedSha256': package['members'][MEMBER]['materializedSha256'], 'nupkgPath': str(original),
                       'nupkgSha256': package['rawSha256'], 'nupkgRawSha512': package['rawSha512'], 'nupkgContentHash': package['contentHash'],
                       'propsMember': PROPS, 'propsPath': str(original.parent / PROPS), 'propsSha256': package['members'][PROPS]['memberSha256'],
                       'compileInclude': '$(MSBuildThisFileDirectory)TUnit.Core.GeneratedNamespace.cs', 'signatureTrust': 'NOT_VERIFIED',
                       'referencingProjects': [{'project': r['project'], 'assetsPath': r['assetsPath'], 'assetsSha256': r['assetsSha256'],
                                                'assetsContentHash': r['expectedContentHash'], 'selectedTargets': sorted(r['selectedBuildAssets'])} for r in refs],
                       'sourceBytesExported': True, 'perProjectCompilerInputProven': False}
            doc['externalPackageSources'][str(original.parent / MEMBER)] = binding
            doc['status'] = 'PACKAGE_BYTES_AND_OFFICIAL_CONTENT_HASH_OBSERVED_NOT_SIGNATURE_TRUST'
    except Exception as error:
        observation_ready = False
        doc['problems'].append(str(error))
    finally:
        # No continuation permission is persisted until this mandatory partial-evidence save succeeds.
        host.save(path, doc)
    doc['observationCompleted'] = observation_ready
    doc['safeToContinue'] = observation_ready and host.safe(doc.get('helperProcess', {})) and doc['helperProcess'].get('exitCode') == 0
    host.save(path, doc)
    return doc

def observation_exit_code(result):
    """Workflow must ALSO require this step's original outcome == success; receipt alone is insufficient."""
    safe = result.get('observationCompleted') is True and result.get('safeToContinue') is True
    if os.environ.get('GITHUB_OUTPUT'):
        with Path(os.environ['GITHUB_OUTPUT']).open('a') as stream:
            stream.write('safe=' + str(safe).lower() + '\n')
    # A comparison mismatch remains INCOMPLETE in the document and fails final validation.
    return 0 if safe else 1

if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('mode', choices=['build-helper', 'collect'])
    parser.add_argument('--stage', type=Path); parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--sdk-dir', type=Path, required=True); parser.add_argument('--source', type=Path); parser.add_argument('--helper-dll', type=Path)
    args = parser.parse_args()
    if args.mode == 'build-helper':
        if args.stage is None: parser.error('--stage required')
        build_helper(args.stage, args.output, args.sdk_dir)
    else:
        if args.source is None or args.helper_dll is None: parser.error('--source and --helper-dll required')
        result = collect(args.source, args.output, args.sdk_dir, args.helper_dll)
        raise SystemExit(observation_exit_code(result))
