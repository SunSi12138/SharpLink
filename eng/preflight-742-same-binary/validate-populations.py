#!/usr/bin/env python3
"""Supplement original summaries with exact D/H workload identity and raw hashes."""
import argparse
import hashlib
import json
import math
import pathlib
import sys

from common import c8

parser = argparse.ArgumentParser()
parser.add_argument('root', type=pathlib.Path)
parser.add_argument('--candidate-tree', required=True)
args = parser.parse_args()
root = args.root.resolve()
identities = {'baseline': '0fe26024b114bb6e78411a9b86276086c045d03d', 'candidate': args.candidate_tree}
scenarios = ('Server1x16', 'Server100x16', 'Server100x4096', 'Client100x16', 'Client100x4096', 'Duplex100x16', 'Duplex100x4096')
files, limited = [], []
for rep in (1, 2, 3):
    for transport in ('tcp', 'sharedmemory'):
        for label, sha in identities.items():
            for scenario in scenarios:
                path = root / 'e2e' / f'{transport}-{label}-r{rep}-{scenario}.json'
                row = json.loads(path.read_text())
                assert row['commit'] == sha and row['transport'] == transport and row['scenario'] == scenario, path
                assert row['validationFailures'] == 0 and row['operations'] > 0, path
                assert row['warmupOperations'] == 30 and row['requestedMeasurementSeconds'] == 5, path
                if row['hitOperationLimit']:
                    limited.append(str(path.relative_to(root)))
                files.append(path)
            for size in (1, 10000):
                path = root / 'c8' / f'{transport}-size{size}-{label}-r{rep}.json'
                row = json.loads(path.read_text())
                c8(row, sha, transport, size)
                assert row['SourceCommit'] == sha, path
                cfg = row['Configuration']
                assert cfg['StreamSize'] == size and cfg['ConcurrencyConfig'] == [8], path
                assert cfg['StreamReceiveWindowBytes'] == 8192 and cfg['ConnectionReceiveWindowBytes'] == 65536, path
                assert cfg['Transport'] == {'tcp': 0, 'sharedmemory': 4}[transport], path
                assert cfg['DurationSeconds'] == 2 and cfg['WarmupSeconds'] == 1 and cfg['Operation'] == 'all', path
                assert cfg['Mode'] == 0 and cfg['RecordingMode'] == 0, path
                assert cfg['MinConnections'] == cfg['MaxConnections'] == 1, path
                results = row['Results']
                expected_operations = {'unary', 'c2s', 's2c', 'duplex'}
                assert len(results) == 4 and {result['Operation'] for result in results} == expected_operations, path
                for result in results:
                    assert result['Concurrency'] == result['WorkerCount'] == 8, path
                    for key in ('Success', 'Failure', 'ValidationFailure', 'Cancelled',
                                'OperationsStartedDuringMeasurement', 'OperationsCompleted'):
                        assert isinstance(result[key], int) and not isinstance(result[key], bool) and result[key] >= 0, (path, key)
                    assert result['Failure'] == result['ValidationFailure'] == result['Cancelled'] == 0, path
                    assert result['Success'] == result['OperationsStartedDuringMeasurement'] == result['OperationsCompleted'] > 0, path
                    assert math.isfinite(result['Qps']) and result['Qps'] > 0, path
                    for key in ('WarmupDurationSeconds', 'MeasurementDurationSeconds'):
                        assert math.isfinite(result[key]) and result[key] > 0, (path, key)
                    assert math.isfinite(result['DrainDurationSeconds']) and result['DrainDurationSeconds'] >= 0, path
                    assert result['ErrorRatePercent'] == 0 and result['TopFailures'] == '', path
                    assert result['RecorderMode'] == 'off' and result['SampleCount'] == result['MaximumSampleCapacity'] == 0, path
                files.append(path)
assert len(files) == 108
assert set((root / 'e2e').glob('*.json')) | set((root / 'c8').glob('*.json')) == set(files)
proof = dict(identities=identities, e2e_reports=84, c8_reports=24, c8_rows=96, c8_operations=['unary', 'c2s', 's2c', 'duplex'], hit_operation_limit=limited,
    raw_sha256={str(path.relative_to(root)): hashlib.sha256(path.read_bytes()).hexdigest() for path in files},
    boundary='Original populations, order, durations, windows and summary checks remain unchanged. Reports reaching the original operation cap are disclosed, not silently dropped. No performance thresholds are altered or invented.')
(root / 'population-validation.json').write_text(json.dumps(proof, indent=2) + '\n')
print(json.dumps(proof, indent=2))
