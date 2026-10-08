#!/usr/bin/env python3
"""Use inherited reconstruction and the frozen production-only R patch."""
import argparse
import hashlib
import json
import pathlib
import re
import shutil
import subprocess

KIT = pathlib.Path(__file__).resolve().parent
SOURCE = KIT.parents[1]
HELPER = '7b84cd6a60dc9f8d8e7dfa8e29dc82c4adc2d535'
HELPER_TREE = 'a5fcfa74feb6b6b6418af97da84cc93f71f0703d'
DEV = '0fe26024b114bb6e78411a9b86276086c045d03d'
RECIPE = '06b737df0d9aab66caed9c446a572b3a8ce4efae'
IDENTITIES = {'D': DEV, 'G2': '398d484fb5b8ab8adb75a74db7d577d929d2dc77',
              'R': 'df4383c7d00ee4a31c129ae42958e013f4ff5385'}
FULL_G2 = '5217ed02fbeb260f68413fba7f4eac21eb197e37bdff31a612fb4973ccf5139b'
R_PATCH = '1e1699632cc556481e7cde1df2b3bb44f9a7c0ff6a7ea45fc0ef0a6f148628ff'
FINAL_PROOF = 'c0754c5ebbff71c29d7f106078acecb2a787e285146270b45f47abf3ef437791'
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
    assert review['status'] == 'independently-reviewed-pass', 'Timing blocked: independent R compiled-path review is pending'
    assert FINAL_PROOF is not None, 'Timing blocked: the final combined machine proof is not frozen'
    assert review['run_id'] == 37855801421 and review['helper_commit'] == HELPER
    assert review['production_trees'] == {label: IDENTITIES[label] for label in ('G2', 'R')}
    assert review['compiled_file'] == 'final-mechanism-proof.json'
    assert review['integrity_file'] == 'run-37855801421-integrity-review.json'
    assert review['human_file'] == 'independent-compiled-review.md'
    assert review['proof_files'][review['compiled_file']] == FINAL_PROOF
    for name, digest in review['proof_files'].items():
        path = (KIT / name).resolve()
        assert path.is_relative_to(KIT) and path.is_file(), name
        assert re.fullmatch('[0-9a-f]{64}', digest) and sha(path) == digest, name
    compiled = json.loads((KIT / review['compiled_file']).read_text())
    assert compiled['status'] == 'PASS_MECHANISM_ONLY'
    assert compiled['run_id'] == review['run_id'] and compiled['helper_commit'] == HELPER
    assert compiled['production_trees'] == review['production_trees']
    assert compiled['production_patch_sha256'] == R_PATCH
    assert compiled['safety_integrity_verified'] is True
    assert compiled['proof_files'] == {name: digest for name, digest in review['proof_files'].items()
                                      if name != review['compiled_file']}
    assert review['integrity_file'] in compiled['proof_files']
    assert review['human_file'] in compiled['proof_files']
    for name in ('owned_snapshot_preserved', 'second_outgoing_copy_replaced_by_pointer',
                 'no_unconditional_compensating_copy', 'deferred_and_refund_value_ownership_preserved'):
        assert compiled['mechanism'][name] is True, name
    jit = compiled['jit']
    assert jit['tier'] == 'Tier1' and jit['pgo'] == 'Synthesized PGO'
    assert jit['admission_inlining_preserved'] is False, 'The observed G2-inline/R-controller-call tradeoff must remain explicit'
    assert jit['performance_claim'] is False and jit['root_osr'] is False
    assert jit['separate_generated_dispatch_wrapper_pgo'] == 'Dynamic PGO'
    assert jit['uncaptured_bodies'] == ['AwaitPreCreditBudgetAndRetainedFlowCreditAsync starter/MoveNext',
                                       'ReturnUnsentStreamCredit destination body']
    assert jit['deferred_refund_outbound_value_copies_inspected'] is True
    native = compiled['native']
    assert native['scope'] == 'compiled-only' and native['workload_executed'] is False
    assert native['performance_claim'] is False
    assert native['deferred_starter_and_refund_bodies_inspected'] is True
    assert compiled['performance_acceptance'] is False
    assert compiled['original_full_acceptance_required'] is True
    assert compiled['prior_negative_results_retained'] is True
    integrity = json.loads((KIT / review['integrity_file']).read_text())
    assert integrity['run'] == {'id': 37855801421, 'attempt': 1, 'head': HELPER,
                                'tree': HELPER_TREE, 'conclusion': 'success'}
    assert integrity['official_artifacts'] == 17 and integrity['packaging_failures'] == []
    assert integrity['full_suites'] == {'G2-tests': 2142, 'R-tests': 2142, 'R-experimental': 2142}
    focused = {'ClientWrapperShouldOwnSnapshotBeforeUserCallbacks': 3,
               'ReentrantPooledReuseShouldRejectTheOwnedOldGeneration': 4,
               'PostAdmissionClockCallbackShouldRefundOnlyTheOwnedLease': 4,
               'PendingSerializedBudgetShouldCaptureLeaseBeforeWrapperReturns': 2,
               'DispatchAndDeferredBoundariesShouldKeepOwnedLeaseParameters': 1}
    assert integrity['focused_suites'] == {'G2-tests': focused, 'R-tests': focused}
    assert integrity['writer_checks'] == 171
    assert integrity['lifecycle']['original'] == {'rows_per_arm': 120, 'max_delta': 0.0, 'limit': 0.01}
    assert native['elf_sha256'] == {arm: row['elf_sha256'] for arm, row in integrity['native'].items()}
    assert {(row['arm'], row['root'], row['managed_size']) for row in integrity['managed_layout']} == {
        (arm, root, size) for arm in ('G2', 'R') for root, size in
        (('SendClientStreamAsync', 256), ('PumpGeneratedOutboundStreamAsync', 304))}
    assert len(integrity['jit']) == 8
    assert all(row['tier'] == 'Tier1' and row['pgo'] == 'Synthesized PGO' and row['osr'] is None
               and row['original_dynamic_pgo_status'] == 'inconclusive' for row in integrity['jit'])
    return review


