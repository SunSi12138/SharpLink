"""Fail-closed offline identity, function, path and exact-tick validation."""
import json
import math
import re
from collections import Counter
from identity import IDENTITY_CODES, IdentityError, framework_policy as materialize_framework_policy, validate_policy, validate_loaded, archive_framework_policy, validate_framework_assemblies
from pathlib import Path
from fractions import Fraction
from common import strict_json, ARMS, CONTRACT, PRODUCTS, inventory, median, nearest_rank, rational, schedule, write, ROOT, source_inventory, gate_schedule

CHECKS = ('orderedValues', 'count', 'errorBeforeFirstItem', 'errorAfterFirstItem', 'cancellation',
          'earlyBreak', 'consumerException', 'serviceException', 'explicitDispose', 'serverCleanup',
          'clientCleanup', 'postFailureRecovery')


CHECK_REASON_CODES = {
    'Wrong source':'source_identity',
    'Wrong executing runtime':'runtime_identity',
    'Wrong generated API':'generated_manifest',
    'Incomplete independent generated manifest':'generated_manifest',
    'Wrong independently generated manifest':'generated_manifest',
    'Strict cleanup incomplete':'cleanup_gate',
    'Normal eligibility incomplete':'cleanup_gate',
    'beforeWarmupDrain incomplete':'cleanup_gate',
    'afterWorkDrain incomplete':'cleanup_gate',
    'Executed snapshot hashes differ':'snapshot_identity',
    'Framework escaped private runtime':'framework_location',
    'Incomplete loaded assembly census':'loaded_census',
}


class CheckError(ValueError):
    def __init__(self, message):
        self.code = CHECK_REASON_CODES.get(message, 'validation_check')
        super().__init__(message)


REASON_CODES = IDENTITY_CODES | frozenset(CHECK_REASON_CODES.values()) | frozenset({'missing_input', 'input_read', 'malformed_input',
                                        'validation_check', 'inventory_mismatch', 'not_attempted_declaration'})


def failure_reason(error):
    """Only internal fixed tokens may be emitted; arbitrary exception text stays local."""
    if isinstance(error, CheckError):
        return error.code if error.code in REASON_CODES else 'validation_check'
    if isinstance(error, IdentityError):
        return error.code if error.code in IDENTITY_CODES else 'validation_check'
    if isinstance(error, FileNotFoundError): return 'missing_input'
    if isinstance(error, OSError): return 'input_read'
    if isinstance(error, (KeyError, TypeError, json.JSONDecodeError)): return 'malformed_input'
    return 'validation_check'


def reason_counts(failures, inventory_errors):
    counts = Counter()
    for failure in failures + inventory_errors:
        code = failure.get('reasonCode', 'inventory_mismatch')
        counts[code if code in REASON_CODES else 'validation_check'] += 1
    return dict(sorted(counts.items()))


def require(condition, message):
    if not condition:
        raise CheckError(message)


NORMAL_CHECKS = ('orderedValues', 'count', 'producerFinally', 'serverCleanup', 'clientCleanup', 'physicalDrain')
PHASES = ('strict-cleanup', 'normal-validate', 'normal-measure')
DRAIN_COUNTS = dict(logicalInvocations=0, activeClientCalls=0, activeClientStreams=0, pendingCalls=0,
                    activeServerCalls=0, normalStarted=1, normalCompleted=1, normalActive=0)


