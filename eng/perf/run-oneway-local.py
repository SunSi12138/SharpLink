#!/usr/bin/env python3
"""Paired, independent-process runner for the generated OneWay local evidence harness."""
import argparse
import hashlib
import itertools
import json
import os
from pathlib import Path
import random
import statistics
import sys
import subprocess

p = argparse.ArgumentParser(description=__doc__)
p.add_argument('--arm', action='append', required=True, help='Label=repository root; e.g. A=/tmp/a')
p.add_argument('--dotnet', default='dotnet')
p.add_argument('--output', type=Path, required=True)
p.add_argument('--rounds', type=int, default=12)
p.add_argument('--iterations', type=int, default=200000)
p.add_argument('--batch', type=int, default=256)
p.add_argument('--scenarios', nargs='+', default=['sync0', 'sync1', 'sync2', 'async0', 'async1', 'async2'])
p.add_argument('--tiered', choices=['0', '1'], default='0')
p.add_argument('--cpus', type=int, default=2)
p.add_argument('--seed', type=int, default=729)
a = p.parse_args()
if min(a.rounds, a.iterations, a.batch, a.cpus) <= 0:
    p.error('All counts must be positive')
arms = dict(v.split('=', 1) for v in a.arm)
if len(arms) != len(a.arm):
    p.error('Arm labels must be unique')
a.output.mkdir(parents=True, exist_ok=False)
env = dict(os.environ, DOTNET_TieredCompilation=a.tiered, DOTNET_TieredPGO=a.tiered,
           DOTNET_PROCESSOR_COUNT=str(a.cpus), DOTNET_ReadyToRun='0')
manifest = {'args': vars(a) | {'output': str(a.output)}, 'arms': {}}
for label, root in arms.items():
    dll = Path(root) / 'test/SharpLink.OneWayLocalEvidence/bin/Release/net10.0/SharpLink.Benchmarks.dll'
    if not dll.exists():
        p.error(f'Build {dll} first')
    manifest['arms'][label] = {'root': str(Path(root).resolve()),
        'head': subprocess.check_output(['git', '-C', root, 'rev-parse', 'HEAD'], text=True).strip(),
        'diff_sha256': hashlib.sha256(subprocess.check_output(['git', '-C', root, 'diff', 'HEAD'])).hexdigest(),
        'binaries': {name: hashlib.sha256((dll.parent / name).read_bytes()).hexdigest()
                     for name in ['SharpLink.Benchmarks.dll', 'SharpLink.Client.dll', 'SharpLink.Abstractions.dll', 'SharpLink.Runtime.dll']},
        'harness_sources': {str(f.relative_to(root)): hashlib.sha256(f.read_bytes()).hexdigest()
            for f in sorted((Path(root) / 'test/SharpLink.OneWayLocalEvidence').glob('*')) if f.is_file()}}
source_sets = [entry['harness_sources'] for entry in manifest['arms'].values()]
if any(source != source_sets[0] for source in source_sets):
    p.error('Harness sources differ across arms')
(a.output / 'manifest.json').write_text(json.dumps(manifest, indent=2) + '\n')
orders = list(itertools.permutations(arms))
rng = random.Random(a.seed)
rng.shuffle(orders)
schedule = []
for round_number in range(a.rounds):
    order = orders[round_number % len(orders)]
    scenarios = a.scenarios.copy()
    rng.shuffle(scenarios)
    for scenario in scenarios:
        for label in order:
            schedule.append({'round': round_number, 'scenario': scenario, 'arm': label, 'order': list(order)})
(a.output / 'schedule.json').write_text(json.dumps(schedule, indent=2) + '\n')
rows = []
failures = []
with (a.output / 'raw.jsonl').open('w') as raw:
    for sample_index, sample in enumerate(schedule):
        label, scenario, round_number = sample['arm'], sample['scenario'], sample['round']
        root = Path(arms[label])
        dll = root / 'test/SharpLink.OneWayLocalEvidence/bin/Release/net10.0/SharpLink.Benchmarks.dll'
        command = [a.dotnet, str(dll), scenario, str(a.iterations), str(a.batch)]
        prefix = a.output / f'{sample_index:04d}-{label}-{scenario}'
        status = dict(sample, command=command)
        try:
            completed = subprocess.run(command, env=env, text=True, capture_output=True, timeout=180)
            prefix.with_suffix('.stdout').write_text(completed.stdout)
            prefix.with_suffix('.stderr').write_text(completed.stderr)
            status['exit_code'] = completed.returncode
            if completed.returncode != 0:
                raise RuntimeError(f'Failed sample: {prefix}')
            row = json.loads(completed.stdout.strip().splitlines()[-1])
            row.update(sample, status='ok')
        except Exception as error:
            status['error'] = repr(error)
            if isinstance(error, subprocess.TimeoutExpired):
                prefix.with_suffix('.stdout').write_bytes(error.stdout or b'')
                prefix.with_suffix('.stderr').write_bytes(error.stderr or b'')
            row = dict(sample, status='failed', error=repr(error))
            raw.write(json.dumps(row) + '\n'); raw.flush()
            prefix.with_suffix('.status.json').write_text(json.dumps(status, indent=2) + '\n')
            failures.append(row)
            print(f'FAILED {round_number + 1}/{a.rounds} {label} {scenario}: {error}', flush=True)
            continue
        prefix.with_suffix('.status.json').write_text(json.dumps(status, indent=2) + '\n')
        rows.append(row)
        raw.write(json.dumps(row) + '\n'); raw.flush()
        print(f'{round_number + 1}/{a.rounds} {label} {scenario}: {row["ns_per_op"]:.1f} ns, {row["bytes_per_op"]:.2f} B', flush=True)

# Bootstrap within-process-launch pairs rather than treating loop iterations as samples.
summary = {}
for scenario in a.scenarios:
    local = [r for r in rows if r['scenario'] == scenario]
    result = {'arms': {}, 'paired_ratios': {}}
    if any(r['scenario'] == scenario for r in failures):
        summary[scenario] = {'status': 'incomplete', 'qualification': 'invalid', 'successful_samples': len(local),
                             'failures': [r for r in failures if r['scenario'] == scenario]}
        continue
    for label in arms:
        samples = [r for r in local if r['arm'] == label]
        result['arms'][label] = {k: statistics.median(r[k] for r in samples)
                                for k in ['ns_per_op', 'bytes_per_op']}
        if all(r.get('caller_thread_bytes_per_op') is not None for r in samples):
            result['arms'][label]['caller_thread_bytes_per_op'] = statistics.median(r['caller_thread_bytes_per_op'] for r in samples)
    for base, candidate in itertools.combinations(arms, 2):
        pairs = [(next(r for r in local if r['arm'] == base and r['round'] == n),
                  next(r for r in local if r['arm'] == candidate and r['round'] == n))
                 for n in range(a.rounds)]
        ratios = [c['ns_per_op'] / b['ns_per_op'] for b, c in pairs]
        boot = sorted(statistics.median(rng.choices(ratios, k=len(ratios))) for _ in range(10000))
        result['paired_ratios'][candidate + '/' + base] = {
            'median': statistics.median(ratios), 'bootstrap_median_ci95': [boot[249], boot[9749]],
            'all_ratios': ratios,
            'median_allocation_delta': statistics.median(c['bytes_per_op'] - b['bytes_per_op'] for b, c in pairs)}
    summary[scenario] = result
(a.output / 'summary.json').write_text(json.dumps(summary, indent=2) + '\n')
print(json.dumps(summary, indent=2))

if failures:
    sys.exit(1)
