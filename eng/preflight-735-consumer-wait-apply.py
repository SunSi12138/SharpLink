#!/usr/bin/env python3
"""One-variable consumer wait experiment on top of the pinned first-receive candidate."""
import hashlib
import json
import pathlib
import subprocess

ROOT=pathlib.Path(__file__).resolve().parents[1]
OUT=ROOT/'artifacts/first-receive-integration'
name='src/SharpLink.Runtime/PooledAsyncStreamDispatcher.cs'
expected='9b5e6a710b8a15e167545bc0674ed9eaf3029da5'

def git(*args):
    return subprocess.check_output(['git',*args],cwd=ROOT,text=True).strip()

if git('hash-object',name)!=expected:
    raise RuntimeError('Unreviewed consumer-dispatcher source')
text=(ROOT/name).read_text()
anchor='    private async ValueTask<bool> SlowMoveNextAsync(long consumerLeaseState)\n'
if text.count(anchor)!=1:
    raise RuntimeError('Expected one consumer wait method')
text=text.replace(anchor,
    '    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]\n'+anchor,1)
(ROOT/name).write_text(text)
old=json.loads((OUT/'provenance.json').read_text())
(OUT/'unpooled-integration-provenance.json').write_text(json.dumps(old,indent=2)+'\n')
paths=sorted(set(old['source_blobs'])|{name,'test/SharpLink.UnitTests/Runtime/PooledDispatcherSlowWaitTests.cs'})
git('add','--',*paths)
patch=subprocess.check_output(['git','diff','--cached','--binary'],cwd=ROOT)
(OUT/'candidate.patch').write_bytes(patch)
provenance=dict(control=old['control'],preflight_head=git('rev-parse','HEAD'),
    disposable_tree=git('write-tree'),source_blobs={p:git('hash-object',p) for p in paths},
    previous_patch_sha256=old['patch_sha256'],patch_sha256=hashlib.sha256(patch).hexdigest(),
    consumer_change='only SlowMoveNextAsync builder; method body and dispatcher fields unchanged',
    acceptance='pending correctness, retention, uninstru... (diagnostic evidence is not Go)')
(OUT/'provenance.json').write_text(json.dumps(provenance,indent=2)+'\n')
(OUT/'candidate-tree.txt').write_text(provenance['disposable_tree']+'\n')
for p in paths:
    target=OUT/'sources'/p
    target.parent.mkdir(parents=True,exist_ok=True)
    target.write_bytes((ROOT/p).read_bytes())
print(json.dumps(provenance,indent=2))
