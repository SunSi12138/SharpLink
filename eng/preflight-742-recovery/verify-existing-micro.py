#!/usr/bin/env python3
"""Revalidate all unchanged failed-job raw outputs, without rerunning measurements."""
import hashlib
import json
import math
import pathlib
import statistics
import sys

root, runtime = pathlib.Path(sys.argv[1]).resolve(), sys.argv[2]
assert runtime in ('pgo0', 'pgo1', 'native')
raw = root / 'raw'
proof = json.loads((raw / 'provenance.json').read_text())
assert proof['runtime'] == runtime
assert proof['identities'] == {'control': 'ea21543ca4d267938834ac24bead069aac2327c0', 'candidate': '4c5943fec3a85089cefa8a1b6dc8c0f6502567ce'}
plan = [(r, label) for r in range(4) for label in (('control', 'candidate') if r % 2 == 0 else ('candidate', 'control'))]
assert proof['plan'] == [list(p) for p in plan]
exits = json.loads((raw / 'exits.json').read_text())
assert len(exits) == 8 and all(row['exit_code'] == 0 for row in exits)
assert [row['name'] for row in exits] == [f'{runtime}-{label}-r{r}' for r, label in plan]
sequential = ['send-no-wait', 'send-periodic-window-update', 'send-starved-control', 'receive-accept', 'receive-consume', 'receive-pair', 'short-stream-control']
contention = ['send-contention', 'receive-pair-contention']
expected = {(s, m, streams, items) for s in sequential for m in ('key', 'resolved')
            for streams in (1, 8, 32, 128) for items in (1, 64, 1000, 100000)}
expected |= {(s, m, streams, 20000) for s in contention for m in ('key', 'resolved') for streams in (32, 128)}
assert len(expected) == 232
summaries, hashes = [], {}
for repeat, label in plan:
    path = raw / f'{runtime}-{label}-r{repeat}.json'
    data = json.loads(path.read_text())
    hashes[path.name] = hashlib.sha256(path.read_bytes()).hexdigest()
    assert len(data) == 232
    keys = [(r['Scenario'], r['Mode'], r['ActiveStreams'], r['ItemsPerStream']) for r in data]
    assert len(set(keys)) == len(keys) and set(keys) == expected
    for row in data:
        reps = 6 if row['Scenario'] in contention else 3
        assert row['Repetitions'] == reps
        assert row['Checksum'] == row['ActiveStreams'] * row['ItemsPerStream'] * reps * 16
        for key in ('NanosecondsPerItem', 'AllocatedBytesPerItem', 'LockContentionsPerItem'):
            assert math.isfinite(row[key]) and row[key] >= 0
        assert row['NanosecondsPerItem'] > 0
    for scenario in sequential + contention:
        groups = {}
        for row in data:
            if row['Scenario'] == scenario:
                groups.setdefault((row['ActiveStreams'], row['ItemsPerStream']), {})[row['Mode']] = row
        gains = [(1 - pair['resolved']['NanosecondsPerItem'] / pair['key']['NanosecondsPerItem']) * 100 for pair in groups.values()]
        summaries.append(dict(runtime=runtime, label=label, repeat=repeat, scenario=scenario, median_cell_gain=statistics.median(gains)))
result = dict(original_run=37757840731, original_head='3f4d8eb6326a4abb820e2f6841a29e4e7bbbb8db',
    original_job_result='failed at verifier only, after every process succeeded', runtime=runtime,
    processes=8, rows_each=232, sequential_repetitions=3, contention_repetitions=6,
    recovery='Read-only complete raw revalidation. No new launch, omitted cell, threshold or source change.',
    raw_sha256=hashes, summaries=summaries)
(root / 'revalidated.json').write_text(json.dumps(result, indent=2) + '\n')
print(json.dumps(result, indent=2))
