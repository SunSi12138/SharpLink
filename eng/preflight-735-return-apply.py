#!/usr/bin/env python3
"""Replace bool/out validation with nullable return on the verified consumer-only candidate."""
import hashlib
import json
import pathlib
import subprocess

ROOT=pathlib.Path(__file__).resolve().parents[1]
OUT=ROOT/'artifacts/first-receive-integration'
def git(*args):
    return subprocess.check_output(['git',*args],cwd=ROOT,text=True).strip()
old=json.loads((OUT/'provenance.json').read_text())
for name,sha in old['source_blobs'].items():
    if git('hash-object',name)!=sha:raise RuntimeError(f'Unverified consumer source: {name}')
name='src/SharpLink.Runtime/StreamFlowController.cs'
expected=git('rev-parse',f'e834d3c28c87ad496989af925515cf21babd308d:{name}')
if git('hash-object',name)!=expected:raise RuntimeError('Unexpected receive controller')
patch=(ROOT/'eng/preflight-735-native-return.patch').read_bytes()
if hashlib.sha256(patch).hexdigest()!='affb4b795a6143a946fc91edc6105ff2badff29db6477cafb828d23828bb5617':
    raise RuntimeError('Unexpected receive-return patch')
subprocess.run(['git','apply','--check','--index','-'],input=patch,cwd=ROOT,check=True)
subprocess.run(['git','apply','--index','-'],input=patch,cwd=ROOT,check=True)
paths=sorted(set(old['source_blobs'])|{name,'test/SharpLink.UnitTests/Runtime/StreamFlowControllerTests.ResolvedStateLease.cs'})
git('add','--',*paths)
final=subprocess.check_output(['git','diff','--cached','--binary'],cwd=ROOT)
(OUT/'consumer-only-provenance.json').write_text(json.dumps(old,indent=2)+'\n')
(OUT/'candidate.patch').write_bytes(final)
identity=dict(control=old['control'],preflight_head=git('rev-parse','HEAD'),
    disposable_tree=git('write-tree'),source_blobs={p:git('hash-object',p) for p in paths},
    patch_sha256=hashlib.sha256(final).hexdigest(),
    change='consumer-only pooling plus nullable receive-state validation return; no forced inlining',
    acceptance='same full populations; no lifecycle or credit semantic change; pending joint acceptance')
(OUT/'provenance.json').write_text(json.dumps(identity,indent=2)+'\n')
for name in paths:
    target=OUT/'sources'/name;target.parent.mkdir(parents=True,exist_ok=True);target.write_bytes((ROOT/name).read_bytes())
print(json.dumps(identity,indent=2))