def validate_sample(data, slot, prepared, manifest, runtime, framework_policy=None, archive_policy=None, phase='normal-measure'):
    arm = slot['arm']
    require(data['arm'] == {'official': 'official-2.0.3', 'dev': 'candidate-8ae9ee36'}[arm], 'Wrong arm')
    require(phase in PHASES and data['phase'] == phase, 'Wrong phase')
    require(data['schemaVersion'] == 3 and type(data['schemaVersion']) is int, 'Wrong sample schema')
    require(phase != 'strict-cleanup' or arm == 'dev', 'Official baseline cannot enter strict cleanup')
    require(data['sourceRevision'] == data['expectedSource'] == CONTRACT[f'{arm}Source'], 'Wrong source')
    require(data['apiVersion'] == CONTRACT[f'{arm}Api'], 'Wrong generated API')
    require(all(type(data[k]) is int for k in ('itemCount', 'workers', 'processorCount', 'processorAffinity', 'apiVersion')), 'Noninteger identity/configuration')
    require((data['mode'], data['itemCount'], data['workers']) == (slot['mode'], slot['items'], 1), 'Wrong cell')
    require(data['runtimeVersion'] == CONTRACT['runtime'], 'Wrong executing runtime')
    require(data['processorCount'] == 2, 'Wrong process CPU count')
    require(data['processorAffinity'] == sum(1 << cpu for cpu in slot['affinity']), 'Wrong executing affinity')
    require(data['sameVersionServerClient'] is True and data['shutdownCompleted'] is True and data['disposeCompleted'] is True, 'Endpoint identity or shutdown incomplete')
    generated = data['generatedManifest']
    expected_abi = {'official':'sharplink-2.0-api4-rpcchannel-codec-provider-v1',
                    'dev':'sharplink-3.0-api5-owner-bound-unsafe-blit-v1'}[arm]
    require(set(generated) == {'arm','sourceRevision','api','protocol','abi','generator','rpcAssemblyHash'},
            'Incomplete independent generated manifest')
    require(generated['arm'] == data['arm'] and generated['api'] == CONTRACT[f'{arm}Api'] and
            generated['sourceRevision'] == CONTRACT[f'{arm}Source'] and
            type(generated['protocol']) is int and generated['protocol'] == 2 and
            generated['generator'] == CONTRACT[f'{arm}Version'] and generated['abi'] == expected_abi,
            'Wrong independently generated manifest')
    # Both pinned sources RpcHash128.ToString use invariant High x16 + Low x16.
    rpc_hash = generated['rpcAssemblyHash']
    require(type(rpc_hash) is str and re.fullmatch(r'[0-9a-f]{32}', rpc_hash) is not None and
            rpc_hash != '0' * 32, 'Missing or malformed RPC assembly identity')
    require(all(data.get(k) == 'default' for k in ('tieredCompilation', 'tieredPgo', 'readyToRun')), 'Changed CLR tiering/PGO/R2R')
    require(data['frameworkLocations'] == [str(runtime.parent / 'shared/Microsoft.NETCore.App/10.0.14/System.Private.CoreLib.dll'), str(runtime.parent / 'shared/Microsoft.AspNetCore.App/10.0.14/Microsoft.AspNetCore.dll')], 'Framework escaped private runtime')
    require((data['transport'], data['connectionPoolMin'], data['connectionPoolMax'], data['readyConnections'],
             data['requestTimeout'], data['heartbeatIntervalSeconds'], data['clientInterceptors'], data['serverInterceptors']) ==
            ('tcp-loopback', 1, 1, 1, 'disabled', 3600, 1, 0), 'Functional configuration changed')
    expected_dlls = {k: v for k, v in manifest['arms'][arm]['snapshot'].items() if k.endswith('.dll')}
    require(data['binaryHashes'] == expected_dlls, 'Executed snapshot hashes differ')
    validate_loaded(data['loadedAssemblies'], arm, prepared, expected_dlls,
                    CONTRACT[f'{arm}Source'], framework_policy, runtime)
    validate_framework_assemblies(data['frameworkAssemblies'], runtime, archive_policy)
    all_names = [a['name'] for a in data['loadedAssemblies'] + data['frameworkAssemblies']]
    require(type(data['loadedAssemblyCount']) is int and data['loadedAssemblyCount'] == len(all_names) == len(set(all_names)), 'Incomplete loaded assembly census')
    require(type(data['validationFailures']) is int and data['validationFailures'] == 0, 'Functional failure')
    require(type(data['strictCleanupExecuted']) is bool and data['strictCleanupExecuted'] == (phase == 'strict-cleanup'), 'Wrong strict cleanup execution')
    if phase == 'strict-cleanup':
        require(set(data['correctness']) == set(CHECKS) and all(data['correctness'][k] is True for k in CHECKS), 'Strict cleanup incomplete')
    else:
        require(data['correctness'] == {}, 'Normal process executed forbidden strict controls')
    require(set(data['normalCorrectness']) == set(NORMAL_CHECKS) and all(data['normalCorrectness'][k] is True for k in NORMAL_CHECKS), 'Normal eligibility incomplete')
    for key in ('beforeWarmupDrain', 'afterWorkDrain'):
        drain = data[key]
        require(set(drain) == set(DRAIN_COUNTS) and all(type(drain[k]) is int and drain[k] == v for k, v in DRAIN_COUNTS.items()), key + ' incomplete')
    wrappers = data['wrapperTypes']
    # Positive names independently verified in both arm sources, not absence-only checks.
    for needle in ('+LogicalInvocationAsyncEnumerable`', 'InterceptedAsyncEnumerable', 'PooledAsyncStreamDispatcher'):
        require(any(needle in t for t in wrappers), f'Positive wrapper proof missing {needle}')
    require(data['interceptorObserved'] is True, 'Client interceptor bypassed')
    dynamic = slot['mode'] == 'dynamic-intercepted'
    require(data['channelType'] == ('SharpLink.Client.SharpLinkModuleRpcChannel' if dynamic else 'SharpLink.Client.SharpLinkClient'), 'Wrong positive channel path')
    if dynamic:
        require('InvokeServerStreamingDeferred' in data['streamType'], 'Dynamic generated forwarding missing')
    proof = data['logicalPathProof']
    require(proof['generalLogicalObserved'] is True and proof['physicalDispatcherObserved'] is True and proof['observedBeforeWarmup'] is True and proof['clientActivityListeners'] is False and proof['clientCallsEnabled'] is False and proof['telemetryWrapperObserved'] is False and proof['completionOwnedObserved'] is False, 'Logical path proof incomplete')
    if phase != 'normal-measure':
        require(all(type(data[k]) is int for k in ('operations', 'elapsedTicks')), 'Noninteger untimed counters')
        require((data['warmupSeconds'], data['requestedSeconds'], data['operations'], data['elapsedTicks']) == (0, 0, 0, 0), 'Cold mode performed timed work')
    if phase == 'normal-measure':
        require((data['warmupSeconds'], data['requestedSeconds']) == (3, 6), 'Wrong timing contract')
        for k in ('operations', 'elapsedTicks', 'stopwatchFrequency'):
            require(type(data[k]) is int and data[k] > 0, f'Invalid exact {k}')


