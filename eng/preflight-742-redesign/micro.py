#!/usr/bin/env python3
"""Retain the unchanged full keyed/resolved micro matrix, including old short API."""
import argparse
import hashlib
import json
import math
import os
import pathlib
import statistics
import subprocess

p = argparse.ArgumentParser()
for name in ('control', 'candidate', 'output'):
    p.add_argument(name, type=pathlib.Path)
p.add_argument('--runtime', choices=('pgo0', 'pgo1', 'native'), required=True)
a = p.parse_args()
roots = {label: getattr(a, label).resolve() for label in ('control', 'candidate')}
out = a.output.resolve()
out.mkdir(parents=True, exist_ok=True)
relative = 'artifacts/redesign-native/SharpLink.Benchmarks' if a.runtime == 'native' else 'test/SharpLink.Benchmarks/bin/Release/net10.0/SharpLink.Benchmarks.dll'
binaries = {label: root / relative for label, root in roots.items()}
identities = {label: json.loads((root / 'artifacts/first-receive-integration/provenance.json').read_text())['disposable_tree'] for label, root in roots.items()}
plan = [(r, label) for r in range(4) for label in (('control', 'candidate') if r % 2 == 0 else ('candidate', 'control'))]
proof = dict(runtime=a.runtime, identities=identities, plan=plan, affinity='0-3',
    binary_sha256={label: hashlib.sha256(binary.read_bytes()).hexdigest() for label, binary in binaries.items()},
    boundary='Unchanged full old API matrix, four balanced independent process repeats; internal key/resolved comparisons are not dev throughput gains.')
(out / 'provenance.json').write_text(json.dumps(proof, indent=2) + '\n')
exits = []
for repeat, label in plan:
    name = f'{a.runtime}-{label}-r{repeat}'
    env = dict(os.environ, DOTNET_PROCESSOR_COUNT='4', DOTNET_ReadyToRun='0',
               DOTNET_TieredCompilation='1', DOTNET_TieredPGO='0' if a.runtime == 'pgo0' else '1',
               DOTNET_TC_QuickJitForLoops='1')
    for key in ('DOTNET_JitDisasm', 'DOTNET_JitStdOutFile'):
        env.pop(key, None)
    command = ['taskset', '-c', '0-3']
    command += [str(binaries[label])] if a.runtime == 'native' else ['dotnet', str(binaries[label]), '--resolved-flow-state-evidence']
    command += ['--repetitions', '3', '--contention-items', '20000', '--output', str(out / f'{name}.json')]
    print('REDESIGN_MICRO', name, flush=True)
    with (out / f'{name}.log').open('w') as log:
        try:
            code = subprocess.run(command, env=env, cwd=roots[label], stdout=log,
                                  stderr=subprocess.STDOUT, timeout=300).returncode
        except subprocess.TimeoutExpired:
            code = 124
    exits.append(dict(name=name, exit_code=code, command=command))
    (out / 'exits.json').write_text(json.dumps(exits, indent=2) + '\n')
assert len(exits) == 8 and all(r['exit_code'] == 0 for r in exits), exits
all_rows = {}
expected = None
for repeat, label in plan:
    rows = json.loads((out / f'{a.runtime}-{label}-r{repeat}.json').read_text())
    assert len(rows) == 232
    keys = {(r['Scenario'], r['Mode'], r['ActiveStreams'], r['ItemsPerStream']) for r in rows}
    assert len(keys) == 232
    if expected is None:
        expected = keys
    assert keys == expected
    for row in rows:
        assert row['Repetitions'] == 3
        assert row['Checksum'] == row['ActiveStreams'] * row['ItemsPerStream'] * 3 * 16
        assert math.isfinite(row['NanosecondsPerItem']) and row['NanosecondsPerItem'] > 0
        assert row['AllocatedBytesPerItem'] >= 0 and row['LockContentionsPerItem'] >= 0
    all_rows[f'{label}-r{repeat}'] = rows
comparisons = []
for label in roots:
    for scenario in sorted({key[0] for key in expected}):
        per_repeat = []
        for repeat in range(4):
            rows = [r for r in all_rows[f'{label}-r{repeat}'] if r['Scenario'] == scenario]
            groups = {}
            for row in rows:
                groups.setdefault((row['ActiveStreams'], row['ItemsPerStream']), {})[row['Mode']] = row
            gains = [(1 - modes['resolved']['NanosecondsPerItem'] / modes['key']['NanosecondsPerItem']) * 100 for modes in groups.values()]
            per_repeat.append(statistics.median(gains))
        comparisons.append(dict(label=label, scenario=scenario, median_cell_gains_by_repeat=per_repeat))
(out / 'comparison.json').write_text(json.dumps(comparisons, indent=2) + '\n')
print(json.dumps(comparisons, indent=2))
print('8/8 full micro populations validated; old short and all negative cells retained.')
