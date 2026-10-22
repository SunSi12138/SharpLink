#!/usr/bin/env python3
"""Archive immutable public-source arms; never apply a patch inside a parent checkout."""
import argparse, hashlib, io, json, os, re, subprocess, tarfile
from pathlib import Path, PurePosixPath
PUBLIC_BASELINE = 'c60393508cecd20886e798a474f189f4faf52c62'
EXPECTED = {'baseline':'c242f33f0041439b176c43460864a888237b79d9', 'candidate-final':'3bf0a1eb6b162016d07f9d9329b1b3ca560f68a3'}
INPUTS = ['src', 'Directory.Build.props', 'Directory.Packages.props', 'global.json']
def git(repo, *args):
    env={k:v for k,v in os.environ.items() if not k.startswith('GIT_')}
    return subprocess.check_output(['git','-C',str(repo.resolve()),*args],env=env)
def prepare_snapshot(repo, commit, expected, destination):
    if not re.fullmatch('[0-9a-f]{40}',commit): raise ValueError('An immutable full commit SHA is required.')
    actual=git(repo,'rev-parse',commit+':src').decode().strip()
    if actual != expected: raise ValueError(f'Source tree mismatch: {commit}:src expected {expected}, observed {actual}')
    if destination.exists(): raise ValueError(f'Refusing to overwrite existing arm: {destination}')
    members={}
    with tarfile.open(fileobj=io.BytesIO(git(repo,'archive',commit,*INPUTS))) as archive:
        for member in archive.getmembers():
            path=PurePosixPath(member.name)
            if path.is_absolute() or '..' in path.parts or member.issym() or member.islnk(): raise ValueError('Unsafe archive member: '+member.name)
            if member.isfile(): members[member.name]=archive.extractfile(member).read()
    for name,data in members.items():
        target=destination/name;target.parent.mkdir(parents=True,exist_ok=True);target.write_bytes(data)
    hashes={name:hashlib.sha256(data).hexdigest() for name,data in sorted(members.items())}
    assert all(hashlib.sha256((destination/name).read_bytes()).hexdigest()==sha for name,sha in hashes.items())
    return {'Commit':commit,'SourceTree':actual,'Files':hashes}
def main():
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('--repo',required=True,type=Path);p.add_argument('--root',type=Path,default=Path(__file__).resolve().parent);p.add_argument('--baseline',default=PUBLIC_BASELINE);p.add_argument('--candidate',required=True);a=p.parse_args()
    root=a.root.resolve();root.mkdir(parents=True,exist_ok=True);report=root/'source-provenance.json'
    result={'Status':'Started','ExpectedSourceTrees':EXPECTED,'RequestedCommits':{'baseline':a.baseline,'candidate-final':a.candidate},'Arms':{}}
    def save(): report.write_text(json.dumps(result,indent=2)+'\n')
    save()
    try:
        for kind in ['shapes','retry']:
            result['Arms'][kind]={}
            for arm,commit in result['RequestedCommits'].items():
                result['Arms'][kind][arm]=prepare_snapshot(a.repo,commit,EXPECTED[arm],root/kind/'arms'/arm);save()
        result['Status']='SourceSnapshotsVerified';save()
    except Exception as exc:
        result['Status']='Failed';result['Error']=str(exc);save();raise
if __name__=='__main__': main()
