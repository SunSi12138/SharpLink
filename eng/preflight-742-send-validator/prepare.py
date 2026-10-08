#!/usr/bin/env python3
"""Reconstruct frozen G2/V production and separate identical test overlays."""
import argparse
import hashlib
import json
import pathlib
import shutil
import subprocess

KIT = pathlib.Path(__file__).resolve().parent
SOURCE = KIT.parents[1]
HELPER = '211c7a94f7107158cd69cfb251ca33156487c48e'
RECIPE = '06b737df0d9aab66caed9c446a572b3a8ce4efae'
TREES = {
    'G2': '398d484fb5b8ab8adb75a74db7d577d929d2dc77',
    'V': '379e7ca76cc3431d464e8b3ffc56e245534b151c',
    'G2-tests': '9cf68caa7258c8ddec6a88f18376de3417a5e1de',
    'V-tests': 'aaf1267b16b162bbd1c24c40fbb921856813d625',
    'V-experimental': 'aaf1267b16b162bbd1c24c40fbb921856813d625',
}
PATCHES = {
    'production.patch': '6d66720e20ba0aa47a8376fb36c938127a992678193b09d0b1743ac2e4da0584',
    'tests.patch': 'b9836bafa764f62686e935a1679b2de8d0d8f38a916a3c1ed252fc2567528f82',
}
FULL_G2 = '5217ed02fbeb260f68413fba7f4eac21eb197e37bdff31a612fb4973ccf5139b'


def git(root, *args):
    return subprocess.check_output(['git', *args], cwd=root, text=True).strip()


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def save(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, indent=2) + '\n')


def run(root, *args):
    subprocess.run(args, cwd=root, check=True)


def pristine(root, tree):
    assert git(root, 'write-tree') == tree
    assert not git(root, 'diff', '--name-only', '--', 'src', 'test')
    assert not git(root, 'ls-files', '--others', '--exclude-standard', '--', 'src', 'test')


def prepare(destination, out):
    exclusions = [':(exclude).github/workflows/742-send-validator-preflight.yml',
                  ':(exclude)eng/preflight-742-send-validator/**']
    for revision in ((HELPER, 'HEAD'), ()):
        assert not git(SOURCE, 'diff', '--name-only', *revision, '--', '.', *exclusions), 'Inherited source/helper drift'
    assert {name: sha(KIT / name) for name in PATCHES} == PATCHES
    author = json.loads((KIT / 'author-manifest.json').read_text())
    assert author['base_tree'] == TREES['G2'] and author['production_tree'] == TREES['V']
    assert author['candidate_tree'] == TREES['V-tests']
    assert all(author['patches'][name]['sha256'] == digest for name, digest in PATCHES.items())
    destination.mkdir(parents=True, exist_ok=True)
    reconstruction = destination / 'g2-reconstruction'
    run(SOURCE, 'python3', str(SOURCE / 'eng/preflight-742-quiescent/prepare.py'), str(reconstruction))
    g2 = reconstruction / 'candidate'
    pristine(g2, TREES['G2'])
    full = g2 / 'artifacts/first-receive-integration/candidate.patch'
    assert sha(full) == FULL_G2
    roots = {'G2': g2}
    for label in ('V', 'G2-tests', 'V-tests', 'V-experimental'):
        root = destination / label
        run(SOURCE, 'git', 'worktree', 'add', '--detach', str(root), RECIPE)
        patches = [full]
        if label.startswith('V'):
            patches.append(KIT / 'production.patch')
        if label != 'V':
            patches.append(KIT / 'tests.patch')
        for patch in patches:
            run(root, 'git', 'apply', '--check', '--index', str(patch))
            run(root, 'git', 'apply', '--index', str(patch))
        pristine(root, TREES[label])
        roots[label] = root
    production_changes = git(SOURCE, 'diff', '--name-only', TREES['G2'], TREES['V']).splitlines()
    assert production_changes == ['src/SharpLink.Runtime/StreamFlowController.cs']
    source_subtrees = {label: git(root, 'rev-parse', TREES[label] + ':src') for label, root in roots.items()}
    for production, overlay in (('G2', 'G2-tests'), ('V', 'V-tests'), ('V', 'V-experimental')):
        assert source_subtrees[production] == source_subtrees[overlay]
        assert git(SOURCE, 'diff', '--name-only', TREES[production], TREES[overlay]).splitlines() == [
            'test/SharpLink.UnitTests/Runtime/StreamFlowControllerTests.InvalidSendLease.cs']
    harness_names = ('test/SharpLink.Benchmarks/GeneratedAbiStreamingEvidenceRunner.cs',
                     'test/SharpLink.StreamLoadTest/Program.cs',
                     'test/SharpLink.Benchmarks/ResolvedFlowStateEvidenceRunner.cs')
    for name in harness_names:
        assert all((root / name).read_bytes() == (g2 / name).read_bytes() for root in roots.values())
    out.mkdir(parents=True, exist_ok=True)
    proof = dict(helper_base=HELPER, execution_commit=git(SOURCE, 'rev-parse', 'HEAD'),
        roots={label: str(root) for label, root in roots.items()}, trees=TREES,
        production_src_subtrees=source_subtrees, production_changes=production_changes,
        patches=PATCHES, full_g2_patch_sha256=FULL_G2,
        helper_sha256={str(path.relative_to(SOURCE)): sha(path) for path in
            [*sorted(path for path in KIT.iterdir() if path.is_file()),
             SOURCE / '.github/workflows/742-send-validator-preflight.yml']},
        common_harness_sha256={name: sha(g2 / name) for name in harness_names},
        boundary='Safety and codegen only; production V and test overlay are distinct identities. '
                 'No timing or performance acceptance. Codegen requires independent inspection.')
    for label, root in roots.items():
        provenance = dict(label=label, disposable_tree=TREES[label], production_tree=TREES['V' if label.startswith('V') else 'G2'],
            production_src_subtree=source_subtrees[label], patches=PATCHES,
            source_blobs={name: git(root, 'hash-object', name) for name in git(root, 'ls-files', '--', 'src', 'test').splitlines()})
        save(root / 'artifacts/first-receive-integration/provenance.json', provenance)
        save(out / f'{label}-source.json', provenance)
    shutil.copyfile(full, out / 'full-g2.patch')
    for name in (*PATCHES, 'author-manifest.json'):
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
