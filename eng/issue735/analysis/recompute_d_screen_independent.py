#!/usr/bin/env python3
"""Independently recompute one frozen Issue #735 D screen run, offline.

Input is one run directory containing nativeaot, jit-pgo0 and jit-pgo1 artifacts.
This reader never imports the primary analyzer, invokes dotnet, changes a binary,
runs a performance sample, or combines V1/V2 measurement populations. Its numerical
core was first written as evidence/new/d_screen_independent_review.py; the CLI adds
explicit input/provenance and completeness checks for delivery.
"""
from pathlib import Path
import argparse
import csv
import hashlib
import json
import math
import re
import statistics
import sys

if not __debug__:
    raise SystemExit("Do not use python -O: assertions verify frozen evidence inputs.")

METRICS = [
    'throughputRpcPerSecond', 'cpuMicrosecondsPerRpc', 'allocationBytesPerRpc',
    'p50Microseconds', 'p95Microseconds', 'p99Microseconds', 'p999Microseconds',
    'threadPoolCompletedWorkItemsPerRpc', 'gen0Collections', 'gen1Collections',
    'gen2Collections', 'gcPauseTotalMicroseconds', 'settlementAndProbeMicroseconds',
    'settledThroughputRpcPerSecond', 'settledCpuMicrosecondsPerRpc',
    'settledAllocationBytesPerRpc', 'settledGcPauseTotalMicroseconds',
    'postCompletionSettlementMicroseconds', 'completedToSettledMicroseconds',
]
AB = re.compile(r'ab-r([01])-(.+)-p([0-3])-([AB])\.txt$')
AA = re.compile(r'aa-r([01])-(.+)-(left|right)\.txt$')


def parse(path):
    return dict(line.split('=', 1) for line in path.read_text(encoding='utf-8-sig').splitlines() if '=' in line)


def summarize(values):
    return {'raw': values, 'median': statistics.median(values),
            'min': min(values), 'max': max(values), 'positive': sum(x > 0 for x in values)}


