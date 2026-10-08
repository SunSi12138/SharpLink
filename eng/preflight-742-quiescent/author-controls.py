#!/usr/bin/env python3
"""Run final author-declared old-N controls with exact counts and restoration."""
import argparse
import hashlib
import json
import pathlib
import re
import subprocess
import sys

parser = argparse.ArgumentParser()
parser.add_argument('baseline', type=pathlib.Path)
parser.add_argument('candidate', type=pathlib.Path)
parser.add_argument('output', type=pathlib.Path)
parser.add_argument('--spec', default='author-controls.json')
args = parser.parse_args()
old_n, candidate, out = (path.resolve() for path in (args.baseline, args.candidate, args.output))
kit = pathlib.Path(__file__).resolve().parent
spec = json.loads((kit / args.spec).read_text())
assert spec['status'] == 'final' and spec['controls'], 'Final F author controls have not been supplied'
out.mkdir(parents=True, exist_ok=True)
(out / 'author-controls-spec.json').write_text(json.dumps(spec, indent=2) + '\n')
for control in spec['controls']:
    patch = kit / control['patch']
    assert hashlib.sha256(patch.read_bytes()).hexdigest() == control['patch_sha256']
    created = [old_n / path for path in control['created_test_paths']]
    assert all(path.is_relative_to(old_n / 'test') and not path.exists() for path in created)
    subprocess.run(['git', 'diff', '--exit-code', '--', 'src', 'test'], cwd=old_n, check=True)
    try:
        subprocess.run(['git', 'apply', '--check', str(patch)], cwd=old_n, check=True)
        subprocess.run(['git', 'apply', str(patch)], cwd=old_n, check=True)
        with (out / f"{control['name']}-build.log").open('w') as log:
            subprocess.run(['dotnet', 'build', 'test/SharpLink.UnitTests', '-c', 'Release', '-p:Platform=AnyCPU',
                '-p:TreatWarningsAsErrors=true', '-m:1', '/nodeReuse:false', '-p:UseSharedCompilation=false'],
                cwd=old_n, stdout=log, stderr=subprocess.STDOUT, check=True)
        path = out / f"{control['name']}-negative.log"
        with path.open('w') as log:
            result = subprocess.run(['dotnet', 'test', '--project', 'test/SharpLink.UnitTests', '-c', 'Release',
                '--no-build', '-p:Platform=AnyCPU', '--', '--treenode-filter', control['filter'],
                '--minimum-expected-tests', str(control['total'])], cwd=old_n, stdout=log, stderr=subprocess.STDOUT)
        text = re.sub(r'\x1b\[[0-9;]*m', '', path.read_text())
        assert result.returncode != 0
        for field, count in (('total', control['total']), ('failed', control['failed']),
                             ('succeeded', control['total'] - control['failed']), ('skipped', 0)):
            assert re.search(rf'{field}:\s+{count}\b', text), (field, path)
        assert control['failure_message'] in text
    finally:
        subprocess.run(['git', 'restore', '--worktree', '--', 'src', 'test'], cwd=old_n, check=True)
        for path in created:
            path.unlink(missing_ok=True)
        subprocess.run(['git', 'diff', '--exit-code', '--', 'src', 'test'], cwd=old_n, check=True)
