#!/usr/bin/env python3
"""Untimed G2 source, harness, and produced-binary identity checks only."""
import argparse
import hashlib
import json
import pathlib
import shutil
import subprocess

KIT = pathlib.Path(__file__).resolve().parent
SOURCE = KIT.parents[1]
HELPER = '0b8af4aa31ab855b17af37da50f93b30f388f14e'
DEV = '0fe26024b114bb6e78411a9b86276086c045d03d'
G2 = '398d484fb5b8ab8adb75a74db7d577d929d2dc77'
FULL_PATCH = '5217ed02fbeb260f68413fba7f4eac21eb197e37bdff31a612fb4973ccf5139b'
HARNESS = ('test/SharpLink.Benchmarks/GeneratedAbiStreamingEvidenceRunner.cs',
           'test/SharpLink.StreamLoadTest/Program.cs')

def git(root, *args):
    return subprocess.check_output(['git', *args], cwd=root, text=True).strip()

def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()

def save(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, indent=2) + '\n')

def check_roots(destination):
    candidate, dev = destination / 'candidate', destination / 'dev'
    assert git(candidate, 'write-tree') == G2, 'Only shipping G2 is accepted, never the diagnostic adapter'
    assert not git(candidate, 'diff', '--name-only', '--', 'src', 'test'), 'G2 source changed outside the index'
    assert git(dev, 'rev-parse', 'HEAD') == DEV
    assert set(git(dev, 'diff', '--name-only').splitlines()) <= set(HARNESS), 'Unexpected dev changes'
    proof = json.loads((candidate / 'artifacts/first-receive-integration/provenance.json').read_text())
    assert proof['disposable_tree'] == G2 and proof['patch_sha256'] == FULL_PATCH
    for path in HARNESS:
        assert (dev / path).read_bytes() == (candidate / path).read_bytes(), path
    return proof

def prepare(destination, out):
    # No inherited helper, measurement source, validator, or original workflow may drift.
    exclusions = [':(exclude).github/workflows/742-quiescent-full-acceptance.yml',
                  ':(exclude)eng/preflight-742-full-g2/**']
    for revision in ((HELPER, 'HEAD'), ()):
        assert not git(SOURCE, 'diff', '--name-only', *revision, '--', '.', *exclusions), 'Inherited helper/source drift'
    subprocess.run(['python3', str(SOURCE / 'eng/preflight-742-quiescent/prepare.py'),
                    str(destination), '--include-dev'], cwd=SOURCE, check=True)
    proof = check_roots(destination)
    out.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(destination / 'identities.json', out / 'identities.json')
    shutil.copytree(destination / 'candidate/artifacts/first-receive-integration', out / 'candidate-g2-source')
    shutil.copytree(destination / 'prior-f/artifacts/first-receive-integration', out / 'parent-f-source')
    (out / 'dev-common-harness.patch').write_text(git(destination / 'dev', 'diff', '--binary') + '\n')
    helper_files = git(SOURCE, 'ls-files', '--', 'eng/preflight-742-quiescent', 'eng/preflight-742-redesign',
                       'eng/preflight-742-full-g2', 'eng/preflight-735-prepare-native-rpc.py').splitlines()
    # The workflow is included even during local pre-publication validation.
    helper_files += ['.github/workflows/742-quiescent-full-acceptance.yml']
    helper_files += [str(path.relative_to(SOURCE)) for path in KIT.glob('*.py')]
    save(out / 'full-acceptance-provenance.json', dict(
        helper_base=HELPER, execution_commit=git(SOURCE, 'rev-parse', 'HEAD'),
        dev_commit=DEV, shipping_g2_tree=G2, g2_full_patch_sha256=FULL_PATCH,
        source_blobs=proof['source_blobs'],
        common_measurement_harness_sha256={path: digest(destination / 'candidate' / path) for path in HARNESS},
        helper_sha256={path: digest(SOURCE / path) for path in sorted(set(helper_files))},
        prior_correctness_and_screen='https://github.com/SunSi12138/SharpLink/actions/runs/37786811781',
        boundary='Unchanged G2 versus dev full original coverage. Prior SHM short/client regressions remain no-go evidence. '
                 'The historical exact-source 2128 default and 2128 experimental correctness result is reused. '
                 'Collection success does not mean performance acceptance or PR readiness. No resampling/retry-to-green policy.'))

def snapshot(destination, out, phase, native_micro):
    check_roots(destination)
    proof = {}
    for label in ('dev', 'candidate'):
        root = destination / label
        tracked = git(root, 'ls-files', '--', 'src', 'test').splitlines()
        files = {name: digest(root / name) for name in tracked}
        binaries = {}
        for project in ('SharpLink.Benchmarks', 'SharpLink.StreamLoadTest'):
            folder = root / 'test' / project / 'bin/Release/net10.0'
            for path in sorted(folder.rglob('*')):
                if path.is_file():
                    binaries[str(path.relative_to(root))] = digest(path)
        assert files and binaries, 'Source and actual JIT outputs must both be retained'
        proof[label] = dict(source_sha256=files, binary_sha256=binaries)
    native = {str(path.relative_to(native_micro)): digest(path) for path in native_micro.rglob('*') if path.is_file()}
    assert native and (native_micro / 'SharpLink.Benchmarks').read_bytes()[:4] == b'\x7fELF'
    proof['candidate_native_micro_publish_sha256'] = native
    save(out / f'source-and-binaries-{phase}.json', proof)
    if phase == 'after':
        assert proof == json.loads((out / 'source-and-binaries-before.json').read_text()), 'Source or measured binary drift'

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('mode', choices=('prepare', 'before', 'after'))
parser.add_argument('destination', type=pathlib.Path)
parser.add_argument('out', type=pathlib.Path)
parser.add_argument('--native-micro', type=pathlib.Path)
args = parser.parse_args()
if args.mode == 'prepare':
    prepare(args.destination.resolve(), args.out.resolve())
else:
    assert args.native_micro is not None
    snapshot(args.destination.resolve(), args.out.resolve(), args.mode, args.native_micro.resolve())
