#!/usr/bin/env python3
"""Six-permutation D/P/R screen; only labels and one predeclared TCP cell are added."""
import argparse
import itertools
import json
import os
import pathlib
import statistics
import subprocess
import sys

from common import c8, rpc, sha, snapshot
from prepare import check_roots


parser = argparse.ArgumentParser(description=__doc__)
for name in ('dev', 'pre', 'candidate', 'output'):
    parser.add_argument(name, type=pathlib.Path)
parser.add_argument('--affinity', required=True)
args = parser.parse_args()
roots = {label: getattr(args, key).resolve() for label, key in (('D', 'dev'), ('P', 'pre'), ('R', 'candidate'))}
identities = check_roots(roots)
out = args.output.resolve()
out.mkdir(parents=True, exist_ok=False)
# Each block uses the same five workloads and the same complete internal c8
# phase sequence. Only the three-arm order varies, through all six permutations.
orders = list(itertools.permutations(('D', 'P', 'R')))
workloads = [('rpc', 'tcp', 'Client100x16'), ('rpc', 'sharedmemory', 'Client100x4096'), ('rpc', 'sharedmemory', 'Server1x16'),
             ('c8', 'sharedmemory', '1'), ('c8', 'sharedmemory', '10000')]
plan = [(block, kind, transport, case, label) for block, order in enumerate(orders)
        for kind, transport, case in workloads for label in order]
assert len(plan) == 90
relative = {'rpc': 'test/SharpLink.Benchmarks/bin/Release/net10.0/SharpLink.Benchmarks.dll',
            'c8': 'test/SharpLink.StreamLoadTest/bin/Release/net10.0/SharpLink.StreamLoadTest.dll'}
before = {label: snapshot(root) for label, root in roots.items()}
proof = dict(identities=identities, orders=orders, plan=plan, affinity=args.affinity,
    processor_count=4, attempts_per_launch=1, source_and_binaries_before=before, 
    boundary='Untraced same-host diagnostic screen only. No throughput acceptance label, omitted cell, numeric waiver, or method-level CPU attribution. Original c8 operation-all phase order is retained.')
(out / 'provenance.json').write_text(json.dumps(proof, indent=2) + '\n')
exits = []
try:
    for block, kind, transport, case, label in plan:
        name = f'b{block}-{kind}-{transport}-{case}-{label}'
        target = out / f'{name}.json'
        binary = roots[label] / relative[kind]
        command = ['taskset', '-c', args.affinity, 'dotnet', str(binary)]
        if kind == 'rpc':
            command += ['--generated-abi-streaming-evidence', case, '30', '5', '200000', str(target), transport]
        else:
            command += ['--mode', 'local', '--transport', transport, '--operation', 'all', '--stream-size', case,
                '--concurrency', '8', '--stream-receive-window-bytes', '8192', '--connection-receive-window-bytes', '65536',
                '--warmup', '1', '--duration', '2', '--recording', 'off', '--json-output', str(target)]
        env = dict(os.environ, DOTNET_PROCESSOR_COUNT='4', DOTNET_ReadyToRun='0', DOTNET_TieredCompilation='1',
            DOTNET_TieredPGO='1', DOTNET_TC_QuickJitForLoops='1', SHARPLINK_BENCHMARK_SHA=identities[label],
            SHARPLINK_COMMIT=identities[label])
        for key in ('DOTNET_JitDisasm', 'DOTNET_JitStdOutFile', 'SHARPLINK_READY_TCP_RECEIVE_BUFFER'):
            env.pop(key, None)
        with (out / f'{name}.log').open('w') as log:
            try:
                code = subprocess.run(command, cwd=roots[label], env=env, stdout=log,
                    stderr=subprocess.STDOUT, timeout=180).returncode
            except subprocess.TimeoutExpired:
                code = 124
        exits.append(dict(name=name, exit_code=code, command=command, identity=identities[label],
            binary_sha256=sha(binary), block=block, kind=kind, transport=transport, case=case, label=label))
        (out / 'exits.json').write_text(json.dumps(exits, indent=2) + '\n')
        print(name, code, flush=True)
