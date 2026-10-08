#!/usr/bin/env python3
"""Use inherited reconstruction and the frozen production-only V patch."""
import argparse
import hashlib
import json
import pathlib
import re
import shutil
import subprocess

KIT = pathlib.Path(__file__).resolve().parent
SOURCE = KIT.parents[1]
HELPER = '598ca7337bc6c832d0b1daa7d8058a53f8c7de5c'
SAFETY_HELPER = '815c1ac1ec1ec4141f38cfe06814d8d0caf4b257'
DEV = '0fe26024b114bb6e78411a9b86276086c045d03d'
RECIPE = '06b737df0d9aab66caed9c446a572b3a8ce4efae'
IDENTITIES = {'D': DEV, 'G2': '398d484fb5b8ab8adb75a74db7d577d929d2dc77',
              'V': '379e7ca76cc3431d464e8b3ffc56e245534b151c'}
FULL_G2 = '5217ed02fbeb260f68413fba7f4eac21eb197e37bdff31a612fb4973ccf5139b'
V_PATCH = '6d66720e20ba0aa47a8376fb36c938127a992678193b09d0b1743ac2e4da0584'
HARNESS = ('test/SharpLink.Benchmarks/GeneratedAbiStreamingEvidenceRunner.cs',
           'test/SharpLink.StreamLoadTest/Program.cs')


def git(root, *args):
    return subprocess.check_output(['git', *args], cwd=root, text=True).strip()


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def save(path, data):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(data, indent=2) + '\n')


def require_review():
    review = json.loads((KIT / 'codegen-review.json').read_text())
    assert review['status'] == 'independently-reviewed-pass', 'Timing blocked: compiled-path review is pending'
    assert review['safety_run_id'] == 37823636416 and review['safety_helper_commit'] == SAFETY_HELPER
    assert review['g2_production_tree'] == IDENTITIES['G2'] and review['v_production_tree'] == IDENTITIES['V']
    expected = {'complete_safety_suites_passed', 'old_out_reference_assignment_control_identified',
                'new_matched_resolved_path_reviewed', 'validation_and_caller_checks_preserved'}
    assert set(review['checks']) == expected and all(value is True for value in review['checks'].values())
    assert set(review['native_binary_sha256']) == {'G2', 'V'}
    for value in [review['review_report_sha256'], *review['native_binary_sha256'].values()]:
        assert isinstance(value, str) and re.fullmatch('[0-9a-f]{64}', value)
    report = (KIT / review['review_report_file']).resolve()
    assert report.is_relative_to(KIT) and report.is_file()
    assert sha(report) == review['review_report_sha256'], 'Independent compiled-path proof changed'
    proof = json.loads(report.read_text())
    assert proof['status'] == 'pass-compiled-mechanism-only' and proof['timing_performed'] is False
    assert proof['run'] == review['safety_run_id'] and proof['helper_commit'] == SAFETY_HELPER
    assert proof['elf_sha256'] == review['native_binary_sha256']
    notes = (KIT / review['review_notes_file']).resolve()
    assert notes.is_relative_to(KIT) and notes.is_file()
    assert sha(notes) == review['review_notes_sha256'], 'Independent compiled-path review notes changed'
    return review


def check_roots(roots):
    for label in ('G2', 'V'):
        root = roots[label]
        assert git(root, 'write-tree') == IDENTITIES[label], 'Production tree required; never a test overlay'
        assert not git(root, 'diff', '--name-only', '--', 'src', 'test')
        assert not git(root, 'ls-files', '--others', '--exclude-standard', '--', 'src', 'test')
    dev = roots['D']
    assert git(dev, 'rev-parse', 'HEAD') == DEV
    assert git(dev, 'write-tree') == git(dev, 'rev-parse', DEV + '^{tree}')
    assert set(git(dev, 'diff', '--name-only').splitlines()) <= set(HARNESS)
    assert not git(dev, 'ls-files', '--others', '--exclude-standard', '--', 'src', 'test')
    for name in HARNESS:
        assert all((root / name).read_bytes() == (roots['G2'] / name).read_bytes() for root in roots.values())
    assert git(SOURCE, 'diff', '--name-only', IDENTITIES['G2'], IDENTITIES['V']).splitlines() == [
        'src/SharpLink.Runtime/StreamFlowController.cs']
    return dict(IDENTITIES)


