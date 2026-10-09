#!/usr/bin/env python3
"""Validate all focused RPC observations; retain every point and paired contrast."""
from collections import defaultdict
import hashlib
import itertools
import json
import math
from pathlib import Path
import statistics as stats
import sys

ARMS = ('baseline', 'safe', 'normalized')
SCENARIOS = ('tcp-add-c1', 'tcp-add-c32', 'shm-add-c1')


def describe(values):
    return {'points': values, 'mean': stats.mean(values), 'median': stats.median(values),
            'sampleStdev': stats.stdev(values), 'min': min(values), 'max': max(values)}


def require(condition, description):
    if not condition:
        raise ValueError(description)


def numeric(value, name, positive=False):
    require(type(value) in (int, float) and math.isfinite(value) and
            (value > 0 if positive else value >= 0), f'Invalid finite nonnegative {name}')


def validate_scenario(doc, scenario, manifest):
    result, configuration = doc['Results'][0], doc['Configuration']
    concurrency = int(scenario.rsplit('-c', 1)[1])
    stream = scenario.startswith('tcp-duplex')
    operation = 'duplex' if stream else 'add'
    require(doc['Workload'] == ('SharpLink.StreamLoadTest' if stream else 'SharpLink.LoadTest'),
            'Workload identity mismatch')
    expected = {
        'Mode': 0, 'Transport': 4 if scenario.startswith('shm-') else 0,
        'Operation': operation, 'ConcurrencyConfig': [concurrency],
        'DurationSeconds': manifest['durationSeconds'], 'WarmupSeconds': manifest['warmupSeconds'],
        'PerformanceProfile': 0, 'MinConnections': 1, 'MaxConnections': 1,
        'MaxSendQueueBytes': 67_108_864, 'RecordingMode': 1,
        'MaximumRecordedOperations': 30_000_000, 'DetailedSharedMemoryEvidence': False,
    }
    expected.update({'StreamSize': 256} if stream else {'MetricsPort': 0})
    for key, value in expected.items():
        require(configuration.get(key) == value, f'{scenario} config {key}: expected {value}')
    require(result['Operation'] == operation and result['Concurrency'] == concurrency and
            result['WorkerCount'] == concurrency, 'Result scenario mismatch')
    for field in ('Qps', 'MeasurementDurationSeconds', 'WarmupDurationSeconds', 'StopwatchFrequency'):
        numeric(result[field], field, positive=True)
    require(result['MeasurementDurationSeconds'] >= manifest['durationSeconds'] * .99,
            'Measurement ended before requested duration')
    require(result['WarmupDurationSeconds'] >= manifest['warmupSeconds'] * .99,
            'Warmup ended before requested duration')
    require(math.isclose(result['Qps'], result['Success'] / result['MeasurementDurationSeconds'],
                         rel_tol=1e-9), 'QPS does not equal success / measurement time')
    numeric(result['DrainDurationSeconds'], 'DrainDurationSeconds')
    require(result['DrainDurationSeconds'] <= configuration['DrainTimeoutSeconds'], 'Drain exceeded timeout')
    percentiles = [result[key] for key in ('P50Us', 'P95Us', 'P99Us', 'P999Us')]
    for value in percentiles:
        numeric(value, 'percentile')
    require(percentiles == sorted(percentiles), 'Unordered percentiles')
    for field in ('CpuMilliseconds', 'AllocatedBytes', 'Gen0Collections', 'Gen1Collections', 'Gen2Collections'):
        numeric(result['Evidence'][field], field)


