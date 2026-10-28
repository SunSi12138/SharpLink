"""Synthetic offline receipts only. No CLR, sockets, workflow or network execution."""
import copy
import datetime
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

HERE = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location('cleanup_analysis_under_test', HERE / 'analyze.py')
a = importlib.util.module_from_spec(spec)
spec.loader.exec_module(a)
NOW = '2026-10-28T19:00:00+00:00'


def write(path, value):
    path.write_text(json.dumps(value, indent=2) + '\n')


def owner(scenario=0, active=False):
    no_token = dict(canBeCanceled=False, isCancellationRequested=False, sourceIdentity=None)
    connection = dict(canBeCanceled=True, isCancellationRequested=False, sourceIdentity=22)
    result = dict(clientType='SharpLink.Client.SharpLinkClient', serverType='SharpLink.Server.SharpLinkServer',
                  serverConnectionType='SharpLink.Server.ClientConnection', serverOwnedConnections=1,
                  clientPendingCalls=0, clientActiveCalls=0, clientActiveStreams=0, serverActiveCalls=0,
                  serverPendingAdmissions=0, lastAcceptedRequestId=scenario + 1, scenarioRequestId=scenario + 1,
                  requestIdSource='service-entry-last-accepted-single-caller', callMapEntryPresent=active,
                  connectionToken=connection, argumentEqualsConnectionToken=True, effectiveEqualsConnectionToken=True,
                  serviceArgumentToken=copy.deepcopy(connection), serviceEffectiveToken=copy.deepcopy(connection), callState=None)
    if active:
        result['callState'] = dict(requestId=scenario + 1, stateRequestId=scenario + 1, generation=1,
                                  stateType='SharpLink.Server.ServerCallState', reason='None', isAbandoned=False,
                                  hasInvocationCancellationSource=True, invocationToken=copy.deepcopy(connection),
                                  argumentEqualsInvocationToken=True, effectiveEqualsInvocationToken=True)
    return result


def make_stages(condition, fail_scenario=None, teardown=False):
    stages = []
    started = completed = active = 0
    elapsed_seconds = 0
    def add(name, scenario, detail=None):
        stages.append(dict(sequence=len(stages) + 1, timestampUtc=(a.utc(NOW) + datetime.timedelta(seconds=elapsed_seconds)).isoformat(), scenario=scenario, stage=name,
                           Started=started, Completed=completed, Active=active, clientLogical=0,
                           owners=owner(scenario, active > 0), detail=detail))
    add('diagnostic-start', 0, dict(condition=condition))
    count = fail_scenario or 4
    first_failure = f'original case {fail_scenario} cleanup failed' if fail_scenario else None
    for scenario in range(1, count + 1):
        detail = dict(tokenCanBeCanceled=condition == 'static-cancellable', explicitlyCancelled=False) if scenario == 4 else None
        add('before-invocation', scenario, detail)
        add('client-interceptor', scenario, dict(invocationTokenCanBeCanceled=scenario == 3 or condition != 'static-none', invocationTokenCancelled=False))
        add('service-created', scenario)
        started += 1
        active += 1
        add('service-started', scenario)
        if scenario != 1:
            add('first-item', scenario, dict(value=0))
        if scenario in (3, 4):
            add('service-before-infinite-wait', scenario)
        if scenario == 3:
            add('caller-cancelled', scenario)
            add('service-token-cancelled', scenario)
        if scenario != fail_scenario:
            active -= 1
            completed += 1
            add('service-finally', scenario)
        if scenario in (1, 2):
            add('expected-error', scenario, dict(count=scenario - 1, code='FailedPrecondition', Message='fixture-before-first-item' if scenario == 1 else 'fixture-after-first-item'))
        add('after-dispose', scenario, dict(seen=1, controlExplicitlyCancelled=False, controlTokenCancelled=False) if scenario == 4 else None)
        add('cleanup-start', scenario, dict(expected=scenario))
        if scenario == fail_scenario:
            elapsed_seconds += 15
            add('cleanup-failure', scenario, dict(expected=scenario, error=first_failure, timeout=True))
            add('first-failure', scenario, dict(error=first_failure))
        else:
            add('cleanup-pass', scenario, dict(expected=scenario))
    if first_failure is None:
        add('cold-checks-passed', 4)
    add('before-server-stop', count, dict(firstFailure=first_failure, readyConnectionsBeforeTeardown=1))
    if fail_scenario:
        add('service-token-cancelled', fail_scenario)
        active = 0
        completed = started
        add('service-finally', fail_scenario)
    add('after-server-stop', count, dict(serverStopCompleted=not teardown))
    add('after-client-stop', count, dict(clientStopCompleted=True))
    add('post-stop-cleanup', count, dict(disposeCompleted=True, teardownErrors=['serverStop: synthetic error'] if teardown else []))
    return stages


