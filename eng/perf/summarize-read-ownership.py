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
MACRO_SCENARIOS = ('tcp-add-c1', 'tcp-add-c32', 'shm-add-c1')
SCENARIOS = ('dispatch', *MACRO_SCENARIOS)

DISPATCH_CASES = ('ordinary-await-before-completion', 'completion-before-ordinary-await',
                  'forced-async-inner-await', 'custom-synchronization-context-await',
                  'custom-task-scheduler-await', 'publisher-synchronization-context-await',
                  'publisher-task-await', 'explicit-late-unsafe-continuation',
                  'explicit-flow-execution-context', 'reentrant-ordinary-await')
ORDINARY_DISPATCH = {'ordinary-await-before-completion', 'completion-before-ordinary-await',
                     'forced-async-inner-await', 'publisher-task-await'}
DISPATCH_TOPOLOGY = {
    'ordinary-await-before-completion': ('async-method-per-read', 'inline-framework-source/default-context', False),
    'completion-before-ordinary-await': ('async-method-per-read', 'completed-framework-source/default-context', False),
    'forced-async-inner-await': ('async-method-per-read', 'async-framework-source/threadpool', False),
    'custom-synchronization-context-await': ('async-method-per-read', 'inline-framework-source/dedicated-synchronization-context', True),
    'custom-task-scheduler-await': ('async-method-per-read', 'inline-framework-source/dedicated-task-scheduler', True),
    'publisher-synchronization-context-await': ('async-method-per-read', 'inline-framework-source/nondefault-publisher-context', True),
    'publisher-task-await': ('async-method-per-read', 'inline-framework-source/task-publisher', True),
    'explicit-late-unsafe-continuation': ('cached-direct-unsafe-continuation', 'completed-framework-source/threadpool', False),
    'explicit-flow-execution-context': ('cached-direct-flowing-continuation', 'inline-framework-source/explicit-execution-context', True),
    'reentrant-ordinary-await': ('single-async-loop', 'inline-framework-source/reentrant-next-read', False),
    'late-continuation-backlog': ('cached-notification-plus-poll-consumer', 'completed-framework-source/one-blocked-threadpool-worker', False),
}
SC_CONTEXT_COUNTERS = ('noSynchronizationContextCount', 'capturedSynchronizationContextCount',
                       'publisherSynchronizationContextCount', 'otherSynchronizationContextCount')
TS_CONTEXT_COUNTERS = ('defaultTaskSchedulerCount', 'capturedTaskSchedulerCount', 'otherTaskSchedulerCount')



def describe(values):
    return {'points': values, 'mean': stats.mean(values), 'median': stats.median(values),
            'sampleStdev': stats.stdev(values), 'min': min(values), 'max': max(values)}


def require(condition, description):
    if not condition:
        raise ValueError(description)


def numeric(value, name, positive=False):
    require(type(value) in (int, float) and math.isfinite(value) and
            (value > 0 if positive else value >= 0), f'Invalid finite nonnegative {name}')