def load_rows(root):
    provenance = json.loads((root / 'provenance.json').read_text())
    require(set(provenance['arms']) == set(ARMS), 'Unexpected exact-arm set')
    for arm in ARMS:
        bound = provenance['arms'][arm]
        require(bound['identity'] == bound['checkoutCommit'] == bound['readerOrigin'] ==
                provenance['manifest'][arm] and bound['synthetic'] is False,
                'An arm is not bound to its exact immutable revision')
    index = json.loads((root / 'execution-index.json').read_text())
    count = provenance['manifest']['rounds']
    planned = {(scenario, r, arm) for scenario in SCENARIOS
               for r in range(1, count + 1) for arm in ARMS}
    actual = [(entry['scenario'], entry['round'], entry['arm']) for entry in index]
    require(len(actual) == len(planned) and set(actual) == planned, 'Incomplete/duplicate observations')
    require(len({entry['file'] for entry in index}) == len(index), 'Duplicate raw filenames')
    rows = defaultdict(dict)
    orders = list(itertools.permutations(ARMS))
    environments = set()
    for entry in index:
        scenario, r, arm = entry['scenario'], entry['round'], entry['arm']
        require(type(entry['position']) is int and 1 <= entry['position'] <= 3, 'Invalid arm position')
        require(orders[(r - 1) % 6][entry['position'] - 1] == arm, 'Unbalanced execution order')
        source = root / entry['file']
        require(hashlib.sha256(source.read_bytes()).hexdigest() == entry['sha256'], 'Raw hash mismatch')
        doc = json.loads(source.read_text())
        identity = provenance['arms'][arm]['identity']
        require(doc['SourceCommit'] == identity, 'Load-test arm mismatch')
        require(len(doc['Results']) == 1, 'Expected exactly one measured stage')
        result = doc['Results'][0]
        require(doc['SchemaVersion'] == 1 and result['RecorderVersion'] == 'worker-local-shared-capacity-v4',
                'Unexpected report schema/recorder contract')
        validate_scenario(doc, scenario, provenance['manifest'])
        environments.add(tuple(doc[key] for key in (
            'SchemaVersion', 'OperatingSystem', 'OsArchitecture', 'ProcessArchitecture',
            'Runtime', 'ProcessorCount', 'ServerGc', 'GcLatencyMode')) + (result['RecorderVersion'],))
        require(result['FormalComparable'] and result['RecorderMode'] == 'formal', 'Nonformal evidence')
        require('Failure' in result, 'Missing mandatory Failure count')
        for field in ('Failure', 'ValidationFailure', 'Cancelled', 'TailObserverFailure'):
            require(result.get(field, 0) == 0, f'{source.name}: {field} is nonzero')
        success = result['Success']
        require(success > 0 and success == result['OperationsStartedDuringMeasurement'] ==
                result['OperationsCompleted'] == result['SampleCount'], 'Incomplete counted operations')
        require(result['MaximumSampleCapacity'] >= success, 'Sample capacity overflow')
        for field in ('P50Us', 'P99Us', 'Qps', 'MeasurementDurationSeconds'):
            require(isinstance(result.get(field), (float, int)) and
                    math.isfinite(result[field]) and result[field] > 0, f'Invalid {field}')
        evidence = result['Evidence']
        rows[(scenario, arm)][r] = {
            'qps': result['Qps'], 'cpuMicrosecondsPerRpc': evidence['CpuMilliseconds'] * 1000 / success,
            'allocatedBytesPerRpc': evidence['AllocatedBytes'] / success,
            'p50Us': result['P50Us'], 'p99Us': result['P99Us'],
            'gen0': evidence['Gen0Collections'], 'gen1': evidence['Gen1Collections'],
            'gen2': evidence['Gen2Collections'],
            'gen0PerMillionRpc': evidence['Gen0Collections'] * 1e6 / success,
            'success': success, 'measurementSeconds': result['MeasurementDurationSeconds'],
            'drainSeconds': result['DrainDurationSeconds'],
        }
    require(len(environments) == 1, 'Different load-test environment or recorder versions')
    return provenance, rows


