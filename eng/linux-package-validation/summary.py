#!/usr/bin/env python3
"""Print bounded identities and evaluated native pack versions; never upload files."""
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import sys
from zipfile import ZipFile

root = Path(sys.argv[1]).resolve()
spec = importlib.util.spec_from_file_location('packaged', root / 'eng/run-packaged-trim-smoke.py')
helper = importlib.util.module_from_spec(spec)
spec.loader.exec_module(helper)
commit = subprocess.check_output(['git', '-C', str(root), 'rev-parse', 'HEAD'], text=True).strip()
manifest = helper.package_manifest(root / 'artifacts/nuget', '3.0.0', commit)
for name, hashes in sorted(manifest.items()):
    package = next(package for package in helper.PACKAGES if name == f'{package}.3.0.0.' + name.rsplit('.', 1)[1])
    metadata = helper._identities.metadata(root / 'artifacts/nuget' / name, package)
    print('PACKAGE_IDENTITY ' + json.dumps({'file': name, 'sha256': hashes['sha256'], 'nuspecCommit': metadata[2]}))
for package in helper.PACKAGES:
    identity = json.loads((root / 'artifacts/public-api/current' / (package + '.assembly.json')).read_text())
    if identity.get('InformationalVersion') != '3.0.0+' + commit or identity.get('Name') != package or identity.get('Version') != '3.0.0.0':
        raise SystemExit('Packed assembly source identity mismatch: ' + package)
    with ZipFile(root / 'artifacts/nuget' / (package + '.3.0.0.nupkg')) as archive:
        assembly_hash = hashlib.sha256(archive.read('lib/net10.0/' + package + '.dll')).hexdigest()
    print('ASSEMBLY_IDENTITY ' + json.dumps({'package': package, 'sha256': assembly_hash, **identity}))
reports = list((root / 'artifacts/packaged-trim').glob('linux-x64-*/evidence.json'))
if len(reports) != 1:
    raise SystemExit('Expected exactly one packaged consumer attempt, got ' + str(len(reports)))
evidence = json.loads(reports[0].read_text())
if evidence.get('commit') != commit or evidence.get('version') != '3.0.0' or evidence.get('rid') != 'linux-x64':
    raise SystemExit('Packaged evidence source/version/RID mismatch')
print('PACKAGED_RESULT ' + json.dumps({k: evidence.get(k) for k in ('status', 'commit', 'version', 'rid', 'error')}))
for consumer in evidence['consumers']:
    print('CONSUMER_RESULT ' + json.dumps({k: consumer[k] for k in ('name', 'trimMode', 'publishAot', 'executableSha256', 'status')}))
# Use exactly the consumer's isolated restore/cache and MSBuild properties. This
# evaluates pack selections, not another restore, publish, or test attempt.
for consumer, mode, aot in helper.CONSUMERS:
    work = Path(evidence['workDirectory']) / consumer
    artifacts = work / 'build'
    assets = list(artifacts.rglob('project.assets.json'))
    if len(assets) != 1:
        if evidence['status'] == 'passed':
            raise SystemExit('Expected exactly one isolated assets file: ' + consumer)
        print('PACK_SELECTION_NOT_REACHED ' + consumer)
        continue
    env = dict(os.environ, NUGET_PACKAGES=str(work / 'packages'), NUGET_HTTP_CACHE_PATH=str(work / 'http-cache'))
    command = [os.environ.get('DOTNET', 'dotnet'), 'msbuild', str(root / 'test' / ('SharpLink.' + consumer) / ('SharpLink.' + consumer + '.csproj')),
               '-nologo', '-t:ResolveFrameworkReferences', '-p:UseArtifactsOutput=true', '-p:ArtifactsPath=' + str(artifacts),
               '-p:Configuration=Release', '-p:RuntimeIdentifier=linux-x64', '-p:SelfContained=true',
               '-p:SharpLinkPackageVersion=3.0.0', *helper.WARNINGS,
               '-getItem:ResolvedILCompilerPack,ResolvedRuntimePack']
    result = subprocess.run(command, cwd=root, env=env, text=True, capture_output=True)
    log = reports[0].parent / (consumer + '-pack-selection.json')
    log.write_text(result.stdout + result.stderr)
    if result.returncode:
        print(result.stdout[-4000:] + result.stderr[-4000:])
        raise SystemExit('Pack selection evaluation failed: ' + consumer)
    items = json.loads(result.stdout)['Items']
    runtime = items.get('ResolvedRuntimePack', [])
    compiler = items.get('ResolvedILCompilerPack', [])
    if not runtime or (aot and not compiler):
        raise SystemExit('Missing actual evaluated native pack selection: ' + consumer)
    for kind, values in items.items():
        for item in values:
            if not item.get('NuGetPackageVersion') or not item.get('NuGetPackageId'):
                raise SystemExit('Native pack lacks actual package ID/version: ' + consumer + '/' + kind)
            print('NATIVE_PACK_SELECTION ' + json.dumps({'consumer': consumer, 'kind': kind,
                  **{k: item[k] for k in ('Identity', 'NuGetPackageId', 'NuGetPackageVersion', 'PackageDirectory', 'RuntimeIdentifier') if k in item}}))
print('SCOPE Linux only; self-contained pack selections above are not controlled by private runtime14; API proposal validation is not semantic approval.')
expected = {(f'SharpLink.{name}', mode, aot, 'passed') for name, mode, aot in helper.CONSUMERS}
actual = [(item['name'], item['trimMode'], item['publishAot'], item['status']) for item in evidence['consumers']]
if evidence['status'] != 'passed' or len(actual) != 3 or set(actual) != expected:
    raise SystemExit('Packaged gate did not pass exactly the three expected consumers')
