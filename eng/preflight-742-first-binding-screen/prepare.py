#!/usr/bin/env python3
"""Use inherited reconstruction and the frozen production-only A patch."""
import argparse
import hashlib
import json
import pathlib
import re
import shutil
import subprocess

KIT = pathlib.Path(__file__).resolve().parent
SOURCE = KIT.parents[1]
HELPER = '48aecc599cb8ee91219d727b10ccdad572a776d4'
SAFETY_HELPER = '634e6ca9693dbb716d08bad45f26adc30801ad38'
DEV = '0fe26024b114bb6e78411a9b86276086c045d03d'
RECIPE = '06b737df0d9aab66caed9c446a572b3a8ce4efae'
IDENTITIES = {'D': DEV, 'G2': '398d484fb5b8ab8adb75a74db7d577d929d2dc77',
              'A': '4cb4ce7a13b06c3a6f3213e30215b6769ed49f90'}
FULL_G2 = '5217ed02fbeb260f68413fba7f4eac21eb197e37bdff31a612fb4973ccf5139b'
A_PATCH = '1dc3031cff439568d3f1c2e86b9e9ab02a246e0e4cc726f676dae8d73f410794'
FINAL_PROOF = '2eaa2cedd53ffb8de405f7572a6f7d3d0db68eee79c018365d61f34b5aa27dbd'
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
    assert review['status'] == 'independently-reviewed-pass', 'Timing blocked: independent A compiled-path review is pending'
    assert review['codegen_run_id'] == 37846827122 and review['codegen_helper_commit'] == HELPER
    assert review['safety_run_id'] == 37844180672 and review['safety_helper_commit'] == SAFETY_HELPER
    assert review['production_trees'] == {label: IDENTITIES[label] for label in ('G2', 'A')}
    expected_files = {'final-mechanism-proof.json', 'independent-compiled-review.md',
                      'layout-and-code-summary.json', 'independent-native-extraction.json',
                      'native-method-map.json', 'run-37846827122-integrity-review.json'}
    assert set(review['proof_files']) == expected_files
    assert review['compiled_file'] == 'final-mechanism-proof.json'
    assert review['integrity_file'] == 'run-37846827122-integrity-review.json'
    assert review['proof_files'][review['compiled_file']] == FINAL_PROOF
    for name, digest in review['proof_files'].items():
        path = (KIT / name).resolve()
        assert path.is_relative_to(KIT) and path.is_file()
        assert re.fullmatch('[0-9a-f]{64}', digest) and sha(path) == digest, name
    compiled = json.loads((KIT / review['compiled_file']).read_text())
    assert compiled['schema'] == 'first-binding-independent-compiled-proof/v1'
    assert compiled['status'] == 'final' and compiled['verdict'] == 'PASS_MECHANISM_ONLY'
    assert compiled['review_completed'] is True
    assert compiled['run_id'] == review['codegen_run_id'] and compiled['helper_commit'] == HELPER
    assert compiled['production_trees'] == review['production_trees']
    assert compiled['production_patch']['sha256'] == A_PATCH
    assert compiled['integrity_proof']['sha256'] == review['proof_files'][review['integrity_file']]
    assert set(compiled['reports']) == expected_files - {review['compiled_file'], review['integrity_file']}
    for name, entry in compiled['reports'].items():
        assert entry['sha256'] == review['proof_files'][name]
    scope = compiled['scope']
    assert scope['payload'] == 'System.Byte[]' and scope['codec'] == 'BlitArrayCodec<byte>, unsized'
    assert scope['jit_instantiation'] == 'System.__Canon' and scope['native_instantiation'] == 'System___Canon'
    assert scope['sdk'] == '10.0.112' and scope['managed_runtime'] == '10.0.12'
    assert scope['jit_tier'] == 'Tier1' and scope['jit_profile'] == 'Synthesized PGO'
    layout = compiled['managed_layout']
    assert layout['exactly_one_field_type_size_changed_per_root'] and layout['all_other_field_names_types_sizes_match']
    assert layout['new_hoisted_send_valuetask_field'] is False and layout['retained_lease_bytes'] == 40
    assert {(row['root'], row['G2_state_bytes'], row['A_state_bytes']) for row in layout['roots']} == {
        ('SendClientStreamAsync', 256, 216), ('PumpGeneratedOutboundStreamAsync', 304, 264)}
    assert all(row['old_awaiter_bytes'] == 56 and row['new_awaiter_bytes'] == 16 and row['delta_bytes'] == -40
               for row in layout['roots'])
    semantics = compiled['compiled_semantics']
    for name in ('first_result_success_bypasses_AsTask', 'one_AsTask_consumption_on_other_first_result_states',
                 'task_reference_reuse_branch_inspected', 'fault_cancel_nonsuccess_handling_before_result_access',
                 'binding_flag_after_successful_lease_result', 'generated_deadline_provider_arbitration_preserved',
                 'timer_false_sets_deadline_won_cancels_observes_and_throws',
                 'value_task_source_and_result_task_fallback_code_still_present'):
        assert semantics[name] is True, name
    assert len(compiled['jit_reviewed_bodies']) == 8
    assert all(row['tier'] == 'Tier1' and row['pgo_source'] == 'Synthesized PGO' and row['osr_entry'] is None
               for row in compiled['jit_reviewed_bodies'])
    assert len(compiled['native_reviewed_bodies']) == 16
    assert all(row['independent_objdump_bytes_match'] for row in compiled['native_reviewed_bodies'])
    assert compiled['native_G2_server_box_alias']['followed'] is True
    summary = json.loads((KIT / 'layout-and-code-summary.json').read_text())['rows']
    assert compiled['tradeoffs'] == summary
    assert all(row['movenext_code_A'] > row['movenext_code_G2'] for row in summary
               if row['runtime'] == 'JIT Tier1/Synthesized PGO')
    native_client = next(row for row in summary if row['runtime'] == 'NativeAOT linux-x64' and row['root'] == 'client')
    assert native_client['movenext_stack_A'] == native_client['movenext_stack_G2'] == 792
    assert native_client['movenext_zero_A'] == native_client['movenext_zero_G2'] == 568
    decision = compiled['decision']
    assert decision['mechanism_gate_pass'] and decision['supports_separately_authorized_bounded_unchanged_settings_timing_screen']
    assert all(decision[name] is False for name in ('throughput_improvement_established', 'cpu_benefit_established',
        'allocation_totals_established_by_this_review', 'promotion_or_acceptance_established', 'complete_dynamic_pgo_chain_established'))
    integrity = json.loads((KIT / review['integrity_file']).read_text())
    assert integrity['run']['id'] == review['codegen_run_id']
    assert integrity['run']['head_sha'] == HELPER and integrity['run']['conclusion'] == 'success'
    assert all(row['verified'] for row in integrity['official_zip_artifacts'])
    assert integrity['reused_safety']['full_suites'] == 3
    assert integrity['reused_safety']['cases_per_full_suite'] == 2186
    assert integrity['reused_safety']['focused_per_default_arm'] == 58
    assert integrity['reused_safety']['writer_checks'] == 171
    assert {row['arm']: row['executable_sha256'] for row in integrity['native']} == {
        arm: entry['sha256'] for arm, entry in compiled['native_elfs'].items()}
    return review


