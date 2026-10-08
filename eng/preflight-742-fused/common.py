"""Untimed source/binary integrity and strict original-workload validation."""
import hashlib
import math
import pathlib
import subprocess

DEV = '0fe26024b114bb6e78411a9b86276086c045d03d'
N = '4c5943fec3a85089cefa8a1b6dc8c0f6502567ce'
OPERATIONS = ('unary', 'c2s', 's2c', 'duplex')


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def snapshot(root):
    files = subprocess.check_output(['git', 'ls-files', '--', 'src', 'test'], cwd=root, text=True).splitlines()
    sources = {name: sha(root / name) for name in files}
    binaries = {}
    for project in ('SharpLink.Benchmarks', 'SharpLink.StreamLoadTest'):
        directory = root / 'test' / project / 'bin/Release/net10.0'
        for path in sorted(directory.rglob('*')):
            if path.is_file():
                binaries[str(path.relative_to(root))] = sha(path)
    assert sources and binaries
    return dict(source_sha256=sources, binary_sha256=binaries)


def finite(value, positive=False):
    assert isinstance(value, (int, float)) and not isinstance(value, bool)
    assert math.isfinite(value) and (value > 0 if positive else value >= 0)


def c8(report, identity, transport, size):
    assert report['SourceCommit'] == identity
    cfg = report['Configuration']
    assert cfg['StreamSize'] == size and cfg['ConcurrencyConfig'] == [8]
    assert cfg['StreamReceiveWindowBytes'] == 8192 and cfg['ConnectionReceiveWindowBytes'] == 65536
    assert cfg['Transport'] == {'tcp': 0, 'sharedmemory': 4}[transport]
    assert cfg['DurationSeconds'] == 2 and cfg['WarmupSeconds'] == 1 and cfg['Operation'] == 'all'
    assert cfg['Mode'] == 0 and cfg['RecordingMode'] == 0
    assert cfg['MinConnections'] == cfg['MaxConnections'] == 1
    results = report['Results']
    assert len(results) == 4 and [r['Operation'] for r in results] == list(OPERATIONS)
    rows = []
    for row in results:
        assert row['Concurrency'] == row['WorkerCount'] == 8
        for key in ('Success', 'Failure', 'ValidationFailure', 'Cancelled', 'OperationsStartedDuringMeasurement', 'OperationsCompleted'):
            assert isinstance(row[key], int) and not isinstance(row[key], bool) and row[key] >= 0
        assert row['Failure'] == row['ValidationFailure'] == row['Cancelled'] == 0
        assert row['Success'] == row['OperationsStartedDuringMeasurement'] == row['OperationsCompleted'] > 0
        assert row['ErrorRatePercent'] == 0 and row['TopFailures'] == ''
        assert row['RecorderMode'] == 'off' and row['SampleCount'] == row['MaximumSampleCapacity'] == 0
        for key in ('Qps', 'WarmupDurationSeconds', 'MeasurementDurationSeconds'):
            finite(row[key], positive=True)
        finite(row['DrainDurationSeconds'])
        evidence = row['Evidence']
        finite(evidence['CpuMilliseconds'], positive=True)
        finite(evidence['AllocatedBytes'])
        rows.append(dict(shape=row['Operation'], operations=row['Success'], rate=row['Qps'],
            cpu_us_op=evidence['CpuMilliseconds'] * 1000 / row['Success'],
            bytes_op=evidence['AllocatedBytes'] / row['Success'],
            measurement_seconds=row['MeasurementDurationSeconds'], drain_seconds=row['DrainDurationSeconds']))
    return rows


def rpc(report, identity, transport, scenario):
    assert report['commit'] == identity and report['transport'] == transport and report['scenario'] == scenario
    assert report['validationFailures'] == 0 and report['operations'] > 0
    assert report['warmupOperations'] == 30 and report['requestedMeasurementSeconds'] == 5
    for key in ('throughputItemsPerSecond', 'cpuUsPerOperation', 'p50Us', 'p99Us'):
        finite(report[key], positive=True)
    finite(report['allocatedBytesPerOperation'])
    return [dict(shape=scenario, operations=report['operations'], rate=report['throughputItemsPerSecond'],
        cpu_us_op=report['cpuUsPerOperation'], bytes_op=report['allocatedBytesPerOperation'],
        p50_us=report['p50Us'], p99_us=report['p99Us'], hit_operation_limit=report['hitOperationLimit'])]
