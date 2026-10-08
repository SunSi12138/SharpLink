#!/usr/bin/env python3
"""Reconstruct exact G2/A production and identical test-only safety overlays."""
import argparse
import hashlib
import json
import pathlib
import shutil
import subprocess

KIT = pathlib.Path(__file__).resolve().parent
SOURCE = KIT.parents[1]
HELPER = 'f1607bf4f584abeb31c6bc65a5ac4b4aff380448'
RECIPE = '06b737df0d9aab66caed9c446a572b3a8ce4efae'
G2 = '398d484fb5b8ab8adb75a74db7d577d929d2dc77'
A = '4cb4ce7a13b06c3a6f3213e30215b6769ed49f90'
FULL_G2 = '5217ed02fbeb260f68413fba7f4eac21eb197e37bdff31a612fb4973ccf5139b'
PRODUCTION_FILES = ['src/SharpLink.Client/ClientConnection.cs',
                    'src/SharpLink.Runtime/RpcSession.GeneratedServerBridge.cs']


def git(root, *args):
    return subprocess.check_output(['git', *args], cwd=root, text=True).strip()


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def save(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, indent=2) + '\n')


def spec():
    value = json.loads((KIT / 'author-manifest.json').read_text())
    assert value['base_tree'] == G2 and value['production_tree'] == A
    assert value['new_test_cases'] > 0
    assert sum(row['count'] for row in value['focused_tests']) == value['new_test_cases']
    assert all(row['count'] > 0 and row['class'] and row['method'] for row in value['focused_tests'])
    for name in ('production.patch', 'tests.patch'):
        assert sha(KIT / name) == value['patches'][name]['sha256'], name
        assert (KIT / name).stat().st_size == value['patches'][name]['bytes'], name
    return value


def trees():
    author = spec()
    return {'G2': G2, 'A': A, 'G2-tests': author['baseline_tests_tree'],
            'A-tests': author['candidate_tree'], 'A-experimental': author['candidate_tree']}


def pristine(root, tree):
    assert git(root, 'write-tree') == tree
    assert not git(root, 'diff', '--name-only', '--', 'src', 'test')
    assert not git(root, 'ls-files', '--others', '--exclude-standard', '--', 'src', 'test')


def prepare(destination, out):
    exclusions = [':(exclude).github/workflows/742-first-binding-preflight.yml',
                  ':(exclude)eng/preflight-742-first-binding/**']
    for revision in ((HELPER, 'HEAD'), ()):
        assert not git(SOURCE, 'diff', '--name-only', *revision, '--', '.', *exclusions), 'Inherited helper/source drift'
    author, identities = spec(), trees()
    destination.mkdir(parents=True, exist_ok=True)
    reconstruction = destination / 'g2-reconstruction'
    subprocess.run(['python3', str(SOURCE / 'eng/preflight-742-quiescent/prepare.py'), str(reconstruction)], check=True)
    g2 = reconstruction / 'candidate'
    pristine(g2, G2)
    full = g2 / 'artifacts/first-receive-integration/candidate.patch'
    assert sha(full) == FULL_G2
    roots = {'G2': g2}
    for label in ('A', 'G2-tests', 'A-tests', 'A-experimental'):
        root = destination / label
        subprocess.run(['git', 'worktree', 'add', '--detach', str(root), RECIPE], cwd=SOURCE, check=True)
        patches = [full]
        if label.startswith('A'):
            patches.append(KIT / 'production.patch')
        if label != 'A':
            patches.append(KIT / 'tests.patch')
        for patch in patches:
            subprocess.run(['git', 'apply', '--check', '--index', str(patch)], cwd=root, check=True)
            subprocess.run(['git', 'apply', '--index', str(patch)], cwd=root, check=True)
        pristine(root, identities[label])
        roots[label] = root
    assert git(SOURCE, 'diff', '--name-only', G2, A).splitlines() == PRODUCTION_FILES
    source_trees = {label: git(root, 'rev-parse', identities[label] + ':src') for label, root in roots.items()}
    test_names = git(SOURCE, 'diff', '--name-only', G2, identities['G2-tests']).splitlines()
    assert test_names and all(name.startswith('test/SharpLink.UnitTests/') for name in test_names)
    for production, overlay in (('G2', 'G2-tests'), ('A', 'A-tests'), ('A', 'A-experimental')):
        assert source_trees[production] == source_trees[overlay]
        assert git(SOURCE, 'diff', '--name-only', identities[production], identities[overlay]).splitlines() == test_names
    # Everything except the two private root implementations is byte-identical,
    # including controller/lifecycle/API snapshots and every workload/validator.
    assert not git(SOURCE, 'diff', '--name-only', G2, A, '--', '.',
                   *(':(exclude)' + name for name in PRODUCTION_FILES))
    common = ('test/SharpLink.Benchmarks/GeneratedAbiStreamingEvidenceRunner.cs',
              'test/SharpLink.StreamLoadTest/Program.cs',
              'test/SharpLink.Benchmarks/ResolvedFlowStateEvidenceRunner.cs')
    for name in common:
        assert all((root / name).read_bytes() == (g2 / name).read_bytes() for root in roots.values())
    proof = dict(helper_base=HELPER, execution_commit=git(SOURCE, 'rev-parse', 'HEAD'),
        roots={label: str(root) for label, root in roots.items()}, trees=identities,
        production_src_subtrees=source_trees, production_changes=PRODUCTION_FILES,
        original_unit_cases=2128, new_test_cases=author['new_test_cases'],
        expected_unit_cases=2128 + author['new_test_cases'], focused_tests=author['focused_tests'],
        patches=author['patches'], full_g2_patch_sha256=FULL_G2,
        helper_sha256={str(path.relative_to(SOURCE)): sha(path) for path in
            [*sorted(path for path in KIT.rglob('*') if path.is_file() and '__pycache__' not in path.parts),
             SOURCE / '.github/workflows/742-first-binding-preflight.yml']},
        common_harness_sha256={name: sha(g2 / name) for name in common},
        boundary='First-binding safety and codegen only. V is excluded. No throughput comparison, readiness or promotion.')
    for label, root in roots.items():
        provenance = dict(label=label, disposable_tree=identities[label],
            production_tree=A if label.startswith('A') else G2,
            production_src_subtree=source_trees[label], patches=author['patches'],
            source_blobs={name: git(root, 'hash-object', name) for name in git(root, 'ls-files', '--', 'src', 'test').splitlines()})
        save(root / 'artifacts/first-receive-integration/provenance.json', provenance)
        save(out / f'{label}-source.json', provenance)
    shutil.copyfile(full, out / 'full-g2.patch')
    for name in ('production.patch', 'tests.patch', 'author-manifest.json'):
        shutil.copyfile(KIT / name, out / name)
    save(destination / 'identities.json', proof)
    save(out / 'identities.json', proof)
    print(json.dumps(proof, indent=2))


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('destination', type=pathlib.Path)
    parser.add_argument('output', type=pathlib.Path)
    args = parser.parse_args()
    prepare(args.destination.resolve(), args.output.resolve())
