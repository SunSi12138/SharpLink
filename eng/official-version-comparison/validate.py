"""Fail-closed offline identity, function, path and exact-tick validation."""
import json
import math
from pathlib import Path
from fractions import Fraction
from common import strict_json, ARMS, CONTRACT, PRODUCTS, inventory, median, nearest_rank, rational, schedule, write

CHECKS = ('orderedValues', 'count', 'errorBeforeFirstItem', 'errorAfterFirstItem', 'cancellation',
          'earlyBreak', 'consumerException', 'serviceException', 'explicitDispose', 'serverCleanup',
          'clientCleanup', 'postFailureRecovery')


def require(condition, message):
    if not condition:
        raise ValueError(message)


def validate_sample(data, slot, prepared, manifest, runtime, cold=False):
    arm = slot['arm']
    require(data['arm'] == {'official': 'official-2.0.3', 'dev': 'dev-57f06f1'}[arm], 'Wrong arm')
    require(data['phase'] == ('validate' if cold else 'measure'), 'Wrong phase')
    require(data['sourceRevision'] == data['expectedSource'] == CONTRACT[f'{arm}Source'], 'Wrong source')
    require(data['apiVersion'] == CONTRACT[f'{arm}Api'], 'Wrong generated API')
    require((data['mode'], data['itemCount'], data['workers']) == (slot['mode'], slot['items'], 1), 'Wrong cell')
    require(data['runtimeVersion'] == CONTRACT['runtime'], 'Wrong executing runtime')
    require(data['processorCount'] == 2, 'Wrong process CPU count')
    require(data['processorAffinity'] == sum(1 << cpu for cpu in slot['affinity']), 'Wrong executing affinity')
    require(data['sameVersionServerClient'] is True and data['shutdownCompleted'] is True and data['disposeCompleted'] is True, 'Endpoint identity or shutdown incomplete')
    require(data['generatedManifest']['api'] == CONTRACT[f'{arm}Api'] and data['generatedManifest']['sourceRevision'] == CONTRACT[f'{arm}Source'], 'Wrong independently generated manifest')
    require(all(data.get(k) == 'default' for k in ('tieredCompilation', 'tieredPgo', 'readyToRun')), 'Changed CLR tiering/PGO/R2R')
    require(len(data['frameworkLocations']) == 2 and all(Path(p).is_relative_to(runtime.parent / 'shared') and '10.0.14' in Path(p).parts for p in data['frameworkLocations']), 'Framework escaped private runtime')
    require((data['transport'], data['connectionPoolMin'], data['connectionPoolMax'], data['readyConnections'],
             data['requestTimeout'], data['heartbeatIntervalSeconds'], data['clientInterceptors'], data['serverInterceptors']) ==
            ('tcp-loopback', 1, 1, 1, 'disabled', 3600, 1, 0), 'Functional configuration changed')
    expected_dlls = {k: v for k, v in manifest['arms'][arm]['snapshot'].items() if k.endswith('.dll')}
    require(data['binaryHashes'] == expected_dlls, 'Executed snapshot hashes differ')
    loaded = data['loadedAssemblies']
    names = [a['name'] for a in loaded]
    required = {f'SharpLink.{n}' for n in ('Abstractions', 'Client', 'Runtime', 'Server')} | {'PhysicalTcpProbe', 'ScalarStreamModule'}
    require(required <= set(names) and len(names) == len(set(names)), 'Missing/duplicate loaded identity')
    for a in loaded:
        path = Path(a['location'])
        require(path.parent == prepared / arm and path.name in expected_dlls and bool(a['mvid']), 'Assembly escaped snapshot')
        require(a['sha256'] == expected_dlls[path.name], 'Loaded assembly hash mismatch')
        if a['name'].startswith('SharpLink.'):
            require(a['productVersion'].split('+')[-1] == CONTRACT[f'{arm}Source'], 'Product provenance mismatch')
    require(data['validationFailures'] == 0, 'Functional failure')
    require(all(data['correctness'].get(k) is True for k in CHECKS), 'Cold correctness incomplete')
    wrappers = data['wrapperTypes']
    # Positive names independently verified in both arm sources, not absence-only checks.
    for needle in ('+LogicalInvocationAsyncEnumerable`', 'InterceptedAsyncEnumerable', 'PooledAsyncStreamDispatcher'):
        require(any(needle in t for t in wrappers), f'Positive wrapper proof missing {needle}')
    require(data['interceptorObserved'] is True, 'Client interceptor bypassed')
    dynamic = slot['mode'] == 'dynamic-intercepted'
    require(('SharpLinkModuleRpcChannel' in data['channelType']) if dynamic else ('SharpLink.Client.SharpLinkClient' == data['channelType']), 'Wrong positive channel path')
    if dynamic:
        require('InvokeServerStreamingDeferred' in data['streamType'], 'Dynamic generated forwarding missing')
    proof = data['logicalPathProof']
    require(proof['generalLogicalObserved'] is True and proof['physicalDispatcherObserved'] is True and proof['observedBeforeWarmup'] is True and proof['clientActivityListeners'] is False and proof['clientCallsEnabled'] is False and proof['telemetryWrapperObserved'] is False and proof['completionOwnedObserved'] is False, 'Logical path proof incomplete')
    if cold:
        require((data['warmupSeconds'], data['requestedSeconds'], data['operations'], data['elapsedTicks']) == (0, 0, 0, 0), 'Cold mode performed timed work')
    if not cold:
        require((data['warmupSeconds'], data['requestedSeconds']) == (3, 6), 'Wrong timing contract')
        for k in ('operations', 'elapsedTicks', 'stopwatchFrequency'):
            require(type(data[k]) is int and data[k] > 0, f'Invalid exact {k}')


