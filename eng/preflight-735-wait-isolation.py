#!/usr/bin/env python3
"""Remove first-receive fusion from the validated pooled-wait candidate, not its safety fixes."""
import hashlib
import json
import pathlib
import subprocess

ROOT = pathlib.Path(__file__).resolve().parents[1]
OUT = ROOT/'artifacts/first-receive-integration'
CONTROL = 'e834d3c28c87ad496989af925515cf21babd308d'
KEEP = {
    'src/SharpLink.Client/SharpLinkClient.Invokers.cs': '0276b5b882c24f089cb4c992a3375da66fecd80c',
    'src/SharpLink.Client/SharpLinkClient.Telemetry.cs': '5c5fc68c86f51787973332fa6defe8c0c2116b3c',
    'src/SharpLink.Runtime/PooledAsyncStreamDispatcher.cs': '67d893f21f86cca2945724db3d2cf58230599a60',
    'test/SharpLink.UnitTests/Client/ClientStreamValueTaskConsumptionTests.cs': '1a6c98e9f80fb9cb0710a2d6781dc25731998e09',
    'test/SharpLink.UnitTests/Runtime/PooledDispatcherSlowWaitTests.cs': '466a2a4966f02d540464f272d951a2a0fb5e48bd',
}

def git(*args):
    return subprocess.check_output(['git', *args], cwd=ROOT, text=True).strip()

old = json.loads((OUT/'provenance.json').read_text())
if old['control'] != CONTROL or not set(KEEP) <= set(old['source_blobs']):
    raise RuntimeError('Expected the verified single-consumption pooled first-receive candidate')
for name, sha in old['source_blobs'].items():
    if git('hash-object', name) != sha:
        raise RuntimeError(f'Unverified reconstruction: {name}')
for name, sha in KEEP.items():
    if old['source_blobs'][name] != sha:
        raise RuntimeError(f'Unexpected retained consumer/wrapper source: {name}')

remove = sorted(set(old['source_blobs'])-set(KEEP))
subprocess.run(['git','restore','--source',CONTROL,'--staged','--worktree','--',*remove],cwd=ROOT,check=True)
subprocess.run(['git','add','--',*KEEP],cwd=ROOT,check=True)
changed = set(git('diff','--cached','--name-only').splitlines())
if changed != set(KEEP):
    raise RuntimeError(f'Unintended wait-only candidate files: {sorted(changed)}')
for name,sha in KEEP.items():
    if git('hash-object',name) != sha:
        raise RuntimeError(f'Lost retained source: {name}')
patch = subprocess.check_output(['git','diff','--cached','--binary'],cwd=ROOT)
(OUT/'fused-wait-control-provenance.json').write_text(json.dumps(old,indent=2)+'\n')
(OUT/'candidate.patch').write_bytes(patch)
identity = dict(control=CONTROL,preflight_head=git('rev-parse','HEAD'),
    disposable_tree=git('write-tree'),source_blobs=KEEP,
    patch_sha256=hashlib.sha256(patch).hexdigest(),
    removed_fusion_files=remove,
    change='only lower consumer-wait pooling plus two exactly-once ValueTask forwarding repairs',
    acceptance='incremental isolation; published route/credit/first-frame paths restored exactly, not a Go verdict')
(OUT/'provenance.json').write_text(json.dumps(identity,indent=2)+'\n')
(OUT/'candidate-tree.txt').write_text(identity['disposable_tree']+'\n')
# Do not leave reverted fusion source copies masquerading as candidate files.
for name in remove:
    (OUT/'sources'/name).unlink(missing_ok=True)
for name in KEEP:
    target=OUT/'sources'/name
    target.parent.mkdir(parents=True,exist_ok=True)
    target.write_bytes((ROOT/name).read_bytes())
print(json.dumps(identity,indent=2))
