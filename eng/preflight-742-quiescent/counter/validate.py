#!/usr/bin/env python3
"""Require every actual RMW and request-monitor count; retain attempts/successes."""
import argparse
import csv
import json
import pathlib

parser = argparse.ArgumentParser()
parser.add_argument('--arm', choices=('F', 'G'), required=True)
parser.add_argument('--output', type=pathlib.Path, required=True)
parser.add_argument('counts', type=pathlib.Path, nargs=2)
args = parser.parse_args()
expected = json.loads((pathlib.Path(__file__).resolve().parent / 'expected-matrix.json').read_text())['F' if args.arm == 'F' else 'G2']
rows = []
for path in args.counts:
    for row in csv.DictReader(path.open(), delimiter='|'):
        for key in ('attempts', 'cas_successes'):
            row[key] = int(row[key])
            assert row[key] >= 0
        assert row['cas_successes'] <= row['attempts']
        rows.append(row)
assert {row['scenario'] for row in rows} == set(expected)
assert len({tuple(row[key] for key in ('scenario', 'phase', 'owner', 'operation', 'site')) for row in rows}) == len(rows)
summary = {}
for scenario, target in expected.items():
    selected = [row for row in rows if row['scenario'] == scenario and row['phase'] == 'complete-stream']
    entries = [row for row in selected if row['owner'] == 'entry' and row['operation'] not in ('lock', 'callback')]
    assert entries
    summary[scenario] = dict(entry_rmw=sum(row['attempts'] for row in entries),
        request_monitors=sum(row['attempts'] for row in selected if row['owner'] == 'request' and row['operation'] == 'lock'),
        entry_cas_attempts=sum(row['attempts'] for row in entries if row['operation'] == 'CompareExchange'),
        entry_cas_successes=sum(row['cas_successes'] for row in entries if row['operation'] == 'CompareExchange'),
        expected=target)
proof = dict(arm=args.arm, scenarios=summary, raw_rows=rows,
    boundary='Instrumented mechanism counts only. Original atomic attempts/results retained; no timing evidence.')
args.output.write_text(json.dumps(proof, indent=2) + '\n')
for scenario, row in summary.items():
    assert row['entry_rmw'] == row['expected']['entry_rmw'], (scenario, row)
    assert row['request_monitors'] == row['expected']['request_monitors'], (scenario, row)
print(json.dumps(summary, indent=2))
