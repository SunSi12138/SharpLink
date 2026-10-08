#!/usr/bin/env python3
"""Pool client pending-read wrappers on the verified consumer-only runtime."""
import hashlib
import json
import pathlib
import subprocess

ROOT = pathlib.Path(__file__).resolve().parents[1]
OUT = ROOT/'artifacts/first-receive-integration'
EXPECTED = {
    'src/SharpLink.Client/SharpLinkClient.Invokers.cs': '0276b5b882c24f089cb4c992a3375da66fecd80c',
    'src/SharpLink.Client/SharpLinkClient.Telemetry.cs': '5c5fc68c86f51787973332fa6defe8c0c2116b3c',
}
def git(*args):
    return subprocess.check_output(['git', *args], cwd=ROOT, text=True).strip()

old = json.loads((OUT/'provenance.json').read_text())
for name, sha in old['source_blobs'].items():
    if git('hash-object', name) != sha:
        raise RuntimeError(f'Unverified consumer-only source: {name}')
for name, sha in EXPECTED.items():
    if git('hash-object', name) != sha:
        raise RuntimeError(f'Unreviewed wrapper source: {name}')
    path = ROOT/name
    text = path.read_text()
    anchor = '        private async ValueTask<bool> AwaitMoveNextAsync(ValueTask<bool> move)'
    if text.count(anchor) != 1:
        raise RuntimeError(f'Unexpected wrapper anchor: {name}')
    path.write_text(text.replace(anchor,
        '        [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]\n'+anchor))
tests = {
    'ClientStreamWaitAllocationTests.cs': 'preflight-735-wrapper-pooling-tests.cs',
    'ClientStreamWaitRetentionTests.cs': 'preflight-735-wrapper-retention-tests.cs',
    'ClientStreamWaitTelemetryTests.cs': 'preflight-735-wrapper-telemetry-tests.cs',
    'ClientStreamWaitTestSupport.cs': 'preflight-735-wrapper-test-support.cs',
}
extra = set()
for target, source in tests.items():
    name = 'test/SharpLink.UnitTests/Client/' + target
    (ROOT/name).write_bytes((ROOT/'eng'/source).read_bytes())
    extra.add(name)
paths = sorted(set(old['source_blobs']) | extra)
git('add', '--', *paths)
patch = subprocess.check_output(['git','diff','--cached','--binary'], cwd=ROOT)
(OUT/'consumer-only-provenance.json').write_text(json.dumps(old, indent=2)+'\n')
(OUT/'candidate.patch').write_bytes(patch)
identity = dict(control=old['control'], preflight_head=git('rev-parse','HEAD'),
    disposable_tree=git('write-tree'), source_blobs={p:git('hash-object',p) for p in paths},
    patch_sha256=hashlib.sha256(patch).hexdigest(),
    change='consumer-only candidate plus pooled logical and telemetry pending-read wrappers',
    acceptance='three-arm incremental screen; no credit/routing changes; not a Go verdict')
(OUT/'provenance.json').write_text(json.dumps(identity, indent=2)+'\n')
for name in paths:
    target=OUT/'sources'/name
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_bytes((ROOT/name).read_bytes())
print(json.dumps(identity, indent=2))
