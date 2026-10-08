#!/usr/bin/env python3
"""Portable validator controls; no measurements, network, or product mutation."""
import json
import pathlib
import subprocess
import sys
import tempfile

kit = pathlib.Path(__file__).resolve().parent
count = 0
with tempfile.TemporaryDirectory(prefix='gates-validation-') as temporary:
    root = pathlib.Path(temporary)
    for name in ('micro', 'aot', 'e2e', 'c8'):
        (root / name).mkdir()
    rows = []
    sequential = ('send-no-wait', 'send-periodic-window-update', 'send-starved-control',
                  'receive-accept', 'receive-consume', 'receive-pair', 'short-stream-control')
    for scenario in sequential + ('send-contention', 'receive-pair-contention'):
        contention = scenario.endswith('-contention')
        for mode in ('key', 'resolved'):
            for active in ((32, 128) if contention else (1, 8, 32, 128)):
                for items in ((20000,) if contention else (1, 64, 1000, 100000)):
                    reps = 6 if contention else 3
                    rows.append(dict(Scenario=scenario, Mode=mode, ActiveStreams=active, ItemsPerStream=items,
                        Repetitions=reps, Checksum=active * items * reps * 16,
                        NanosecondsPerItem=1, AllocatedBytesPerItem=0, LockContentionsPerItem=0))
    assert len(rows) == 232
    micro_files = [root / 'micro' / f'pgo{pgo}-r{repeat}.json' for pgo in (0, 1) for repeat in (1, 2, 3)] + [root / 'aot/micro.json']
    for path in micro_files:
        path.write_text(json.dumps(rows))
    def check(script, succeeds):
        global count
        result = subprocess.run([sys.executable, str(kit / script), str(root)], capture_output=True, text=True)
        assert (result.returncode == 0) == succeeds, (script, succeeds, result.stdout, result.stderr)
        count += 1
    check('validate-micro.py', True)
    for mutate in ('contention-repetitions', 'checksum', 'missing', 'duplicate', 'nan'):
        changed = json.loads(json.dumps(rows))
        if mutate == 'contention-repetitions':
            changed[-1]['Repetitions'] = 3
        elif mutate == 'checksum':
            changed[0]['Checksum'] += 1
        elif mutate == 'missing':
            changed.pop()
        elif mutate == 'duplicate':
            changed[-1] = changed[-2]
        else:
            changed[0]['NanosecondsPerItem'] = float('nan')
        micro_files[0].write_text(json.dumps(changed))
        check('validate-micro.py', False)
        micro_files[0].write_text(json.dumps(rows))
    scenarios = ('Server1x16', 'Server100x16', 'Server100x4096', 'Client100x16', 'Client100x4096', 'Duplex100x16', 'Duplex100x4096')
    identities = {'baseline': '0fe26024b114bb6e78411a9b86276086c045d03d', 'candidate': '522585079b97c99f7986eb50f3ed6c69d4086bfb'}
    for repeat in (1, 2, 3):
        for transport in ('tcp', 'sharedmemory'):
            for label, sha in identities.items():
                for scenario in scenarios:
                    path = root / 'e2e' / f'{transport}-{label}-r{repeat}-{scenario}.json'
                    path.write_text(json.dumps(dict(commit=sha, transport=transport, scenario=scenario,
                        validationFailures=0, operations=100, warmupOperations=30, requestedMeasurementSeconds=5, hitOperationLimit=False)))
                for size in (1, 10000):
                    path = root / 'c8' / f'{transport}-size{size}-{label}-r{repeat}.json'
                    path.write_text(json.dumps(dict(SourceCommit=sha, Configuration=dict(StreamSize=size,
                        ConcurrencyConfig=[8], StreamReceiveWindowBytes=8192, ConnectionReceiveWindowBytes=65536,
                        Transport={'tcp': 0, 'sharedmemory': 4}[transport], DurationSeconds=2, WarmupSeconds=1, Operation='all'),
                        Results=[dict(Operation=operation, RecorderMode='off') for operation in ('unary', 'c2s', 's2c', 'duplex')])))
    check('validate-populations.py', True)
    path = root / 'c8/tcp-size1-baseline-r1.json'
    saved = path.read_text()
    for mutate in ('identity', 'window', 'duration', 'recording'):
        changed = json.loads(saved)
        if mutate == 'identity':
            changed['SourceCommit'] = 'wrong'
        elif mutate == 'window':
            changed['Configuration']['StreamReceiveWindowBytes'] = 16384
        elif mutate == 'duration':
            changed['Configuration']['DurationSeconds'] = 1
        else:
            changed['Results'][1]['RecorderMode'] = 'on'
        path.write_text(json.dumps(changed))
        check('validate-populations.py', False)
        path.write_text(saved)
    path.unlink()
    check('validate-populations.py', False)
print(f'{count}/{count} validator controls passed, including rejection of the historical 3-vs-6 repetition error.')