def prepare(destination, out):
    assert git(SOURCE, 'diff', '--name-status', SAFETY_HELPER, HELPER).splitlines() == [
        'A\t.github/workflows/742-validator-evidence-repack.yml',
        'A\teng/preflight-742-artifact-review/repack.py'], 'Base update must only add the approved artifact repack'
    safety_files = git(SOURCE, 'ls-tree', '-r', '--name-only', SAFETY_HELPER, '--',
        '.github/workflows/742-send-validator-preflight.yml', 'eng/preflight-742-send-validator').splitlines()
    assert len(safety_files) == 9
    for name in safety_files:
        assert (SOURCE / name).read_bytes() == subprocess.check_output(
            ['git', 'show', SAFETY_HELPER + ':' + name], cwd=SOURCE), 'Phase-one helper drift: ' + name
    exclusions = [':(exclude)eng/preflight-742-send-validator-screen/**',
                  ':(exclude).github/workflows/742-send-validator-screen.yml']
    for revision in ((HELPER, 'HEAD'), ()):
        assert not git(SOURCE, 'diff', '--name-only', *revision, '--', '.', *exclusions), 'Inherited source/helper drift'
    destination.mkdir(parents=True, exist_ok=True)
    reconstruction = destination / 'g2-reconstruction'
    subprocess.run(['python3', str(SOURCE / 'eng/preflight-742-quiescent/prepare.py'),
                    str(reconstruction), '--include-dev'], check=True)
    g2 = reconstruction / 'candidate'
    full = g2 / 'artifacts/first-receive-integration/candidate.patch'
    patch = SOURCE / 'eng/preflight-742-send-validator/production.patch'
    assert sha(full) == FULL_G2 and sha(patch) == V_PATCH
    candidate = destination / 'V'
    subprocess.run(['git', 'worktree', 'add', '--detach', str(candidate), RECIPE], cwd=SOURCE, check=True)
    for item in (full, patch):
        subprocess.run(['git', 'apply', '--check', '--index', str(item)], cwd=candidate, check=True)
        subprocess.run(['git', 'apply', '--index', str(item)], cwd=candidate, check=True)
    roots = {'D': reconstruction / 'dev', 'G2': g2, 'V': candidate}
    check_roots(roots)
    out.mkdir(parents=True, exist_ok=True)
    proof = dict(helper_base=HELPER, safety_source_helper=SAFETY_HELPER, reconstruction_recipe=RECIPE,
        execution_commit=git(SOURCE, 'rev-parse', 'HEAD'),
        identities=IDENTITIES, roots={label: str(root) for label, root in roots.items()},
        production_src_subtrees={label: git(root, 'rev-parse', (DEV if label == 'D' else IDENTITIES[label]) + ':src')
                                for label, root in roots.items()},
        full_g2_patch_sha256=FULL_G2, production_v_patch_sha256=V_PATCH,
        common_measurement_harness_sha256={name: sha(g2 / name) for name in HARNESS},
        helper_sha256={str(path.relative_to(SOURCE)): sha(path) for path in
            [*(path for path in KIT.iterdir() if path.is_file()),
             SOURCE / '.github/workflows/742-send-validator-screen.yml']},
        safety_helper_sha256={name: sha(SOURCE / name) for name in safety_files},
        inherited_helpers_sha256={name: sha(SOURCE / name) for name in
            ('eng/preflight-742-quiescent/prepare.py', 'eng/preflight-742-quiescent/screen.py',
             'eng/preflight-742-quiescent/common.py')},
        boundary='D/G2/V production source. The 90-launch JIT screen is bounded diagnostic evidence, not full acceptance.')
    save(out / 'identities.json', proof)
    save(destination / 'identities.json', proof)
    for label, root in roots.items():
        save(out / (label + '-source-sha256.json'), {name: sha(root / name) for name in
            git(root, 'ls-files', '--', 'src', 'test').splitlines()})
    shutil.copyfile(full, out / 'full-g2.patch')
    shutil.copyfile(patch, out / 'production-v.patch')
    (out / 'dev-common-harness.patch').write_text(git(roots['D'], 'diff', '--binary') + '\n')
    print(json.dumps(proof, indent=2))


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('destination', type=pathlib.Path)
    parser.add_argument('output', type=pathlib.Path)
    parser.add_argument('--require-codegen-review', action='store_true')
    args = parser.parse_args()
    if args.require_codegen_review:
        require_review()
    prepare(args.destination.resolve(), args.output.resolve())
