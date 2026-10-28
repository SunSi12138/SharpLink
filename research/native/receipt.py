"""Fail-closed, offline validation of an independently reviewed build receipt.
The externally supplied digest binds the review decision; this is not a build attestation.
"""
import hashlib,json,re,tarfile
from pathlib import Path

PRODUCTS={'SharpLink.Runtime.dll','SharpLink.Client.dll','SharpLink.Server.dll','SharpLink.Abstractions.dll','SharpLink.Sdk.dll'}
SOURCE_ANCHORS=('Directory.Build.props','Directory.Packages.props','src/SharpLink.Runtime/WakeupSignal.cs','src/SharpLink.Runtime/RpcSession.SendPump.cs')
ROOT_KEYS={'schema','arm','rid','sourceCommit','sdkVersion','runtimeVersion','dotnet','probeDll','sourceRoot','generatedRoot','buildLog','buildSucceeded','productDiffReviewed','archiveVerification','files'}

def digest(path,algorithm='sha256'):
    with path.open('rb') as stream:return hashlib.file_digest(stream,algorithm).hexdigest()

def exact_keys(value,keys,label):
    if type(value) is not dict or set(value)!=keys:raise ValueError('Invalid schema: '+label)

def absolute(value):
    if type(value) is not str or not value or not Path(value).is_absolute():raise ValueError('Expected absolute path')
    path=Path(value)
    if path.resolve()!=path or path.is_symlink():raise ValueError('Noncanonical/symlink path: '+value)
    return path

def tree(root,skip=()):
    if not root.is_dir():raise ValueError('Missing directory: '+str(root))
    result=set()
    for p in root.rglob('*'):
        if any(part in skip for part in p.relative_to(root).parts):continue
        if p.is_symlink():raise ValueError('Symlink in evidence closure: '+str(p))
        if p.is_file():result.add(p)
    return result

def check_archives(rows,lock,rid):
    expected={r['component']:r for r in lock['artifacts'] if r['rid']==rid}
    if type(rows) is not list or len(rows)!=3:raise ValueError('Three official archives required')
    found={}
    for row in rows:
        exact_keys(row,{'component','path','sha512','officialUrl'},'archive')
        if row['component'] not in expected or row['component'] in found:raise ValueError('Unknown/duplicate archive')
        ref=expected[row['component']];p=absolute(row['path'])
        if row['sha512']!=ref['hash'] or row['officialUrl']!=ref['url']:raise ValueError('Official artifact lock mismatch')
        if digest(p,'sha512')!=ref['hash']:raise ValueError('Archive bytes do not match official SHA512')
        found[row['component']]=p
    return found

def verify_installed(archive,root,required):
    """Read verified archive, never extract; match installed bytes to official members."""
    remaining=set(required)
    with tarfile.open(archive,'r:gz') as tar:
        for member in tar:
            name=member.name.removeprefix('./')
            relative=Path(name)
            if relative.is_absolute() or '..' in relative.parts:raise ValueError('Invalid official archive member path')
            path=root/relative
            if path not in remaining:continue
            if not member.isfile():raise ValueError('Required official member is not regular')
            stream=tar.extractfile(member)
            if stream is None:raise ValueError('Missing archive member bytes')
            with stream:actual=hashlib.file_digest(stream,'sha256').hexdigest()
            if digest(path)!=actual:raise ValueError('Installed official member differs: '+str(path))
            remaining.remove(path)
    if remaining:raise ValueError('Required installed files absent from archive: '+str(sorted(map(str,remaining))))