class SyntheticCampaign:
    def __init__(self, root, failures=None, teardown=False):
        self.root = root / 'evidence'
        self.prepared = root / 'prepared'
        self.runtime = root / 'runtime/dotnet'
        self.runtime.parent.mkdir()
        self.runtime.write_text('not executable; analysis must not launch this file')
        self.root.mkdir()
        self.prepared.mkdir()
        (self.root / 'runs').mkdir()
        self.policy = {name: dict(path=str(self.runtime.parent / 'shared/Microsoft.AspNetCore.App/10.0.14' / (name + '.dll')),
                                 sha256=sha, mvid=mvid, productVersion=a.identity.FRAMEWORK_PRODUCT_VERSION)
                       for name, (sha, mvid) in a.identity.FRAMEWORK.items()}
        self.manifest = dict(schema=1, sdk='10.0.102', status='build-only', contract=a.PRODUCT,
                             fixturePurpose='cleanup-diagnostic', toolRevision='f' * 40,
                             toolSourceHashes=a.common.source_inventory(HERE.parent / 'official-version-comparison'),
                             fixtureSourceHashes=a.common.source_inventory(HERE / 'fixture'), arms={})
        for arm in ('official', 'dev'):
            folder = self.prepared / arm
            folder.mkdir()
            for name in [f'SharpLink.{name}' for name in a.common.PRODUCTS] + ['PhysicalTcpProbe', 'ScalarStreamModule', *a.identity.FRAMEWORK]:
                (folder / (name + '.dll')).write_bytes((arm + name).encode())
            snapshot = a.common.inventory(folder)
            self.manifest['arms'][arm] = dict(snapshot=snapshot, generated={'generated.cs': 'a' * 64}, **{
                name: dict(dllSha256=snapshot['SharpLink.' + name + '.dll']) for name in a.common.PRODUCTS})
        write(self.prepared / 'prepared.json', self.manifest)
        write(self.root / 'prepared.json', self.manifest)
        write(self.root / 'protocol.json', a.PROTOCOL)
        write(self.root / 'schedule.json', a.schedule())
        write(self.root / 'framework-policy.json', self.policy)
        write(self.root / 'topology-selection.json', dict(mapping='# CPU,CORE,SOCKET,ONLINE\n0,0,0,Y\n1,1,0,Y\n', allowed=[0, 1], selected=[0, 1]))
        write(self.root / 'quiet-window.json', dict(noOtherClrBuildTest=True, reviewedToolCommit='f' * 40,
                                                  attempt=1, owner='root-authorized synthetic offline data', timestampUtc=NOW))
        observation = dict(utc=NOW, files={'/proc/stat': 'synthetic observation'})
        write(self.root / 'before.json', observation)
        write(self.root / 'after.json', observation)
        fail_rows = []
        for slot in a.schedule():
            scenario = (failures or {}).get(slot['id'])
            raw = self.raw(slot, scenario, teardown)
            write(self.raw_path(slot['id']), raw)
            host = dict(command=['taskset', '-c', '0,1', str(self.runtime),
                                 str(self.prepared / slot['arm'] / 'PhysicalTcpProbe.dll'), slot['condition'], str(self.raw_path(slot['id']))],
                        before=observation, after=observation, wallSeconds=16 if scenario else 1,
                        status='failed' if scenario or teardown else 'success', exitCode=1 if scenario or teardown else 0)
            write(self.host_path(slot['id']), host)
            self.write_stages(slot['id'], make_stages(slot['condition'], scenario, teardown))
            if scenario or teardown:
                fail_rows.append(dict(slot=slot['id'], status='failed', exitCode=1))
        write(self.root / 'failures.json', fail_rows)
        write(self.root / 'complete.json', dict(attemptedProcesses=6, timedProcesses=0, warmupSeconds=0,
                                               failureSlots=fail_rows, decisionBoundary=a.PROTOCOL['decisionBoundary']))

    def raw_path(self, slot):
        return self.root / 'runs' / (slot + '.json')

    def host_path(self, slot):
        return self.root / 'runs' / (slot + '.host.json')

    def stage_path(self, slot):
        return self.root / 'runs' / (slot + '.json.stages.jsonl')

    def write_stages(self, slot, stages):
        self.stage_path(slot).write_text(''.join(json.dumps(row) + '\n' for row in stages))

    def raw(self, slot, fail_scenario, teardown):
        arm, condition = slot['arm'], slot['condition']
        source = a.PRODUCT[f'{arm}Source']
        arm_name = 'official-2.0.3' if arm == 'official' else 'dev-57f06f1'
        snapshot = self.manifest['arms'][arm]['snapshot']
        loaded = [dict(name=name, location=str(self.prepared / arm / (name + '.dll')),
                       productVersion=a.PRODUCT[f'{arm}Version'] + '+' + source,
                       sha256=snapshot[name + '.dll'], mvid='synthetic-mvid')
                  for name in ['SharpLink.' + name for name in a.common.PRODUCTS] + ['PhysicalTcpProbe', 'ScalarStreamModule']]
        loaded += [dict(name=name, location=policy['path'], sha256=policy['sha256'], mvid=policy['mvid'], productVersion=policy['productVersion']) for name, policy in self.policy.items()]
        checks = dict(orderedValues=True, count=True)
        checks.update({key: True for i, key in enumerate(a.CHECKS, 1) if fail_scenario is None or i < fail_scenario})
        dynamic = condition == 'dynamic-none'
        return dict(schemaVersion=1, status='Failed' if fail_scenario or teardown else 'Passed', condition=condition,
                    arm=arm_name, sourceRevision=source, expectedSource=source, apiVersion=a.PRODUCT[f'{arm}Api'],
                    phase='cleanup-diagnostic-only', timingExecuted=False, operations=0, itemCount=1, workers=1,
                    cleanupGuardSeconds=15, warmupSeconds=0, requestedSeconds=0, originalRun='40747822819', originalAttempt=1,
                    preservesOriginalFailure=True, firstFailure=f'original case {fail_scenario} cleanup failed' if fail_scenario else None,
                    observationFailure=None, teardownErrors=['serverStop: synthetic error'] if teardown else [],
                    coldCheckResults=checks, serverStopCompleted=not teardown, clientStopCompleted=True, disposeCompleted=True,
                    finalCounters=dict(Started=fail_scenario or 4, Completed=fail_scenario or 4, Active=0, clientLogical=0),
                    runtimeVersion='10.0.14', processorCount=2, processorAffinity=3, sameVersionServerClient=True,
                    transport='tcp-loopback', connectionPoolMin=1, connectionPoolMax=1, readyConnections=1, requestTimeout='disabled',
                    heartbeatIntervalSeconds=3600, heartbeatTimeoutSeconds=7200, clientInterceptors=1, serverInterceptors=0,
                    tieredCompilation='default', tieredPgo='default', readyToRun='default',
                    frameworkLocations=[str(self.runtime.parent / 'shared/Microsoft.NETCore.App/10.0.14/System.Private.CoreLib.dll'),
                                        str(self.runtime.parent / 'shared/Microsoft.AspNetCore.App/10.0.14/Microsoft.AspNetCore.dll')],
                    binaryHashes=snapshot, loadedAssemblies=loaded,
                    generatedManifest=dict(arm=arm_name, sourceRevision=source, api=a.PRODUCT[f'{arm}Api'], protocol=2,
                                           abi='sharplink-2.0-api4-rpcchannel-codec-provider-v1' if arm == 'official' else 'sharplink-3.0-api5-owner-bound-unsafe-blit-v1',
                                           generator=a.PRODUCT[f'{arm}Version'], rpcAssemblyHash='synthetic-rpc-hash'),
                    mode='dynamic-intercepted' if dynamic else 'static-intercepted',
                    channelType='SharpLink.Client.SharpLinkModuleRpcChannel' if dynamic else 'SharpLink.Client.SharpLinkClient',
                    streamType='InvokeServerStreamingDeferred' if dynamic else 'Client+LogicalInvocationAsyncEnumerable`1',
                    wrapperTypes=['Client+LogicalInvocationAsyncEnumerable`1', 'Client+InterceptedAsyncEnumerable`1', 'SharpLink.Runtime.PooledAsyncStreamDispatcher`1'],
                    logicalPathProof=dict(generalLogicalObserved=True, physicalDispatcherObserved=True, observedBeforeDiagnosticCases=True,
                                          clientActivityListeners=False, clientCallsEnabled=False, telemetryWrapperObserved=False, completionOwnedObserved=False),
                    interceptorObserved=True, wireObservation=dict(status='not-observed', sourceInference='Source inference only; not wire measurement'),
                    stages=slot['id'] + '.json.stages.jsonl', timestampUtc=NOW)

    def analyze(self):
        return a.analyze(self.root, self.prepared, self.runtime)


class AnalysisTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.base = Path(self.temp.name)
        self.slot = 'static-none-official'

    def campaign(self, **kwargs):
        return SyntheticCampaign(self.base, **kwargs)

    def assert_invalid(self, campaign):
        result = campaign.analyze()
        self.assertEqual(result['status'], 'Invalid', {'errors': result['errors'], 'slots': [(r['slot'], r['status'], r.get('error')) for r in result['slots']]})
        self.assertEqual(len(result['slots']), 6)
        self.assertTrue((campaign.root / 'analysis.json').is_file())
        return result

    def test_all_six_successes_are_complete_without_hypothesis_requirement(self):
        result = self.campaign().analyze()
        self.assertEqual(result['status'], 'CompleteNoObservedFailures', result)
        self.assertEqual(len(result['slots']), 6)
        self.assertEqual(result['observedFunctionalFailures'], [])
        self.assertTrue(all(r['completedScenarios'] == [1, 2, 3, 4] for r in result['slots']))

    def test_original_symmetric_failure_remains_failed_after_shutdown_rescue(self):
        result = self.campaign(failures={'static-none-official': 4, 'static-none-dev': 4}).analyze()
        self.assertEqual(result['status'], 'CompleteObservedFailures', result)
        for row in result['slots'][:2]:
            self.assertEqual(row['functionalStatus'], 'Failed')
            self.assertEqual(row['completedScenarios'], [1, 2, 3])
            self.assertEqual(row['beforeServerStop']['Active'], 1)
            self.assertEqual(row['postStopCleanup']['Active'], 0)
            self.assertTrue(row['firstFailure'])

    def test_earlier_original_case_failure_is_retained_not_missing_case4_error(self):
        result = self.campaign(failures={self.slot: 2}).analyze()
        self.assertEqual(result['status'], 'CompleteObservedFailures', result)
        self.assertEqual(result['slots'][0]['attemptedScenarios'], [1, 2])
        self.assertEqual(result['slots'][0]['completedScenarios'], [1])

    def test_control_failure_is_outcome_neutral(self):
        result = self.campaign(failures={'static-cancellable-official': 4, 'dynamic-none-dev': 4}).analyze()
        self.assertEqual(result['status'], 'CompleteObservedFailures', result)

    def test_original_failure_disconnection_is_observed_not_automatically_invalid(self):
        campaign = self.campaign(failures={self.slot: 4})
        raw = a.read(campaign.raw_path(self.slot)); raw['readyConnections'] = 0
        write(campaign.raw_path(self.slot), raw)
        stages = make_stages('static-none', 4)
        next(stage for stage in stages if stage['stage'] == 'before-server-stop')['detail']['readyConnectionsBeforeTeardown'] = 0
        campaign.write_stages(self.slot, stages)
        self.assertEqual(campaign.analyze()['status'], 'CompleteObservedFailures')

    def test_teardown_failure_never_green(self):
        result = self.campaign(teardown=True).analyze()
        self.assertEqual(result['status'], 'CompleteObservedFailures', result)
        self.assertEqual(len(result['observedFunctionalFailures']), 6)

    def test_status_failure_masking_rejected(self):
        campaign = self.campaign(failures={self.slot: 4})
        data = a.read(campaign.raw_path(self.slot)); data['status'] = 'Passed'
        write(campaign.raw_path(self.slot), data)
        host = a.read(campaign.host_path(self.slot)); host.update(status='success', exitCode=0)
        write(campaign.host_path(self.slot), host)
        self.assert_invalid(campaign)

    def test_missing_original_first_failure_rejected(self):
        campaign = self.campaign(failures={self.slot: 4})
        data = a.read(campaign.raw_path(self.slot)); data['firstFailure'] = None
        write(campaign.raw_path(self.slot), data)
        self.assert_invalid(campaign)

    def test_missing_host_raw_or_stages_each_invalid(self):
        for kind in ('raw_path', 'host_path', 'stage_path'):
            with self.subTest(kind=kind), tempfile.TemporaryDirectory() as temp:
                campaign = SyntheticCampaign(Path(temp))
                getattr(campaign, kind)(self.slot).unlink()
                self.assert_invalid(campaign)

    def test_unexpected_slot_rejected(self):
        campaign = self.campaign()
        write(campaign.raw_path('extra-slot'), {})
        self.assert_invalid(campaign)

    def test_truncated_stage_file_rejected(self):
        campaign = self.campaign()
        path = campaign.stage_path(self.slot); path.write_text(path.read_text().rstrip('\n'))
        self.assert_invalid(campaign)

    def test_duplicate_json_key_and_nonfinite_rejected(self):
        for text in ['{"x":1,"x":2}', '{"x":NaN}', '{"x":Infinity}', '{"x":-Infinity}', '{"x":1e400}']:
            with self.subTest(text=text), self.assertRaises(ValueError):
                a.strict_json(text)
        campaign = self.campaign()
        path = campaign.raw_path(self.slot)
        path.write_text(path.read_text().replace('"schemaVersion": 1', '"schemaVersion": 1, "schemaVersion": 1'))
        self.assert_invalid(campaign)

    def test_timeout_or_start_failure_rejected_even_with_final_json(self):
        for error in ('TimeoutExpired', 'FileNotFoundError'):
            with self.subTest(error=error), tempfile.TemporaryDirectory() as temp:
                campaign = SyntheticCampaign(Path(temp), failures={self.slot: 4})
                host = a.read(campaign.host_path(self.slot)); host.update(exitCode=None, error=error)
                write(campaign.host_path(self.slot), host)
                self.assert_invalid(campaign)

    def test_stage_sequence_and_original_order_rejected(self):
        for mutation in ('duplicate', 'scenario', 'missing-service'):
            with self.subTest(mutation=mutation), tempfile.TemporaryDirectory() as temp:
                campaign = SyntheticCampaign(Path(temp))
                stages = make_stages('static-none')
                if mutation == 'duplicate':
                    stages[1]['sequence'] = 1
                elif mutation == 'scenario':
                    stages[1]['scenario'] = 2
                else:
                    next(s for s in stages if s['stage'] == 'service-started')['stage'] = 'unobserved'
                campaign.write_stages(self.slot, stages)
                self.assert_invalid(campaign)

    def test_completed_cancellation_callback_race_requires_actual_finally_cancellation(self):
        for observed_cancelled in (True, False):
            with self.subTest(observed_cancelled=observed_cancelled), tempfile.TemporaryDirectory() as temp:
                campaign = SyntheticCampaign(Path(temp))
                stages = make_stages('static-none')
                stages = [stage for stage in stages if not (stage['scenario'] == 3 and stage['stage'] == 'service-token-cancelled')]
                for i, stage in enumerate(stages, 1):
                    stage['sequence'] = i
                    if stage['scenario'] == 3 and stage['stage'] == 'service-finally':
                        stage['owners']['serviceEffectiveToken']['isCancellationRequested'] = observed_cancelled
                campaign.write_stages(self.slot, stages)
                result = campaign.analyze()
                self.assertEqual(result['status'], 'CompleteNoObservedFailures' if observed_cancelled else 'Invalid')

    def test_original_failure_before_item_cleanup_boundary_is_valid_partial_prefix(self):
        campaign = self.campaign(failures={self.slot: 4})
        raw = a.read(campaign.raw_path(self.slot)); raw['firstFailure'] = 'MoveNext failed before first item'
        write(campaign.raw_path(self.slot), raw)
        stages = make_stages('static-none', 4)
        removed = {'first-item', 'service-before-infinite-wait', 'after-dispose', 'cleanup-start', 'cleanup-failure'}
        stages = [stage for stage in stages if not (stage['scenario'] == 4 and stage['stage'] in removed)]
        for i, stage in enumerate(stages, 1):
            stage['sequence'] = i
            if stage['stage'] == 'first-failure':
                stage['detail']['error'] = raw['firstFailure']
            if stage['stage'] == 'before-server-stop':
                stage['detail']['firstFailure'] = raw['firstFailure']
        campaign.write_stages(self.slot, stages)
        self.assertEqual(campaign.analyze()['status'], 'CompleteObservedFailures')

    def test_failed_cleanup_requires_all_reached_boundary_receipts(self):
        for missing in ('client-interceptor', 'service-created', 'service-started', 'first-item',
                        'after-dispose', 'cleanup-start', 'cleanup-failure'):
            with self.subTest(missing=missing), tempfile.TemporaryDirectory() as temp:
                campaign = SyntheticCampaign(Path(temp), failures={self.slot: 4})
                stages = make_stages('static-none', 4)
                stages = [stage for stage in stages if not (stage['scenario'] == 4 and stage['stage'] == missing)]
                for i, stage in enumerate(stages, 1):
                    stage['sequence'] = i
                campaign.write_stages(self.slot, stages)
                self.assert_invalid(campaign)

    def test_failed_cleanup_preserves_timeout_flag_and_original_fifteen_seconds(self):
        for mutation in ('short-guard', 'false-timeout', 'after-first-failure'):
            with self.subTest(mutation=mutation), tempfile.TemporaryDirectory() as temp:
                campaign = SyntheticCampaign(Path(temp), failures={self.slot: 4})
                stages = make_stages('static-none', 4)
                failure = next(stage for stage in stages if stage['stage'] == 'cleanup-failure')
                if mutation == 'short-guard':
                    failure['timestampUtc'] = NOW
                elif mutation == 'false-timeout':
                    failure['detail'].update(timeout=False, error='System.Threading.Tasks.TaskCanceledException: expired')
                else:
                    i = stages.index(failure)
                    stages[i], stages[i + 1] = stages[i + 1], stages[i]
                    for n, stage in enumerate(stages, 1):
                        stage['sequence'] = n
                campaign.write_stages(self.slot, stages)
                self.assert_invalid(campaign)

    def test_corrupt_owner_missing_fields_and_map_identity_rejected(self):
        for mutation in ('missing', 'error', 'identity'):
            with self.subTest(mutation=mutation), tempfile.TemporaryDirectory() as temp:
                campaign = SyntheticCampaign(Path(temp))
                stages = make_stages('static-none')
                if mutation == 'missing':
                    del stages[0]['owners']['clientPendingCalls']
                elif mutation == 'error':
                    stages[0]['owners'] = {'observationError': 'failed reflection'}
                else:
                    next(s for s in stages if s['owners']['callState'])['owners']['callState']['stateRequestId'] = 999
                campaign.write_stages(self.slot, stages)
                self.assert_invalid(campaign)

    def test_arbitrary_caller_framework_policy_cannot_authorize_wrong_identity(self):
        campaign = self.campaign()
        policy = a.read(campaign.root / 'framework-policy.json')
        name = next(iter(policy)); policy[name]['sha256'] = '0' * 64
        write(campaign.root / 'framework-policy.json', policy)
        for slot in a.schedule():
            raw = a.read(campaign.raw_path(slot['id']))
            next(item for item in raw['loadedAssemblies'] if item['name'] == name)['sha256'] = '0' * 64
            write(campaign.raw_path(slot['id']), raw)
        self.assert_invalid(campaign)

    def test_loaded_framework_origin_hash_mvid_version_rejected(self):
        for key in ('location', 'sha256', 'mvid', 'productVersion'):
            with self.subTest(key=key), tempfile.TemporaryDirectory() as temp:
                campaign = SyntheticCampaign(Path(temp))
                raw = a.read(campaign.raw_path(self.slot))
                next(item for item in raw['loadedAssemblies'] if item['name'] in a.identity.FRAMEWORK)[key] = 'wrong'
                write(campaign.raw_path(self.slot), raw)
                self.assert_invalid(campaign)

    def test_product_source_snapshot_and_archive_drift_rejected(self):
        for mutation in ('source', 'binary', 'archive', 'purpose'):
            with self.subTest(mutation=mutation), tempfile.TemporaryDirectory() as temp:
                campaign = SyntheticCampaign(Path(temp))
                if mutation == 'source':
                    raw = a.read(campaign.raw_path(self.slot)); raw['sourceRevision'] = '0' * 40
                    write(campaign.raw_path(self.slot), raw)
                elif mutation == 'binary':
                    (campaign.prepared / 'official/SharpLink.Client.dll').write_bytes(b'drift')
                else:
                    archived = a.read(campaign.root / 'prepared.json')
                    archived['fixturePurpose' if mutation == 'purpose' else 'toolRevision'] = 'wrong'
                    write(campaign.root / 'prepared.json', archived)
                self.assert_invalid(campaign)

    def test_topology_affinity_timing_and_wire_claim_mutations_rejected(self):
        for key, value in [('processorAffinity', 1), ('processorCount', 1), ('cleanupGuardSeconds', 30),
                           ('operations', 1), ('timingExecuted', True), ('observationFailure', 'reflection error'),
                           ('wireObservation', dict(status='observed', sourceInference='source'))]:
            with self.subTest(key=key), tempfile.TemporaryDirectory() as temp:
                campaign = SyntheticCampaign(Path(temp))
                raw = a.read(campaign.raw_path(self.slot)); raw[key] = value
                write(campaign.raw_path(self.slot), raw)
                self.assert_invalid(campaign)

    def test_same_guest_core_cannot_pass_topology(self):
        campaign = self.campaign()
        topology = a.read(campaign.root / 'topology-selection.json')
        topology['mapping'] = '0,0,0,Y\n1,0,0,Y\n'
        write(campaign.root / 'topology-selection.json', topology)
        self.assert_invalid(campaign)

    def test_failure_inventory_must_retain_original_failed_slot(self):
        campaign = self.campaign(failures={self.slot: 4})
        write(campaign.root / 'failures.json', [])
        self.assert_invalid(campaign)

    def test_cli_failure_exit_policy(self):
        campaign = self.campaign(failures={self.slot: 4})
        argv = ['analyze.py', '--input', str(campaign.root), '--prepared', str(campaign.prepared), '--runtime-dotnet', str(campaign.runtime)]
        with patch('sys.argv', argv), patch('builtins.print'), self.assertRaises(SystemExit) as caught:
            a.main()
        self.assertEqual(caught.exception.code, 1)

    def test_no_performance_summary_and_source_inference_separate(self):
        result = self.campaign().analyze()
        for forbidden in ('qps', 'p99', 'target', 'ratio'):
            self.assertNotIn(forbidden, ' '.join(result).lower())
        self.assertIn('not observed', result['wireBoundary'])
        self.assertTrue(all(row['wireObservation']['status'] == 'not-observed' for row in result['slots']))


if __name__ == '__main__':
    unittest.main()