def check_roots(roots):
    for label in ('G2', 'R'):
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
    assert git(SOURCE, 'diff', '--name-only', IDENTITIES['G2'], IDENTITIES['R']).splitlines() == [
        'src/SharpLink.Runtime/RpcSession.ClientStreamPublication.cs',
        'src/SharpLink.Runtime/RpcSession.GeneratedServerBridge.cs',
        'src/SharpLink.Runtime/RpcSession.PreCreditStreaming.cs']
    return dict(IDENTITIES)


def prepare(destination, out):
    assert git(SOURCE, 'rev-parse', HELPER + '^{tree}') == HELPER_TREE
    safety_files = git(SOURCE, 'ls-tree', '-r', '--name-only', HELPER, '--',
        '.github/workflows/742-readonly-lease-preflight.yml', 'eng/preflight-742-readonly-lease').splitlines()
    assert safety_files
    for name in safety_files:
        assert (SOURCE / name).read_bytes() == subprocess.check_output(
            ['git', 'show', HELPER + ':' + name], cwd=SOURCE), 'Safety/codegen helper drift: ' + name
    exclusions = [':(exclude)eng/preflight-742-readonly-lease-screen/**',
                  ':(exclude).github/workflows/742-readonly-lease-screen.yml']
    for revision in ((HELPER, 'HEAD'), (HELPER,), ()):
        assert not git(SOURCE, 'diff', '--name-only', *revision, '--', '.', *exclusions), 'Inherited source/helper drift'
    destination.mkdir(parents=True, exist_ok=True)
    reconstruction = destination / 'g2-reconstruction'
    subprocess.run(['python3', str(SOURCE / 'eng/preflight-742-quiescent/prepare.py'),
                    str(reconstruction), '--include-dev'], check=True)
    g2 = reconstruction / 'candidate'
    full = g2 / 'artifacts/first-receive-integration/candidate.patch'
    patch = SOURCE / 'eng/preflight-742-readonly-lease/production.patch'
    assert sha(full) == FULL_G2 and sha(patch) == R_PATCH
    candidate = destination / 'R'
    subprocess.run(['git', 'worktree', 'add', '--detach', str(candidate), RECIPE], cwd=SOURCE, check=True)
    for item in (full, patch):
        subprocess.run(['git', 'apply', '--check', '--index', str(item)], cwd=candidate, check=True)
        subprocess.run(['git', 'apply', '--index', str(item)], cwd=candidate, check=True)
    roots = {'D': reconstruction / 'dev', 'G2': g2, 'R': candidate}
    check_roots(roots)
    out.mkdir(parents=True, exist_ok=True)
    proof = dict(helper_base=HELPER, safety_source_helper=HELPER, codegen_source_helper=HELPER, reconstruction_recipe=RECIPE,
        execution_commit=git(SOURCE, 'rev-parse', 'HEAD'),
        identities=IDENTITIES, roots={label: str(root) for label, root in roots.items()},
        production_src_subtrees={label: git(root, 'rev-parse', (DEV if label == 'D' else IDENTITIES[label]) + ':src')
                                for label, root in roots.items()},
        full_g2_patch_sha256=FULL_G2, production_r_patch_sha256=R_PATCH,
        common_measurement_harness_sha256={name: sha(g2 / name) for name in HARNESS},
        helper_sha256={str(path.relative_to(SOURCE)): sha(path) for path in
            [*(path for path in KIT.rglob('*') if path.is_file() and '__pycache__' not in path.parts),
             SOURCE / '.github/workflows/742-readonly-lease-screen.yml']},
        codegen_helper_sha256={name: sha(SOURCE / name) for name in safety_files},
        inherited_helpers_sha256={name: sha(SOURCE / name) for name in
            ('eng/preflight-742-quiescent/prepare.py', 'eng/preflight-742-quiescent/screen.py',
             'eng/preflight-742-quiescent/common.py')},
        boundary='D/G2/R production source. The 90-launch JIT screen is bounded diagnostic evidence, not full acceptance.')
    save(out / 'identities.json', proof)
    save(destination / 'identities.json', proof)
    for label, root in roots.items():
        save(out / (label + '-source-sha256.json'), {name: sha(root / name) for name in
            git(root, 'ls-files', '--', 'src', 'test').splitlines()})
    shutil.copyfile(full, out / 'full-g2.patch')
    shutil.copyfile(patch, out / 'production-r.patch')
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
