"""Fail-closed DiagnosticOnly result checks; no QPS/quantiles or performance promotion."""
import re
from pathlib import Path
from common import CONTRACT, SOURCE, TREE, inventory, old_identity, source_inventory

NORMAL_CHECKS = {'orderedValues', 'count', 'producerFinally', 'serverCleanup', 'clientCleanup', 'physicalDrain'}
RESULT_FIELDS = set('''schemaVersion purpose variant pid arm expectedSource sourceRevision apiVersion phase mode itemCount
workers generatedManifest normalCorrectness postNormalCorrectness beforeWorkDrain afterWorkDrain logicalPathProof
shutdownCompleted disposeCompleted transport sameVersionServerClient connectionPoolMin connectionPoolMax readyConnections
requestTimeout heartbeatIntervalSeconds heartbeatTimeoutSeconds serverInterceptors clientInterceptors channelType streamType
wrapperTypes interceptorObserved port warmupSeconds workSeconds workStartTick workEndTick markerStartTick markerStopTick
elapsedTicks stopwatchFrequency operations cpuTicks cpuTickUnit allocatedBytes gc0 gc1 gc2 validationFailures runtimeVersion
runtime os architecture processorCount processorAffinity serverGc tieredCompilation tieredPgo readyToRun frameworkLocations
loadedAssemblies frameworkAssemblies loadedAssemblyCount binaryHashes timestampUtc'''.split())


def require(condition, code):
    if not condition:
        raise ValueError(code)