def validate_receipt(path,reviewed_digest,plan,lock):
    if not re.fullmatch('[0-9a-f]{64}',reviewed_digest) or digest(path)!=reviewed_digest:
        raise ValueError('Independently reviewed receipt digest mismatch')
    r=json.loads(path.read_text());exact_keys(r,ROOT_KEYS,'receipt')
    if type(r['schema']) is not int or r['schema']!=2:raise ValueError('Receipt schema must be integer 2')
    if r['buildSucceeded'] is not True or r['productDiffReviewed'] is not True:raise ValueError('Approval flags must be boolean true')
    if r['arm'] not in plan['armOrder'] or r['rid'] not in plan['rids']:raise ValueError('Unknown arm/RID')
    if r['sourceCommit']!=plan['baseline' if r['arm']=='D' else 'candidate']:raise ValueError('Wrong source commit')
    if r['sdkVersion']!=plan['sdk'] or r['runtimeVersion']!=plan['runtime']:raise ValueError('Wrong SDK/runtime pin')
    dotnet=absolute(r['dotnet']);dll=absolute(r['probeDll']);source=absolute(r['sourceRoot']);generated=absolute(r['generatedRoot']);build=absolute(r['buildLog'])
    root=dotnet.parent;output=dll.parent
    if dotnet.name!='dotnet' or dll.name!='WakeupNativeProbe.dll':raise ValueError('Unexpected entrypoint names')
    if type(r['files']) is not list or not r['files']:raise ValueError('Missing file closure')
    indexed={}
    for row in r['files']:
        exact_keys(row,{'path','sha256'},'file');p=absolute(row['path'])
        if p in indexed or type(row['sha256']) is not str or not re.fullmatch('[0-9a-f]{64}',row['sha256']):raise ValueError('Duplicate/invalid file hash')
        if digest(p)!=row['sha256']:raise ValueError('Prepared file changed: '+str(p))
        indexed[p]=row['sha256']
    version=plan['runtime'];core=root/'shared/Microsoft.NETCore.App'/version;asp=root/'shared/Microsoft.AspNetCore.App'/version;fxr=root/'host/fxr'/version
    fxr_versions=[]
    for directory in (root/'host/fxr').iterdir():
        if not directory.is_dir() or not re.fullmatch(r'[0-9]+\.[0-9]+\.[0-9]+',directory.name):raise ValueError('Unexpected hostfxr installation entry')
        fxr_versions.append(tuple(map(int,directory.name.split('.'))))
    if not fxr_versions or max(fxr_versions)!=tuple(map(int,version.split('.'))):raise ValueError('Host would select a different hostfxr version')
    extension='.so' if r['rid']=='linux-x64' else '.dylib'
    required={dotnet,dll,build,core/('libcoreclr'+extension),core/('libclrjit'+extension),core/'System.Private.CoreLib.dll',core/('libhostpolicy'+extension),fxr/('libhostfxr'+extension)}
    required|={output/name for name in PRODUCTS}
    config=dll.with_suffix('.runtimeconfig.json');deps=dll.with_suffix('.deps.json');required|={config,deps}
    required|=tree(output)|tree(core)|tree(asp)|tree(fxr)|tree(generated)|tree(source/'src',skip=('bin','obj'))
    required|={source/name for name in SOURCE_ANCHORS}
    required|={p for p in source.iterdir() if p.is_file() and (p.suffix in {'.props','.targets'} or p.name.lower() in {'global.json','nuget.config'})}
    if not any(p.suffix=='.cs' for p in tree(generated)):raise ValueError('Missing generated C# sources')
    if not required.issubset(indexed):raise ValueError('Required closure missing: '+str(sorted(map(str,required-set(indexed)))))
    runtime=json.loads(config.read_text());options=runtime.get('runtimeOptions',{})
    if options.get('tfm')!='net10.0' or options.get('rollForward')!='Disable':raise ValueError('Runtimeconfig must pin runtime with rollForward Disable')
    frameworks=options.get('frameworks',[options['framework']] if 'framework' in options else [])
    if not frameworks or any(f.get('version')!=version or f.get('name') not in {'Microsoft.NETCore.App','Microsoft.AspNetCore.App'} for f in frameworks):raise ValueError('Runtimeconfig framework mismatch')
    if 'Microsoft.NETCore.App' not in {f['name'] for f in frameworks}:raise ValueError('Core framework missing')
    # All configuration is review-bound; reject known performance/profiler/loading overrides too.
    if any(k not in {'tfm','framework','frameworks','rollForward','configProperties'} for k in options):raise ValueError('Unexpected runtimeconfig setting')
    if any(any(x in k.lower() for x in ('tier','jit','gc','threadpool','profiler','startup','readytorun')) for k in options.get('configProperties',{})):raise ValueError('Runtimeconfig tuning forbidden')
    dep=json.loads(deps.read_text());targets=dep.get('targets',{});libraries=dep.get('libraries',{})
    if not targets or not libraries:raise ValueError('Empty deps graph')
    names={key.split('/')[0]+'.dll' for key in libraries}
    if not PRODUCTS.issubset(names):raise ValueError('Required products absent from deps graph')
    for target in targets.values():
        for package in target.values():
            for asset in package.get('runtime',{}):
                if asset.endswith('.dll') and not (output/Path(asset).name).is_file():raise ValueError('Unresolved runtime dependency: '+asset)
    archives=check_archives(r['archiveVerification'],lock,r['rid'])
    verify_installed(archives['runtime'],root,tree(core)|tree(fxr)|{dotnet})
    verify_installed(archives['aspnetcore-runtime'],root,tree(asp))
    return r
