#!/usr/bin/env python3
"""Pinned baseline RED and candidate GREEN for the hosted allocator accessor."""
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import traceback

BASELINE = 'fbaf3e40a1d36133846f101bf1aacfbec6ec4a3d'
CANDIDATE = 'd0ea436cdab159ebbb354f88b1e9700ddd6704eb'
FIXTURE = 'test/SharpLink.UnitTests/Hosting/AnonymousPipeAllocatorAccessorTests.cs'
PROJECT = 'test/SharpLink.UnitTests/SharpLink.UnitTests.csproj'
repo = Path.cwd()
out = repo / 'artifacts/anonymous-pipe-accessor'
out.mkdir(parents=True, exist_ok=False)
work_root = Path(os.environ['RUNNER_TEMP']) / ('allocator-' + os.environ['GITHUB_RUN_ID'])
work_root.mkdir(parents=True, exist_ok=False)
result = {'baseline': BASELINE, 'candidate': CANDIDATE, 'runs': [], 'status': 'running'}

def save():
    (out / 'result.json').write_text(json.dumps(result, indent=2) + '\n')

def run(args, work, name):
    completed = subprocess.run(args, cwd=work, text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    (out / (name + '.log')).write_text(completed.stdout)
    print(completed.stdout, flush=True)
    return completed.returncode, re.sub(r'\x1b\[[0-9;]*m', '', completed.stdout)

def validate(code, text, name, total, failed, expected_error=None):
    counts = {}
    for key in ('total', 'failed', 'succeeded', 'skipped'):
        matches = re.findall(r'\b' + key + r':\s*(\d+)\b', text)
        counts[key] = int(matches[-1]) if matches else (0 if key == 'failed' else None)
    expected = {'total': total, 'failed': failed, 'succeeded': total - failed, 'skipped': 0}
    row = {'name': name, 'exitCode': code, 'counts': counts, 'expected': expected}
    result['runs'].append(row)
    save()
    if counts != expected or ((code == 0) != (failed == 0)):
        raise RuntimeError(f'{name}: unexpected exit/counts: {row}')
    if expected_error and expected_error not in text:
        raise RuntimeError(f'{name}: original missing-accessor failure was not reproduced')

try:
    result['workflowSha'] = subprocess.check_output(['git', 'rev-parse', 'HEAD'], text=True).strip()
    subprocess.run(['git', 'fetch', 'origin', BASELINE, CANDIDATE], check=True)
    fixture = subprocess.check_output(['git', 'show', CANDIDATE + ':' + FIXTURE])
    result['fixtureSha256'] = hashlib.sha256(fixture).hexdigest()
    (out / 'dotnet-info.txt').write_text(subprocess.check_output(['dotnet', '--info'], text=True))
    save()
    for arm, sha in [('baseline', BASELINE), ('candidate', CANDIDATE)]:
        work = work_root / arm
        subprocess.run(['git', 'worktree', 'add', '--detach', str(work), sha], check=True)
        (work / FIXTURE).write_bytes(fixture)
        if hashlib.sha256((work / FIXTURE).read_bytes()).hexdigest() != result['fixtureSha256']:
            raise RuntimeError('Fixture mismatch')
        (out / (arm + '-overlay.txt')).write_text(subprocess.check_output(['git', 'status', '--short'], cwd=work, text=True))
        code, text = run(['dotnet', 'build', PROJECT, '-c', 'Release', '-v', 'minimal'], work, arm + '-build')
        if code:
            raise RuntimeError(arm + ' build failed; this is not regression evidence')
        if arm == 'baseline':
            args = ['dotnet', 'test', '--project', PROJECT, '-c', 'Release', '--no-build',
                    '--treenode-filter', '/*/*/AnonymousPipeAllocatorAccessorTests/ConfiguredAllocatorShouldRemainAvailableAcrossStartup',
                    '--maximum-parallel-tests', '1']
            code, text = run(args, work, arm + '-focused')
            validate(code, text, arm + '-focused', 4, 2,
                     "No service for type 'SharpLink.Hosting.IAnonymousPipeAllocatorAccessor' has been registered.")
        else:
            args = ['dotnet', 'test', '--project', PROJECT, '-c', 'Release', '--no-build',
                    '--treenode-filter', '/*/*/AnonymousPipeAllocatorAccessorTests/*', '--maximum-parallel-tests', '1']
            code, text = run(args, work, arm + '-focused')
            validate(code, text, arm + '-focused', 11, 0)
            code, text = run(['dotnet', 'test', '--project', PROJECT, '-c', 'Release', '--no-build',
                              '--maximum-parallel-tests', '8'], work, arm + '-full')
            validate(code, text, arm + '-full', 2106, 0)
    result['status'] = 'expected-baseline-red-and-candidate-green-passed'
    save()
except BaseException as error:
    result['status'] = 'failed'
    result['error'] = str(error)
    (out / 'error.txt').write_text(traceback.format_exc())
    save()
    raise
