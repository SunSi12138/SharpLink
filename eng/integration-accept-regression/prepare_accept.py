"""Source-only exactmain original Integration preparation; never copy/alter a fixture."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess

COORDINATOR='src/SharpLink.Server/ServerLifecycleCoordinator.cs'


def require(value,message):
    if not value:raise ValueError(message)


def sha(path):return hashlib.sha256(path.read_bytes()).hexdigest()


def blob_bytes(raw):return hashlib.sha1(b'blob '+str(len(raw)).encode()+b'\0'+raw).hexdigest()


def git(root,*args):
    p=subprocess.run(['git','-C',str(root),*args],capture_output=True,text=True,timeout=20,
                     env=dict(os.environ,GIT_NO_LAZY_FETCH='1',GIT_TERMINAL_PROMPT='0'))
    require(p.returncode==0,'Local source preparation git failed')
    return p.stdout.strip()


def upgrade_bytes(raw,catalog):
    require(blob_bytes(raw)==catalog['centralBeforeGitBlob'],'Wrong original central package bytes')
    before=b'<PackageVersion Include="TUnit" Version="1.73.19" />'
    require(raw.count(before)==1,'Ambiguous original TUnit declaration')
    after=raw.replace(before,b'<PackageVersion Include="TUnit" Version="1.78.0" />')
    require(blob_bytes(after)==catalog['centralAfterGitBlob'],'Package transform differs from exact PR971')
    return after


def main():
    p=argparse.ArgumentParser();p.add_argument('--checkout',type=Path,required=True)
    p.add_argument('--variant',choices=('baseline','fixed'),required=True);p.add_argument('--manifest-sha256',required=True)
    args=p.parse_args();payload=Path(__file__).resolve().parent
    require(sha(payload/'SOURCE.json')==args.manifest_sha256,'Wrong originalaccept source manifest')
    catalog=json.loads((payload/'SOURCE.json').read_text());root=args.checkout.resolve(strict=True)
    require(Path(git(root,'rev-parse','--show-toplevel')).resolve()==root and git(root,'rev-parse','HEAD')==catalog['base'],'Wrong exactmain checkout')
    require(not git(root,'status','--porcelain','--untracked-files=all'),'Exactmain checkout must be clean')
    require(not git(root,'ls-files','--others','--ignored','--exclude-standard'),'Ignored preexisting source/output forbidden')
    for relative,pin in catalog['sourcePins'].items():
        file=root/relative
        require(file.is_file() and not file.is_symlink() and sha(file)==pin['sha256'] and blob_bytes(file.read_bytes())==pin['gitBlob'],'Wrong pinned original project/fixture input')
    for relative in catalog['absent']:require(not (root/relative).exists() and not (root/relative).is_symlink(),'Unexpected main import selector')
    for relative,pin in catalog['framePins'].items():
        require(sha(root/relative)==pin['sha256'],'Wrong original event origin source')
    coordinator=root/COORDINATOR
    require(not coordinator.is_symlink() and blob_bytes(coordinator.read_bytes())==catalog['coordinatorBeforeGitBlob'],'Wrong coordinator preimage')
    if args.variant=='fixed':
        git(root,'apply','--check',str(payload/'terminal-signal.patch'));git(root,'apply',str(payload/'terminal-signal.patch'))
        require(sha(coordinator)==catalog['coordinatorAfterSHA256'],'Wrong fixed coordinator bytes')
    packages=root/'Directory.Packages.props';packages.write_bytes(upgrade_bytes(packages.read_bytes(),catalog))
    expected=['Directory.Packages.props']+([COORDINATOR] if args.variant=='fixed' else [])
    require(sorted(git(root,'diff','--name-only').splitlines())==sorted(expected),'Unexpected originalaccept source mutation')
    require(not git(root,'ls-files','--others','--exclude-standard'),'New/untracked fixture forbidden')
    print(json.dumps({'stage':'originalaccept-source-prepared','base':catalog['base'],'variant':args.variant,'tunit':'1.78.0','fixtureUnchanged':True,'executedTests':False}))


if __name__=='__main__':main()
