#!/usr/bin/env python3
"""Reconstruct immutable dev/R source trees; never use the execution branch as a treatment."""
import argparse, hashlib, json, pathlib, shutil, subprocess
KIT=pathlib.Path(__file__).resolve().parent
SOURCE=KIT.parents[1]
DEV='0fe26024b114bb6e78411a9b86276086c045d03d'
RECIPE='06b737df0d9aab66caed9c446a572b3a8ce4efae'
IDENTITIES={'dev':DEV,'candidate':'df4383c7d00ee4a31c129ae42958e013f4ff5385'}
PATCHES={'full-g2.patch':'5217ed02fbeb260f68413fba7f4eac21eb197e37bdff31a612fb4973ccf5139b','production-r.patch':'1e1699632cc556481e7cde1df2b3bb44f9a7c0ff6a7ea45fc0ef0a6f148628ff'}
HARNESS=('test/SharpLink.Benchmarks/GeneratedAbiStreamingEvidenceRunner.cs','test/SharpLink.StreamLoadTest/Program.cs')
def git(root,*args): return subprocess.check_output(['git',*args],cwd=root,text=True).strip()
def sha(path): return hashlib.sha256(path.read_bytes()).hexdigest()
def save(path,value): path.parent.mkdir(parents=True,exist_ok=True);path.write_text(json.dumps(value,indent=2)+'\n')
def check_roots(roots):
    for label in ('candidate',):
        root=roots[label]
        assert git(root,'write-tree')==IDENTITIES[label], 'Wrong production tree or test overlay: '+label
        assert not git(root,'diff','--name-only'), 'Unstaged mutation: '+label
        assert not git(root,'ls-files','--others','--exclude-standard','--','src','test')
    d=roots['dev']
    assert git(d,'rev-parse','HEAD')==DEV
    assert git(d,'write-tree')==git(d,'rev-parse',DEV+'^{tree}')
    assert set(git(d,'diff','--name-only').splitlines())<=set(HARNESS)
    assert not git(d,'ls-files','--others','--exclude-standard','--','src','test')
    for name in HARNESS:
        assert all((root/name).read_bytes()==(roots['candidate']/name).read_bytes() for root in roots.values()),name
    return dict(IDENTITIES)
def prepare(destination,out):
    for name,digest in json.loads((KIT/'dependencies.json').read_text()).items(): assert sha(SOURCE/name)==digest,name
    for name,digest in PATCHES.items(): assert sha(KIT/name)==digest,name
    destination.mkdir(parents=True,exist_ok=False)
    roots={label:destination/label for label in IDENTITIES}
    for label,root in roots.items():
        subprocess.run(['git','worktree','add','--detach',str(root),DEV if label=='dev' else RECIPE],cwd=SOURCE,check=True)
        for patch in (() if label=='dev' else ('full-g2.patch','production-r.patch')):
            subprocess.run(['git','apply','--check','--index',str(KIT/patch)],cwd=root,check=True)
            subprocess.run(['git','apply','--index',str(KIT/patch)],cwd=root,check=True)
    for name in HARNESS: shutil.copyfile(roots['candidate']/name,roots['dev']/name)
    check_roots(roots)
    proof=dict(identities=IDENTITIES,reconstruction_recipe=RECIPE,execution_commit=git(SOURCE,'rev-parse','HEAD'),patch_sha256=PATCHES,
        roots={k:str(v) for k,v in roots.items()},production_src_subtrees={k:git(v,'rev-parse',(DEV if k=='dev' else IDENTITIES[k])+':src') for k,v in roots.items()},
        original_helper_commit='211c7a94f7107158cd69cfb251ca33156487c48e',inherited_helper_sha256=json.loads((KIT/'dependencies.json').read_text()),
        common_measurement_harness_sha256={n:sha(roots['candidate']/n) for n in HARNESS},
        helper_sha256={str(p.relative_to(SOURCE)):sha(p) for p in [*(p for p in KIT.rglob('*') if p.is_file() and '__pycache__' not in p.parts),SOURCE/'.github/workflows/742-final-r.yml']},
        boundary='Frozen R full original validation coverage. Collection success is not performance acceptance; all earlier regressions remain evidence.')
    save(out/'identities.json',proof);save(destination/'identities.json',proof)
    for label,root in roots.items(): save(out/(label+'-source-sha256.json'),{n:sha(root/n) for n in git(root,'ls-files','--','src','test').splitlines()})
    for name in PATCHES: shutil.copyfile(KIT/name,out/name)
    (out/'dev-common-harness.patch').write_text(git(roots['dev'],'diff','--binary')+'\n')
    print(json.dumps(proof,indent=2))
if __name__=='__main__':
    p=argparse.ArgumentParser();p.add_argument('destination',type=pathlib.Path);p.add_argument('output',type=pathlib.Path);a=p.parse_args();prepare(a.destination.resolve(),a.output.resolve())