def review(root):
    plan = json.loads((root / 'screen-plan.json').read_text())
    exits = json.loads((root / 'exits.json').read_text())
    binary_verification = {}
    for arm, identity in plan['sourceTrees'].items():
        binary = root / 'binaries' / arm / ('SharpLink.Benchmarks' if plan['runtimePopulation'] == 'nativeaot' else 'SharpLink.Benchmarks.dll')
        observed_hash = hashlib.sha256(binary.read_bytes()).hexdigest()
        assert observed_hash == identity['binarySha256'], binary
        binary_verification[arm] = observed_hash
    reports = {}
    metadata = []
    grouped, aa = {}, {}
    errors = []
    for path in sorted((root / 'samples').glob('*.txt')):
        match = AB.fullmatch(path.name) or AA.fullmatch(path.name)
        if not match:
            continue
        r = parse(path)
        reports[path.name] = r
        ab_match = AB.fullmatch(path.name)
        expected_arm = ab_match.group(4) if ab_match else 'A'
        identity = plan['sourceTrees'][expected_arm]
        for report_key, plan_key in [
            ('measurementSourceSha','MeasurementSourceSha'), ('baselineSha','BaselineSha'),
            ('candidateSha','CandidateSha'), ('runtimeSourceTreeSha','RuntimeSourceTreeSha'),
            ('controllerSourceSha256','ControllerSourceSha256'), ('sendPumpSourceSha256','SendPumpSourceSha256'),
            ('compiledMode','Mode'), ('measuredBinarySha256','binarySha256')]:
            assert r[report_key] == identity[plan_key], (path,report_key)
        assert r['b3ReadyWriterEnabled'] == 'false' and r['productionScheduler'] == 'dev-SendPump', path
        is_native = plan['runtimePopulation'] == 'nativeaot'
        assert r['builtForNativeAot'] == str(is_native).lower(), path
        assert r['dynamicCodeSupported'] == r['dynamicCodeCompiled'] == str(not is_native).lower(), path
        if r.get('status') != 'passed':
            errors.append(f'{path.name}:status={r.get("status")}')
            continue
        raw_path = Path(str(path) + '.samples.csv')
        latencies = []
        with raw_path.open(encoding='utf-8-sig', newline='') as f:
            for row in csv.DictReader(f):
                tick_delta = int(row['end_timestamp']) - int(row['start_timestamp'])
                assert tick_delta == int(row['latency_ticks']), raw_path
                us = tick_delta * 1e6 / float(r['stopwatchFrequency'])
                assert math.isclose(us, float(row['latency_us']), rel_tol=1e-12, abs_tol=1e-7), raw_path
                latencies.append(us)
        count = int(r['successfulRpcs'])
        assert count == len(latencies) == int(r['concurrency']) * int(r['measuredOpsPerWorker']), path
        assert int(r['failedRpcs']) == 0, path
        latencies.sort()
        for key, fraction in [('p50Microseconds', .5), ('p95Microseconds', .95),
                              ('p99Microseconds', .99), ('p999Microseconds', .999)]:
            expected = latencies[math.ceil(count * fraction) - 1]
            assert math.isclose(expected, float(r[key]), rel_tol=1e-12, abs_tol=1e-7), (path, key)
        for numerator, denominator, key in [
            ('successfulRpcs', 'elapsedSeconds', 'throughputRpcPerSecond'),
            ('cpuTotalMicroseconds', 'successfulRpcs', 'cpuMicrosecondsPerRpc'),
            ('allocationBytesTotal', 'successfulRpcs', 'allocationBytesPerRpc'),
            ('successfulRpcs', 'settledElapsedSeconds', 'settledThroughputRpcPerSecond'),
            ('settledCpuTotalMicroseconds', 'successfulRpcs', 'settledCpuMicrosecondsPerRpc'),
            ('settledAllocationBytesTotal', 'successfulRpcs', 'settledAllocationBytesPerRpc')]:
            if key not in r:
                continue
            assert math.isclose(float(r[numerator]) / float(r[denominator]), float(r[key]), rel_tol=1e-12), (path, key)
        if 'settledElapsedSeconds' in r:
            assert float(r['settledElapsedSeconds']) >= float(r['elapsedSeconds']), path
            assert float(r['settledCpuTotalMicroseconds']) >= float(r['cpuTotalMicroseconds']), path
            assert float(r['settledAllocationBytesTotal']) >= float(r['allocationBytesTotal']), path
        with Path(str(path) + '.fairness.csv').open(encoding='utf-8-sig', newline='') as f:
            fairness = [float(row['jain_index']) for row in csv.DictReader(f) if row['jain_index'] != 'N/A']
        metadata.append({'file': path.name, 'sha256': hashlib.sha256(path.read_bytes()).hexdigest(),
                         'rawSha256': hashlib.sha256(raw_path.read_bytes()).hexdigest(),
                         'rpcs': count, 'p999TailCount': count * .001,
                         'elapsedSeconds': float(r['elapsedSeconds']),
                         'cpuCoresEffective': float(r['cpuTotalMicroseconds']) / 1e6 / float(r['elapsedSeconds']),
                         'gcPauseShare': float(r['gcPauseTotalMicroseconds']) / 1e6 / float(r['elapsedSeconds']),
                         'minimumJain': min(fairness),
                         'settlementAndProbeMicroseconds': float(r['settlementAndProbeMicroseconds']) if 'settlementAndProbeMicroseconds' in r else None,
                         'completedToSettledMicroseconds': float(r['completedToSettledMicroseconds']) if 'completedToSettledMicroseconds' in r else None,
                         'settledAllocationExtraBytesPerRpc': float(r['settledAllocationBytesPerRpc']) - float(r['allocationBytesPerRpc']) if 'settledAllocationBytesPerRpc' in r else None,
                         'settledCpuExtraMicrosecondsPerRpc': float(r['settledCpuMicrosecondsPerRpc']) - float(r['cpuMicrosecondsPerRpc']) if 'settledCpuMicrosecondsPerRpc' in r else None})
        if ab_match:
            rnd, cell, pos, arm = ab_match.groups()
            grouped.setdefault(cell, {})[(int(rnd), int(pos))] = (arm, path.name, r)
        else:
            rnd, cell, side = AA.fullmatch(path.name).groups()
            aa.setdefault(cell, {})[(int(rnd), side)] = (path.name, r)
    cells = {}
    for cell, runs in sorted(grouped.items()):
        pairs = []
        for rnd in (0, 1):
            for left in (0, 2):
                if (rnd, left) not in runs or (rnd, left + 1) not in runs:
                    errors.append(f'{cell}:missing-pair:{rnd}:{left}')
                    continue
                pair = {x[0]: (x[1], x[2]) for x in (runs[rnd, left], runs[rnd, left + 1])}
                assert set(pair) == {'A', 'B'}
                metrics = {}
                for key in METRICS:
                    if key not in pair['A'][1] or key not in pair['B'][1]:
                        continue
                    av, bv = float(pair['A'][1][key]), float(pair['B'][1][key])
                    metrics[key] = {'A': av, 'B': bv, 'delta': bv-av,
                                    'percent': (bv/av-1)*100 if av else None}
                pairs.append({'round': rnd, 'left': left, 'order': ''.join(runs[rnd, p][0] for p in (left,left+1)),
                              'A': pair['A'][0], 'B': pair['B'][0], 'metrics': metrics})
        stats = {}
        for key in METRICS:
            values = [p['metrics'][key]['percent'] for p in pairs
                      if key in p['metrics'] and p['metrics'][key]['percent'] is not None]
            if values:
                stats[key] = summarize(values)
        cells[cell] = {'pairs': pairs, 'changesPercent': stats,
                       'allocationDeltaBytes': summarize([p['metrics']['allocationBytesPerRpc']['delta'] for p in pairs])}
    aacells = {}
    for cell, runs in sorted(aa.items()):
        metrics = {}
        for key in METRICS:
            values = []
            for rnd in (0, 1):
                if (rnd,'left') not in runs or (rnd,'right') not in runs:
                    continue
                a, b = runs[rnd,'left'][1], runs[rnd,'right'][1]
                if key in a and key in b and float(a[key]):
                    values.append((float(b[key])/float(a[key])-1)*100)
            if values:
                metrics[key] = summarize(values)
        aacells[cell] = metrics
    identities = {k: sorted({r.get(k,'<missing>') for r in reports.values()}) for k in [
        'measurementSourceSha','baselineSha','candidateSha','measuredBinarySha256','compiledMode',
        'builtForNativeAot','dynamicCodeSupported','dynamicCodeCompiled','machine','processorCount',
        'serverGc','runtime','env.DOTNET_TieredPGO','env.DOTNET_TieredCompilation',
        'actualClientStreamReceiveWindowBytes','actualClientConnectionReceiveWindowBytes',
        'actualClientMaxSendQueueBytes','actualClientMaxPreCreditSerializedBytes',
        'productionScheduler','b3ReadyWriterEnabled','tierStabilizationVerified']}
    expected_hashes = {x['MeasurementSourceSha'] for x in plan['sourceTrees'].values()}
    assert set(identities['measurementSourceSha']) == expected_hashes
    assert len(expected_hashes) == 1, 'A/B have different measurement sources'
    return {'sourceRoot': str(root), 'runtimePopulation': plan['runtimePopulation'],
            'planSha256': hashlib.sha256((root/'screen-plan.json').read_bytes()).hexdigest(),
            'reportCount': len(reports),'rawQuantileReportsVerified': len(metadata),
            'binarySha256Verified': binary_verification,
            'failedProcesses': [x for x in exits if x.get('exitCode') != 0],
            'errors': errors, 'identities': identities, 'cells': cells, 'aaCells': aacells,
            'reportMetadata': metadata}