def exact_metrics(data, ticks):
    for key in ('arm', 'mode', 'itemCount'):
        require(ticks[key] == data[key], f'Raw tick identity mismatch {key}')
    for k in ('operations', 'elapsedTicks', 'stopwatchFrequency'):
        require(type(ticks[k]) is int and ticks[k] == data[k] and ticks[k] > 0, f'Tick metadata mismatch {k}')
    require(ticks['workers'] == 1 and len(ticks['samples']) == 1 and ticks['samples'][0]['worker'] == 0, 'Worker vector mismatch')
    require(type(ticks['measurementStartTick']) is int and type(ticks['measurementEndTick']) is int and ticks['measurementEndTick'] - ticks['measurementStartTick'] == ticks['elapsedTicks'], 'Wrong exact elapsed time')
    durations = ticks['samples'][0]['durationTicks']
    require(len(durations) == data['operations'], 'Tick count mismatch')
    require(data['elapsedTicks'] >= data['requestedSeconds'] * data['stopwatchFrequency'], 'Truncated measurement window')
    require(all(type(t) is int and 0 < t <= data['elapsedTicks'] for t in durations), 'Nonpositive/invalid duration or outside measurement envelope')
    require(sum(durations) <= data['elapsedTicks'], 'Serial C1 duration sum exceeds measurement envelope')
    def display(key, exact):
        require(type(data[key]) in (float, int) and math.isfinite(data[key]) and math.isclose(data[key], float(exact), rel_tol=1e-12, abs_tol=1e-12), f'Display differs from exact ticks {key}')
    display('elapsedSeconds', Fraction(data['elapsedTicks'], data['stopwatchFrequency']))
    display('operationsPerSecond', Fraction(data['operations'] * data['stopwatchFrequency'], data['elapsedTicks']))
    for key, numerator, denominator in [('p50Us',1,2), ('p95Us',95,100), ('p99Us',99,100), ('p999Us',999,1000), ('maxUs',1,1)]:
        display(key, Fraction(nearest_rank(durations, numerator, denominator) * 1000000, data['stopwatchFrequency']))
    p99 = nearest_rank(durations, 99, 100)
    require(p99 > 0, 'Nonpositive P99')
    return {'qps': Fraction(data['operations'] * data['stopwatchFrequency'], data['elapsedTicks']),
            'p99Seconds': Fraction(p99, data['stopwatchFrequency']), 'p99Ticks': p99,
            'p50Ticks': nearest_rank(durations, 1, 2), 'p999Ticks': nearest_rank(durations, 999, 1000)}


