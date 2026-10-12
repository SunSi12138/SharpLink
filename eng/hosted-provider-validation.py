#!/usr/bin/env python3
"""Pinned baseline RED and candidate GREEN for the hosted service provider precedence."""
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import traceback

BASELINE = 'fbaf3e40a1d36133846f101bf1aacfbec6ec4a3d'
CURRENT_BASE = '51ec03928aa841f2c27b257e93a2fa0353edf641'
CANDIDATE = 'c70053b604e74dcce0822fcbda71ba4f7fdfb07a'
FIXTURE = 'test/SharpLink.IntegrationTests/HostedServiceProviderIntegrationTests.cs'
PROJECT = 'test/SharpLink.IntegrationTests/SharpLink.IntegrationTests.csproj'
repo = Path.cwd()
out = repo / 'artifacts/hosted-provider'
out.mkdir(parents=True, exist_ok=False)
work_root = Path(os.environ['RUNNER_TEMP']) / ('provider-' + os.environ['GITHUB_RUN_ID'])
work_root.mkdir(parents=True, exist_ok=False)
result = {'baseline': BASELINE, 'candidate': CANDIDATE, 'currentBase': CURRENT_BASE, 'runs': [], 'status': 'running'}

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
    if expected_error:
        for lifetime in ('Singleton', 'Connection', 'Call'):
            for mode in (0, 1, 3):
                marker = f'failed HostedRpcShouldUseSelectedProviderAndHonorOwnership({lifetime}, {mode})'
                if marker not in text:
                    raise RuntimeError(f'{name}: missing expected provider-overwrite failure: {marker}')
        for marker in ['expected application dependency; received host:',
                       'failed ExplicitProviderShouldNotFallBackToHostForMissingBusinessDependency',
                       'missing dependency in the explicit provider must fail the real RPC without Host fallback']:
            if marker not in text:
                raise RuntimeError(f'{name}: missing semantic RED evidence: {marker}')

try:
    result['workflowSha'] = subprocess.check_output(['git', 'rev-parse', 'HEAD'], text=True).strip()
    subprocess.run(['git', 'fetch', 'origin', BASELINE, CURRENT_BASE, CANDIDATE], check=True)
    fixture = subprocess.check_output(['git', 'show', CANDIDATE + ':' + FIXTURE])
    result['fixtureSha256'] = hashlib.sha256(fixture).hexdigest()
    (out / 'dotnet-info.txt').write_text(subprocess.check_output(['dotnet', '--info'], text=True))
    save()
    for arm, sha in [('baseline', BASELINE), ('current-base', CURRENT_BASE), ('candidate', CANDIDATE)]:
        work = work_root / arm
        subprocess.run(['git', 'worktree', 'add', '--detach', str(work), sha], check=True)
        (work / FIXTURE).write_bytes(fixture)
        (work / PROJECT).write_bytes(subprocess.check_output(['git', 'show', CANDIDATE + ':' + PROJECT]))
        if hashlib.sha256((work / FIXTURE).read_bytes()).hexdigest() != result['fixtureSha256']:
            raise RuntimeError('Fixture mismatch')
        (out / (arm + '-overlay.txt')).write_text(subprocess.check_output(['git', 'status', '--short'], cwd=work, text=True))
        code, text = run(['dotnet', 'build', PROJECT, '-c', 'Release', '-v', 'minimal'], work, arm + '-build')
        if code:
            raise RuntimeError(arm + ' build failed; this is not regression evidence')
        args = ['dotnet', 'run', '--project', PROJECT, '-c', 'Release', '--no-build', '--',
                '--treenode-filter', '/*/*/HostedServiceProviderIntegrationTests/*',
                '--maximum-parallel-tests', '1', '--timeout', '120s']
        code, text = run(args, work, arm + '-focused')
        validate(code, text, arm + '-focused', 13, 0 if arm == 'candidate' else 10,
                 expected_error=arm != 'candidate')
        if arm == 'candidate':
            code, text = run(['dotnet', 'run', '--project', PROJECT, '-c', 'Release', '--no-build', '--',
                              '--maximum-parallel-tests', '1', '--timeout', '120s'], work, arm + '-full')
            validate(code, text, arm + '-full', 467, 0)
    result['status'] = 'expected-baseline-red-and-candidate-green-passed'
    save()
except BaseException as error:
    result['status'] = 'failed'
    result['error'] = str(error)
    (out / 'error.txt').write_text(traceback.format_exc())
    save()
    raise
