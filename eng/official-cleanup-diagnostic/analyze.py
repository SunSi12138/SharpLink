#!/usr/bin/env python3
"""Offline, fail-closed six-slot observation audit. Never starts a runtime.

A complete original functional failure is valid observation, not a functional pass.
CLI exits 1 for Invalid OR CompleteObservedFailures to avoid a green result masking it.
"""
import argparse
import datetime
import importlib.util
import json
import math
from pathlib import Path
import re

ROOT = Path(__file__).resolve().parent


def load_module(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


common = load_module('cleanup_analysis_product_common', ROOT.parent / 'official-version-comparison/common.py')
identity = load_module('cleanup_analysis_fixed_identity', ROOT / 'identity.py')
PRODUCT = common.CONTRACT


def require(condition, message):
    if not condition:
        raise ValueError(message)


def strict_json(text):
    value = common.strict_json(text)
    def finite(item):
        if isinstance(item, float):
            require(math.isfinite(item), 'Nonfinite JSON number')
        elif isinstance(item, dict):
            for child in item.values():
                finite(child)
        elif isinstance(item, list):
            for child in item:
                finite(child)
    finite(value)
    return value


def read(path):
    return strict_json(path.read_text())


PROTOCOL = read(ROOT / 'protocol.json')
CHECKS = ('errorBeforeFirstItem', 'errorAfterFirstItem', 'cancellation', 'earlyBreak')
STAGES = {'diagnostic-start', 'before-invocation', 'client-interceptor', 'service-created',
          'service-started', 'service-before-infinite-wait', 'service-token-cancelled', 'service-finally',
          'first-item', 'expected-error', 'caller-cancelled', 'after-dispose', 'cleanup-start',
          'cleanup-pass', 'cleanup-failure', 'first-failure', 'cold-checks-passed',
          'before-server-stop', 'after-server-stop', 'after-client-stop', 'post-stop-cleanup'}
ERRORS = (OSError, ValueError, KeyError, TypeError, AttributeError, IndexError)


def schedule():
    return [dict(id=f'{condition}-{arm}', condition=condition, arm=arm)
            for index, condition in enumerate(('static-none', 'static-cancellable', 'dynamic-none'))
            for arm in (('dev', 'official') if index == 1 else ('official', 'dev'))]


def integer(value, name, minimum=0):
    require(type(value) is int and value >= minimum, f'Invalid integer {name}')


def utc(value):
    require(isinstance(value, str), 'Missing timestamp')
    instant = datetime.datetime.fromisoformat(value.replace('Z', '+00:00'))
    require(instant.utcoffset() == datetime.timedelta(0), 'Timestamp must be UTC')
    return instant


def token(value):
    require(isinstance(value, dict), 'Missing token owner receipt')
    require(type(value['canBeCanceled']) is bool and type(value['isCancellationRequested']) is bool,
            'Invalid token flags')
    source = value['sourceIdentity']
    require((type(source) is int) if value['canBeCanceled'] else source is None, 'Invalid token source identity')
    require(not value['isCancellationRequested'] or value['canBeCanceled'], 'Uncancellable token marked cancelled')


def validate_owners(owners):
    require(isinstance(owners, dict) and 'observationError' not in owners, 'Owner observation failed/missing')
    for key in ('serverOwnedConnections', 'clientPendingCalls', 'clientActiveCalls', 'clientActiveStreams',
                'serverActiveCalls', 'serverPendingAdmissions'):
        integer(owners[key], key)
    require(owners['serverOwnedConnections'] <= 1, 'More than one server connection')
    for key in ('clientType', 'serverType', 'serverConnectionType'):
        require(isinstance(owners[key], str) and owners[key], 'Missing owner type ' + key)
    for key in ('serviceArgumentToken', 'serviceEffectiveToken', 'connectionToken'):
        token(owners[key])
    for key in ('argumentEqualsConnectionToken', 'effectiveEqualsConnectionToken', 'callMapEntryPresent'):
        require(type(owners[key]) is bool, 'Missing owner equality/map receipt ' + key)
    for key in ('lastAcceptedRequestId', 'scenarioRequestId'):
        integer(owners[key], key)
    require(owners['requestIdSource'] in ('service-entry-last-accepted-single-caller',
                                         'latest-accepted-before-service-entry'), 'Unknown request identity source')
    state = owners['callState']
    if not owners['callMapEntryPresent']:
        require(state is None, 'Absent map entry has call-state data')
    else:
        require(isinstance(state, dict), 'Present map entry lacks call-state data')
        require(type(state['requestId']) is int and state['requestId'] == state['stateRequestId'] == owners['scenarioRequestId'],
                'Call-state request identity mismatch')
        integer(state['generation'], 'call generation')
        require(isinstance(state['stateType'], str) and state['stateType'], 'Missing call-state type')
        require(isinstance(state['reason'], str) and state['reason'], 'Missing cancellation reason')
        token(state['invocationToken'])
        for key in ('isAbandoned', 'hasInvocationCancellationSource', 'argumentEqualsInvocationToken', 'effectiveEqualsInvocationToken'):
            require(type(state[key]) is bool, 'Missing call-state field ' + key)
    # RuntimeHelpers hashes are diagnostic labels, not proof of equality. Keep the
    # actual CancellationToken.Equals booleans; do not infer equality from hash labels.


def validate_stages(stages, data, slot):
    require(isinstance(stages, list) and stages, 'No stage observations')
    before = []
    cleanup_pass = []
    first_failures = []
    names = []
    for index, stage in enumerate(stages, 1):
        require(type(stage['sequence']) is int and stage['sequence'] == index, 'Stage sequence missing/duplicate/out of order')
        utc(stage['timestampUtc'])
        integer(stage['scenario'], 'scenario')
        require(stage['scenario'] <= 4, 'Out-of-scope scenario')
        require(isinstance(stage['stage'], str) and stage['stage'] in STAGES, 'Unknown/missing stage name')
        names.append(stage['stage'])
        for key in ('Started', 'Completed', 'Active', 'clientLogical'):
            integer(stage[key], key)
        # Counters are separate atomic reads: do not invent a simultaneous snapshot
        # or reject transient Started/Completed/Active arithmetic races.
        validate_owners(stage['owners'])
        if stage['stage'] == 'before-invocation':
            before.append(stage['scenario'])
            if stage['scenario'] == 4:
                detail = stage['detail']
                require(detail['tokenCanBeCanceled'] is (slot['condition'] == 'static-cancellable') and
                        detail['explicitlyCancelled'] is False, 'Original/control case4 token changed')
        if stage['stage'] == 'client-interceptor':
            detail = stage['detail']
            require(type(detail['invocationTokenCanBeCanceled']) is bool and type(detail['invocationTokenCancelled']) is bool,
                    'Missing actual client invocation token receipt')
        if stage['stage'] in ('cleanup-start', 'cleanup-pass', 'cleanup-failure'):
            require(type(stage['detail']['expected']) is int and stage['detail']['expected'] == stage['scenario'], 'Cleanup guard targets wrong scenario')
        if stage['stage'] == 'cleanup-pass':
            require(stage['Started'] == stage['Completed'] == stage['scenario'] and stage['Active'] == stage['clientLogical'] == 0,
                    'Cleanup-pass masks nonzero owner/producer counters')
            cleanup_pass.append(stage['scenario'])
        if stage['stage'] == 'after-dispose' and stage['scenario'] == 4:
            require(stage['detail']['seen'] == 1 and stage['detail']['controlExplicitlyCancelled'] is False,
                    'Original early break changed or control explicitly cancelled')
            require(type(stage['detail']['controlTokenCancelled']) is bool, 'Missing control cancellation state')
        if stage['stage'] == 'first-failure':
            first_failures.append(stage)
    require(names[0] == 'diagnostic-start' and stages[0]['scenario'] == 0, 'Missing diagnostic start')
    require(before == list(range(1, len(before) + 1)) and len(before) <= 4, 'Original scenario invocation order changed')
    require(cleanup_pass == list(range(1, len(cleanup_pass) + 1)), 'Original cleanup pass order changed')
    checks = data['coldCheckResults']
    require(isinstance(checks, dict) and set(checks) <= {'orderedValues', 'count', *CHECKS}, 'Unknown correctness result')
    require(checks.get('orderedValues') is True and checks.get('count') is True and all(v is True for v in checks.values()),
            'Invalid initial/cold correctness receipt')
    completed = [i for i, key in enumerate(CHECKS, 1) if checks.get(key) is True]
    require(completed == list(range(1, len(completed) + 1)) and cleanup_pass == completed,
            'Cold-check results do not match original cleanup pass prefix')
    require(len(before) in (len(completed), len(completed) + 1), 'Missing or excess original invocations')
    for scenario in completed:
        entries = [entry for entry in stages if entry['scenario'] == scenario]
        sequence = [entry['stage'] for entry in entries]
        for required in ('before-invocation', 'client-interceptor', 'service-created', 'service-started',
                         'service-finally', 'after-dispose', 'cleanup-start', 'cleanup-pass'):
            require(sequence.count(required) == 1, 'Missing/duplicate completed-case receipt: ' + required)
        require(sequence.index('before-invocation') < sequence.index('client-interceptor') and
                sequence.index('service-created') < sequence.index('service-started') < sequence.index('service-finally') and
                sequence.index('after-dispose') < sequence.index('cleanup-start') < sequence.index('cleanup-pass'),
                'Completed case changed original event ordering')
        if scenario in (1, 2):
            require(sequence.count('expected-error') == 1, 'Expected remote error receipt missing')
        if scenario in (2, 3, 4):
            require(sequence.count('first-item') == 1, 'First-item receipt missing')
        if scenario == 3:
            # Disposing the registration in finally can race callback delivery.
            # Require actual cancellation, never CanBeCanceled alone; preserve the
            # absent callback rather than inventing a delivered notification.
            cancelled_at_finally = any(entry['stage'] == 'service-finally' and
                                      entry['owners']['serviceEffectiveToken']['isCancellationRequested'] is True
                                      for entry in entries)
            require(sequence.count('caller-cancelled') == 1 and
                    ('service-token-cancelled' in sequence or cancelled_at_finally),
                    'Original explicit cancellation observation missing')
        if scenario < len(before):
            previous_pass = next(entry['sequence'] for entry in entries if entry['stage'] == 'cleanup-pass')
            next_start = next(entry['sequence'] for entry in stages if entry['scenario'] == scenario + 1 and entry['stage'] == 'before-invocation')
            require(previous_pass < next_start, 'Next original case began before preceding cleanup passed')
    # A functional failure is not permission to omit the observations that must
    # already exist at its reached boundary. In particular, a cleanup timeout
    # means invocation, first-item/error, disposal and cleanup-start all happened.
    failure_sequence = first_failures[0]['sequence'] if first_failures else len(stages) + 1
    for scenario in before:
        entries = [entry for entry in stages if entry['scenario'] == scenario and entry['sequence'] < failure_sequence]
        sequence = [entry['stage'] for entry in entries]
        def one(name):
            require(sequence.count(name) == 1, f'Scenario {scenario}: missing/duplicate {name} receipt')
            return next(entry for entry in entries if entry['stage'] == name)
        def precedes(left, right):
            require(one(left)['sequence'] < one(right)['sequence'], f'Scenario {scenario}: {left} must precede {right}')
        one('before-invocation')
        for name in ('client-interceptor', 'service-created', 'service-started', 'first-item',
                     'expected-error', 'caller-cancelled', 'after-dispose', 'cleanup-start', 'cleanup-failure'):
            require(sequence.count(name) <= 1, f'Scenario {scenario}: duplicated {name}')
        if 'service-created' in sequence:
            precedes('before-invocation', 'client-interceptor')
            precedes('client-interceptor', 'service-created')
        if 'service-started' in sequence:
            precedes('service-created', 'service-started')
        if 'first-item' in sequence:
            precedes('service-started', 'first-item')
            require(one('first-item')['detail']['value'] == 0, 'Original first item changed')
        if 'expected-error' in sequence:
            precedes('service-started', 'expected-error')
            detail = one('expected-error')['detail']
            require(scenario in (1, 2) and detail['count'] == scenario - 1 and
                    detail['code'] == 'FailedPrecondition' and detail['Message'] ==
                    ('fixture-before-first-item' if scenario == 1 else 'fixture-after-first-item'),
                    'Expected remote error identity changed')
        if 'caller-cancelled' in sequence:
            require(scenario == 3, 'Explicit cancellation outside original case3')
            precedes('first-item', 'caller-cancelled')
        if 'after-dispose' in sequence:
            if scenario in (1, 2):
                precedes('expected-error', 'after-dispose')
            else:
                precedes('first-item', 'after-dispose')
            if scenario == 3:
                precedes('caller-cancelled', 'after-dispose')
        if 'cleanup-start' in sequence:
            precedes('after-dispose', 'cleanup-start')
            require(sequence.count('cleanup-pass') + sequence.count('cleanup-failure') == 1,
                    'Entered cleanup guard lacks exactly one original result receipt')
        if 'cleanup-failure' in sequence:
            require(scenario == before[-1] and data['firstFailure'] is not None and scenario not in completed,
                    'Cleanup failure was omitted or masked by a later success')
            precedes('cleanup-start', 'cleanup-failure')
            # This boundary requires actual server entry, not just a client error.
            one('client-interceptor'); one('service-created'); one('service-started')
            if scenario in (2, 3, 4):
                one('first-item')
            failure = one('cleanup-failure')
            require(type(failure['detail']['timeout']) is bool and isinstance(failure['detail']['error'], str) and failure['detail']['error'],
                    'Missing cleanup failure/timeout reason')
            if any(name in failure['detail']['error'] for name in ('TaskCanceledException', 'OperationCanceledException')):
                require(failure['detail']['timeout'] is True, 'Cleanup cancellation exception lacks expired guard receipt')
            if failure['detail']['timeout']:
                elapsed = (utc(failure['timestampUtc']) - utc(one('cleanup-start')['timestampUtc'])).total_seconds()
                require(elapsed >= 15, 'Cleanup timeout occurred before original 15-second guard')
            require(failure['sequence'] < failure_sequence, 'Cleanup failure not retained before first failure')
    cleanup_failures = [entry for entry in stages if entry['stage'] == 'cleanup-failure']
    require(len(cleanup_failures) <= 1 and all(entry['sequence'] < failure_sequence for entry in cleanup_failures),
            'Cleanup failure missing from original pre-failure prefix')
    stop_names = ('before-server-stop', 'after-server-stop', 'after-client-stop', 'post-stop-cleanup')
    require(all(names.count(name) == 1 for name in stop_names), 'Missing/duplicate stop receipts')
    stop_indices = [names.index(name) for name in stop_names]
    require(stop_indices == sorted(stop_indices), 'Shutdown observation order changed')
    before_stop = stages[stop_indices[0]]
    require(before_stop['detail']['firstFailure'] == data['firstFailure'], 'Original failure overwritten before stop')
    require(type(before_stop['detail']['readyConnectionsBeforeTeardown']) is int and
            before_stop['detail']['readyConnectionsBeforeTeardown'] == data['readyConnections'], 'Before-stop connection receipt mismatch')
    for name, flag in (('after-server-stop', 'serverStopCompleted'), ('after-client-stop', 'clientStopCompleted'),
                       ('post-stop-cleanup', 'disposeCompleted')):
        require(type(data[flag]) is bool and stages[names.index(name)]['detail'][flag] is data[flag], 'Shutdown flag mismatch')
    require(stages[names.index('post-stop-cleanup')]['detail']['teardownErrors'] == data['teardownErrors'], 'Teardown errors changed')
    require(all(index < stop_indices[0] for index, name in enumerate(names)
                if name in ('before-invocation', 'cleanup-start', 'cleanup-pass', 'cleanup-failure', 'cold-checks-passed', 'first-failure')),
            'Original-case result recorded only after server stop')
    if data['firstFailure'] is not None:
        require(isinstance(data['firstFailure'], str) and data['firstFailure'], 'Empty original failure')
        require(len(first_failures) == 1 and first_failures[0]['detail']['error'] == data['firstFailure'], 'Missing/masked first failure')
        require('cold-checks-passed' not in names and data['status'] == 'Failed', 'Original failure relabeled as pass')
    else:
        require(not first_failures and before == [1, 2, 3, 4] and completed == [1, 2, 3, 4] and names.count('cold-checks-passed') == 1,
                'Missing original success or first-failure receipt')
    if data['status'] == 'Passed':
        require(data['firstFailure'] is None and not data['teardownErrors'] and all(data[k] is True for k in
                ('serverStopCompleted', 'clientStopCompleted', 'disposeCompleted')), 'Passed masks cleanup/shutdown failure')
    else:
        require(data['firstFailure'] is not None or bool(data['teardownErrors']), 'Failed lacks retained failure reason')
    # Full per-stage facts, including pre-stop nonzero and post-stop zero, are retained.
    return dict(attemptedScenarios=before, completedScenarios=completed,
                originalFailureBeforeStop=data['firstFailure'] is not None,
                beforeServerStop=before_stop, postStopCleanup=stages[names.index('post-stop-cleanup')])


def validate_slot(data, host, stages, slot, prepared, manifest, runtime, policy, selected, root):
    arm, condition = slot['arm'], slot['condition']
    for key in ('schemaVersion', 'apiVersion', 'operations', 'itemCount', 'workers', 'cleanupGuardSeconds',
                'warmupSeconds', 'requestedSeconds', 'originalAttempt', 'processorCount', 'processorAffinity',
                'connectionPoolMin', 'connectionPoolMax', 'readyConnections', 'heartbeatIntervalSeconds',
                'heartbeatTimeoutSeconds', 'clientInterceptors', 'serverInterceptors'):
        integer(data[key], key)
    source = PRODUCT[f'{arm}Source']
    arm_name = {'official': 'official-2.0.3', 'dev': 'dev-57f06f1'}[arm]
    require(data['schemaVersion'] == 1 and data['status'] in ('Passed', 'Failed'), 'Unknown result schema/status')
    require(data['condition'] == condition and data['arm'] == arm_name, 'Wrong diagnostic slot')
    require(data['sourceRevision'] == data['expectedSource'] == source and data['apiVersion'] == PRODUCT[f'{arm}Api'], 'Wrong product source/API')
    require(data['phase'] == 'cleanup-diagnostic-only' and data['timingExecuted'] is False and type(data['operations']) is int and data['operations'] == 0,
            'Timed work is outside diagnostic scope')
    require(not any(k in data for k in ('durationTicks', 'elapsedTicks', 'operationsPerSecond', 'p99Us', 'samples')),
            'Unexpected timing evidence')
    require(data['itemCount'] == data['workers'] == 1 and data['cleanupGuardSeconds'] == 15 and
            data['warmupSeconds'] == data['requestedSeconds'] == 0, 'Original item/worker/cleanup guard changed')
    require(data['originalRun'] == '40747822819' and data['originalAttempt'] == 1 and data['preservesOriginalFailure'] is True,
            'Original failure campaign identity changed')
    require(data['observationFailure'] is None, 'Owner observation failure invalidates attribution')
    require(isinstance(data['teardownErrors'], list) and all(isinstance(e, str) and e for e in data['teardownErrors']), 'Malformed teardown errors')
    expected_exit = 0 if data['status'] == 'Passed' else 1
    require(host['status'] == ('success' if expected_exit == 0 else 'failed') and type(host['exitCode']) is int and host['exitCode'] == expected_exit and not host.get('error'),
            'Missing process, timeout/start failure, or result/exit mismatch')
    expected_command = ['taskset', '-c', ','.join(map(str, selected)), str(runtime),
                        str(prepared / arm / 'PhysicalTcpProbe.dll'), condition, str(root / 'runs' / (slot['id'] + '.json'))]
    require(host['command'] == expected_command, 'Executed command differs from bounded slot')
    for point in ('before', 'after'):
        utc(host[point]['utc'])
        require(isinstance(host[point]['files'], dict) and host[point]['files'], 'Missing host observations')
    require(type(host['wallSeconds']) in (int, float) and math.isfinite(host['wallSeconds']) and host['wallSeconds'] >= 0, 'Invalid host wall receipt')
    require(data['runtimeVersion'] == '10.0.14' and data['processorCount'] == 2 and data['processorAffinity'] == sum(1 << cpu for cpu in selected),
            'Actual runtime/affinity mismatch')
    require(data['sameVersionServerClient'] is True, 'Same-version endpoint receipt missing')
    require((data['transport'], data['connectionPoolMin'], data['connectionPoolMax'], data['requestTimeout'],
             data['heartbeatIntervalSeconds'], data['heartbeatTimeoutSeconds'], data['clientInterceptors'], data['serverInterceptors']) ==
            ('tcp-loopback', 1, 1, 'disabled', 3600, 7200, 1, 0), 'Original transport/path configuration changed')
    require(data['readyConnections'] in (0, 1) and (data['status'] != 'Passed' or data['readyConnections'] == 1),
            'Invalid before-stop connection count')
    require(all(data[k] == 'default' for k in ('tieredCompilation', 'tieredPgo', 'readyToRun')), 'Runtime tiering environment changed')
    expected_frameworks = [str(runtime.parent / 'shared/Microsoft.NETCore.App/10.0.14/System.Private.CoreLib.dll'),
                           str(runtime.parent / 'shared/Microsoft.AspNetCore.App/10.0.14/Microsoft.AspNetCore.dll')]
    require(data['frameworkLocations'] == expected_frameworks, 'Framework locations escape private runtime14')
    snapshot = manifest['arms'][arm]['snapshot']
    require(data['binaryHashes'] == {k: v for k, v in snapshot.items() if k.endswith('.dll')}, 'Executed own-arm DLL inventory mismatch')
    identity.validate_loaded(data['loadedAssemblies'], arm, prepared, snapshot, source, policy, runtime)
    generated = data['generatedManifest']
    expected_abi = {'official': 'sharplink-2.0-api4-rpcchannel-codec-provider-v1', 'dev': 'sharplink-3.0-api5-owner-bound-unsafe-blit-v1'}[arm]
    require((generated['arm'], generated['sourceRevision'], generated['api'], generated['protocol'], generated['abi'], generated['generator']) ==
            (arm_name, source, PRODUCT[f'{arm}Api'], 2, expected_abi, PRODUCT[f'{arm}Version']) and bool(generated['rpcAssemblyHash']), 'Own generator manifest mismatch')
    dynamic = condition == 'dynamic-none'
    require(data['mode'] == ('dynamic-intercepted' if dynamic else 'static-intercepted'), 'Wrong invocation mode')
    require(data['channelType'] == ('SharpLink.Client.SharpLinkModuleRpcChannel' if dynamic else 'SharpLink.Client.SharpLinkClient'), 'Wrong channel owner')
    require(('InvokeServerStreamingDeferred' in data['streamType']) if dynamic else ('+LogicalInvocationAsyncEnumerable`' in data['streamType']), 'Wrong stream owner')
    for needle in ('+LogicalInvocationAsyncEnumerable`', '+InterceptedAsyncEnumerable`', 'SharpLink.Runtime.PooledAsyncStreamDispatcher`'):
        require(any(needle in entry for entry in data['wrapperTypes']), 'Missing positive wrapper proof ' + needle)
    require(not any('CompletionOwned' in entry or '+TelemetryAsync' in entry for entry in data['wrapperTypes']), 'Unexpected wrapper path')
    proof = data['logicalPathProof']
    for key in ('generalLogicalObserved', 'physicalDispatcherObserved', 'observedBeforeDiagnosticCases'):
        require(proof[key] is True, 'Missing positive logical path receipt')
    for key in ('clientActivityListeners', 'clientCallsEnabled', 'telemetryWrapperObserved', 'completionOwnedObserved'):
        require(proof[key] is False, 'Telemetry/optimized path differs')
    require(data['interceptorObserved'] is True, 'Interceptor bypassed')
    wire = data['wireObservation']
    require(wire['status'] == 'not-observed' and isinstance(wire['sourceInference'], str) and wire['sourceInference'], 'Wire inference presented as measurement')
    require(data['stages'] == slot['id'] + '.json.stages.jsonl', 'Stage artifact reference differs')
    utc(data['timestampUtc'])
    result = validate_stages(stages, data, slot)
    final = data['finalCounters']
    for key in ('Started', 'Completed', 'Active', 'clientLogical'):
        integer(final[key], 'final ' + key)
    if data['status'] == 'Passed':
        require(final == {'Started': 4, 'Completed': 4, 'Active': 0, 'clientLogical': 0}, 'Passed has nonzero final cleanup')
    return result


def analyze(root, prepared, runtime):
    root, prepared, runtime = Path(root).resolve(), Path(prepared).resolve(), Path(runtime).resolve()
    errors, results = [], []
    manifest, policy, selected = {}, {}, []
    def attempt(kind, action):
        try:
            return action()
        except ERRORS as error:
            errors.append(dict(kind=kind, error=str(error)))
            return None
    def setup():
        actual = read(prepared / 'prepared.json')
        require(actual == read(root / 'prepared.json'), 'Archived preparation differs from actual prepared manifest')
        require(actual['schema'] == 1 and actual['sdk'] == '10.0.102' and actual['status'] == 'build-only' and
                actual['contract'] == PRODUCT and actual['fixturePurpose'] == 'cleanup-diagnostic', 'Wrong preparation contract/purpose')
        require(isinstance(actual['toolRevision'], str) and re.fullmatch('[0-9a-f]{40}', actual['toolRevision']), 'Missing full reviewed tool identity')
        require(actual['toolSourceHashes'] == common.source_inventory(ROOT.parent / 'official-version-comparison') and
                actual['fixtureSourceHashes'] == common.source_inventory(ROOT / 'fixture'), 'Reviewed preparation source inventory mismatch')
        for arm in ('official', 'dev'):
            snapshot = actual['arms'][arm]['snapshot']
            require(snapshot and common.inventory(prepared / arm) == snapshot, 'Prepared own-arm filesystem inventory drift: ' + arm)
            for name in common.PRODUCTS:
                require(actual['arms'][arm][name]['dllSha256'] == snapshot['SharpLink.' + name + '.dll'], 'Package-to-output DLL identity mismatch')
            require(actual['arms'][arm]['generated'], 'Own-arm generator output receipt missing')
        require(read(root / 'protocol.json') == PROTOCOL and read(root / 'schedule.json') == schedule(), 'Protocol/six-slot schedule drift')
        archived_policy = read(root / 'framework-policy.json')
        identity.validate_policy(archived_policy, runtime)
        topology = read(root / 'topology-selection.json')
        require(isinstance(topology['allowed'], list) and all(type(cpu) is int and cpu >= 0 for cpu in topology['allowed']) and
                topology['allowed'] == sorted(set(topology['allowed'])), 'Invalid allowed guest CPU inventory')
        cpus = common.select_cpus(topology['mapping'], topology['allowed'])
        require(cpus == topology['selected'], 'Selected pair is not deterministic distinct guest-core pair')
        quiet = read(root / 'quiet-window.json')
        require(quiet['noOtherClrBuildTest'] is True and quiet['reviewedToolCommit'] == actual['toolRevision'] and quiet['attempt'] == 1 and quiet['owner'], 'Quiet-window/review identity absent')
        utc(quiet['timestampUtc'])
        for name in ('before.json', 'after.json'):
            value = read(root / name)
            utc(value['utc'])
            require(isinstance(value['files'], dict) and value['files'], 'Missing campaign host receipt')
        completion = read(root / 'complete.json')
        require(completion['attemptedProcesses'] == 6 and completion['timedProcesses'] == 0 and completion['warmupSeconds'] == 0 and
                completion['decisionBoundary'] == PROTOCOL['decisionBoundary'], 'Incomplete/expanded campaign')
        require(not (root / 'ticks').exists() and not (root / 'measurement-start.json').exists(), 'Unexpected timing artifacts')
        return actual, archived_policy, cpus
    configured = attempt('campaign-integrity', setup)
    if configured:
        manifest, policy, selected = configured
    expected = {s['id'] for s in schedule()}
    for kind, pattern, suffix in [('raw', '*.json', '.json'), ('host', '*.host.json', '.host.json'), ('stages', '*.jsonl', '.json.stages.jsonl')]:
        files = list((root / 'runs').glob(pattern))
        if kind == 'raw':
            files = [p for p in files if not p.name.endswith('.host.json')]
        actual = {p.name.removesuffix(suffix) for p in files}
        if actual != expected:
            errors.append(dict(kind=kind + '-inventory', missing=sorted(expected - actual), unexpected=sorted(actual - expected)))
    expected_failures = []
    for slot in schedule():
        result = dict(slot=slot['id'], arm=slot['arm'], condition=slot['condition'], status='Invalid')
        results.append(result)
        try:
            raw = read(root / 'runs' / (slot['id'] + '.json'))
            host = read(root / 'runs' / (slot['id'] + '.host.json'))
            stage_path = root / 'runs' / (slot['id'] + '.json.stages.jsonl')
            text = stage_path.read_text()
            require(text.endswith('\n'), 'Truncated final stage record')
            stages = [strict_json(line) for line in text.splitlines()]
            result.update(functionalStatus=raw.get('status'), firstFailure=raw.get('firstFailure'),
                          coldChecks=raw.get('coldCheckResults'), finalCounters=raw.get('finalCounters'),
                          wireObservation=raw.get('wireObservation'), stageFacts=stages, host=host)
            require(configured is not None, 'Campaign integrity invalid; no slot accepted')
            receipts = validate_slot(raw, host, stages, slot, prepared, manifest, runtime, policy, selected, root)
            result.update(status='ValidObservation', **receipts)
            if host['status'] != 'success':
                expected_failures.append(dict(slot=slot['id'], status=host['status'], exitCode=host['exitCode']))
        except ERRORS as error:
            result['error'] = str(error)
    def completion_receipts():
        require(read(root / 'failures.json') == expected_failures and read(root / 'complete.json')['failureSlots'] == expected_failures,
                'Driver failure inventory differs from observed process exits')
    if all(row['status'] == 'ValidObservation' for row in results):
        attempt('failure-inventory', completion_receipts)
    invalid = bool(errors) or any(row['status'] == 'Invalid' for row in results)
    observed_failures = [row['slot'] for row in results if row.get('functionalStatus') == 'Failed']
    status = 'Invalid' if invalid else ('CompleteObservedFailures' if observed_failures else 'CompleteNoObservedFailures')
    summary = dict(schema=1, campaign=PROTOCOL['campaign'], status=status, errors=errors, slots=results,
                   observedFunctionalFailures=observed_failures, timedProcesses=0,
                   decisionBoundary='Owner/token/counter observations only; no causal, product-regression, performance or release acceptance claim.',
                   wireBoundary='Wire frames were not observed. Source inference is explicitly separate from actual token equality, cancellation, owner-map and counter receipts.',
                   exitPolicy='Exit 1 for Invalid or CompleteObservedFailures; exit 0 only for complete observations without original or teardown functional failures.')
    root.mkdir(parents=True, exist_ok=True)
    common.write(root / 'analysis.json', summary)
    return summary


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--input', type=Path, required=True)
    parser.add_argument('--prepared', type=Path, required=True)
    parser.add_argument('--runtime-dotnet', type=Path, required=True)
    args = parser.parse_args()
    result = analyze(args.input, args.prepared, args.runtime_dotnet)
    print(result['status'])
    raise SystemExit(0 if result['status'] == 'CompleteNoObservedFailures' else 1)


if __name__ == '__main__':
    main()