finally:
    after = {label: snapshot(root) for label, root in roots.items()}
    (out / 'source-and-binaries-after.json').write_text(json.dumps(after, indent=2) + '\n')
    check_roots(roots)
    assert before == after, 'Source or binary bytes changed during the screen'
rows, failures, raw_hashes = [], [], {}
for launch in exits:
    path = out / f"{launch['name']}.json"
    if path.exists():
        raw_hashes[path.name] = sha(path)
    try:
        assert launch['exit_code'] == 0
        report = json.loads(path.read_text())
        assert report['processorCount' if launch['kind'] == 'rpc' else 'ProcessorCount'] == 4
        if launch['kind'] == 'rpc':
            selected = rpc(report, launch['identity'], launch['transport'], launch['case'])
        else:
            selected = c8(report, launch['identity'], launch['transport'], int(launch['case']))
        rows += [dict(block=launch['block'], label=launch['label'], kind=launch['kind'],
            transport=launch['transport'], case=launch['case'], **row) for row in selected]
        raw_hashes[path.name] = sha(path)
    except (AssertionError, OSError, ValueError, KeyError, TypeError) as error:
        failures.append(dict(name=launch['name'], exit_code=launch['exit_code'], validation_error=repr(error)))
(out / 'validated-rows.json').write_text(json.dumps(rows, indent=2) + '\n')
(out / 'validation-failures.json').write_text(json.dumps(failures, indent=2) + '\n')
(out / 'raw-sha256.json').write_text(json.dumps(raw_hashes, indent=2) + '\n')
assert len(exits) == 90 and not failures and len(rows) == 198, 'Incomplete/invalid population; all launches and failures retained without retry'
comparisons = []
normalized = []
for kind, transport, case in workloads:
    shapes = [case] if kind == 'rpc' else ['unary', 'c2s', 's2c', 'duplex']
    for shape in shapes:
        selected = [r for r in rows if (r['kind'], r['transport'], r['case'], r['shape']) == (kind, transport, case, shape)]
        grouped = {label: sorted((r for r in selected if r['label'] == label), key=lambda r: r['block']) for label in roots}
        assert all([r['block'] for r in group] == list(range(6)) for group in grouped.values())
        divisor = (1 if case == 'Server1x16' else 100) if kind == 'rpc' else (1 if shape == 'unary' else int(case))
        medians = {label: {key: statistics.median(r[key] for r in grouped[label])
                          for key in ('rate', 'cpu_us_op', 'bytes_op')} for label in roots}
        normalized.append(dict(kind=kind, transport=transport, case=case, shape=shape,
            absolute_medians={label: dict(values, bytes_item=values['bytes_op'] / divisor) for label, values in medians.items()},
            dev_normalized={label: {key: value / medians['D'][key] if medians['D'][key] else None
                                   for key, value in values.items()} for label, values in medians.items()}))
        for baseline, candidate_label in (('D', 'P'), ('D', 'R'), ('P', 'R')):
            metrics = {}
            for key in ('rate', 'cpu_us_op', 'bytes_op'):
                base, candidate = [r[key] for r in grouped[baseline]], [r[key] for r in grouped[candidate_label]]
                metrics[key] = dict(baseline=base, candidate=candidate,
                    paired_percent=[(c / b - 1) * 100 if b else None for b, c in zip(base, candidate)],
                    ratio_of_medians_percent=(statistics.median(candidate) / statistics.median(base) - 1) * 100 if statistics.median(base) else None,
                    median_absolute_delta=statistics.median(candidate) - statistics.median(base))
            comparisons.append(dict(kind=kind, transport=transport, case=case, shape=shape, baseline=baseline, candidate=candidate_label, metrics=metrics))
(out / 'comparisons.json').write_text(json.dumps(comparisons, indent=2) + '\n')
(out / 'dev-normalized-medians.json').write_text(json.dumps(normalized, indent=2) + '\n')
print(json.dumps(comparisons, indent=2))
print('90/90 launches and 198 complete result rows validated. Collection success is not performance acceptance.')
