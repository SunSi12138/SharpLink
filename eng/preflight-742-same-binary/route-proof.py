#!/usr/bin/env python3
"""Separate generated-RPC route counters; never mixed with untraced timing DLLs."""
import hashlib
import json
import os
import pathlib
import subprocess
import sys
import tempfile

source, out = (pathlib.Path(arg).resolve() for arg in sys.argv[1:3])
kit = pathlib.Path(__file__).resolve().parent
spec = json.loads((kit / 'adapter.json').read_text())
out.mkdir(parents=True, exist_ok=True)
work = pathlib.Path(tempfile.mkdtemp(prefix='742-route-count-', dir=os.environ.get('RUNNER_TEMP')))
assert not work.is_relative_to(out)
root = work / 'source'
subprocess.run(['git', 'worktree', 'add', '--detach', str(root), '06b737df0d9aab66caed9c446a572b3a8ce4efae'], cwd=source, check=True)
subprocess.run(['git', 'apply', '--index', str(source / 'artifacts/first-receive-integration/candidate.patch')], cwd=root, check=True)
assert subprocess.check_output(['git', 'write-tree'], cwd=root, text=True).strip() == spec['diagnostic_tree']
assert hashlib.sha256((kit / 'route-counter.patch').read_bytes()).hexdigest() == spec['route_counter_patch_sha256']
subprocess.run(['git', 'apply', '--check', '--index', str(kit / 'route-counter.patch')], cwd=root, check=True)
subprocess.run(['git', 'apply', '--index', str(kit / 'route-counter.patch')], cwd=root, check=True)
assert subprocess.check_output(['git', 'write-tree'], cwd=root, text=True).strip() == spec['route_counter_tree']
with (out / 'build.log').open('w') as log:
    subprocess.run(['dotnet', 'build', 'test/SharpLink.RouteCountProbe/SharpLink.RouteCountProbe.csproj', '-c', 'Release',
        '-m:1', '/nodeReuse:false', '-p:UseSharedCompilation=false', '-p:TreatWarningsAsErrors=true'], cwd=root,
        stdout=log, stderr=subprocess.STDOUT, check=True)
bin_dir = root / 'test/SharpLink.RouteCountProbe/bin/Release/net10.0'
hashes = {str(path.relative_to(bin_dir)): hashlib.sha256(path.read_bytes()).hexdigest() for path in bin_dir.rglob('*') if path.is_file()}
proof = dict(diagnostic_source=spec['diagnostic_tree'], counter_source=spec['route_counter_tree'],
    counter_patch_sha256=spec['route_counter_patch_sha256'], binary_sha256_before=hashes,
    source_sha256={path: hashlib.sha256((root / path).read_bytes()).hexdigest() for path in
        subprocess.check_output(['git', 'diff', '--name-only', spec['diagnostic_tree'], spec['route_counter_tree']], cwd=root, text=True).splitlines()},
    boundary='Separate counter build over real generated RPC, paired in-memory transport. Healthy route coverage only; no timing, SHM-performance, GC-retention or failure-path inference.')
(out / 'provenance.json').write_text(json.dumps(proof, indent=2) + '\n')
for repeat in (1, 2, 3):
    for mode in ('0', '1'):
        path = out / f'mode-{mode}-run-{repeat}.json'
        env = dict(os.environ)
        env[spec['flag_name']] = mode
        with path.open('w') as output, path.with_suffix('.err').open('w') as errors:
            subprocess.run(['dotnet', str(bin_dir / 'SharpLink.UnitTests.dll')], env=env, cwd=root,
                stdout=output, stderr=errors, check=True)
        report = json.loads(path.read_text())
        assert report['passed'] and report['diagnosticOnly'] and not report['timingClaims']
        assert report['mode'] == ('ON' if mode == '1' else 'OFF')
        assert report['warmupCalls'] == 8 and report['measuredCalls'] == 64
        stages = {stage['stage']: stage for stage in report['stages']}
        assert len(stages) == 8
        for stage in ('unary.warmup', 'unary.measured'):
            assert all(value == 0 for side in stages[stage]['delta'].values() for value in side.values())
        for stage, calls in (('server1.warmup', 8), ('server1.measured', 64)):
            counters = stages[stage]['delta']['Client']
            for key in ('RegisterCore', 'DispatchChunk', 'CompleteStream', 'DispatchesDrainedNotification'):
                assert counters[key] == calls
            for key in ('TryBeginQuiescentDirectCompletion', 'QuiescentClaimSucceeded', 'QuiescentCompletionSucceeded'):
                assert counters[key] == (calls if mode == '1' else 0)
        for stage in report['stages']:
            for side in stage['states'].values():
                assert side['activeStreams'] == side['busyEntries'] == 0 and side['pooledDispatcherReferencesClean']
        for side in ('clientFrameworkTasks', 'serverFrameworkTasks'):
            shutdown = stages['teardown'][side]
            assert shutdown['IsSealed'] and shutdown['IsDrained'] and shutdown['ActiveTasks'] == 0
        assert hashes == {str(path.relative_to(bin_dir)): hashlib.sha256(path.read_bytes()).hexdigest() for path in bin_dir.rglob('*') if path.is_file()}
proof['binary_sha256_after'] = hashes
proof['reports_sha256'] = {path.name: hashlib.sha256(path.read_bytes()).hexdigest() for path in out.glob('mode-*-run-*.json')}
(out / 'provenance.json').write_text(json.dumps(proof, indent=2) + '\n')
print('6/6 actual generated-RPC count processes verified; no timing claim.')