def exact_metrics(data, ticks):
    require(type(ticks['schemaVersion']) is int and ticks['schemaVersion'] == 3, 'Wrong raw tick schema')
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
    for key in ('allocationStartBytes', 'allocationEndBytes', 'allocatedBytes'):
        require(type(data[key]) is int and data[key] >= 0 and
                type(ticks[key]) is int and ticks[key] == data[key], 'Invalid exact allocation counters')
    require(data['allocationEndBytes'] - data['allocationStartBytes'] == data['allocatedBytes'],
            'Allocation delta mismatch')
    allocation = Fraction(data['allocatedBytes'], data['operations'])
    display('allocatedBytesPerOperation', allocation)
    p99 = nearest_rank(durations, 99, 100)
    require(p99 > 0, 'Nonpositive P99')
    return {'allocationBytesPerOperation': allocation, 'allocatedBytes': data['allocatedBytes'],
            'qps': Fraction(data['operations'] * data['stopwatchFrequency'], data['elapsedTicks']),
            'p99Seconds': Fraction(p99, data['stopwatchFrequency']), 'p99Ticks': p99,
            'p50Ticks': nearest_rank(durations, 1, 2), 'p999Ticks': nearest_rank(durations, 999, 1000)}


def validate_host(host, slot, prepared, runtime, root, phase, index):
    require(host['status'] == 'success' and type(host['exitCode']) is int and host['exitCode'] == 0,
            'Process failed, timeout, or incomplete shutdown')
    require(host['slotId'] == slot['id'] and host['phase'] == phase and
            type(host['launchIndex']) is int and host['launchIndex'] == index, 'Wrong process slot/order')
    require(host['processGroupQuiescent'] is True and host.get('descendantLeak', False) is False and host.get('timedOut', False) is False, 'Process did not exit cleanly and quiescently')
    require(type(host['pid']) is int and host['pid'] > 0, 'Missing fresh process identity')
    directory = {'strict-cleanup': 'strict', 'normal-validate': 'normal', 'normal-measure': 'runs'}[phase]
    warmup, seconds = (3, 6) if phase == 'normal-measure' else (0, 0)
    expected = ['taskset', '-c', ','.join(map(str, slot['affinity'])), str(runtime),
                str(prepared / slot['arm'] / 'PhysicalTcpProbe.dll'), slot['mode'], str(slot['items']),
                '1', str(warmup), str(seconds), str(root / directory / (slot['id'] + '.json')), phase]
    if phase == 'normal-measure':
        expected.append(str(root / 'ticks' / (slot['id'] + '.json')))
    require(host['command'] == expected, 'Wrong process command or output identity')


