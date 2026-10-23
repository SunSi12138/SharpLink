#!/usr/bin/env python3
"""Create production-only source snapshots from public commits; never invoke repository tests."""
import argparse,hashlib,io,json,os,subprocess,tarfile
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
# An archive has no .git. In hosted CI it is nested inside the feature checkout;
# allowing repository discovery makes git apply filter paths against that outer
# checkout's subdirectory prefix and silently skip this production patch.
# Keep application strictly rooted in the archived arm, independent of inherited
# Git routing variables. The unchanged complete hash manifest remains the gate.
apply_env={key:value for key,value in os.environ.items() if not key.startswith('GIT_')}
apply_env['GIT_CEILING_DIRECTORIES']=str(baseline.parent.resolve())
subprocess.run(['git','apply','--whitespace=nowarn','-'],input=patch,cwd=baseline,
               env=apply_env,check=True)
expected=json.loads((root/'expected-prerequisite-sources.json').read_text())
actual=hashes(baseline)
differences=sorted(name for name in actual.keys()|expected.keys() if actual.get(name)!=expected.get(name))
identity={'Status':'PrerequisiteSourceIdentityMismatch' if differences else 'PrerequisiteSourceIdentityVerified',
          'published-prerequisites':{'Physical':physical,'Pump':pump,'PumpDiffParent':parent,
            'PumpPatchSha256':hashlib.sha256(patch).hexdigest()},
          'ExpectedManifestSha256':hashlib.sha256((root/'expected-prerequisite-sources.json').read_bytes()).hexdigest(),
          'Differences':[{'Path':name,'ExpectedSha256':expected.get(name),'ActualSha256':actual.get(name)} for name in differences]}
# The existing always-upload step includes this path, including failed preparation.
# A failed diagnostic is explicitly not a verified baseline manifest.
(root/'source-provenance.json').write_text(json.dumps(identity,indent=2)+'\n')
if differences:raise RuntimeError('Prerequisite source identity mismatch: '+str(differences))
feature=root/'arms/candidate';snapshot(candidate,feature)
dispatch='src/SharpLink.Server/SharpLinkServer.InvocationDispatch.cs'
if (feature/dispatch).read_bytes() != (baseline/dispatch).read_bytes():
 raise RuntimeError('Candidate changed immediate/queued invocation dispatch; this no-queue probe needs renewed scope review.')
manifest={'Status':'SourceSnapshotsVerified','published-prerequisites':{'Physical':physical,'Pump':pump,'PumpDiffParent':parent,'PumpPatchSha256':hashlib.sha256(patch).hexdigest(),'Files':actual},'candidate':{'Base':candidate,'SourceTree':git('rev-parse',candidate+':src').decode().strip(),'Files':hashes(feature)}}
(root/'source-provenance.json').write_text(json.dumps(manifest,indent=2)+'\n')
print('Production-only prerequisite identity verified; source snapshots prepared.')