def check_roots(roots):
    for label in ('G2', 'A'):
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
    assert git(SOURCE, 'diff', '--name-only', IDENTITIES['G2'], IDENTITIES['A']).splitlines() == [
        'src/SharpLink.Client/ClientConnection.cs', 'src/SharpLink.Runtime/RpcSession.GeneratedServerBridge.cs']
    return dict(IDENTITIES)


def prepare(destination, out):
    safety_files = git(SOURCE, 'ls-tree', '-r', '--name-only', HELPER, '--',
        '.github/workflows/742-first-binding-preflight.yml', 'eng/preflight-742-first-binding').splitlines()
    assert safety_files
    for name in safety_files:
        assert (SOURCE / name).read_bytes() == subprocess.check_output(
            ['git', 'show', HELPER + ':' + name], cwd=SOURCE), 'Safety/codegen helper drift: ' + name
    exclusions = [':(exclude)eng/preflight-742-first-binding-screen/**',
                  ':(exclude).github/workflows/742-first-binding-screen.yml']
    for revision in ((HELPER, 'HEAD'), ()):
        assert not git(SOURCE, 'diff', '--name-only', *revision, '--', '.', *exclusions), 'Inherited source/helper drift'
    destination.mkdir(parents=True, exist_ok=True)
    reconstruction = destination / 'g2-reconstruction'
    subprocess.run(['python3', str(SOURCE / 'eng/preflight-742-quiescent/prepare.py'),
                    str(reconstruction), '--include-dev'], check=True)
    g2 = reconstruction / 'candidate'
    full = g2 / 'artifacts/first-receive-integration/candidate.patch'
    patch = SOURCE / 'eng/preflight-742-first-binding/production.patch'
    assert sha(full) == FULL_G2 and sha(patch) == A_PATCH
    candidate = destination / 'A'
    subprocess.run(['git', 'worktree', 'add', '--detach', str(candidate), RECIPE], cwd=SOURCE, check=True)
    for item in (full, patch):
        subprocess.run(['git', 'apply', '--check', '--index', str(item)], cwd=candidate, check=True)
        subprocess.run(['git', 'apply', '--index', str(item)], cwd=candidate, check=True)
    roots = {'D': reconstruction / 'dev', 'G2': g2, 'A': candidate}
    check_roots(roots)
    out.mkdir(parents=True, exist_ok=True)
    proof = dict(helper_base=HELPER, safety_source_helper=SAFETY_HELPER, codegen_source_helper=HELPER, reconstruction_recipe=RECIPE,
        execution_commit=git(SOURCE, 'rev-parse', 'HEAD'),
        identities=IDENTITIES, roots={label: str(root) for label, root in roots.items()},
        production_src_subtrees={label: git(root, 'rev-parse', (DEV if label == 'D' else IDENTITIES[label]) + ':src')
                                for label, root in roots.items()},
        full_g2_patch_sha256=FULL_G2, production_a_patch_sha256=A_PATCH,
        common_measurement_harness_sha256={name: sha(g2 / name) for name in HARNESS},
        helper_sha256={str(path.relative_to(SOURCE)): sha(path) for path in
            [*(path for path in KIT.iterdir() if path.is_file()),
             SOURCE / '.github/workflows/742-first-binding-screen.yml']},
        codegen_helper_sha256={name: sha(SOURCE / name) for name in safety_files},
        inherited_helpers_sha256={name: sha(SOURCE / name) for name in
            ('eng/preflight-742-quiescent/prepare.py', 'eng/preflight-742-quiescent/screen.py',
             'eng/preflight-742-quiescent/common.py')},
        boundary='D/G2/A production source. The 90-launch JIT screen is bounded diagnostic evidence, not full acceptance.')
    save(out / 'identities.json', proof)
    save(destination / 'identities.json', proof)
    for label, root in roots.items():
        save(out / (label + '-source-sha256.json'), {name: sha(root / name) for name in
            git(root, 'ls-files', '--', 'src', 'test').splitlines()})
    shutil.copyfile(full, out / 'full-g2.patch')
    shutil.copyfile(patch, out / 'production-a.patch')
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