def frozen_plan():
    plan = []
    for phase in PHASES:
        for slot in schedule() if phase == 'normal-measure' else gate_schedule(phase):
            plan.append(dict(slot, phase=phase, launchIndex=len(plan)))
    return plan


def phase_census(root):
    """Describe observed launches separately from validation failures and absent records.

    Missing host records do not prove non-execution. A not-attempted declaration is
    shown only for exact allowlisted plan entries without a host, and remains labelled
    declared. All 60 expected slots still undergo fail-closed acceptance validation.
    """
    plan = frozen_plan()
    expected = {(slot['phase'], slot['id']): slot for slot in plan}
    declared = set()
    declaration = root / 'not-attempted.json'
    declaration_status = 'absent'
    if declaration.exists():
        try:
            records = strict_json(declaration.read_text())
            require(type(records) is list, 'Not-attempted declaration must be a list')
            for record in records:
                require(type(record) is dict, 'Malformed not-attempted entry')
                key = (record['phase'], record['id'])
                require(key in expected and record == expected[key] and key not in declared,
                        'Unexpected, changed or duplicate not-attempted entry')
                declared.add(key)
            declaration_status = 'valid'
        except (OSError, ValueError, KeyError, TypeError):
            declared.clear()
            declaration_status = 'invalid'
    result = []
    for phase in PHASES:
        directory = {'strict-cleanup':'strict', 'normal-validate':'normal', 'normal-measure':'runs'}[phase]
        row = dict(phase=phase, expected=0, attempted=0, cleanExit=0, failedExit=0,
                   launchFailed=0, invalidHost=0, missingHost=0, notAttemptedDeclared=0,
                   missingUnclassified=0, failedSlots=[])
        for slot in (slot for slot in plan if slot['phase'] == phase):
            row['expected'] += 1
            key = (phase, slot['id'])
            path = root / directory / (slot['id'] + '.host.json')
            if not path.exists():
                sample = root / directory / (slot['id'] + '.json')
                ticks = root / 'ticks' / (slot['id'] + '.json')
                if key in declared and (sample.exists() or (phase == 'normal-measure' and ticks.exists())):
                    declared.remove(key)
                    declaration_status = 'invalid'
                row['missingHost'] += 1
                row['notAttemptedDeclared' if key in declared else 'missingUnclassified'] += 1
                continue
            try:
                host = strict_json(path.read_text())
                require(host['slotId'] == slot['id'] and host['phase'] == phase and
                        type(host['launchIndex']) is int and host['launchIndex'] == slot['launchIndex'],
                        'Host identity mismatch')
                if key in declared:
                    declaration_status = 'invalid'
                    raise ValueError('Started host contradicts not-attempted declaration')
                if host['pid'] is None:
                    require(host['status'] == 'failed' and host['exitCode'] is None, 'Unclear launch result')
                    row['launchFailed'] += 1
                    row['failedSlots'].append(slot['id'])
                    continue
                require(type(host['pid']) is int and host['pid'] > 0 and
                        type(host['exitCode']) is int and host['status'] in ('success', 'failed'),
                        'Incomplete process exit record')
                row['attempted'] += 1
                clean = (host['status'] == 'success' and host['exitCode'] == 0 and
                         host['processGroupQuiescent'] is True and host.get('descendantLeak', False) is False and
                         host.get('timedOut', False) is False)
                row['cleanExit' if clean else 'failedExit'] += 1
                if not clean: row['failedSlots'].append(slot['id'])
            except (OSError, ValueError, KeyError, TypeError):
                row['invalidHost'] += 1
        result.append(row)
    if declaration_status == 'invalid':
        for row in result:
            row['missingUnclassified'] += row['notAttemptedDeclared']
            row['notAttemptedDeclared'] = 0
    return {'phases':result, 'notAttemptedDeclaration':declaration_status}


