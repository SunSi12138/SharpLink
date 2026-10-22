#!/usr/bin/env python3
"""Create production-only source snapshots from public commits; never invoke repository tests."""
import argparse,hashlib,io,json,subprocess,tarfile
from pathlib import Path
p=argparse.ArgumentParser(description=__doc__)
p.add_argument('--repo',type=Path,required=True)
p.add_argument('--candidate',required=True)
p.add_argument('--physical',default='b488fe43aec90962adb2e5a6bcca04153d142092')
p.add_argument('--pump',default='f4be372ae4b013c767eb7f5eb0e43aad1cdb41fe')
p.add_argument('--parent',default='37b92f20e20ce8a7b751660d56d636a0ce1660e7')
a=p.parse_args();root=Path(__file__).resolve().parent

def git(*args):return subprocess.check_output(['git','-C',str(a.repo),*args])
def commit(ref):return git('rev-parse',ref+'^{commit}').decode().strip()
def snapshot(sha,target):
 target.mkdir(parents=True,exist_ok=False)
 data=git('archive',sha,'src','Directory.Build.props','Directory.Packages.props','global.json')
 with tarfile.open(fileobj=io.BytesIO(data)) as archive:archive.extractall(target,filter='data')
def hashes(folder):return {str(p.relative_to(folder)):hashlib.sha256(p.read_bytes()).hexdigest() for p in folder.rglob('*') if p.is_file()}
physical,pump,parent,candidate=map(commit,[a.physical,a.pump,a.parent,a.candidate])
baseline=root/'arms/published-prerequisites';snapshot(physical,baseline)
patch=git('diff','--binary',parent,pump,'--','src')
subprocess.run(['git','apply','-'],input=patch,cwd=baseline,check=True)
expected=json.loads((root/'expected-prerequisite-sources.json').read_text())
actual=hashes(baseline)
differences=sorted(name for name in actual.keys()|expected.keys() if actual.get(name)!=expected.get(name))
if differences:raise RuntimeError('Prerequisite source identity mismatch: '+str(differences))
feature=root/'arms/candidate';snapshot(candidate,feature)
dispatch='src/SharpLink.Server/SharpLinkServer.InvocationDispatch.cs'
if (feature/dispatch).read_bytes() != (baseline/dispatch).read_bytes():
 raise RuntimeError('Candidate changed immediate/queued invocation dispatch; this no-queue probe needs renewed scope review.')
manifest={'published-prerequisites':{'Physical':physical,'Pump':pump,'PumpDiffParent':parent,'PumpPatchSha256':hashlib.sha256(patch).hexdigest(),'Files':actual},'candidate':{'Base':candidate,'SourceTree':git('rev-parse',candidate+':src').decode().strip(),'Files':hashes(feature)}}
(root/'source-provenance.json').write_text(json.dumps(manifest,indent=2)+'\n')
print('Production-only prerequisite identity verified; source snapshots prepared.')
