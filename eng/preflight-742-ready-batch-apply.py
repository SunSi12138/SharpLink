#!/usr/bin/env python3
"""Apply the source-pinned, single-ready collector patch to the verified candidate."""
import hashlib
import json
import pathlib
import subprocess

ROOT = pathlib.Path(__file__).resolve().parents[1]
OUT = ROOT / 'artifacts/first-receive-integration'
EXPECTED = {
    'src/SharpLink.Client/SharpLinkClient.Invokers.cs': '0276b5b882c24f089cb4c992a3375da66fecd80c',
    'src/SharpLink.Client/SharpLinkClient.Telemetry.cs': '5c5fc68c86f51787973332fa6defe8c0c2116b3c',
    'src/SharpLink.Runtime/PooledAsyncStreamDispatcher.cs': '67d893f21f86cca2945724db3d2cf58230599a60',
    'src/SharpLink.Runtime/StreamFlowController.cs': '26321b693c3e2af5ae90a96951aea22d5e752520',
    'test/SharpLink.UnitTests/Client/ClientStreamValueTaskConsumptionTests.cs': '1a6c98e9f80fb9cb0710a2d6781dc25731998e09',
    'test/SharpLink.UnitTests/Runtime/PooledDispatcherSlowWaitTests.cs': '466a2a4966f02d540464f272d951a2a0fb5e48bd',
    'test/SharpLink.UnitTests/Runtime/StreamFlowControllerTests.ResolvedStateLease.cs': 'c087eaace770bc5ef03b2e846e238c0bcc188d8b',
}
def git(*args):
    return subprocess.check_output(['git', *args], cwd=ROOT, text=True).strip()
old = json.loads((OUT / 'provenance.json').read_text())
assert old['source_blobs'] == EXPECTED, old
for name, sha in EXPECTED.items():
    assert git('hash-object', name) == sha, name
patch = (ROOT / 'eng/preflight-742-ready-batch.patch').read_bytes()
assert hashlib.sha256(patch).hexdigest() == '077cb83b75469b600c8acfce4e45c635d5b12d1310de7434ecc25f59894ddf76'
subprocess.run(['git', 'apply', '--check', '--index', '-'], input=patch, cwd=ROOT, check=True)
subprocess.run(['git', 'apply', '--index', '-'], input=patch, cwd=ROOT, check=True)
paths = sorted(set(EXPECTED) | set(git('diff', '--cached', '--name-only').splitlines()))
assert all(p.startswith(('src/', 'test/')) for p in paths), paths
final = subprocess.check_output(['git', 'diff', '--cached', '--binary'], cwd=ROOT)
(OUT / 'hinted-control-provenance.json').write_text(json.dumps(old, indent=2) + '\n')
(OUT / 'candidate.patch').write_bytes(final)
identity = dict(control=old['control'], preflight_head=git('rev-parse', 'HEAD'),
    disposable_tree=git('write-tree'), source_blobs={p: git('hash-object', p) for p in paths},
    patch_sha256=hashlib.sha256(final).hexdigest(),
    change='verified helper-boundary candidate plus first-ready inline collector; overflow only for additional waiters',
    acceptance='unchanged full populations and gates; completion outside gate; no waiter pooling or lifecycle change; joint acceptance pending')
(OUT / 'provenance.json').write_text(json.dumps(identity, indent=2) + '\n')
for name in paths:
    target = OUT / 'sources' / name
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_bytes((ROOT / name).read_bytes())
print(json.dumps(identity, indent=2))