def summarize(root, prepared, runtime, archives_dir=None):
    manifest = strict_json((root / 'prepared.json').read_text())
    require(manifest['contract'] == CONTRACT, 'Contract mismatch')
    results, failures, inventory_errors = {}, [], []
    try:
        require(manifest == strict_json((prepared / 'prepared.json').read_text()), 'Archived prepared manifest differs from actual prepared evidence')
        require(manifest['fixturePurpose'] == 'normal-comparison', 'Wrong prepared fixture purpose')
        require(manifest['toolSourceHashes'] == source_inventory(ROOT), 'Reviewed tool source drift')
        require(manifest['fixtureSourceHashes'] == source_inventory(ROOT / 'fixture'), 'Fixture source drift')
        for arm in ARMS:
            require(inventory(prepared / arm) == manifest['arms'][arm]['snapshot'], 'Prepared snapshot inventory drift')
    except (OSError, ValueError, KeyError, TypeError) as error:
        inventory_errors.append({'kind': 'prepared-integrity', 'error': str(error), 'reasonCode': failure_reason(error)})
    execution_policy = None
    try:
        execution_policy = strict_json((root / 'framework-policy.json').read_text())
        validate_policy(execution_policy, runtime)
        require(execution_policy == materialize_framework_policy(runtime), 'Framework execution policy differs from actual fixed runtime files')
    except (OSError, ValueError, KeyError, TypeError) as error:
        inventory_errors.append({'kind':'framework-policy', 'error':str(error), 'reasonCode':failure_reason(error)})
        execution_policy = None
    archive_policy = None
    try:
        archive_policy = strict_json((root / 'framework-archive-policy.json').read_text())
        require(archive_policy == archive_framework_policy(runtime, archives_dir), 'Framework archive policy differs from verified runtime archives')
    except (OSError, ValueError, KeyError, TypeError) as error:
        inventory_errors.append({'kind':'framework-archive-policy', 'error':str(error), 'reasonCode':failure_reason(error)})
        archive_policy = None
    affinity = []
    try:
        topology = strict_json((root / 'topology-selection.json').read_text())
        from common import select_cpus
        affinity = select_cpus(topology['mapping'], set(topology['allowed']))
        require(affinity == topology['selected'], 'Selected topology mismatch')
        require(strict_json((root / 'schedule.json').read_text()) == schedule(), 'Declared schedule drift')
        expected_plan = frozen_plan()
        require(strict_json((root / 'phase-schedule.json').read_text()) == expected_plan, 'Phase schedule drift')
    except (OSError, ValueError, KeyError, TypeError) as error:
        inventory_errors.append({'kind': 'schedule/topology', 'error': str(error), 'reasonCode': failure_reason(error)})
    for directory, phase, arms, start in [('strict', 'strict-cleanup', ('dev',), 0),
                                          ('normal', 'normal-validate', ARMS, 4)]:
        expected_preflight = {f'{mode}-n{count}-{arm}' for count in CONTRACT['items']
                              for mode in CONTRACT['modes'] for arm in arms}
        for kind, pattern in [('sample', '*.json'), ('host', '*.host.json')]:
            actual = {p.name.removesuffix('.host.json') if kind == 'host' else p.stem
                      for p in (root / directory).glob(pattern)
                      if kind != 'sample' or not p.name.endswith('.host.json')}
            if actual != expected_preflight:
                inventory_errors.append({'kind': directory + '-' + kind,
                                         'missing': sorted(expected_preflight - actual),
                                         'unexpected': sorted(actual - expected_preflight)})
        try:
            gate = strict_json((root / (directory + '-gate.json')).read_text())
            require(gate == {'status': 'Passed', 'failures': []}, 'Dedicated gate missing/failed')
        except (OSError, ValueError, KeyError, TypeError) as error:
            inventory_errors.append({'kind': directory + '-gate', 'error': str(error), 'reasonCode': failure_reason(error)})
        index = start
        for count in CONTRACT['items']:
            for mode in CONTRACT['modes']:
                for arm in arms:
                    name = f'{mode}-n{count}-{arm}'
                    slot = dict(id=name, arm=arm, items=count, mode=mode, affinity=affinity)
                    try:
                        host = strict_json((root / directory / f'{name}.host.json').read_text())
                        validate_host(host, slot, prepared, runtime, root, phase, index)
                        data = strict_json((root / directory / f'{name}.json').read_text())
                        validate_sample(data, slot, prepared, manifest, runtime, framework_policy=execution_policy, archive_policy=archive_policy, phase=phase)
                    except (OSError, ValueError, KeyError, TypeError) as error:
                        inventory_errors.append({'kind': directory + '-sample', 'slot': name, 'error': str(error), 'reasonCode': failure_reason(error)})
                    index += 1
    expected = {s['id'] for s in schedule()}
    for kind, paths in [('raw', root.glob('runs/*.json')), ('ticks', root.glob('ticks/*.json')), ('host', root.glob('runs/*.host.json'))]:
        actual = {p.name.removesuffix('.host.json') if kind == 'host' else p.stem for p in paths if kind != 'raw' or not p.name.endswith('.host.json')}
        if actual != expected:
            inventory_errors.append({'kind': kind, 'missing': sorted(expected - actual), 'unexpected': sorted(actual - expected)})
    for index, slot in enumerate(schedule(), start=12):
        name = slot['id']
        try:
            host = strict_json((root / 'runs' / f'{name}.host.json').read_text())
            validate_host(host, dict(slot, affinity=affinity), prepared, runtime, root, 'normal-measure', index)
            data = strict_json((root / 'runs' / f'{name}.json').read_text())
            validate_sample(data, dict(slot, affinity=affinity), prepared, manifest, runtime, framework_policy=execution_policy, archive_policy=archive_policy)
            results[name] = exact_metrics(data, strict_json((root / 'ticks' / f'{name}.json').read_text()))
        except (OSError, ValueError, KeyError, TypeError) as error:
            failures.append({'slot': name, 'error': str(error), 'reasonCode': failure_reason(error)})
    census = phase_census(root)
    if census['notAttemptedDeclaration'] == 'invalid':
        inventory_errors.append({'kind':'not-attempted-declaration', 'error':'Malformed declaration', 'reasonCode':'not_attempted_declaration'})
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
    summary = dict(failureReasonCounts=reason_counts(failures, inventory_errors), census=census, status='Invalid' if failures or inventory_errors else 'DescriptiveOnly',
                   decisionBoundary=CONTRACT['releaseBoundary'], failures=failures, inventoryErrors=inventory_errors,
                   rawExactMetrics={n: {k: rational(v) for k, v in ms.items()} for n, ms in results.items()}, cells=rows,
                   inference='Six pairs per cell, no confidence/causal/final acceptance claim; all cells reported separately, no pooled quantiles or hiding unfavorable cells.')
    write(root / 'summary.json', summary)
    return summary


