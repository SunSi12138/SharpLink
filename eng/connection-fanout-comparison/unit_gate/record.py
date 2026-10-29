"""Bounded success-record contract for both the TCP driver and offline validator."""
from pathlib import Path
import re

from .provenance import SOURCE, TREE, read_corpus
from .validate import require, uid_digest

SCENARIOS = ('RegisteredConsumerSuspensionsShouldNotAllocateAfterWarmup',
             'SuccessfulSuspensionsShouldNotAllocateAfterWarmup')
REQUIRED_ASSEMBLIES = frozenset({'SharpLink.UnitTests.dll', 'SharpLink.Server.dll',
    'SharpLink.Runtime.dll', 'SharpLink.Client.dll', 'allocation-probe/SharpLink.ReadOwnershipAllocationProbe.dll'})


def expected_record(tool_revision):
    """Expected invariant fields only; callers must supply actual verified closure hashes."""
    raw, ordinary, _ = read_corpus(Path(__file__).parent)
    return {'schema': 1, 'kind': 'candidate-ordinary-unit-gate', 'status': 'passed',
        'candidateSource': SOURCE, 'candidateTree': TREE, 'toolRevision': tool_revision,
        'sdk': '10.0.102', 'runtime': '10.0.14', 'rawCount': len(raw), 'rawUidSha256': uid_digest(raw),
        'ordinaryCount': len(ordinary), 'ordinaryUidSha256': uid_digest(ordinary),
        'executedCount': len(ordinary), 'executedUidSha256': uid_digest(ordinary),
        'passedCount': len(ordinary), 'failedCount': 0, 'skippedCount': 0,
        'cancelledCount': 0, 'timedOutCount': 0, 'flakyCount': 0,
        'allocationChildren': [{'scenario': scenario, 'runtime': '10.0.14', 'architecture': 'X64',
            'executed': 1, 'passed': True} for scenario in SCENARIOS]}


def validate_record(record, tool_revision=None):
    require(isinstance(record, dict), 'unit record is not an object')
    revision = record.get('toolRevision')
    require(isinstance(revision, str) and re.fullmatch('[0-9a-f]{40}', revision), 'bad unit tool revision')
    require(tool_revision is None or revision == tool_revision, 'unit record belongs to different tooling')
    expected = expected_record(revision)
    require(set(record) == set(expected) | {'assemblySha256'}, 'unit record keys mismatch')
    for key, value in expected.items():
        require(type(record[key]) is type(value) and record[key] == value, 'unit record invariant mismatch: ' + key)
    children = record['allocationChildren']
    require(all(type(child.get('executed')) is int and child.get('executed') == 1 and
                child.get('passed') is True for child in children), 'bad allocation child witness fields')
    hashes = record['assemblySha256']
    require(isinstance(hashes, dict) and REQUIRED_ASSEMBLIES <= set(hashes), 'unit closure incomplete')
    require(all(isinstance(name, str) and name and not Path(name).is_absolute() and '..' not in Path(name).parts
                and isinstance(value, str) and re.fullmatch('[0-9a-f]{64}', value)
                for name, value in hashes.items()), 'unit closure hash invalid')
    return record
