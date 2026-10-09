#!/usr/bin/env python3
"""Validate every planned observation and report all points, spread, and paired deltas."""
from collections import defaultdict
import hashlib
import itertools
import json
import math
from pathlib import Path
import statistics as stats
import sys

ARMS = ('baseline', 'fixed', 'optimized')
SCENARIOS = ('micro', 'tcp-add-c1', 'tcp-add-c32', 'tcp-add-c128',
             'shm-add-c1', 'shm-add-c32', 'tcp-duplex256-c1')


def describe(values):
    return {'points': values, 'mean': stats.mean(values), 'median': stats.median(values),
            'sampleStdev': stats.stdev(values), 'min': min(values), 'max': max(values)}


def require(condition, description):
    if not condition:
        raise ValueError(description)


def numeric(value, name, positive=False):
    require(type(value) in (int, float) and math.isfinite(value) and
            (value > 0 if positive else value >= 0), f'Invalid finite nonnegative {name}')


def validate_micro(doc, manifest):
    expected = {(mode, wrapped) for mode in ('sync', 'suspend', 'suspend-consumer-await')
                for wrapped in (False, True)}
    actual = [(cell['mode'], cell['wrapped']) for cell in doc['rows']]
    require(len(actual) == len(expected) and set(actual) == expected, 'Missing/duplicate micro cells')
    for cell in doc['rows']:
        require(cell['iterations'] == manifest['microIterations'], 'Micro iteration mismatch')
        for key in ('nanosecondsPerRead', 'allocatedBytesPerRead', 'cpuNanosecondsPerRead',
                    'gen0', 'gen1', 'gen2'):
            numeric(cell[key], key)
    construction = doc['construction']
    require(len(construction) == 2 and {cell['wrapped'] for cell in construction} == {False, True},
            'Missing/duplicate construction cells')
    for cell in construction:
        require(cell['count'] == 10_000, 'Construction count mismatch')
        numeric(cell['allocatedBytesPerReader'], 'allocatedBytesPerReader')


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
    micro_environments = set()
    for entry in index:
        scenario, r, arm = entry['scenario'], entry['round'], entry['arm']
        require(type(entry['position']) is int and 1 <= entry['position'] <= 3, 'Invalid arm position')
        require(orders[(r - 1) % 6][entry['position'] - 1] == arm, 'Unbalanced execution order')
        source = root / entry['file']
        require(hashlib.sha256(source.read_bytes()).hexdigest() == entry['sha256'], 'Raw hash mismatch')
        doc = json.loads(source.read_text())
        identity = provenance['arms'][arm]['identity']
        if scenario == 'micro':
            require(doc['sourceCommit'] == identity and doc['arm'] == arm, 'Micro arm mismatch')
            require(doc['tieredCompilation'] == '0', 'Micro tiered compilation was enabled')
            validate_micro(doc, provenance['manifest'])
            micro_environments.add(tuple(doc[key] for key in ('runtime', 'architecture', 'processorCount', 'serverGc')))
            for cell in doc['rows']:
                label = f"micro-{cell['mode']}-{'wrapped' if cell['wrapped'] else 'raw'}"
                require(cell['iterations'] == provenance['manifest']['microIterations'], 'Micro count mismatch')
                rows[(label, arm)][r] = {key: cell[key] for key in (
                    'nanosecondsPerRead', 'allocatedBytesPerRead', 'cpuNanosecondsPerRead', 'gen0', 'gen1', 'gen2')}
            require(len(doc['construction']) == 2, 'Missing construction cells')
            for cell in doc['construction']:
                label = 'construction-' + ('wrapped' if cell['wrapped'] else 'raw')
                rows[(label, arm)][r] = {'allocatedBytesPerReader': cell['allocatedBytesPerReader']}
            continue
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
    require(len(micro_environments) == 1, 'Different micro environments across arms')
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
        for reference, optimized in (('baseline', 'optimized'),
                                     ('fixed', 'optimized'),
                                     ('baseline', 'fixed')):
            left, right = rows[(scenario, reference)], rows[(scenario, optimized)]
            metrics = {}
            for metric in left[1]:
                absolute = [right[r][metric] - left[r][metric] for r in range(1, count + 1)]
                relative = [(right[r][metric] / left[r][metric] - 1) * 100
                            for r in range(1, count + 1) if left[r][metric] != 0]
                metrics[metric] = {'absoluteDelta': describe(absolute),
                                   'percentDelta': describe(relative) if len(relative) == count else None}
            comparisons[f'{scenario}/{optimized}-versus-{reference}'] = metrics
    result = {'provenance': provenance, 'observations': descriptions, 'pairedComparisons': comparisons,
              'interpretation': [
                  'The zero-failure gate applies to measured stages; the reused tools do not persist discarded warmup failure counts.',
                  'All six balanced rounds are included; mean/median/stdev/min/max and paired deltas are descriptive, not a merge or statistical significance verdict.',
                  'All three arms are exact immutable revisions: frozen dev baseline, correctness-fixed d271c3a8, and optimized staging candidate. No production source is transplanted.',
                  'Load-test CPU/allocation/GC cover the combined local client/server/harness and completed measured operations, including bounded drain.',
                  'GC counts are per equal-duration stage; GC per million RPC normalizes differing completed work.',
                  'Raw artifacts contain each stage summary, not every individual latency sample. P99 is the per-stage per-RPC quantile; summary medians do not combine raw latency populations.',
                  'SharedMemory bypasses ReadOwnershipPipeReader and is a negative control for noise.',
                  'Micro time measures fake-reader overhead, not full RPC latency or throughput. Construction bytes and steady-state allocation are separate.',
                  'Steady-state allocation results cannot establish buffer/exception lifetime correctness. Use retention/context regression tests separately.',
                  'Hosted runner results cannot be directly compared with historical Apple M4 measurements. No failed/slow samples are selectively retried or discarded.'
              ]}
    (root / 'comparison.json').write_text(json.dumps(result, indent=2) + '\n')
    lines = ['# Read-ownership same-run comparison', '',
             f"Baseline: `{provenance['manifest']['baseline']}`", '',
             f"Optimized: `{provenance['manifest']['optimized']}`", '',
             f"Fixed reference: `{provenance['manifest']['fixed']}` (exact commit)", '',
             'Six balanced orders on one GitHub runner; all observations retained. Values below are medians.', '',
             '| Scenario | Arm | QPS | CPU us/RPC | B/RPC | p99 us | Gen0 |',
             '|---|---|---:|---:|---:|---:|---:|']
    for scenario in SCENARIOS[1:]:
        for arm in ARMS:
            cells = descriptions[f'{scenario}/{arm}']
            values = [f"{cells[metric]['median']:.3f}" for metric in
                      ('qps', 'cpuMicrosecondsPerRpc', 'allocatedBytesPerRpc', 'p99Us', 'gen0')]
            lines.append('| ' + ' | '.join([scenario, arm, *values]) + ' |')
    lines += ['', '## Paired percent deltas (mean ± sample stdev)', '',
              '| Scenario | Contrast | QPS % | CPU/RPC % | B/RPC % | p99 % |',
              '|---|---|---:|---:|---:|---:|']
    for scenario in SCENARIOS[1:]:
        for reference in ('baseline', 'fixed'):
            metrics = comparisons[f'{scenario}/optimized-versus-{reference}']
            values = []
            for metric in ('qps', 'cpuMicrosecondsPerRpc', 'allocatedBytesPerRpc', 'p99Us'):
                value = metrics[metric]['percentDelta']
                values.append('n/a' if value is None else f"{value['mean']:+.2f} ± {value['sampleStdev']:.2f}")
            lines.append('| ' + ' | '.join([scenario, f'optimized vs {reference}', *values]) + ' |')
    lines += ['', '## Isolated reader cells (median)', '',
              '| Cell | Arm | ns/read | B/read |', '|---|---|---:|---:|']
    for scenario in sorted({s for s, _ in rows if s.startswith('micro-')}):
        for arm in ARMS:
            cell = descriptions[f'{scenario}/{arm}']
            lines.append(f"| {scenario} | {arm} | {cell['nanosecondsPerRead']['median']:.3f} | {cell['allocatedBytesPerRead']['median']:.3f} |")
    lines += ['', '## Fixed construction allocation', '']
    for arm in ARMS:
        wrapped = descriptions[f'construction-wrapped/{arm}']['allocatedBytesPerReader']['median']
        raw = descriptions[f'construction-raw/{arm}']['allocatedBytesPerReader']['median']
        lines.append(f'- {arm}: {wrapped:.1f} B constructed total, {raw:.1f} B inner, {wrapped - raw:.1f} B wrapper setup')
    lines += ['', *['- ' + text for text in result['interpretation']]]
    (root / 'comparison.md').write_text('\n'.join(lines) + '\n')
    print('\n'.join(lines))


if __name__ == '__main__':
    summarize(Path(sys.argv[1]))