def validate_dispatch(doc, scenario, manifest):
    suite = 'backlog' if scenario == 'dispatch-backlog' else 'main'
    cases = ('late-continuation-backlog',) if suite == 'backlog' else DISPATCH_CASES
    expected = {(case, wrapped) for case in cases for wrapped in (False, True)}
    actual = [(cell['caseId'], cell['wrapped']) for cell in doc['rows']]
    require(doc['schemaVersion'] == 1 and doc['benchmark'] == 'read-ownership-dispatch'
            and doc['suite'] == suite, 'Unexpected dispatch schema/suite')
    require(doc['allocationScope'] == 'process-wide-precise', 'Worker-thread allocation excluded')
    count = manifest['dispatchIterations']
    require(doc['iterations'] == count and doc['warmupIterations'] == 5000, 'Dispatch count mismatch')
    require(len(actual) == len(expected) and set(actual) == expected, 'Missing/duplicate dispatch cells')
    for cell in doc['rows']:
        case = cell['caseId']
        require(type(cell['wrapped']) is bool and cell['iterations'] == count, 'Invalid dispatch arm/count')
        require(cell['category'] == ('ordinary' if case in ORDINARY_DISPATCH else 'special'),
                'Dispatch category mismatch')
        require((cell['consumer'], cell['backend'], cell['specialContext']) == DISPATCH_TOPOLOGY[case],
                'Unexpected dispatch topology')
        context = cell['contextObservation']
        for key in (*SC_CONTEXT_COUNTERS, *TS_CONTEXT_COUNTERS, 'taskIdPresentCount', 'threadPoolThreadCount'):
            require(type(context[key]) is int and 0 <= context[key] <= count, 'Invalid context counter')
        require(sum(context[key] for key in SC_CONTEXT_COUNTERS) == count and
                sum(context[key] for key in TS_CONTEXT_COUNTERS) == count, 'Incomplete consumer context observations')
        if case == 'custom-synchronization-context-await':
            require(context['capturedSynchronizationContextCount'] == count, 'Captured context not preserved')
        if case == 'custom-task-scheduler-await':
            require(context['capturedTaskSchedulerCount'] == count, 'Captured scheduler not preserved')
        require(cell['drained'] is True, 'Undrained dispatch work')
        require(cell['backlog'] is (suite == 'backlog') and
                cell['threadPoolBlockVerified'] is (suite == 'backlog'), 'Backlog not controlled')
        for key in ('reads', 'resultConsumptions', 'advances', 'consumerCompletions'):
            require(type(cell[key]) is int and cell[key] == count, 'Incomplete dispatch ' + key)
        for key in ('innerCallbacksRegistered', 'innerCallbacksCompleted',
                    'notificationCallbacksExpected', 'notificationCallbacksCompleted'):
            require(type(cell[key]) is int and cell[key] >= 0, 'Invalid dispatch callback counter')
        expected_inner = 0 if case == 'completion-before-ordinary-await' and cell['wrapped'] is False else count
        require(cell['innerCallbacksRegistered'] == cell['innerCallbacksCompleted'] == expected_inner,
                'Inner callback path skipped or not fully drained')
        notification_count = count if case in ('explicit-late-unsafe-continuation',
                                               'explicit-flow-execution-context',
                                               'late-continuation-backlog') else 0
        require(cell['notificationCallbacksExpected'] == cell['notificationCallbacksCompleted'] ==
                notification_count, 'Notification callbacks not fully drained')
        numeric(cell['quiescenceSeconds'], 'quiescenceSeconds')
        require(cell['quiescenceSeconds'] <= cell['elapsedSeconds'], 'Quiescence exceeds elapsed time')
        for key in ('elapsedSeconds', 'nanosecondsPerOperation', 'operationsPerSecond'):
            numeric(cell[key], key, positive=True)
        for key in ('allocatedBytes', 'allocatedBytesPerOperation', 'cpuNanosecondsPerOperation',
                    'gen0', 'gen1', 'gen2'):
            numeric(cell[key], key)
        require(type(cell['allocatedBytes']) is int, 'Nonintegral allocation total')
        require(math.isclose(cell['allocatedBytesPerOperation'], cell['allocatedBytes'] / count,
                             rel_tol=1e-9, abs_tol=1e-9), 'Dispatch allocation denominator mismatch')
        require(math.isclose(cell['nanosecondsPerOperation'], cell['elapsedSeconds'] * 1e9 / count,
                             rel_tol=1e-9), 'Dispatch latency denominator mismatch')
        require(math.isclose(cell['operationsPerSecond'], count / cell['elapsedSeconds'],
                             rel_tol=1e-9), 'Dispatch throughput denominator mismatch')
    construction = doc['construction']
    require(len(construction) == 2 and {cell['wrapped'] for cell in construction} == {False, True},
            'Missing/duplicate dispatch construction')
    for cell in construction:
        require(cell['count'] == 10_000, 'Dispatch construction count mismatch')
        numeric(cell['allocatedBytes'], 'construction total')
        numeric(cell['allocatedBytesPerReader'], 'construction per-reader')
        require(math.isclose(cell['allocatedBytesPerReader'], cell['allocatedBytes'] / cell['count'],
                             rel_tol=1e-9, abs_tol=1e-9), 'Construction denominator mismatch')



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
    dispatch_environments = set()
    for entry in index:
        scenario, r, arm = entry['scenario'], entry['round'], entry['arm']
        require(type(entry['position']) is int and 1 <= entry['position'] <= 3, 'Invalid arm position')
        require(orders[(r - 1) % 6][entry['position'] - 1] == arm, 'Unbalanced execution order')
        source = root / entry['file']
        require(hashlib.sha256(source.read_bytes()).hexdigest() == entry['sha256'], 'Raw hash mismatch')
        doc = json.loads(source.read_text())
        identity = provenance['arms'][arm]['identity']
        if scenario in ('dispatch', 'dispatch-backlog'):
            require(doc['sourceCommit'] == identity and doc['arm'] == arm, 'Dispatch arm mismatch')
            require(doc['tieredCompilation'] == '0', 'Dispatch tiered compilation was enabled')
            validate_dispatch(doc, scenario, provenance['manifest'])
            dispatch_environments.add(tuple(doc[key] for key in
                                            ('runtime', 'architecture', 'processorCount', 'serverGc')))
            cells = {}
            for cell in doc['rows']:
                label = f"dispatch-{cell['caseId']}-{'wrapped' if cell['wrapped'] else 'raw'}"
                rows[(label, arm)][r] = {key: cell[key] for key in (
                    'nanosecondsPerOperation', 'operationsPerSecond', 'allocatedBytesPerOperation',
                    'cpuNanosecondsPerOperation', 'quiescenceSeconds', 'gen0', 'gen1', 'gen2')}
                cells[(cell['caseId'], cell['wrapped'])] = cell
            for case in {cell['caseId'] for cell in doc['rows']}:
                # Do not clamp negative process-wide control differences or subtract timings.
                delta = cells[(case, True)]['allocatedBytesPerOperation'] - cells[(case, False)]['allocatedBytesPerOperation']
                rows[(f'dispatch-{case}-incremental', arm)][r] = {'allocatedBytesPerOperation': delta}
            for cell in doc['construction']:
                label = f"dispatch-construction-{doc['suite']}-{'wrapped' if cell['wrapped'] else 'raw'}"
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
    require(len(dispatch_environments) == 1, 'Different dispatch environments across arms')
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
                  'All arms are exact immutable revisions: current frozen dev130fae63, unchanged dev-synced a4f2d1b5, and the reviewed notification-cell candidate. No production source is transplanted; no prior run supplies timing points.',
                  'The zero-failure gate applies to measured stages only; existing load tools discard warmup outcomes.',
                  'CPU/allocation/GC cover the combined local client/server/harness and completed measured operations, including bounded drain. GC per million RPC normalizes completed work.',
                  'Raw artifacts contain stage summaries, not individual latency samples. P99 is a per-stage quantile; summary medians do not pool latency populations.',
                  'SharedMemory bypasses ReadOwnershipPipeReader. No source-level queue-hop or read-suspension attribution is inferred from uncollected counters.',
                  'The unchanged all-thread dispatch main suite runs separately with tiering disabled, including all ordinary/special cells and construction. Its wrapped-minus-raw allocation is incremental wrapper cost; total async-consumer allocation is not wrapper allocation. Original six-cell micro and backlog are not executed.',
                  'A focused performance result does not replace independently reviewed source or exact-candidate Fast/allocation validation, or authorize PR promotion/merge.',
                  'No failed or slow observation is selectively retried or discarded. Hosted runs and historical M4 measurements cannot be compared by absolute timing.'
              ]}
    (root / 'comparison.json').write_text(json.dumps(result, indent=2) + '\n')
    lines = ['# Focused read-ownership RPC comparison', '',
             f"Baseline: `{provenance['manifest']['baseline']}`", '',
             f"Unchanged dev-synced reference: `{provenance['manifest']['safe']}`", '',
             f"Notification-cell candidate: `{provenance['manifest']['normalized']}`", '',
             'Six balanced orders on one GitHub runner; all observations retained. Values below are medians.', '',
             '| Scenario | Arm | QPS | CPU us/RPC | B/RPC | p99 us | Gen0 |',
             '|---|---|---:|---:|---:|---:|---:|']
    for scenario in MACRO_SCENARIOS:
        for arm in ARMS:
            cells = descriptions[f'{scenario}/{arm}']
            values = [f"{cells[metric]['median']:.3f}" for metric in
                      ('qps', 'cpuMicrosecondsPerRpc', 'allocatedBytesPerRpc', 'p99Us', 'gen0')]
            lines.append('| ' + ' | '.join([scenario, arm, *values]) + ' |')
    lines += ['', '## Paired percent deltas (mean ± sample stdev)', '',
              '| Scenario | Contrast | QPS % | CPU/RPC % | B/RPC % | p99 % |',
              '|---|---|---:|---:|---:|---:|']
    for scenario in MACRO_SCENARIOS:
        for reference in ('baseline', 'safe'):
            metrics = comparisons[f'{scenario}/normalized-versus-{reference}']
            values = []
            for metric in ('qps', 'cpuMicrosecondsPerRpc', 'allocatedBytesPerRpc', 'p99Us'):
                value = metrics[metric]['percentDelta']
                values.append('n/a' if value is None else f"{value['mean']:+.2f} ± {value['sampleStdev']:.2f}")
            lines.append('| ' + ' | '.join([scenario, f'normalized vs {reference}', *values]) + ' |')
    lines += ['', '## Existing all-thread dispatch allocation probe', '',
              'Tiering is disabled only for this separate probe. Values are medians; every signed incremental point is retained in JSON. No timing subtraction is performed.', '',
              '| Case | Arm | Raw B/op | Wrapped B/op | Incremental B/op |',
              '|---|---|---:|---:|---:|']
    for case in DISPATCH_CASES:
        for arm in ARMS:
            values = [descriptions[f'dispatch-{case}-{kind}/{arm}']['allocatedBytesPerOperation']['median']
                      for kind in ('raw', 'wrapped', 'incremental')]
            lines.append('| ' + ' | '.join([case, arm, *[f'{v:.6f}' for v in values]]) + ' |')
    lines += ['', '## Fixed reader construction allocation', '',
              '| Arm | Raw B/reader | Wrapped B/reader |', '|---|---:|---:|']
    for arm in ARMS:
        values = [descriptions[f'dispatch-construction-main-{kind}/{arm}']['allocatedBytesPerReader']['median']
                  for kind in ('raw', 'wrapped')]
        lines.append('| ' + ' | '.join([arm, *[f'{v:.3f}' for v in values]]) + ' |')
    lines += ['', *['- ' + text for text in result['interpretation']]]
    (root / 'comparison.md').write_text('\n'.join(lines) + '\n')
    print('\n'.join(lines))



if __name__ == '__main__':
    summarize(Path(sys.argv[1]))
