#!/usr/bin/env python3
"""Drive the actual hosted capture boundary in two pinned products with identical scheduling instrumentation."""
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import traceback

BASELINE = '1d9cbc7a68fec1bf716ccd35ca3c74f5b6b2a093'
CANDIDATE = 'be2fc93a6f346f1ddcb41efc4787684aac4d25f9'
SOURCE = 'src/SharpLink.Hosting/AnonymousPipeAllocatorAccessor.cs'
PROBE = 'test/SharpLink.UnitTests/Hosting/AnonymousPipeFrozenCaptureProbe.cs'
PROJECT = 'test/SharpLink.UnitTests/SharpLink.UnitTests.csproj'
repo = Path.cwd()
out = repo / 'artifacts/anonymous-pipe-frozen-capture'
out.mkdir(parents=True, exist_ok=False)
work_root = Path(os.environ['RUNNER_TEMP']) / ('frozen-allocator-' + os.environ['GITHUB_RUN_ID'] + '-' + os.environ['GITHUB_RUN_ATTEMPT'])
work_root.mkdir(parents=True, exist_ok=False)
probe = (repo / 'eng/anonymous-pipe-frozen-validation/AnonymousPipeFrozenCaptureProbe.cs').read_bytes()
result = {'baseline': BASELINE, 'candidate': CANDIDATE, 'fixtureSha256': hashlib.sha256(probe).hexdigest(),
          'instrumentation': 'Identical scheduling-only AppContext Action immediately after Volatile.Write in CaptureTransport; actual HostedService.StartAsync is used.',
          'runs': [], 'arms': {}, 'status': 'running'}

def save():
    (out / 'result.json').write_text(json.dumps(result, indent=2) + '\n')

def run(args, work, name):
    completed = subprocess.run(args, cwd=work, text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=600)
    (out / (name + '.log')).write_text(completed.stdout)
    print(completed.stdout, flush=True)
    return completed.returncode, re.sub(r'\x1b\[[0-9;]*m', '', completed.stdout)

def require(args, work, name):
    code, text = run(args, work, name)
    if code:
        raise RuntimeError(name + ' failed; this is not regression evidence')

def validate(code, text, name, total, failed):
    counts = {}
    for key in ('total', 'failed', 'succeeded', 'skipped'):
        matches = re.findall(r'\b' + key + r':\s*(\d+)\b', text)
        counts[key] = int(matches[-1]) if matches else (0 if key == 'failed' else None)
    expected = {'total': total, 'failed': failed, 'succeeded': total - failed, 'skipped': 0}
    result['runs'].append({'name': name, 'exitCode': code, 'counts': counts, 'expected': expected})
    save()
    if counts != expected or ((code == 0) != (failed == 0)):
        raise RuntimeError(f'{name}: unexpected exit/counts: {code}, {counts}, expected {expected}')
    if failed:
        for marker in ['failed AcceptedConfigurationMustSurviveHostedBuild(False)',
                       'accepted transport was lost across hosted Build']:
            if marker not in text:
                raise RuntimeError(name + ': expected exact lost-reference failure is absent')

try:
    result['workflowSha'] = subprocess.check_output(['git', 'rev-parse', 'HEAD'], text=True).strip()
    subprocess.run(['git', 'fetch', 'origin', BASELINE, CANDIDATE], check=True)
    (out / 'dotnet-info.txt').write_text(subprocess.check_output(['dotnet', '--info'], text=True))
    (out / 'product.patch').write_bytes(subprocess.check_output(['git', 'diff', BASELINE, CANDIDATE, '--', 'src']))
    save()
    for arm, sha in [('baseline', BASELINE), ('candidate', CANDIDATE)]:
        work = work_root / arm
        subprocess.run(['git', 'worktree', 'add', '--detach', str(work), sha], check=True)
        (out / (arm + '-production-tree.txt')).write_bytes(subprocess.check_output(['git', 'ls-tree', '-r', 'HEAD', '--', 'src'], cwd=work))
        source = work / SOURCE
        original = source.read_bytes()
        original_text = original.decode()
        before = ('    internal void CaptureTransport() =>\n'
                  '        Volatile.Write(ref _allocator, builder.Transport as IAnonymousPipeAllocator);') if arm == 'baseline' else (
                  '    internal void CaptureTransport(IServerTransportListener? transport) =>\n'
                  '        Volatile.Write(ref _allocator, transport as IAnonymousPipeAllocator);')
        if original_text.count(before) != 1:
            raise RuntimeError(arm + ': capture source is not the reviewed exact shape')
        lines = before.splitlines()
        after = (lines[0].removesuffix(' =>') + '\n    {\n' + lines[1] + '\n'
                 '        (AppContext.GetData("SharpLink.Validation.FrozenAllocatorCapture") as Action)?.Invoke();\n    }')
        source.write_text(original_text.replace(before, after))
        (work / PROBE).write_bytes(probe)
        result['arms'][arm] = {'head': sha, 'originalSourceSha256': hashlib.sha256(original).hexdigest(),
                               'instrumentedSourceSha256': hashlib.sha256(source.read_bytes()).hexdigest()}
        (out / (arm + '-instrumentation.patch')).write_bytes(subprocess.check_output(['git', 'diff', '--', 'src'], cwd=work))
        save()
        require(['dotnet', 'build', PROJECT, '-c', 'Release', '-v', 'minimal'], work, arm + '-instrumented-build')
        args = ['dotnet', 'test', '--project', PROJECT, '-c', 'Release', '--no-build',
                '--treenode-filter', '/*/*/AnonymousPipeFrozenCaptureProbe/*', '--maximum-parallel-tests', '1']
        code, text = run(args, work, arm + '-race-probe')
        validate(code, text, arm + '-race-probe', 2, 1 if arm == 'baseline' else 0)
        # The process-global diagnostic hook is never run with the wider suite.
        # Restore the exact production bytes and remove the probe before rebuilding.
        source.write_bytes(original)
        (work / PROBE).unlink()
        if subprocess.check_output(['git', 'status', '--porcelain'], cwd=work, text=True).strip():
            raise RuntimeError(arm + ': product was not exactly restored after instrumentation')
        result['arms'][arm]['restoredExactProduct'] = True
        save()
        if arm == 'candidate':
            require(['dotnet', 'build', PROJECT, '-c', 'Release', '-v', 'minimal'], work, 'candidate-clean-build')
            code, text = run(['dotnet', 'test', '--project', PROJECT, '-c', 'Release', '--no-build',
                              '--treenode-filter', '/*/*/AnonymousPipeAllocatorCaptureTests/*',
                              '--maximum-parallel-tests', '1'], work, 'candidate-capture-ownership')
            validate(code, text, 'candidate-capture-ownership', 4, 0)
            code, text = run(['dotnet', 'test', '--project', PROJECT, '-c', 'Release', '--no-build',
                              '--maximum-parallel-tests', '8'], work, 'candidate-clean-full')
            validate(code, text, 'candidate-clean-full', 2121, 0)
    result['status'] = 'expected-baseline-red-and-candidate-green-passed'
    save()
except BaseException as error:
    result['status'] = 'failed'
    result['error'] = str(error)
    (out / 'error.txt').write_text(traceback.format_exc())
    save()
    raise
