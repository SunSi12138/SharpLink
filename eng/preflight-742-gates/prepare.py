#!/usr/bin/env python3
"""Reconstruct archived N, then H=N+directional gates, independently of CI HEAD."""
import argparse
import hashlib
import json
import pathlib
import shutil
import subprocess

KIT = pathlib.Path(__file__).resolve().parent
SOURCE = KIT.parents[1]
RECIPE = '06b737df0d9aab66caed9c446a572b3a8ce4efae'
RECONSTRUCTION_REVISION = '1082dec14b9d0e3bbe43a823227e69d80a808069'
DEV = '0fe26024b114bb6e78411a9b86276086c045d03d'
N_TREE = '4c5943fec3a85089cefa8a1b6dc8c0f6502567ce'
H_TREE = '522585079b97c99f7986eb50f3ed6c69d4086bfb'
GATE_SHA256 = '02490bd5382b4d7bc7ff50e9a578bae874ae1d8dd9b81ca0e0dc0aae64a5ced9'


def run(root, *command):
    subprocess.run(command, cwd=root, check=True)


def git(root, *args):
    return subprocess.check_output(['git', *args], cwd=root, text=True).strip()


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('destination', type=pathlib.Path)
parser.add_argument('--include-dev', action='store_true')
args = parser.parse_args()
destination = args.destination.resolve()
destination.mkdir(parents=True, exist_ok=True)
# Verify the existing fixed recipe rather than silently accepting future edits.
recipe_files = ['prepare.py', 'patches.json', 'send.patch', 'receive.patch', 'tail.patch']
recipe_hashes = {}
for name in recipe_files:
    relative = f'eng/preflight-742-redesign/{name}'
    expected = subprocess.check_output(['git', 'show', f'{RECONSTRUCTION_REVISION}:{relative}'], cwd=SOURCE)
    assert (SOURCE / relative).read_bytes() == expected, f'Pinned reconstruction changed: {relative}'
    recipe_hashes[relative] = hashlib.sha256(expected).hexdigest()
assert digest(KIT / 'gates.patch') == GATE_SHA256, 'Directional gate patch changed'
assert digest(SOURCE / 'eng/preflight-735-prepare-native-rpc.py') == '8fe332d89dea963424cb0f38893d67771efa5907b9f1c4ee4b3ecaa6a61af3fb', 'Original JSON-only NativeAOT adapter changed'
reconstruction = destination / 'n-reconstruction'
run(SOURCE, 'python3', 'eng/preflight-742-redesign/prepare.py', str(reconstruction), '--patches',
    *(str(SOURCE / 'eng/preflight-742-redesign' / f'{name}.patch') for name in ('send', 'receive', 'tail')))
control = reconstruction / 'control'
old_n = reconstruction / 'candidate'
assert git(old_n, 'write-tree') == N_TREE, 'N must match archived full tree, not the e834+38 overlay tree'
n_patch = old_n / 'artifacts/first-receive-integration/candidate.patch'
candidate = destination / 'candidate'
run(SOURCE, 'git', 'worktree', 'add', '--detach', str(candidate), RECIPE)
for patch, expected_tree in ((n_patch, N_TREE), (KIT / 'gates.patch', H_TREE)):
    run(candidate, 'git', 'apply', '--check', '--index', str(patch))
    run(candidate, 'git', 'apply', '--index', str(patch))
    assert git(candidate, 'write-tree') == expected_tree, f'Unexpected tree after {patch.name}'
# The only maintainability change is the explanatory reason on this one entry.
# Compare all other parsed data, including every numeric allowance, exactly.
old_budget = json.loads((old_n / 'eng/maintainability/baseline.json').read_text())
new_budget = json.loads((candidate / 'eng/maintainability/baseline.json').read_text())
def without_reasons(value):
    if isinstance(value, dict):
        return {key: without_reasons(item) for key, item in value.items() if key != 'reason'}
    if isinstance(value, list):
        return [without_reasons(item) for item in value]
    return value
assert without_reasons(old_budget) == without_reasons(new_budget), 'Maintainability allowances changed'
paths = git(candidate, 'diff', '--cached', '--name-only').splitlines()
assert all(path.startswith(('src/', 'test/')) or path in (
    'doc/adr/0002-directional-flow-control-gates.md', 'eng/maintainability/baseline.json') for path in paths), paths
patch = subprocess.check_output(['git', 'diff', '--cached', '--binary'], cwd=candidate)
proof = dict(label='H', preflight_head=git(SOURCE, 'rev-parse', 'HEAD'),
    reconstruction_recipe=RECIPE, reconstruction_revision=RECONSTRUCTION_REVISION,
    recipe_sha256=recipe_hashes, parent_n_tree=N_TREE, disposable_tree=H_TREE,
    parent_n_patch_sha256=digest(n_patch), incremental_gate_patch_sha256=GATE_SHA256,
    patch_sha256=hashlib.sha256(patch).hexdigest(),
    source_blobs={path: git(candidate, 'hash-object', path) for path in paths},
    change='N plus bounded directional StreamFlowController gates',
    maintainability_budget_data_unchanged=True,
    constructor_cost='Second Lock has a fixed controller construction cost; measured separately, never subtracted from production results.',
    acceptance='Collection is not performance acceptance. Preserve all original dev/H cells and negative results.')
out = candidate / 'artifacts/first-receive-integration'
out.mkdir(parents=True, exist_ok=True)
(out / 'provenance.json').write_text(json.dumps(proof, indent=2) + '\n')
(out / 'candidate.patch').write_bytes(patch)
for path in paths:
    target = out / 'sources' / path
    target.parent.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(candidate / path, target)
identities = dict(control=json.loads((control / 'artifacts/first-receive-integration/provenance.json').read_text()),
    old_n=json.loads((old_n / 'artifacts/first-receive-integration/provenance.json').read_text()), candidate=proof)
if args.include_dev:
    dev = destination / 'dev'
    run(SOURCE, 'git', 'worktree', 'add', '--detach', str(dev), DEV)
    identities['dev'] = dict(label='D', revision=DEV, disposable_tree=git(dev, 'write-tree'))
    # Original acceptance uses one identical measurement harness with each runtime.
    harnesses = ('test/SharpLink.Benchmarks/GeneratedAbiStreamingEvidenceRunner.cs',
                 'test/SharpLink.StreamLoadTest/Program.cs')
    copied = {}
    for path in harnesses:
        expected = subprocess.check_output(['git', 'show', f'{RECIPE}:{path}'], cwd=SOURCE)
        assert (candidate / path).read_bytes() == (old_n / path).read_bytes() == expected, path
        (dev / path).write_bytes(expected)
        copied[path] = hashlib.sha256(expected).hexdigest()
    identities['dev']['common_measurement_harness_sha256'] = copied
(destination / 'identities.json').write_text(json.dumps(identities, indent=2) + '\n')
print(json.dumps(identities, indent=2))
