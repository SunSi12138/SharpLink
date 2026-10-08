#!/usr/bin/env python3
"""Reconstruct exact archived F, then apply only a final reviewed G increment."""
import argparse
import hashlib
import json
import pathlib
import shutil
import subprocess

KIT = pathlib.Path(__file__).resolve().parent
SOURCE = KIT.parents[1]
RECIPE = '06b737df0d9aab66caed9c446a572b3a8ce4efae'
REDESIGN_RECIPE = '1082dec14b9d0e3bbe43a823227e69d80a808069'
DEV = '0fe26024b114bb6e78411a9b86276086c045d03d'
N_TREE = '4c5943fec3a85089cefa8a1b6dc8c0f6502567ce'
PARENT = json.loads((KIT / 'parent-f.json').read_text())
SPEC = json.loads((KIT / 'candidate.json').read_text())


def run(root, *args):
    subprocess.run(args, cwd=root, check=True)


def git(root, *args):
    return subprocess.check_output(['git', *args], cwd=root, text=True).strip()


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def record(root, label, **extra):
    paths = git(root, 'diff', '--cached', '--name-only').splitlines()
    assert paths and all(path.startswith(('src/', 'test/')) or path == 'eng/maintainability/baseline.json' for path in paths)
    patch = subprocess.check_output(['git', 'diff', '--cached', '--binary'], cwd=root)
    proof = dict(label=label, preflight_head=git(SOURCE, 'rev-parse', 'HEAD'), reconstruction_recipe=RECIPE,
        disposable_tree=git(root, 'write-tree'), patch_sha256=hashlib.sha256(patch).hexdigest(),
        source_blobs={path: git(root, 'hash-object', path) for path in paths}, **extra)
    out = root / 'artifacts/first-receive-integration'
    out.mkdir(parents=True, exist_ok=True)
    (out / 'provenance.json').write_text(json.dumps(proof, indent=2) + '\n')
    (out / 'candidate.patch').write_bytes(patch)
    for path in paths:
        target = out / 'sources' / path
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(root / path, target)
    return proof


parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('destination', type=pathlib.Path)
parser.add_argument('--include-dev', action='store_true')
parser.add_argument('--parent-only', action='store_true', help='Validate only already-approved F; never creates or runs G.')
args = parser.parse_args()
if not args.parent_only:
    if not SPEC['candidate_tree'] or not SPEC['patches'] or not SPEC.get('audit_approved', False):
        raise SystemExit('Final G source/proof/audit not pinned. Local draft only; do not dispatch.')
    assert SPEC['parent_tree'] == PARENT['tree']
    for name, expected in SPEC['author_allocation_evidence'].items():
        assert digest(KIT / name) == expected, name
assert PARENT['parent_tree'] == N_TREE
assert digest(KIT / 'parent-f.patch') == PARENT['incremental_patch_sha256']
for name in ('prepare.py', 'patches.json', 'send.patch', 'receive.patch', 'tail.patch'):
    relative = f'eng/preflight-742-redesign/{name}'
    expected = subprocess.check_output(['git', 'show', f'{REDESIGN_RECIPE}:{relative}'], cwd=SOURCE)
    assert (SOURCE / relative).read_bytes() == expected, relative
assert digest(SOURCE / 'eng/preflight-735-prepare-native-rpc.py') == '8fe332d89dea963424cb0f38893d67771efa5907b9f1c4ee4b3ecaa6a61af3fb'
destination = args.destination.resolve()
destination.mkdir(parents=True, exist_ok=True)
reconstruction = destination / 'n-reconstruction'
run(SOURCE, 'python3', 'eng/preflight-742-redesign/prepare.py', str(reconstruction), '--patches',
    *(str(SOURCE / 'eng/preflight-742-redesign' / f'{name}.patch') for name in ('send', 'receive', 'tail')))