def summarize(root):
    provenance, rows = load_rows(root)
    count = provenance['manifest']['rounds']
    descriptions = {}
    comparisons = {}
    for (scenario, arm), by_round in sorted(rows.items()):
        require(set(by_round) == set(range(1, count + 1)), 'Missing metric observations')
        descriptions[f'{scenario}/{arm}'] = {
            metric: describe([by_round[r][metric] for r in range(1, count + 1)])
            for metric in by_round[1]}
    for scenario in sorted({scenario for scenario, _ in rows}):
        for reference, normalized in (('baseline', 'normalized'),
                                           ('safe', 'normalized'),
                                           ('baseline', 'safe')):
            left, right = rows[(scenario, reference)], rows[(scenario, normalized)]
            metrics = {}
            for metric in left[1]:
                absolute = [right[r][metric] - left[r][metric] for r in range(1, count + 1)]
                relative = [(right[r][metric] / left[r][metric] - 1) * 100
                            for r in range(1, count + 1) if left[r][metric] != 0]
                metrics[metric] = {'absoluteDelta': describe(absolute),
                                   'percentDelta': describe(relative) if len(relative) == count and not scenario.endswith('-incremental') else None}
            comparisons[f'{scenario}/{normalized}-versus-{reference}'] = metrics
    result = {'provenance': provenance, 'observations': descriptions, 'pairedComparisons': comparisons,
              'interpretation': [
                  'This focused series tests TCP Add c1 as primary, TCP c32 as batching control and SHM c1 as a bypass/noise control. It is not a full production or transport regression verdict.',
                  'Six balanced orders retain all points; the unchanged two-second warmup and six-second measured stages remain short/noisy. Descriptive means/medians/stdev/ranges and paired differences are not confidence intervals or proof of equivalence.',
                  'All arms are exact immutable revisions: frozen dev072, safe ac903, and normalized staging candidate. No production source is transplanted; no prior run supplies timing points.',
                  'The zero-failure gate applies to measured stages only; existing load tools discard warmup outcomes.',
                  'CPU/allocation/GC cover the combined local client/server/harness and completed measured operations, including bounded drain. GC per million RPC normalizes completed work.',
                  'Raw artifacts contain stage summaries, not individual latency samples. P99 is a per-stage quantile; summary medians do not pool latency populations.',
                  'SharedMemory bypasses ReadOwnershipPipeReader. No source-level queue-hop or read-suspension attribution is inferred from uncollected counters.',
                  'Micro/dispatch/backlog cases are not executed in this focused batch. Ordinary-await allocation scope and special-context costs retain their separate prior evidence; this run does not refresh all those paths.',
                  'A focused performance result does not replace independently reviewed source or exact-candidate Fast/allocation validation, or authorize PR promotion/merge.',
                  'No failed or slow observation is selectively retried or discarded. Hosted runs and historical M4 measurements cannot be compared by absolute timing.'
              ]}
    (root / 'comparison.json').write_text(json.dumps(result, indent=2) + '\n')
    lines = ['# Focused read-ownership RPC comparison', '',
             f"Baseline: `{provenance['manifest']['baseline']}`", '',
             f"Safe reference: `{provenance['manifest']['safe']}`", '',
             f"Normalized candidate: `{provenance['manifest']['normalized']}`", '',
             'Six balanced orders on one GitHub runner; all observations retained. Values below are medians.', '',
             '| Scenario | Arm | QPS | CPU us/RPC | B/RPC | p99 us | Gen0 |',
             '|---|---|---:|---:|---:|---:|---:|']
    for scenario in SCENARIOS:
        for arm in ARMS:
            cells = descriptions[f'{scenario}/{arm}']
            values = [f"{cells[metric]['median']:.3f}" for metric in
                      ('qps', 'cpuMicrosecondsPerRpc', 'allocatedBytesPerRpc', 'p99Us', 'gen0')]
            lines.append('| ' + ' | '.join([scenario, arm, *values]) + ' |')
    lines += ['', '## Paired percent deltas (mean ± sample stdev)', '',
              '| Scenario | Contrast | QPS % | CPU/RPC % | B/RPC % | p99 % |',
              '|---|---|---:|---:|---:|---:|']
    for scenario in SCENARIOS:
        for reference in ('baseline', 'safe'):
            metrics = comparisons[f'{scenario}/normalized-versus-{reference}']
            values = []
            for metric in ('qps', 'cpuMicrosecondsPerRpc', 'allocatedBytesPerRpc', 'p99Us'):
                value = metrics[metric]['percentDelta']
                values.append('n/a' if value is None else f"{value['mean']:+.2f} ± {value['sampleStdev']:.2f}")
            lines.append('| ' + ' | '.join([scenario, f'normalized vs {reference}', *values]) + ' |')
    lines += ['', *['- ' + text for text in result['interpretation']]]
    (root / 'comparison.md').write_text('\n'.join(lines) + '\n')
    print('\n'.join(lines))



if __name__ == '__main__':
    summarize(Path(sys.argv[1]))
