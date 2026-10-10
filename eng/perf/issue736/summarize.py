"""Fail closed on missing/malformed evidence; print every control, not only winners."""
import json
import math
import pathlib
import statistics
import sys

root = pathlib.Path(sys.argv[1])
mode = sys.argv[2]
assert mode in ('fullopt', 'pgo', 'native')
arms = ('baseline', 'candidate', 'lazy')
arities = (0, 1, 2, 4, 8, 127)
requests = (1, 8, 32, 128)
pairs = range(1, 7)
allocations = ('registrationBytesPerRequest', 'cleanupBytesPerRequest', 'individualTerminalLifecycleBytesPerRequest')
expected_files = {f'{arm}-{pair}.json' for arm in arms for pair in pairs}
assert {p.name for p in root.glob('*.json')} == expected_files, 'Incomplete or unexpected process files'
expected_rows = {('manager', n, a, r) for n in requests for a in arities for r in range(5)}
expected_rows |= {(f, a) for f in allocations for a in arities}
if mode != 'native':
    expected_rows |= {('known-route', a, r) for a in arities for r in range(7)}
docs = {}
for name in sorted(expected_files):
    data = json.loads((root/name).read_text())
    assert isinstance(data, list)
    rows = {}
    for row in data:
        assert isinstance(row, dict)
        assert row['arity'] in arities
        if 'ns' in row:
            assert math.isfinite(row['ns']) and row['ns'] > 0
            assert isinstance(row['bytes'], int) and row['bytes'] == 0, (name, 'per-frame allocation', row)
            if row.get('layer') == 'known-route':
                assert set(row) == {'layer', 'arity', 'repeat', 'ns', 'bytes'}
                key = ('known-route', row['arity'], row['repeat'])
            else:
                assert set(row) == {'requests', 'arity', 'repeat', 'ns', 'bytes'}
                key = ('manager', row['requests'], row['arity'], row['repeat'])
        else:
            fields = set(row) - {'arity'}
            assert len(fields) == 1 and next(iter(fields)) in allocations
            field = next(iter(fields))
            assert math.isfinite(row[field]) and row[field] >= 0
            key = (field, row['arity'])
        assert key not in rows, (name, 'duplicate row', key)
        rows[key] = row
    assert set(rows) == expected_rows, (name, 'unexpected row population', set(rows) ^ expected_rows)
    docs[name.removesuffix('.json')] = rows
print(f'Validated {len(docs)} process files and {len(expected_rows) * len(docs)} complete rows; all measured frame samples allocated 0 B.')
print('Active sets are serial traversal, not concurrent workers. NativeAOT omits the dynamic known-route attribution layer.')
for layer in (('known-route',) if mode != 'native' else ()) + requests:
    print('layer/active-requests', layer)
    for arity in arities:
        values = {}
        for arm in arms:
            values[arm] = []
            for pair in pairs:
                rows = docs[f'{arm}-{pair}']
                keys = [('known-route', arity, r) for r in range(7)] if layer == 'known-route' else [('manager', layer, arity, r) for r in range(5)]
                values[arm].append(statistics.median(rows[k]['ns'] for k in keys))
        medians = {arm: round(statistics.median(values[arm]), 4) for arm in arms}
        comparisons = {}
        for control in ('baseline', 'lazy'):
            delta = [c-b for c, b in zip(values['candidate'], values[control])]
            pct = [(c/b-1)*100 for c, b in zip(values['candidate'], values[control])]
            comparisons[control] = {'medianDeltaNs': statistics.median(delta), 'medianDeltaPercent': statistics.median(pct), 'allPairedDeltaPercent': pct}
        print(json.dumps({'arity': arity, 'medianNs': medians, 'candidateVersus': comparisons}))
print('Amortized managed bytes per request; registration includes shared-map growth. Not retained heap size.')
for arity in arities:
    result = {'arity': arity}
    for arm in arms:
        result[arm] = {field: [docs[f'{arm}-{pair}'][(field, arity)][field] for pair in pairs] for field in allocations}
    print(json.dumps(result))
