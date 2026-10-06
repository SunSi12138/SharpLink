#!/usr/bin/env python3
"""Second compact-route candidate: inline full-lease reconstruction, not a gate change."""
import json
import pathlib
import runpy
import subprocess

ROOT = pathlib.Path(__file__).resolve().parents[1]
base_script = ROOT/'eng/preflight-735-compact-route.py'
expected_script = '23f6c6db1a40f7bdc776141e3c7a3aafada6eb46'
actual = subprocess.check_output(['git','hash-object',str(base_script)],cwd=ROOT,text=True).strip()
if actual != expected_script:
    raise RuntimeError(f'Unreviewed compact transform: {actual}')
runpy.run_path(str(base_script),run_name='__main__')
name = 'src/SharpLink.Runtime/StreamManager.Routing.cs'
path = ROOT/name
expected = '6892964be060052f8bfa140139274c13de1cde33'
actual = subprocess.check_output(['git','hash-object',name],cwd=ROOT,text=True).strip()
if actual != expected:
    raise RuntimeError(f'Unreviewed unhinted compact source: {actual}')
text = path.read_text()
anchor = '        internal StreamFlowController.ResolvedReceiveCreditLease GetReceiveCreditLease(\n'
if text.count(anchor) != 1:
    raise RuntimeError('Expected exactly one full-lease reconstruction boundary')
text = text.replace(anchor,
    '        [MethodImpl(MethodImplOptions.AggressiveInlining)]\n'+anchor,1)
path.write_text(text)
out = ROOT/'artifacts/compact-route'
(out/'StreamManager.Routing.cs').write_text(text)
blobs = {p:subprocess.check_output(['git','hash-object',p],cwd=ROOT,text=True).strip()
         for p in ('src/SharpLink.Runtime/StreamManager.cs',name)}
(out/'candidate-blobs.json').write_text(json.dumps(blobs,indent=2)+'\n')
print(json.dumps({'variant':'compact-inline','candidate_blobs':blobs},indent=2))
