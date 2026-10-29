"""Both-arm ordinary and actual dynamic-retirement success contract, bound to preparation."""
from pathlib import Path
import re
import hashlib
import json
from common import ARMS, CONTRACT
from .provenance import read_corpus
from .validate import require, uid_digest

SCENARIOS = ('RegisteredConsumerSuspensionsShouldNotAllocateAfterWarmup',
             'SuccessfulSuspensionsShouldNotAllocateAfterWarmup')
DYNAMIC_UID = 'SharpLink.IntegrationTests.RuntimeAssemblyIntegrationTests.1.1.DynamicStreamTerminalMustRetainPhysicalOwnersThroughSequentialDisposal(System.String).3.1.0'
CLOSURE_NAMES = frozenset(name + '.dll' for name in (
    'SharpLink.Abstractions', 'SharpLink.Client', 'SharpLink.Runtime', 'SharpLink.Sdk', 'SharpLink.Server',
    'Microsoft.Extensions.DependencyInjection', 'Microsoft.Extensions.DependencyInjection.Abstractions',
    'Microsoft.Extensions.Logging.Abstractions', 'System.Threading.RateLimiting'))
REQUIRED_ASSEMBLIES = CLOSURE_NAMES | frozenset({'SharpLink.UnitTests.dll',
    'allocation-probe/SharpLink.ReadOwnershipAllocationProbe.dll'})
INTEGRATION_ASSEMBLIES = CLOSURE_NAMES | frozenset({'SharpLink.IntegrationTests.dll'})


def expected_arm(arm):
    raw, ordinary, _ = read_corpus(Path(__file__).parent, arm)
    return {'arm': arm, 'source': CONTRACT[arm + 'Source'], 'tree': CONTRACT[arm + 'Tree'],
        'sdk': CONTRACT['sdk'], 'runtime': CONTRACT['runtime'], 'framework': 'Microsoft.NETCore.App',
        'rawCount': len(raw), 'rawUidSha256': uid_digest(raw),
        'ordinaryCount': len(ordinary), 'ordinaryUidSha256': uid_digest(ordinary),
        'executedCount': len(ordinary), 'executedUidSha256': uid_digest(ordinary),
        'passedCount': len(ordinary), 'failedCount': 0, 'skippedCount': 0,
        'cancelledCount': 0, 'timedOutCount': 0, 'flakyCount': 0,
        'allocationChildren': [{'scenario': scenario, 'runtime': CONTRACT['runtime'], 'architecture': 'X64',
            'executed': 1, 'passed': True} for scenario in SCENARIOS],
        'dynamicRetirement': {'uid': DYNAMIC_UID, 'runtime': CONTRACT['runtime'], 'executed': 1,
                              'passed': 1, 'failed': 0, 'skipped': 0, 'cancelled': 0, 'timedOut': 0, 'flaky': 0}}


def expected_record(tool_revision):
    return {'schema': 2, 'kind': 'both-arm-ordinary-and-dynamic-gate', 'status': 'passed',
            'toolRevision': tool_revision, 'arms': {arm: expected_arm(arm) for arm in ARMS}}


def check_hashes(hashes, required, exact=False):
    require(isinstance(hashes, dict) and (set(hashes) == required if exact else required <= set(hashes)),
            'unit closure incomplete')
    require(all(isinstance(name, str) and name and not Path(name).is_absolute() and '..' not in Path(name).parts
                and isinstance(value, str) and re.fullmatch('[0-9a-f]{64}', value)
                for name, value in hashes.items()), 'unit closure hash invalid')


def graph_digest(snapshot):
    check_hashes(snapshot, set())
    require(bool(snapshot), 'empty correctness graph snapshot')
    return hashlib.sha256(json.dumps(snapshot, sort_keys=True, separators=(',', ':')).encode()).hexdigest()


def validate_record(record, tool_revision=None, manifest=None):
    require(isinstance(record, dict), 'unit record is not an object')
    revision = record.get('toolRevision')
    require(isinstance(revision, str) and re.fullmatch('[0-9a-f]{40}', revision), 'bad unit tool revision')
    require(tool_revision is None or revision == tool_revision, 'unit record belongs to different tooling')
    expected = expected_record(revision)
    require(set(record) == set(expected), 'unit record keys mismatch')
    for key in set(expected) - {'arms'}:
        require(type(record[key]) is type(expected[key]) and record[key] == expected[key], 'unit record invariant mismatch: ' + key)
    require(isinstance(record['arms'], dict) and set(record['arms']) == set(ARMS), 'unit arm census mismatch')
    for arm in ARMS:
        data, invariant = record['arms'][arm], expected['arms'][arm]
        require(isinstance(data, dict) and set(data) == set(invariant) | {'assemblySha256', 'integrationAssemblySha256', 'frozenClosure', 'correctnessGraphSha256'},
                'unit arm record keys mismatch')
        for key, value in invariant.items():
            require(type(data[key]) is type(value) and data[key] == value, 'unit arm invariant mismatch: ' + arm + '/' + key)
        require(all(type(child.get('executed')) is int and child['executed'] == 1 and child.get('passed') is True
                    for child in data['allocationChildren']), 'bad allocation child witness fields')
        require(all(type(value) is int for key, value in data['dynamicRetirement'].items()
                    if key not in ('uid', 'runtime')), 'bad dynamic witness counters')
        require(isinstance(data['correctnessGraphSha256'], str) and re.fullmatch('[0-9a-f]{64}', data['correctnessGraphSha256']),
                'invalid correctness graph digest')
        check_hashes(data['assemblySha256'], REQUIRED_ASSEMBLIES)
        check_hashes(data['integrationAssemblySha256'], INTEGRATION_ASSEMBLIES)
        check_hashes(data['frozenClosure'], CLOSURE_NAMES, exact=True)
        for name, sha in data['frozenClosure'].items():
            require(data['assemblySha256'][name] == data['integrationAssemblySha256'][name] == sha,
                    'ordinary/integration frozen product bytes differ')
        if manifest is not None:
            prepared = manifest['arms'][arm]
            require(data['correctnessGraphSha256'] == graph_digest(prepared['correctness']['graphSnapshot']),
                    'gate/prepared full correctness graph differs')
            require(data['frozenClosure'] == prepared['closure'], 'gate/prepared closure differs')
            require(data['assemblySha256'] == prepared['correctness']['unitSnapshot'] and
                    data['integrationAssemblySha256'] == prepared['correctness']['integrationSnapshot'],
                    'gate/prepared correctness snapshots differ')
            require(prepared['source']['commit'] == data['source'] and prepared['source']['tree'] == data['tree'],
                    'gate/prepared source differs')
    return record
