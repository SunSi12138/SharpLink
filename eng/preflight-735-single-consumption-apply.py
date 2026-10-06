#!/usr/bin/env python3
"""Repair single-use client ValueTask forwarding and calibrate the context-retention oracle."""
import base64
import gzip
import hashlib
import json
import pathlib
import subprocess
import sys

ROOT=pathlib.Path(__file__).resolve().parents[1]
OUT=ROOT/'artifacts/first-receive-integration'
CLIENTS=('src/SharpLink.Client/SharpLinkClient.Invokers.cs','src/SharpLink.Client/SharpLinkClient.Telemetry.cs')
EXPECTED={
    CLIENTS[0]:'f7322022595a79e2261af23f9916c095cc5754bc',
    CLIENTS[1]:'0d6d3022870ffafbc1b7a9cca924350fb27e42b4',
    'test/SharpLink.UnitTests/Runtime/PooledDispatcherSlowWaitTests.cs':'72528915f81552239d30cd8f63d5d2493cfb6c69',
}
FINAL={
    CLIENTS[0]:'0276b5b882c24f089cb4c992a3375da66fecd80c',
    CLIENTS[1]:'5c5fc68c86f51787973332fa6defe8c0c2116b3c',
    'test/SharpLink.UnitTests/Runtime/PooledDispatcherSlowWaitTests.cs':'466a2a4966f02d540464f272d951a2a0fb5e48bd',
    'test/SharpLink.UnitTests/Client/ClientStreamValueTaskConsumptionTests.cs':'1a6c98e9f80fb9cb0710a2d6781dc25731998e09',
}
def git(*args):
    return subprocess.check_output(['git',*args],cwd=ROOT,text=True).strip()

mode=sys.argv[1] if len(sys.argv)>1 else 'apply'
if mode=='negative-clients':
    for name in CLIENTS:
        if git('hash-object',name)!=FINAL[name]:
            raise RuntimeError('Negative control requires the verified final source first')
        (ROOT/name).write_bytes(subprocess.check_output(['git','show',f'e834d3c28c87ad496989af925515cf21babd308d:{name}'],cwd=ROOT))
        if git('hash-object',name)!=EXPECTED[name]:
            raise RuntimeError('Wrong negative-control source')
    print('Restored exactly the two original wrapper sources; runtime builder and tests unchanged.')
    sys.exit(0)
if mode=='restore-clients':
    for name in CLIENTS:
        (ROOT/name).write_bytes((OUT/'sources'/name).read_bytes())
        if git('hash-object',name)!=FINAL[name]:
            raise RuntimeError('Failed to restore exact wrapper source')
    print('Restored exact validated single-consumption sources.')
    sys.exit(0)
if mode!='apply':
    raise SystemExit('expected apply, negative-clients or restore-clients')

for name,sha in EXPECTED.items():
    if git('hash-object',name)!=sha:
        raise RuntimeError(f'Unreviewed input: {name}')
encoded=(ROOT/'eng/preflight-735-single-consumption.patch.b64').read_text().strip()
patch=gzip.decompress(base64.b64decode(encoded,validate=True))
if hashlib.sha256(patch).hexdigest()!='c26b8d185f338214abf0c8e9d4f79731cb3b76449e737307f313858e64de0242':
    raise RuntimeError('Single-consumption source patch integrity mismatch')
subprocess.run(['git','apply','--check','--index','-'],input=patch,cwd=ROOT,check=True)
subprocess.run(['git','apply','--index','-'],input=patch,cwd=ROOT,check=True)
for name,sha in FINAL.items():
    if git('hash-object',name)!=sha:
        raise RuntimeError(f'Unexpected final source: {name}')
old=json.loads((OUT/'provenance.json').read_text())
(OUT/'pre-wrapper-fix-provenance.json').write_text(json.dumps(old,indent=2)+'\n')
paths=sorted(set(old['source_blobs'])|set(FINAL))
git('add','--',*paths)
complete_patch=subprocess.check_output(['git','diff','--cached','--binary'],cwd=ROOT)
(OUT/'candidate.patch').write_bytes(complete_patch)
(OUT/'single-consumption.patch').write_bytes(patch)
provenance=dict(control=old['control'],preflight_head=git('rev-parse','HEAD'),
    disposable_tree=git('write-tree'),source_blobs={p:git('hash-object',p) for p in paths},
    previous_patch_sha256=old['patch_sha256'],patch_sha256=hashlib.sha256(complete_patch).hexdigest(),
    change='consume the synchronous inner ValueTask exactly once and return a copied bool; lower consumer pooling unchanged',
    test_boundary='joined creator thread and inline wait completion exclude active helper frames; a deliberately rooted context must remain alive until released',
    acceptance='pending complete correctness, real-RPC and NativeAOT results; all previous failures retained')
(OUT/'provenance.json').write_text(json.dumps(provenance,indent=2)+'\n')
(OUT/'candidate-tree.txt').write_text(provenance['disposable_tree']+'\n')
for name in paths:
    target=OUT/'sources'/name
    target.parent.mkdir(parents=True,exist_ok=True)
    target.write_bytes((ROOT/name).read_bytes())
print(json.dumps(provenance,indent=2))