old_n = reconstruction / 'candidate'
assert git(old_n, 'write-tree') == N_TREE
prior_f = destination / 'prior-f'
run(SOURCE, 'git', 'worktree', 'add', '--detach', str(prior_f), RECIPE)
for patch, expected_tree in ((old_n / 'artifacts/first-receive-integration/candidate.patch', N_TREE),
                              (KIT / 'parent-f.patch', PARENT['tree'])):
    run(prior_f, 'git', 'apply', '--check', '--index', str(patch))
    run(prior_f, 'git', 'apply', '--index', str(patch))
    assert git(prior_f, 'write-tree') == expected_tree
f_proof = record(prior_f, 'F', parent_n_tree=N_TREE,
    incremental_patch_sha256=PARENT['incremental_patch_sha256'], reference_run=PARENT['reference_run'],
    change='Unchanged independently reviewed F0390 control', acceptance='Historical F screen remains a no-go; control only.')
assert f_proof['patch_sha256'] == PARENT['full_source_patch_sha256'], 'Original complete F source patch differs'
identities = dict(original_control=json.loads((reconstruction / 'control/artifacts/first-receive-integration/provenance.json').read_text()),
    old_n=json.loads((old_n / 'artifacts/first-receive-integration/provenance.json').read_text()), prior_f=f_proof)
roots = [prior_f]
if not args.parent_only:
    candidate = destination / 'candidate'
    run(SOURCE, 'git', 'worktree', 'add', '--detach', str(candidate), RECIPE)
    full_f = prior_f / 'artifacts/first-receive-integration/candidate.patch'
    run(candidate, 'git', 'apply', '--check', '--index', str(full_f))
    run(candidate, 'git', 'apply', '--index', str(full_f))
    assert git(candidate, 'write-tree') == PARENT['tree'], 'G must start from exact complete F'
    for entry in SPEC['patches']:
        patch = (KIT / entry['file']).resolve()
        assert patch.is_relative_to(KIT) and digest(patch) == entry['sha256'], entry
        run(candidate, 'git', 'apply', '--check', '--index', str(patch))
        run(candidate, 'git', 'apply', '--index', str(patch))
    assert git(candidate, 'write-tree') == SPEC['candidate_tree']
    def without_reasons(value):
        if isinstance(value, dict):
            return {key: without_reasons(item) for key, item in value.items() if key != 'reason'}
        if isinstance(value, list):
            return [without_reasons(item) for item in value]
        return value
    assert without_reasons(json.loads((prior_f / 'eng/maintainability/baseline.json').read_text())) == without_reasons(json.loads((candidate / 'eng/maintainability/baseline.json').read_text()))
    identities['candidate'] = record(candidate, 'G', parent_f_tree=PARENT['tree'],
        parent_f_full_patch_sha256=PARENT['full_source_patch_sha256'], incremental_patches=SPEC['patches'],
        change='Final default-only quiescent-retirement G2 ablation over F, screen label G; unchanged ineligible fallback',
        author_allocation_evidence=SPEC['author_allocation_evidence'], author_runtime_sha256=SPEC['author_runtime_sha256'],
        maintainability_budget_data_unchanged=True,
        acceptance='Pending unchanged D/F/G screen and full correctness. RMW reduction is a mechanism, not performance acceptance.')
    roots.append(candidate)
if args.include_dev:
    dev = destination / 'dev'
    run(SOURCE, 'git', 'worktree', 'add', '--detach', str(dev), DEV)
    harnesses = {}
    for path in ('test/SharpLink.Benchmarks/GeneratedAbiStreamingEvidenceRunner.cs', 'test/SharpLink.StreamLoadTest/Program.cs'):
        expected = subprocess.check_output(['git', 'show', f'{RECIPE}:{path}'], cwd=SOURCE)
        assert all((root / path).read_bytes() == expected for root in roots), path
        (dev / path).write_bytes(expected)
        harnesses[path] = hashlib.sha256(expected).hexdigest()
    identities['dev'] = dict(label='D', revision=DEV, disposable_tree=git(dev, 'write-tree'), common_measurement_harness_sha256=harnesses)
(destination / 'identities.json').write_text(json.dumps(identities, indent=2) + '\n')
print(json.dumps(identities, indent=2))
