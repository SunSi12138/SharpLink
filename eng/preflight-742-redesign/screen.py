#!/usr/bin/env python3
"""Untraced, source-pinned whole-redesign screen; never a substitute for acceptance."""
import argparse
import hashlib
import json
import math
import os
import pathlib
import statistics
import subprocess

parser = argparse.ArgumentParser()
for name in ('dev', 'control', 'candidate', 'output'):
    parser.add_argument(name, type=pathlib.Path)
parser.add_argument('--runtime', choices=('jit', 'native'), default='jit')
args = parser.parse_args()
roots = {label: getattr(args, label).resolve() for label in ('dev', 'control', 'candidate')}
out = args.output.resolve()
out.mkdir(parents=True, exist_ok=True)
identities = {'dev': '0fe26024b114bb6e78411a9b86276086c045d03d'}
for label in ('control', 'candidate'):
    provenance = json.loads((roots[label] / 'artifacts/first-receive-integration/provenance.json').read_text())
    identities[label] = provenance['disposable_tree']
orders = [('dev', 'control', 'candidate'), ('control', 'candidate', 'dev'), ('candidate', 'dev', 'control')]
workloads = [('e2e', 'sharedmemory', 'Server1x16'), ('e2e', 'sharedmemory', 'Duplex100x16')]
if args.runtime == 'jit':
    workloads += [('c8', transport, str(size)) for transport in ('tcp', 'sharedmemory') for size in (1, 10000)]
plan = [(repeat, kind, transport, case, label) for repeat in range(3)
        for kind, transport, case in workloads for label in orders[repeat]]
paths = {'e2e': 'test/SharpLink.Benchmarks/bin/Release/net10.0/SharpLink.Benchmarks.dll',
         'c8': 'test/SharpLink.StreamLoadTest/bin/Release/net10.0/SharpLink.StreamLoadTest.dll'}
if args.runtime == 'native':
    paths = {'e2e': 'artifacts/redesign-native-rpc/SharpLink.Benchmarks'}
binaries = {label: {kind: str(root / path) for kind, path in paths.items()} for label, root in roots.items()}
proof = dict(identities=identities, runtime=args.runtime, plan=plan, affinity='0-3', attempts_per_launch=1,
    boundary='Whole redesign C-to-N and absolute dev-to-N diagnostic only; no component throughput attribution; no acceptance gate replacement.',
    binary_sha256={label: {kind: hashlib.sha256(pathlib.Path(path).read_bytes()).hexdigest()
                         for kind, path in values.items()} for label, values in binaries.items()})
(out / 'provenance.json').write_text(json.dumps(proof, indent=2) + '\n')
exits = []
for repeat, kind, transport, case, label in plan:
    name = f'{kind}-{transport}-{case}-{label}-r{repeat}'
    target = out / f'{name}.json'
    env = dict(os.environ, DOTNET_PROCESSOR_COUNT='4', DOTNET_ReadyToRun='0',
               DOTNET_TieredCompilation='1', DOTNET_TieredPGO='1', DOTNET_TC_QuickJitForLoops='1',
               SHARPLINK_BENCHMARK_SHA=identities[label], SHARPLINK_COMMIT=identities[label])
    for key in ('DOTNET_JitDisasm', 'DOTNET_JitStdOutFile', 'SHARPLINK_READY_TCP_RECEIVE_BUFFER'):
        env.pop(key, None)
    command = ['taskset', '-c', '0-3']
    command += [binaries[label][kind]] if args.runtime == 'native' else ['dotnet', binaries[label][kind]]
    if kind == 'e2e':
        if args.runtime == 'jit':
            command += ['--generated-abi-streaming-evidence']
        command += [case, '30', '5', '200000', str(target), transport]
    else:
        command += ['--mode', 'local', '--transport', transport, '--operation', 'all',
                    '--stream-size', case, '--concurrency', '8', '--stream-receive-window-bytes', '8192',
                    '--connection-receive-window-bytes', '65536', '--warmup', '1', '--duration', '2',
                    '--recording', 'off', '--json-output', str(target)]
    print('REDESIGN_RUN', name, flush=True)
    with (out / f'{name}.log').open('w') as log:
        try:
            code = subprocess.run(command, cwd=roots[label], env=env, stdout=log,
                                  stderr=subprocess.STDOUT, timeout=180).returncode
        except subprocess.TimeoutExpired:
            code = 124
    exits.append(dict(name=name, exit_code=code, command=command, identity=identities[label]))
    (out / 'exits.json').write_text(json.dumps(exits, indent=2) + '\n')
    print('REDESIGN_EXIT', name, code, flush=True)
