#!/usr/bin/env python3
"""Apply the exact locally tested first-receive integration to a disposable checkout."""
import gzip
import hashlib
import json
import pathlib
import subprocess

ROOT = pathlib.Path(__file__).resolve().parents[1]
OUT = ROOT / 'artifacts/first-receive-integration'
CONTROL = 'e834d3c28c87ad496989af925515cf21babd308d'
PARTS = [
    '3f76d4c4d4ea0f6749dc9391dc2e04d11bc40c10',
    'f31b8d82198af6252ab19c87abb2afa9fc596c2c',
    '098d7227959e9e6545a9031f211d33cd11ecb1d4',
    '5d85b0e97c872beb07cf8b4ca630bc9895323819',
    '9d5c0d43d5e442dd3c635b2eb7fe933a5057c249',
]
SOURCES = {
    'src/SharpLink.Runtime/RpcSession.FirstReceive.cs': 'aeb61b504feaafd4ccda06fc39ae6b16a2d477cb',
    'src/SharpLink.Runtime/RpcSession.cs': '7c1796bfdafb3b676591dc7f6cbcdb4ecbd905b1',
    'src/SharpLink.Runtime/StreamFlowController.ReceiveAdmission.cs': 'b32cf734cd0a5b3d480a9ac71258fd5a9dca183e',
    'src/SharpLink.Runtime/StreamManager.FirstReceive.cs': 'b845d749e338b436cfa825e4b101e9b80b92b0c3',
    'src/SharpLink.Runtime/StreamManager.Routing.cs': '1fb52d9d5f61dbcb751b803bda6dede3c3eaa029',
    'src/SharpLink.Runtime/StreamManager.cs': '50ea9e79d14dbb525ea0b11a5eb8cacef9bd93c0',
    'test/SharpLink.UnitTests/Runtime/StreamManagerFirstReceiveIntegrationTests.cs': 'f1e781a52482f3d46a1068b4b00e785d3c296c36',
}


def git(*args):
    return subprocess.check_output(['git', *args], cwd=ROOT, text=True).strip()


OUT.mkdir(parents=True, exist_ok=True)
git('diff', '--exit-code', CONTROL, '--', 'src', 'test')
compressed = bytearray()
for index, expected in enumerate(PARTS):
    part = ROOT / f'eng/preflight-735-first-receive/patch.part-{index:02}'
    if git('hash-object', str(part)) != expected:
        raise RuntimeError(f'Transfer part mismatch: {part}')
    compressed.extend(part.read_bytes())
if hashlib.sha256(compressed).hexdigest() != '64b156d1be33109f28987444b3e011825f69825724aa37941599c66befc6572d':
    raise RuntimeError('Compressed patch checksum mismatch')
patch = gzip.decompress(compressed)
if hashlib.sha256(patch).hexdigest() != '111f6a122b9481f732e319c42ab9f5d6aefddf6b0a845e26cd22b0b9a7440279':
    raise RuntimeError('Plain patch checksum mismatch')
path = OUT / 'candidate.patch'
path.write_bytes(patch)
git('apply', '--check', str(path))
git('apply', '--index', str(path))
actual = {name: git('hash-object', name) for name in SOURCES}
if actual != SOURCES:
    raise RuntimeError(f'Locally validated source blobs do not match: {actual}')
provenance = dict(control=CONTROL, preflight_head=git('rev-parse', 'HEAD'),
                  disposable_tree=git('write-tree'), source_blobs=actual,
                  local_runtime_test_tree='a8044b0a029aaf8d4254b0c8131c8de942ea72f5',
                  patch_sha256=hashlib.sha256(patch).hexdigest(),
                  production_scope='direct consumption-aware routes only; PreAdmission remains eager',
                  acceptance='blocked; local TCP Server1x16 regression retained')
(OUT / 'provenance.json').write_text(json.dumps(provenance, indent=2) + '\n')
(OUT / 'candidate-tree.txt').write_text(provenance['disposable_tree'] + '\n')
print(json.dumps(provenance, indent=2))
