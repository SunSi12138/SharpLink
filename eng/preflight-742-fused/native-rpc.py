#!/usr/bin/env python3
"""Actual NativeAOT D/F RPC: the full original 7x2x3 paired population and order."""
import hashlib
import json
import math
import os
import pathlib
import statistics
import subprocess
import sys

roots = {label: pathlib.Path(value).resolve() for label, value in zip(('baseline', 'candidate'), sys.argv[1:3])}
out = pathlib.Path(sys.argv[3]).resolve()
affinity = sys.argv[4]
out.mkdir(parents=True, exist_ok=True)
spec = json.loads((pathlib.Path(__file__).resolve().parent / 'candidate.json').read_text())
assert spec['candidate_tree'], 'Final F identity is not pinned'
identities = {'baseline': '0fe26024b114bb6e78411a9b86276086c045d03d', 'candidate': spec['candidate_tree']}
assert json.loads((roots['candidate'] / 'artifacts/first-receive-integration/provenance.json').read_text())['disposable_tree'] == identities['candidate']
scenarios = ('Server1x16', 'Server100x16', 'Server100x4096', 'Client100x16', 'Client100x4096', 'Duplex100x16', 'Duplex100x4096')
plan = [(rep, transport, scenario, label) for rep in (1, 2, 3) for transport in ('tcp', 'sharedmemory')
        for scenario in (scenarios if rep % 2 else tuple(reversed(scenarios)))
        for label in (('baseline', 'candidate') if rep % 2 else ('candidate', 'baseline'))]
binaries = {label: root / 'artifacts/fused-native-rpc/SharpLink.Benchmarks' for label, root in roots.items()}
hashes = {label: hashlib.sha256(path.read_bytes()).hexdigest() for label, path in binaries.items()}
def snapshot(root):
    tracked = subprocess.check_output(['git', 'ls-files', '--', 'src', 'test'], cwd=root, text=True).splitlines()
    paths = [root / path for path in tracked]
    paths += [p for p in (root / 'test/SharpLink.FirstReceiveRpcEvidence').iterdir() if p.is_file()]
    paths += [p for p in (root / 'artifacts/fused-native-rpc').rglob('*') if p.is_file()]
    return {str(p.relative_to(root)): hashlib.sha256(p.read_bytes()).hexdigest() for p in paths}
before = {label: snapshot(root) for label, root in roots.items()}
proof = dict(identities=identities, runtime='actual NativeAOT', plan=plan, affinity=affinity,
    binary_sha256=hashes, source_and_binaries_before=before, attempts_per_launch=1,
    boundary='Full original RPC cells and AB/BA/AB order, 30 warmups, 5-second window, 200000 cap. Native host adaptation only changes out-of-measurement JSON metadata. This does not replace original JIT/micro/c8 acceptance.')
(out / 'provenance.json').write_text(json.dumps(proof, indent=2) + '\n')
exits = []
for rep, transport, scenario, label in plan:
    name = f'{transport}-{label}-r{rep}-{scenario}'
    target = out / f'{name}.json'
    env = dict(os.environ, SHARPLINK_BENCHMARK_SHA=identities[label], DOTNET_ReadyToRun='0',
        DOTNET_TieredCompilation='1', DOTNET_TieredPGO='1', DOTNET_TC_QuickJitForLoops='1')
    command = ['taskset', '-c', affinity, str(binaries[label]), scenario, '30', '5', '200000', str(target), transport]
    with (out / f'{name}.log').open('w') as log:
        try:
            code = subprocess.run(command, env=env, cwd=roots[label], stdout=log,
                stderr=subprocess.STDOUT, timeout=180).returncode
        except subprocess.TimeoutExpired:
            code = 124
    exits.append(dict(name=name, exit_code=code, command=command, identity=identities[label]))
    (out / 'exits.json').write_text(json.dumps(exits, indent=2) + '\n')
    print(name, code, flush=True)
after = {label: snapshot(root) for label, root in roots.items()}
(out / 'source-and-binaries-after.json').write_text(json.dumps(after, indent=2) + '\n')
assert before == after, 'Native RPC source or binary bytes changed'
assert len(exits) == 84 and all(row['exit_code'] == 0 for row in exits), 'All failures retained; no retry or missing-cell substitution'
rows, raw_hashes = [], {}
for rep, transport, scenario, label in plan:
    path = out / f'{transport}-{label}-r{rep}-{scenario}.json'
    report = json.loads(path.read_text())
    assert report['commit'] == identities[label] and report['scenario'] == scenario and report['transport'] == transport
    assert report['validationFailures'] == 0 and report['operations'] > 0
    assert report['warmupOperations'] == 30 and report['requestedMeasurementSeconds'] == 5
    assert 'NATIVE_RPC_EVIDENCE dynamicCodeSupported=False' in path.with_suffix('.log').read_text()
    for name in ('throughputItemsPerSecond', 'cpuUsPerOperation', 'p50Us', 'p99Us', 'allocatedBytesPerItem'):
        assert math.isfinite(report[name]) and report[name] > 0, (path, name)
    rows.append(dict(label=label, repeat=rep, **report))
    raw_hashes[path.name] = hashlib.sha256(path.read_bytes()).hexdigest()
summary = []
for transport in ('tcp', 'sharedmemory'):
    for scenario in scenarios:
        selected = [r for r in rows if r['transport'] == transport and r['scenario'] == scenario]
        paired = {label: sorted((r for r in selected if r['label'] == label), key=lambda r: r['repeat']) for label in roots}
        metrics = {}
        for key in ('throughputItemsPerSecond', 'cpuUsPerOperation', 'p50Us', 'p99Us', 'allocatedBytesPerItem'):
            base = [r[key] for r in paired['baseline']]
            candidate = [r[key] for r in paired['candidate']]
            assert len(base) == len(candidate) == 3
            metrics[key] = dict(baseline=base, candidate=candidate,
                paired_percent=[(c / b - 1) * 100 for b, c in zip(base, candidate)],
                ratio_of_medians_percent=(statistics.median(candidate) / statistics.median(base) - 1) * 100,
                median_absolute_delta=statistics.median(candidate) - statistics.median(base))
        summary.append(dict(transport=transport, scenario=scenario, metrics=metrics))
(out / 'summary.json').write_text(json.dumps(summary, indent=2) + '\n')
(out / 'validated-population.json').write_text(json.dumps(dict(processes=84, raw_sha256=raw_hashes,
    hit_operation_limit=[dict(label=r['label'], repeat=r['repeat'], transport=r['transport'], scenario=r['scenario']) for r in rows if r['hitOperationLimit']],
    boundary='Every original RPC cell retained. Numeric results, including regressions, require review; successful collection is not performance acceptance.'), indent=2) + '\n')
print(json.dumps(summary, indent=2))