def print_summary(result):
    """Allowlisted ordinary CI metrics only. Never print raw inputs, errors, paths, or commands."""
    print('NORMAL_COMPARISON ' + result['status'] +
          f" invalidTimingSlots={len(result['failures'])} inventoryErrors={len(result['inventoryErrors'])}")
    print(f"expectedSources official={CONTRACT['officialSource']} API={CONTRACT['officialApi']}"
          f" candidate={CONTRACT['devSource']} API={CONTRACT['devApi']} runtime={CONTRACT['runtime']}"
          f" allSampleIdentitiesVerified={result['status'] == 'DescriptiveOnly'}")
    safe_reasons = result.get('failureReasonCounts', {})
    if safe_reasons:
        print('failureReasons ' + ' '.join(f'{code}={safe_reasons[code]}' for code in sorted(REASON_CODES)
              if type(safe_reasons.get(code)) is int and safe_reasons[code] > 0))
    for row in result.get('census', {}).get('phases', []):
        if row.get('phase') not in PHASES:
            continue
        fields = ('expected','attempted','cleanExit','failedExit','launchFailed','invalidHost',
                  'missingHost','notAttemptedDeclared','missingUnclassified')
        require(all(type(row[k]) is int and 0 <= row[k] <= 60 for k in fields), 'Invalid census count')
        print(row['phase'] + ' ' + ' '.join(f'{key}={row[key]}' for key in fields))
        allowed = {slot['id'] for slot in frozen_plan() if slot['phase'] == row['phase']}
        failed = [slot_id for slot_id in row['failedSlots'] if slot_id in allowed]
        if failed:
            print('  failedSlots=' + ','.join(failed[:8]) + (f' (+{len(failed)-8} more)' if len(failed)>8 else ''))
    if result['status'] == 'DescriptiveOnly':
        for index, slot in enumerate(schedule(), start=12):
            metrics = result['rawExactMetrics'][slot['id']]
            print(f"ROUND launchIndex={index} slot={slot['id']}"
                  f" qps={metrics['qps']['decimal']:.6f}"
                  f" p99Us={metrics['p99Seconds']['decimal'] * 1000000:.6f}"
                  f" processWideAllocationBPerOp={metrics['allocationBytesPerOperation']['decimal']:.6f}")
    for row in result['cells']:
        mode = row['mode'] if row['mode'] in CONTRACT['modes'] else 'INVALID_MODE'
        count = row['items'] if row['items'] in CONTRACT['items'] else 'INVALID_COUNT'
        label = f"{mode} n={count} pairs={row['completePairs']}/6"
        if result['status'] != 'DescriptiveOnly':
            print(label + ' INVALID no-target-conclusion')
            continue
        ratio = lambda key: ','.join(f"{v['decimal']:.6f}" for v in row['pairedRatios'][key])
        print(label + f" qpsPairs=[{ratio('qps')}] p99Pairs=[{ratio('p99Seconds')}]")
        print(f"  medianQps={row['qpsMedianPairedRatio']['decimal']:.6f}"
              f" medianP99={row['p99MedianPairedRatio']['decimal']:.6f}"
              f" qpsTargetGapPP={row['qpsTargetGapPercentagePoints']['decimal']:.3f}"
              f" p99TargetGapPP={row['p99TargetGapPercentagePoints']['decimal']:.3f}"
              f" observedTargets={row['observedMedianTargetsReached']}")
    print('Normal-completion descriptive comparison only; no final release acceptance. Raw rounds and provenance retained only in the 3-day temporary CI artifact.')


if __name__ == '__main__':
    import argparse
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--input', type=Path, required=True)
    p.add_argument('--prepared', type=Path, required=True)
    p.add_argument('--runtime-dotnet', type=Path, required=True)
    p.add_argument('--runtime-archives', type=Path, required=True)
    a = p.parse_args()
    try:
        result = summarize(a.input.resolve(), a.prepared.resolve(), a.runtime_dotnet.resolve(), a.runtime_archives.resolve())
    except (OSError, ValueError, KeyError, TypeError) as error:
        if a.input.is_dir():
            write(a.input / 'validator-failure.json', {'status':'Invalid', 'error':repr(error)})
        print('NORMAL_COMPARISON Invalid validator-input-failure reason=' + failure_reason(error) + '; no target conclusion')
        raise SystemExit(1)
    print_summary(result)
    raise SystemExit(result['status'] == 'Invalid')
