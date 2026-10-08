#!/usr/bin/env python3
"""Prepare untouched shipping G2 and one separately identified diagnostic tree."""
import argparse
import hashlib
import json
import pathlib
import shutil
import subprocess

kit = pathlib.Path(__file__).resolve().parent
source = kit.parents[1]
spec = json.loads((kit / 'adapter.json').read_text())
recipe = '06b737df0d9aab66caed9c446a572b3a8ce4efae'
parser = argparse.ArgumentParser()
parser.add_argument('destination', type=pathlib.Path)
parser.add_argument('--shipping-only', action='store_true')
args = parser.parse_args()
if not args.shipping_only:
    assert spec['audit_approved'] and spec['diagnostic_tree'] and spec['flag_name'], 'Final reviewed diagnostic adapter is not pinned'
    assert spec['on_value'] != spec['off_value']
    for name, expected in spec['author_route_proof_sha256'].items():
        assert hashlib.sha256((kit / name).read_bytes()).hexdigest() == expected, name
    assert hashlib.sha256((kit / spec['adapter_patch']).read_bytes()).hexdigest() == spec['adapter_patch_sha256']
assert hashlib.sha256((kit / 'shipping-g2.patch').read_bytes()).hexdigest() == spec['shipping_patch_sha256']
destination = args.destination.resolve()
destination.mkdir(parents=True, exist_ok=True)
identities = {}
for label in ('shipping',) if args.shipping_only else ('shipping', 'diagnostic'):
    root = destination / label
    subprocess.run(['git', 'worktree', 'add', '--detach', str(root), recipe], cwd=source, check=True)
    subprocess.run(['git', 'apply', '--check', '--index', str(kit / 'shipping-g2.patch')], cwd=root, check=True)
    subprocess.run(['git', 'apply', '--index', str(kit / 'shipping-g2.patch')], cwd=root, check=True)
    tree = lambda: subprocess.check_output(['git', 'write-tree'], cwd=root, text=True).strip()
    assert tree() == spec['shipping_tree'], 'Untouched G2 identity changed'
    if label == 'diagnostic':
        subprocess.run(['git', 'apply', '--check', '--index', str(kit / spec['adapter_patch'])], cwd=root, check=True)
        subprocess.run(['git', 'apply', '--index', str(kit / spec['adapter_patch'])], cwd=root, check=True)
        assert tree() == spec['diagnostic_tree']
        paths = subprocess.check_output(['git', 'diff', '--name-only', spec['shipping_tree'], tree()], cwd=root, text=True).splitlines()
        assert paths and all(path.startswith('src/SharpLink.Runtime/') or path == 'test/SharpLink.UnitTests/Runtime/QuiescentDiagnosticModeTests.cs' for path in paths), paths
    harnesses = {}
    for path in ('test/SharpLink.Benchmarks/GeneratedAbiStreamingEvidenceRunner.cs', 'test/SharpLink.StreamLoadTest/Program.cs'):
        expected = subprocess.check_output(['git', 'show', f'{recipe}:{path}'], cwd=root)
        assert (root / path).read_bytes() == expected, path
        harnesses[path] = hashlib.sha256(expected).hexdigest()
    changed = subprocess.check_output(['git', 'diff', '--cached', '--name-only'], cwd=root, text=True).splitlines()
    patch = subprocess.check_output(['git', 'diff', '--cached', '--binary'], cwd=root)
    proof = dict(label=label, disposable_tree=tree(), shipping_tree=spec['shipping_tree'], recipe=recipe,
        patch_sha256=hashlib.sha256(patch).hexdigest(), harness_sha256=harnesses,
        source_blobs={path: subprocess.check_output(['git', 'hash-object', path], cwd=root, text=True).strip() for path in changed},
        boundary='Untouched G2 control' if label == 'shipping' else 'Diagnostic-only adapter; one build shared by ON and OFF; never a shipping candidate')
    out = root / 'artifacts/first-receive-integration'
    out.mkdir(parents=True, exist_ok=True)
    (out / 'provenance.json').write_text(json.dumps(proof, indent=2) + '\n')
    (out / 'candidate.patch').write_bytes(patch)
    for path in changed:
        target = out / 'sources' / path
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(root / path, target)
    identities[label] = proof
(destination / 'identities.json').write_text(json.dumps(identities, indent=2) + '\n')
print(json.dumps(identities, indent=2))