def verify_complete_plan(root):
    """Reject missing arms/positions; failed collections are never success samples."""
    plan = json.loads((root / 'screen-plan.json').read_text())
    collection = json.loads((root / 'collection-result.json').read_text())
    exits = json.loads((root / 'exits.json').read_text())
    assert collection['collectionSucceeded'] is True, root
    assert collection['failedProcesses'] == [], root
    assert all(row['exitCode'] == 0 for row in exits), root
    assert len(exits) == collection['processRows'], root
    assert plan['runtimePopulation'] == root.name, root
    assert plan['abOrders'] == [['A', 'B', 'B', 'A'], ['B', 'A', 'A', 'B']], root
    expected = set()
    for rnd, order in enumerate(plan['abOrders']):
        for transport in ('tcp', 'sharedmemory'):
            for shape, count, size, concurrency, _ops in plan['cases']:
                cell = f'{transport}-{shape}-{count}x{size}-c{concurrency}'
                for pos, arm in enumerate(order):
                    expected.add(f'ab-r{rnd}-{cell}-p{pos}-{arm}.txt')
            for shape, count, size, concurrency, _ops in plan['aaCases']:
                cell = f'{transport}-{shape}-{count}x{size}-c{concurrency}'
                for side in ('left', 'right'):
                    expected.add(f'aa-r{rnd}-{cell}-{side}.txt')
    actual = {p.name for p in (root / 'samples').glob('*.txt')
              if AB.fullmatch(p.name) or AA.fullmatch(p.name)}
    assert len(expected) == 144, (root, len(expected))
    assert expected == actual, (root, sorted(expected - actual), sorted(actual - expected))
    process_names = [r['name'] + '.txt' for r in exits
                     if AB.fullmatch(r['name'] + '.txt') or AA.fullmatch(r['name'] + '.txt')]
    assert len(process_names) == len(set(process_names)), root
    assert set(process_names) == expected, root
    return {name: hashlib.sha256((root / name).read_bytes()).hexdigest()
            for name in ('screen-plan.json', 'exits.json', 'collection-result.json')}


