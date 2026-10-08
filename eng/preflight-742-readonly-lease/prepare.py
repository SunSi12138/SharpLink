#!/usr/bin/env python3
"""Reconstruct exact G2/R production and identical test-only safety overlays."""
import argparse
import hashlib
import json
import pathlib
import shutil
import subprocess

KIT = pathlib.Path(__file__).resolve().parent
SOURCE = KIT.parents[1]
HELPER = '34c17de5c565db796bf5ead8cbce88e787133dec'
RECIPE = '06b737df0d9aab66caed9c446a572b3a8ce4efae'
G2 = '398d484fb5b8ab8adb75a74db7d577d929d2dc77'
R = 'df4383c7d00ee4a31c129ae42958e013f4ff5385'
FULL_G2 = '5217ed02fbeb260f68413fba7f4eac21eb197e37bdff31a612fb4973ccf5139b'
PRODUCTION_FILES = ['src/SharpLink.Runtime/RpcSession.ClientStreamPublication.cs',
                    'src/SharpLink.Runtime/RpcSession.GeneratedServerBridge.cs',
                    'src/SharpLink.Runtime/RpcSession.PreCreditStreaming.cs']


def git(root, *args):
    return subprocess.check_output(['git', *args], cwd=root, text=True).strip()


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def save(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, indent=2) + '\n')


def spec():
    value = json.loads((KIT / 'author-manifest.json').read_text())
    assert value['base_tree'] == G2 and value['production_tree'] == R
    assert value['original_suite_cases'] == 2128
    assert value['new_test_cases'] > 0
    assert value['expected_overlay_suite_cases'] == 2128 + value['new_test_cases']
    assert value['production_files'] == PRODUCTION_FILES
    assert sum(row['count'] for row in value['focused_tests']) == value['new_test_cases']
    assert all(row['count'] > 0 and row['class'] and row['method'] for row in value['focused_tests'])
    for name, pinned in value['source_manifests'].items():
        assert sha(KIT / name) == pinned['sha256']
        assert (KIT / name).stat().st_size == pinned['bytes']
    for row in value['focused_tests']:
        assert row['filter'] == '/*/*/' + row['class'] + '/' + row['method'] + '*'
    for name in ('production.patch', 'tests.patch'):
        assert sha(KIT / name) == value['patches'][name]['sha256'], name
        assert (KIT / name).stat().st_size == value['patches'][name]['bytes'], name
    return value


def trees():
    author = spec()
    return {'G2': G2, 'R': R, 'G2-tests': author['baseline_tests_tree'],
            'R-tests': author['candidate_tree'], 'R-experimental': author['candidate_tree']}


def pristine(root, tree):
    assert git(root, 'write-tree') == tree
    assert not git(root, 'diff', '--name-only'), 'Tracked build-input/source drift'
    assert not git(root, 'ls-files', '--others', '--exclude-standard', '--', 'src', 'test')


def prepare(destination, out):
    exclusions = [':(exclude).github/workflows/742-readonly-lease-preflight.yml',
                  ':(exclude)eng/preflight-742-readonly-lease/**']
    for revision in ((HELPER, 'HEAD'), ('--cached', HELPER), ()):
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
    for label in ('R', 'G2-tests', 'R-tests', 'R-experimental'):
        root = destination / label
        subprocess.run(['git', 'worktree', 'add', '--detach', str(root), RECIPE], cwd=SOURCE, check=True)
        patches = [full]
        if label.startswith('R'):
            patches.append(KIT / 'production.patch')
        if label != 'R':
            patches.append(KIT / 'tests.patch')
        for patch in patches:
            subprocess.run(['git', 'apply', '--check', '--index', str(patch)], cwd=root, check=True)
            subprocess.run(['git', 'apply', '--index', str(patch)], cwd=root, check=True)
        pristine(root, identities[label])
        roots[label] = root
    subprocess.run(['python3', str(KIT / 'source-contract.py'), str(roots['G2']), str(roots['R'])], check=True)
    assert git(SOURCE, 'diff', '--name-only', G2, R).splitlines() == PRODUCTION_FILES
    source_trees = {label: git(root, 'rev-parse', identities[label] + ':src') for label, root in roots.items()}
    test_names = git(SOURCE, 'diff', '--name-only', G2, identities['G2-tests']).splitlines()
    assert test_names and all(name.startswith('test/SharpLink.UnitTests/') for name in test_names)
    for production, overlay in (('G2', 'G2-tests'), ('R', 'R-tests'), ('R', 'R-experimental')):
        assert source_trees[production] == source_trees[overlay]
        assert git(SOURCE, 'diff', '--name-only', identities[production], identities[overlay]).splitlines() == test_names
    # Everything except the three retained-dispatch source files is byte-identical,
    # including controller/lifecycle/API snapshots and every workload/validator.
    assert not git(SOURCE, 'diff', '--name-only', G2, R, '--', '.',
                   *(':(exclude)' + name for name in PRODUCTION_FILES))
    original_gates = ('eng/run-allocation-gate.sh', 'eng/check-project-reference-boundaries.py',
        'eng/check-maintainability.sh', 'eng/preflight-742-redesign/lifecycle.py',
        'eng/preflight-742-quiescent/actual-setup.py', 'eng/preflight-742-quiescent/production-lifecycle.py',
        'eng/preflight-735-prepare-native-rpc.py', 'eng/preflight-742-send-validator-jit/inventory.py',
        'eng/preflight-742-quiescent/common.py')
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
             SOURCE / '.github/workflows/742-readonly-lease-preflight.yml']},
        frozen_gate_sha256={name: sha(SOURCE / name) for name in original_gates},
        common_harness_sha256={name: sha(g2 / name) for name in common},
        boundary='Readonly-inner-lease safety and codegen only. Failed A/V are excluded. No throughput comparison, readiness or promotion.')
    for label, root in roots.items():
        provenance = dict(label=label, disposable_tree=identities[label],
            production_tree=R if label.startswith('R') else G2,
            production_src_subtree=source_trees[label], patches=author['patches'],
            source_blobs={name: git(root, 'hash-object', name) for name in git(root, 'ls-files', '--', 'src', 'test').splitlines()})
        reviewed_label = 'R-tests' if label == 'R-experimental' else label
        reviewed = json.loads((KIT / (reviewed_label + '-source-hashes.json')).read_text())
        assert reviewed['tree'] == identities[label]
        assert reviewed['src_subtree'] == source_trees[label]
        assert reviewed['test_subtree'] == git(root, 'rev-parse', identities[label] + ':test')
        assert set(reviewed['files']) == set(provenance['source_blobs'])
        for name, pinned in reviewed['files'].items():
            assert pinned['git_blob'] == provenance['source_blobs'][name]
            assert pinned['sha256'] == sha(root / name), (label, name)
        provenance['reviewed_source_manifest_sha256'] = sha(KIT / (reviewed_label + '-source-hashes.json'))
        save(root / 'artifacts/first-receive-integration/provenance.json', provenance)
        save(out / f'{label}-source.json', provenance)
    shutil.copyfile(full, out / 'full-g2.patch')
    for name in ('production.patch', 'tests.patch', 'author-manifest.json', *author['source_manifests']):
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