def summarize(root, prepared, runtime):
    manifest = strict_json((root / 'prepared.json').read_text())
    require(manifest['contract'] == CONTRACT, 'Contract mismatch')
    results, failures, inventory_errors = {}, [], []
    try:
        require(manifest == strict_json((prepared / 'prepared.json').read_text()), 'Archived prepared manifest differs from actual prepared evidence')
        for arm in ARMS:
            require(inventory(prepared / arm) == manifest['arms'][arm]['snapshot'], 'Prepared snapshot inventory drift')
    except (OSError, ValueError, KeyError, TypeError) as error:
        inventory_errors.append({'kind': 'prepared-integrity', 'error': str(error)})
    affinity = []
    try:
        topology = strict_json((root / 'topology-selection.json').read_text())
        from common import select_cpus
        affinity = select_cpus(topology['mapping'], set(topology['allowed']))
        require(affinity == topology['selected'], 'Selected topology mismatch')
        gate = strict_json((root / 'cold-gate.json').read_text())
        require(gate == {'status': 'Passed', 'failures': []}, 'Dedicated cold gate missing/failed')
        for count in CONTRACT['items']:
            for mode in CONTRACT['modes']:
                for arm in ARMS:
                    name = f'{mode}-n{count}-{arm}'
                    host = strict_json((root / 'cold' / f'{name}.host.json').read_text())
                    require(host['status'] == 'success' and host['exitCode'] == 0, 'Cold process failed')
                    data = strict_json((root / 'cold' / f'{name}.json').read_text())
                    validate_sample(data, dict(arm=arm, items=count, mode=mode, affinity=affinity), prepared, manifest, runtime, cold=True)
    except (OSError, ValueError, KeyError, TypeError) as error:
        inventory_errors.append({'kind': 'cold/topology', 'error': str(error)})
    expected = {s['id'] for s in schedule()}
    for kind, paths in [('raw', root.glob('runs/*.json')), ('ticks', root.glob('ticks/*.json')), ('host', root.glob('runs/*.host.json'))]:
        actual = {p.name.removesuffix('.host.json') if kind == 'host' else p.stem for p in paths if kind != 'raw' or not p.name.endswith('.host.json')}
        if actual != expected:
            inventory_errors.append({'kind': kind, 'missing': sorted(expected - actual), 'unexpected': sorted(actual - expected)})
    for slot in schedule():
        name = slot['id']
        try:
            host = strict_json((root / 'runs' / f'{name}.host.json').read_text())
            require(host['status'] == 'success' and host['exitCode'] == 0, 'Process failed, timeout, or incomplete shutdown')
            data = strict_json((root / 'runs' / f'{name}.json').read_text())
            validate_sample(data, dict(slot, affinity=affinity), prepared, manifest, runtime)
            results[name] = exact_metrics(data, strict_json((root / 'ticks' / f'{name}.json').read_text()))
        except (OSError, ValueError, KeyError, TypeError) as error:
            failures.append({'slot': name, 'error': str(error)})
    rows = []
    for count in CONTRACT['items']:
        for mode in CONTRACT['modes']:
            ratios = {'qps': [], 'p99Seconds': []}
            missing_pairs = []
            for pair in range(1, 7):
                names = [f'{mode}-n{count}-r{pair}-{a}' for a in ARMS]
                if not all(n in results for n in names):
                    missing_pairs.append(pair)
                    continue
                for metric in ratios:
                    ratios[metric].append(results[names[1]][metric] / results[names[0]][metric])
            row = dict(mode=mode, items=count, completePairs=len(ratios['qps']), missingPairs=missing_pairs,
                       pairedRatios={m: [rational(v) for v in vs] for m, vs in ratios.items()})
            if not missing_pairs and not failures and not inventory_errors:
                qps, latency = median(ratios['qps']), median(ratios['p99Seconds'])
                row.update(qpsMedianPairedRatio=rational(qps), p99MedianPairedRatio=rational(latency),
                           qpsGainPercent=rational((qps - 1) * 100), p99ReductionPercent=rational((1 - latency) * 100),
                           qpsTargetGapPercentagePoints=rational((Fraction(11, 10) - qps) * 100),
                           p99TargetGapPercentagePoints=rational((latency - Fraction(4, 5)) * 100),
                           observedMedianTargetsReached=qps >= Fraction(11, 10) and latency <= Fraction(4, 5))
            rows.append(row)
    summary = dict(status='Invalid' if failures or inventory_errors else 'DescriptiveOnly',
                   decisionBoundary=CONTRACT['releaseBoundary'], failures=failures, inventoryErrors=inventory_errors,
                   rawExactMetrics={n: {k: rational(v) for k, v in ms.items()} for n, ms in results.items()}, cells=rows,
                   inference='Six pairs per cell, no confidence/causal/final acceptance claim; all cells reported separately, no pooled quantiles or hiding unfavorable cells.')
    write(root / 'summary.json', summary)
    return summary


if __name__ == '__main__':
    import argparse
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--input', type=Path, required=True)
    p.add_argument('--prepared', type=Path, required=True)
    p.add_argument('--runtime-dotnet', type=Path, required=True)
    a = p.parse_args()
    result = summarize(a.input.resolve(), a.prepared.resolve(), a.runtime_dotnet.resolve())
    print(result['status'])
    raise SystemExit(result['status'] == 'Invalid')
