#!/usr/bin/env python3
"""Validate original 232-row populations; sequential reps=3, AB+BA contention=6."""
import hashlib
import json
import math
import pathlib
import sys

root = pathlib.Path(sys.argv[1]).resolve()
sequential = ('send-no-wait', 'send-periodic-window-update', 'send-starved-control',
              'receive-accept', 'receive-consume', 'receive-pair', 'short-stream-control')
contention = ('send-contention', 'receive-pair-contention')
expected = {(s, m, streams, items) for s in sequential for m in ('key', 'resolved')
            for streams in (1, 8, 32, 128) for items in (1, 64, 1000, 100000)}
expected |= {(s, m, streams, 20000) for s in contention for m in ('key', 'resolved') for streams in (32, 128)}
assert len(expected) == 232
files = [root / 'micro' / f'pgo{pgo}-r{rep}.json' for pgo in (0, 1) for rep in (1, 2, 3)]
files.append(root / 'aot/micro.json')
assert set((root / 'micro').glob('*.json')) == set(files[:-1]), 'Unexpected micro population'
hashes = {}
for path in files:
    rows = json.loads(path.read_text())
    assert len(rows) == len(expected), path
    keys = [(r['Scenario'], r['Mode'], r['ActiveStreams'], r['ItemsPerStream']) for r in rows]
    assert len(set(keys)) == len(keys) and set(keys) == expected, path
    for row in rows:
        reps = 6 if row['Scenario'] in contention else 3
        assert row['Repetitions'] == reps, (path, row)
        assert row['Checksum'] == row['ActiveStreams'] * row['ItemsPerStream'] * reps * 16, (path, row)
        for name in ('NanosecondsPerItem', 'AllocatedBytesPerItem', 'LockContentionsPerItem'):
            assert math.isfinite(row[name]) and row[name] >= 0, (path, row)
        assert row['NanosecondsPerItem'] > 0
    hashes[str(path.relative_to(root))] = hashlib.sha256(path.read_bytes()).hexdigest()
proof = dict(processes=7, rows_each=232, sequential_repetitions=3, contention_repetitions=6,
    raw_sha256=hashes, boundary='Original candidate-internal keyed/resolved micro. No cells omitted; no dev-to-H throughput claim.')
(root / 'micro-population-validation.json').write_text(json.dumps(proof, indent=2) + '\n')
print(json.dumps(proof, indent=2))