assert len(exits) == len(plan) and all(row['exit_code'] == 0 for row in exits), 'Incomplete population; failures retained without retry'

rows = []
for repeat, kind, transport, case, label in plan:
    path = out / f'{kind}-{transport}-{case}-{label}-r{repeat}.json'
    report = json.loads(path.read_text())
    common = dict(repeat=repeat, kind=kind, transport=transport, case=case, label=label)
    if kind == 'e2e':
        assert report['commit'] == identities[label] and report['scenario'] == case
        assert report['transport'] == transport and report['validationFailures'] == 0
        assert report['warmupOperations'] == 30 and report['requestedMeasurementSeconds'] == 5
        assert report['operations'] > 0 and not report['hitOperationLimit']
        if args.runtime == 'native':
            assert 'NATIVE_RPC_EVIDENCE dynamicCodeSupported=False' in path.with_suffix('.log').read_text()
        rows.append(dict(common, shape=case, rate=report['throughputItemsPerSecond'],
                         cpu_us_op=report['cpuUsPerOperation'], bytes_op=report['allocatedBytesPerOperation'],
                         operations=report['operations'], p99_us=report['p99Us']))
    else:
        assert report['SourceCommit'] == identities[label]
        cfg = report['Configuration']
        assert cfg['StreamSize'] == int(case) and cfg['ConcurrencyConfig'] == [8]
        assert cfg['Transport'] == {'tcp': 0, 'sharedmemory': 4}[transport]
        assert report['ProcessorCount'] == 4
        assert cfg['StreamReceiveWindowBytes'] == 8192 and cfg['ConnectionReceiveWindowBytes'] == 65536
        assert cfg['DurationSeconds'] == 2 and cfg['WarmupSeconds'] == 1 and cfg['Operation'] == 'all'
        assert {r['Operation'] for r in report['Results']} == {'unary', 'c2s', 's2c', 'duplex'}
        for row in report['Results']:
            assert row['Failure'] == row['Cancelled'] == row['ValidationFailure'] == 0
            assert row['Success'] == row['OperationsStartedDuringMeasurement'] == row['OperationsCompleted'] > 0
            assert row['RecorderMode'] == 'off'
            evidence = row['Evidence']
            rows.append(dict(common, shape=row['Operation'], rate=row['Qps'],
                             cpu_us_op=evidence['CpuMilliseconds'] * 1000 / row['Success'],
                             bytes_op=evidence['AllocatedBytes'] / row['Success'], operations=row['Success'],
                             drain_seconds=row['DrainDurationSeconds'], measurement_seconds=row['MeasurementDurationSeconds']))
for row in rows:
    assert all(math.isfinite(row[key]) and row[key] > 0 for key in ('rate', 'cpu_us_op', 'bytes_op')), row
(out / 'validated-rows.json').write_text(json.dumps(rows, indent=2) + '\n')
comparisons = []
for kind, transport, case in workloads:
    shapes = [case] if kind == 'e2e' else ['unary', 'c2s', 's2c', 'duplex']
    for shape in shapes:
        selected = [r for r in rows if (r['kind'], r['transport'], r['case'], r['shape']) == (kind, transport, case, shape)]
        grouped = {label: sorted((r for r in selected if r['label'] == label), key=lambda r: r['repeat']) for label in roots}
        for baseline in ('dev', 'control'):
            assert len(grouped[baseline]) == len(grouped['candidate']) == 3
            metrics = {}
            for metric in ('rate', 'cpu_us_op', 'bytes_op'):
                base = [r[metric] for r in grouped[baseline]]
                cand = [r[metric] for r in grouped['candidate']]
                metrics[metric] = dict(baseline=base, candidate=cand,
                    paired_percent=[(c / b - 1) * 100 for b, c in zip(base, cand)],
                    ratio_of_medians_percent=(statistics.median(cand) / statistics.median(base) - 1) * 100,
                    median_absolute_delta=statistics.median(cand) - statistics.median(base))
            comparisons.append(dict(kind=kind, transport=transport, case=case, shape=shape, baseline=baseline, metrics=metrics))
(out / 'comparison.json').write_text(json.dumps(comparisons, indent=2) + '\n')
print(json.dumps(comparisons, indent=2))
print(f'{len(exits)}/{len(plan)} launches and all populations validated; collection success is not performance acceptance.')
