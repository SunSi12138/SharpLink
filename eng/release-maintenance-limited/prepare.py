#!/usr/bin/env python3
"""Build exactly one frozen candidate binary; invoked only after review/release."""
import argparse, hashlib, json, os, platform, subprocess
from pathlib import Path
from process import execute

ROOT = Path(__file__).resolve().parent
CANDIDATE = '8ae9ee36ebec82f82e03185432601a830c865e95'
TREE = '1f0c9291c7394d012d3241a9cf31482b0734b697'

def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()

def inventory(root):
    return {str(p.relative_to(root)): sha(p) for p in sorted(root.rglob('*')) if p.is_file() and not p.is_symlink()}

def git(root, *args):
    return subprocess.check_output(['git', *args], cwd=root, text=True).strip()

def source_identity(source):
    if git(source, 'rev-parse', 'HEAD') != CANDIDATE or git(source, 'rev-parse', 'HEAD^{tree}') != TREE or git(source, 'status', '--porcelain'):
        raise ValueError('Product must be pristine exact candidate/tree')
    paths = git(source, 'ls-files').splitlines()
    return {p: sha(source/p) for p in paths if p.startswith('src/') or p in ('global.json', 'Directory.Build.props', 'Directory.Build.targets', 'Directory.Packages.props', 'NuGet.config')}

def main():
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('--source', type=Path, required=True); p.add_argument('--dotnet', type=Path, required=True)
    p.add_argument('--runtime-dotnet', type=Path, required=True); p.add_argument('--output', type=Path, required=True)
    a=p.parse_args(); source=a.source.resolve(); sdk=a.dotnet.resolve(); runtime=a.runtime_dotnet.resolve(); out=a.output.resolve()
    out.mkdir(parents=True, exist_ok=False)
    sources=source_identity(source)
    version=subprocess.check_output([str(sdk),'--version'],cwd=ROOT,text=True,timeout=30).strip()
    if version != '10.0.102': raise ValueError('SDK must be exactly10.0.102')
    runtimes=subprocess.check_output([str(runtime),'--list-runtimes'],text=True,timeout=30)
    if [line.split()[:2] for line in runtimes.splitlines()] != [['Microsoft.AspNetCore.App','10.0.14'],['Microsoft.NETCore.App','10.0.14']]:
        raise ValueError('Private host must have only .NET/ASP.NET10.0.14')
    if subprocess.check_output([str(runtime),'--list-sdks'],text=True,timeout=30).strip(): raise ValueError('Private runtime must not have an SDK')
    (out/'sdk-info.txt').write_text(subprocess.check_output([str(sdk),'--info'],cwd=ROOT,text=True,timeout=30))
    (out/'runtime-info.txt').write_text(subprocess.check_output([str(runtime),'--info'],text=True,timeout=30))
    (out/'cpuinfo.txt').write_text(Path('/proc/cpuinfo').read_text())
    (out/'os-release.txt').write_text(Path('/etc/os-release').read_text())
    runtime_files=[runtime, runtime.parent/'shared/Microsoft.NETCore.App/10.0.14/System.Private.CoreLib.dll',
        runtime.parent/'shared/Microsoft.NETCore.App/10.0.14/libcoreclr.so',runtime.parent/'shared/Microsoft.NETCore.App/10.0.14/libclrjit.so',runtime.parent/'shared/Microsoft.NETCore.App/10.0.14/libhostpolicy.so',runtime.parent/'host/fxr/10.0.14/libhostfxr.so']
    receipt={'candidate':CANDIDATE,'candidateTree':TREE,'sdk':version,'runtime':'10.0.14','runtimeFiles':{str(x):sha(x) for x in runtime_files},
        'runtimeClosure':inventory(runtime.parent),'productSources':sources,'fixtureSources':{str(x.relative_to(ROOT)):sha(x) for x in sorted(ROOT.rglob('*')) if x.is_file() and '__pycache__' not in str(x)},
        'machine':platform.uname()._asdict(),'environment':{k:v for k,v in os.environ.items() if k.startswith(('DOTNET_','COMPlus_'))}}
    (out/'input-identity.json').write_text(json.dumps(receipt,indent=2)+'\n')
    command=[str(sdk),'build',str(ROOT/'probe/HotCostProbe.csproj'),'-c','Release','-m:1','-p:UseSharedCompilation=false',f'-p:SubjectRoot={source}','--artifacts-path',str(out/'build')]
    (out/'build-command.json').write_text(json.dumps(command,indent=2)+'\n')
    host=execute(command,out,'build',os.environ.copy(),timeout=600,cwd=ROOT)
    if host['status']!='success' or host['exitCode']!=0 or not host['processGroupQuiescent']:
        raise ValueError('Build failed/timed out/leaked or group cleanup uncertain')
    if source_identity(source) != sources: raise ValueError('Product source changed during build')
    binaries=inventory(out/'build/bin')
    required=['HotCostProbe/release/SharpLink.UnitTests.dll','HotCostDynamic.Contracts/release/HotCostDynamic.Contracts.dll','HotCostDynamic.Services/release/HotCostDynamic.Services.dll']
    if not all(x in binaries for x in required): raise ValueError('Missing probe/dynamic contracts/services output')
    (out/'binary-identity.json').write_text(json.dumps(binaries,indent=2)+'\n')

if __name__=='__main__': main()