def print_summary(result):
    for pop, row in result.items():
        print(pop, 'reports', row['reportCount'], 'verified', row['rawQuantileReportsVerified'],
              'failed', len(row['failedProcesses']), 'errors', len(row['errors']))
        for cell, c in row['cells'].items():
            s = c['changesPercent']
            print(cell, 'completed rpc/cpu/alloc/p99/p999',
                  ' / '.join(f'{s[k]["median"]:+.2f}'
                             for k in METRICS[:3] + ['p99Microseconds', 'p999Microseconds']),
                  'rpc-pairs', ','.join(f'{x:+.2f}' for x in s['throughputRpcPerSecond']['raw']))
            if 'settledThroughputRpcPerSecond' in s:
                print('  settled rpc/cpu/alloc',
                      ' / '.join(f'{s[k]["median"]:+.2f}' for k in
                                 ['settledThroughputRpcPerSecond', 'settledCpuMicrosecondsPerRpc',
                                  'settledAllocationBytesPerRpc']),
                      'rpc-pairs', ','.join(f'{x:+.2f}' for x in
                                           s['settledThroughputRpcPerSecond']['raw']))
        print('A/A (separate baseline comparisons; never included in A/B pairs)')
        for cell, s in row['aaCells'].items():
            keys = (['settledThroughputRpcPerSecond', 'settledCpuMicrosecondsPerRpc',
                     'settledAllocationBytesPerRpc'] if 'settledThroughputRpcPerSecond' in s
                    else METRICS[:3]) + ['p99Microseconds', 'p999Microseconds']
            print(cell, ' | '.join(k + ':' + ','.join(f'{x:+.2f}' for x in s[k]['raw'])
                                  for k in keys))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--input', type=Path, required=True,
                        help='One extracted run directory with all three runtime subdirectories')
    parser.add_argument('--output', type=Path, required=True,
                        help='JSON output outside the input directory; a .provenance.json sidecar is written')
    args = parser.parse_args()
    root, output = args.input.resolve(), args.output.resolve()
    if output.is_relative_to(root):
        parser.error('--output must be outside the original input directory')
    result, input_hashes = {}, {}
    for pop in ('nativeaot', 'jit-pgo0', 'jit-pgo1'):
        input_hashes[pop] = verify_complete_plan(root / pop)
        result[pop] = review(root / pop)
        row = result[pop]
        assert row['reportCount'] == row['rawQuantileReportsVerified'] == 144, pop
        assert len(row['cells']) == 16 and len(row['aaCells']) == 4, pop
        assert not row['failedProcesses'] and not row['errors'], pop
    measurement_hashes = {sha for row in result.values()
                          for sha in row['identities']['measurementSourceSha']}
    assert len(measurement_hashes) == 1, 'Never mix different measurement hashes in one run'
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(result, indent=2) + '\n')
    provenance = {
        'schema': 'sharplink-D-independent-analysis-provenance-v1',
        'classification': 'offline recomputation, not a new RPC measurement or production Go',
        'analysisSourceSha256': hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
        'inputDirectory': str(root), 'outputFile': str(output),
        'outputSha256': hashlib.sha256(output.read_bytes()).hexdigest(),
        'inputMetadataSha256': input_hashes,
        'measurementSourceSha': next(iter(measurement_hashes)),
        'runtimePopulations': 3, 'verifiedReports': 432, 'abProcesses': 384, 'aaProcesses': 48,
        'samplePooling': 'none; each runtime and each original 4-pair cell remains distinct',
        'usedPrimaryAnalyzer': False,
        'numericalCoreOriginal': 'evidence/new/d_screen_independent_review.py',
        'originalNumericalCoreFileSha256': '349a972075e1aad2c3733ead4950793f6e45494eb9630c28885498763658a358',
    }
    output.with_suffix('.provenance.json').write_text(json.dumps(provenance, indent=2) + '\n')
    print_summary(result)
    print('Provenance:', output.with_suffix('.provenance.json'))


if __name__ == '__main__':
    main()
