#!/usr/bin/env python3
"""Untimed frozen R source, harness, and produced-binary identity checks only."""
import argparse
import hashlib
import json
import pathlib
import shutil
import subprocess

KIT = pathlib.Path(__file__).resolve().parent
SOURCE = KIT.parents[1]

def git(root, *args):
    return subprocess.check_output(['git', *args], cwd=root, text=True).strip()

def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()

def save(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, indent=2) + '\n')

def check_roots(destination):
    import prepare as reconstruction
    return reconstruction.check_roots({'dev': destination / 'dev', 'candidate': destination / 'candidate'})

def prepare(destination, out):
    import prepare as reconstruction
    reconstruction.prepare(destination, out)

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