def validate_result(data, variant, preparedPath, manifest, runtimePath, frameworkPolicy, archivePolicy, affinity, expectedPid):
    prepared = Path(preparedPath)
    runtime = Path(runtimePath)
    require(type(data) is dict and set(data) == RESULT_FIELDS, 'diagnostic_result_fields')
    require(variant in ('control', 'trace') and data['variant'] == variant, 'diagnostic_variant')
    require(type(expectedPid) is int and expectedPid > 0 and type(data['pid']) is int and data['pid'] == expectedPid,
            'diagnostic_pid')
    require(type(data['schemaVersion']) is int and data['schemaVersion'] == 1 and
            data['purpose'] == 'DiagnosticOnly' and data['phase'] == 'diagnostic-work', 'diagnostic_scope')
    require(data['arm'] == 'baseline-3d76a3a8' and data['sourceRevision'] == data['expectedSource'] == SOURCE,
            'diagnostic_source')
    require(manifest['contract'] == CONTRACT and manifest['sourceRevision'] == SOURCE and manifest['sourceTree'] == TREE and
            manifest['sdk'] == '10.0.102' and manifest['fixturePurpose'] == 'DiagnosticOnly' and
            manifest['status'] == 'build-only' and set(manifest['arms']) == {'baseline'}, 'diagnostic_prepared_manifest')
    require(manifest['toolSourceHashes'] == source_inventory(), 'diagnostic_tool_source_hashes')
    snapshot = manifest['arms']['baseline']['snapshot']
    require(inventory(prepared / 'baseline') == snapshot, 'diagnostic_snapshot_drift')
    expected_dlls = {key: value for key, value in snapshot.items() if key.endswith('.dll')}
    require(data['binaryHashes'] == expected_dlls, 'diagnostic_snapshot_hashes')
    require(all(type(data[key]) is int for key in ('apiVersion','itemCount','workers','processorCount','processorAffinity',
            'connectionPoolMin','connectionPoolMax','readyConnections','heartbeatIntervalSeconds','heartbeatTimeoutSeconds',
            'serverInterceptors','clientInterceptors','port','warmupSeconds','workSeconds','validationFailures')), 'diagnostic_integer_identity')
    require((data['apiVersion'], data['mode'], data['itemCount'], data['workers'], data['warmupSeconds'], data['workSeconds']) ==
            (5, 'static-intercepted', 100, 1, 3, 20), 'diagnostic_fixed_workload')
    require((data['transport'],data['connectionPoolMin'],data['connectionPoolMax'],data['readyConnections'],data['requestTimeout'],
             data['heartbeatIntervalSeconds'],data['heartbeatTimeoutSeconds'],data['serverInterceptors'],data['clientInterceptors']) ==
            ('tcp-loopback',1,1,1,'disabled',3600,7200,0,1), 'diagnostic_transport_configuration')
    require(0 < data['port'] <= 65535, 'diagnostic_port')
    require(data['runtimeVersion'] == '10.0.14' and data['architecture'] == 'X64' and data['processorCount'] == 2,
            'diagnostic_runtime')
    require(type(affinity) in (list, tuple) and len(affinity) == len(set(affinity)) == 2 and
            all(type(cpu) is int and cpu >= 0 for cpu in affinity) and
            data['processorAffinity'] == sum(1 << cpu for cpu in affinity), 'diagnostic_affinity')
    require(all(data[key] == 'default' for key in ('tieredCompilation','tieredPgo','readyToRun')), 'diagnostic_jit_settings')
    require(type(data['serverGc']) is bool, 'diagnostic_gc_identity')
    require(all(data[key] is True for key in ('shutdownCompleted','disposeCompleted','sameVersionServerClient','interceptorObserved')) and
            data['validationFailures'] == 0, 'diagnostic_shutdown_or_function')
    generated = data['generatedManifest']
    require(type(generated) is dict and set(generated) == {'arm','sourceRevision','api','protocol','abi','generator','rpcAssemblyHash'},
            'diagnostic_generated_manifest_shape')
    require(generated['arm'] == data['arm'] and generated['sourceRevision'] == SOURCE and
            type(generated['api']) is int and generated['api'] == 5 and type(generated['protocol']) is int and generated['protocol'] == 2 and
            generated['generator'] == '3.0.0' and generated['abi'] == 'sharplink-3.0-api5-owner-bound-unsafe-blit-v1' and
            type(generated['rpcAssemblyHash']) is str and re.fullmatch(r'[0-9a-f]{32}', generated['rpcAssemblyHash']) is not None and
            generated['rpcAssemblyHash'] != '0'*32, 'diagnostic_generated_identity')
    for key in ('normalCorrectness','postNormalCorrectness'):
        proof = data[key]
        require(type(proof) is dict and set(proof) == NORMAL_CHECKS and all(proof[name] is True for name in NORMAL_CHECKS),
                'diagnostic_normal_health')
    for key, ordinal in (('beforeWorkDrain', 1), ('afterWorkDrain', 2)):
        expected = dict(logicalInvocations=0, activeClientCalls=0, activeClientStreams=0, pendingCalls=0,
                        activeServerCalls=0, normalStarted=ordinal, normalCompleted=ordinal, normalActive=0)
        drain = data[key]
        require(type(drain) is dict and set(drain) == set(expected) and
                all(type(drain[name]) is int and drain[name] == value for name,value in expected.items()), 'diagnostic_drain')
    wrappers = data['wrapperTypes']
    require(type(wrappers) is list and all(type(value) is str and value for value in wrappers) and len(wrappers) == len(set(wrappers)),
            'diagnostic_wrapper_shape')
    for needle in ('+LogicalInvocationAsyncEnumerable`','InterceptedAsyncEnumerable','PooledAsyncStreamDispatcher'):
        require(any(needle in value for value in wrappers), 'diagnostic_wrapper_missing')
    require(not any('CompletionOwned' in value or '+TelemetryAsync' in value for value in wrappers) and
            data['channelType'] == 'SharpLink.Client.SharpLinkClient' and
            '+LogicalInvocationAsyncEnumerable`' in data['streamType'], 'diagnostic_static_path')
    require(data['logicalPathProof'] == dict(clientActivityListeners=False,clientCallsEnabled=False,telemetryWrapperObserved=False,
            completionOwnedObserved=False,generalLogicalObserved=True,physicalDispatcherObserved=True,observedBeforeWarmup=True) and
            all(type(value) is bool for value in data['logicalPathProof'].values()), 'diagnostic_telemetry_path')
    for key in ('workStartTick','workEndTick','markerStartTick','markerStopTick','elapsedTicks','stopwatchFrequency','operations'):
        require(type(data[key]) is int and data[key] > 0, 'diagnostic_positive_counter')
    require(data['markerStartTick'] <= data['workStartTick'] < data['workEndTick'] <= data['markerStopTick'] and
            data['workEndTick'] - data['workStartTick'] == data['elapsedTicks'] and
            data['elapsedTicks'] >= 20*data['stopwatchFrequency'], 'diagnostic_work_window')
    for key in ('cpuTicks','allocatedBytes','gc0','gc1','gc2'):
        require(type(data[key]) is int and data[key] >= 0, 'diagnostic_total_counter')
    require(data['cpuTickUnit'] == '100ns', 'diagnostic_cpu_unit')
    require(data['frameworkLocations'] == [str(runtime.parent/'shared/Microsoft.NETCore.App/10.0.14/System.Private.CoreLib.dll'),
            str(runtime.parent/'shared/Microsoft.AspNetCore.App/10.0.14/Microsoft.AspNetCore.dll')], 'diagnostic_framework_roots')
    old_identity.validate_loaded(data['loadedAssemblies'], 'baseline', prepared, expected_dlls, SOURCE, frameworkPolicy, runtime)
    old_identity.validate_framework_assemblies(data['frameworkAssemblies'], runtime, archivePolicy)
    names = [record['name'] for record in data['loadedAssemblies'] + data['frameworkAssemblies']]
    require(type(data['loadedAssemblyCount']) is int and data['loadedAssemblyCount'] == len(names) == len(set(names)), 'diagnostic_loaded_census')
    return data
