#!/usr/bin/env python3
"""Reconstruct fixed C and proposed N independently of the publishing harness head."""
import argparse
import hashlib
import json
import pathlib
import shutil
import subprocess

p = argparse.ArgumentParser()
p.add_argument('destination', type=pathlib.Path)
p.add_argument('--include-dev', action='store_true')
p.add_argument('--patches', nargs='+', required=True)
a = p.parse_args()
source = pathlib.Path(__file__).resolve().parents[2]
destination = a.destination.resolve()
destination.mkdir(parents=True, exist_ok=True)
recipe = '06b737df0d9aab66caed9c446a572b3a8ce4efae'
control_tree = 'ea21543ca4d267938834ac24bead069aac2327c0'
def run(root, *command):
    subprocess.run(command, cwd=root, check=True)
def git(root, *args):
    return subprocess.check_output(['git', *args], cwd=root, text=True).strip()
patches = [pathlib.Path(name).resolve() for name in a.patches]
manifest = json.loads((pathlib.Path(__file__).resolve().parent / 'patches.json').read_text())
assert {p.name for p in patches} == set(manifest), 'Incomplete redesign patch set'
for patch in patches:
    assert hashlib.sha256(patch.read_bytes()).hexdigest() == manifest[patch.name], patch
identities = {}
labels = ['control', 'candidate']
if a.include_dev:
    labels.insert(0, 'dev')
for label in labels:
    root = destination / label
    revision = '0fe26024b114bb6e78411a9b86276086c045d03d' if label == 'dev' else recipe
    run(source, 'git', 'worktree', 'add', '--detach', str(root), revision)
    if label == 'dev':
        identities[label] = {'revision': revision}
        continue
    run(root, 'python3', 'eng/preflight-735-first-receive-apply.py')
    shutil.copyfile(root / 'eng/preflight-735-first-await-tests.cs', root / 'test/SharpLink.UnitTests/Runtime/StreamManagerFirstReceiveAwaitAllocationTests.cs')
    run(root, 'python3', 'eng/preflight-735-first-await-apply.py')
    shutil.copyfile(root / 'eng/preflight-735-consumer-wait-tests.cs', root / 'test/SharpLink.UnitTests/Runtime/PooledDispatcherSlowWaitTests.cs')
    for script in ('consumer-wait-apply', 'single-consumption-apply', 'wait-isolation', 'hint-apply'):
        run(root, 'python3', f'eng/preflight-735-{script}.py')
    run(root, 'python3', 'eng/preflight-742-ready-batch-apply.py')
    assert git(root, 'write-tree') == control_tree, label
    if label == 'candidate':
        for patch in patches:
            run(root, 'git', 'apply', '--check', '--index', str(patch))
            run(root, 'git', 'apply', '--index', str(patch))
    if label == 'candidate':
        assert git(root, 'write-tree') == '4c5943fec3a85089cefa8a1b6dc8c0f6502567ce', 'Unexpected integrated source tree'
    paths = git(root, 'diff', '--cached', '--name-only').splitlines()
    assert paths and all(path.startswith(('src/', 'test/')) for path in paths), paths
    patch = subprocess.check_output(['git', 'diff', '--cached', '--binary'], cwd=root)
    proof = dict(control='e834d3c28c87ad496989af925515cf21babd308d', preflight_head=git(source, 'rev-parse', 'HEAD'),
        reconstruction_recipe=recipe, prior_control_tree=control_tree, disposable_tree=git(root, 'write-tree'),
        source_blobs={path: git(root, 'hash-object', path) for path in paths},
        patch_sha256=hashlib.sha256(patch).hexdigest(),
        incremental_patches={p.name: hashlib.sha256(p.read_bytes()).hexdigest() for p in patches} if label == 'candidate' else {},
        change='whole lifecycle/send ownership redesign' if label == 'candidate' else 'unchanged nine-blob ready-batch control',
        acceptance='pending unchanged dev acceptance; screen is diagnostic only')
    out = root / 'artifacts/first-receive-integration'
    (out / 'provenance.json').write_text(json.dumps(proof, indent=2) + '\n')
    (out / 'candidate.patch').write_bytes(patch)
    for path in paths:
        target = out / 'sources' / path
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(root / path, target)
    identities[label] = proof
if a.include_dev:
    for path in ('test/SharpLink.Benchmarks/GeneratedAbiStreamingEvidenceRunner.cs', 'test/SharpLink.StreamLoadTest/Program.cs'):
        content = subprocess.check_output(['git', 'show', f'{recipe}:{path}'], cwd=source)
        for label in labels:
            assert label == 'dev' or (destination / label / path).read_bytes() == content
            (destination / label / path).write_bytes(content)
(destination / 'identities.json').write_text(json.dumps(identities, indent=2) + '\n')
print(json.dumps(identities, indent=2))
