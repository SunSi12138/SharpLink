#!/usr/bin/env python3
"""Fail-closed discovery, executed UID, outcome, console and TRX census."""
import hashlib
import re
import xml.etree.ElementTree as ET

COUNT = 2214
EXECUTED_UID_SHA256 = '9f738e6133dab2a992ba86cc8b09f21198babacfeb13a1f3ff15e246551d58c3'
# These five [Explicit] process workers are discovered but not part of the normal
# unfiltered suite. Never select them: two are timing/TZ probes, three need drivers.
EXPLICIT_UIDS = frozenset('SharpLink.UnitTests.Validation.' + name for name in (
    'AdmissionDiagnosticsValidationProbe.1.1.Run.1.1.0',
    'CodecValidationProbe.1.1.DateTimeCrossZone.1.1.0',
    'CodecValidationProbe.1.1.DateTimeOffsetFragmentation.1.1.0',
    'DateTimeOffsetPerformanceValidationProbe.1.1.Run.1.1.0',
    'PendingLifecycleValidationProbe.1.1.Run.1.1.0',
))


def require(condition, message):
    if not condition:
        raise ValueError(message)


def inspect_build(text):
    require(re.findall(r'^\s*(\d+) Warning\(s\)\s*$', text, re.M) == ['0'], 'Expected one zero-warning build summary')
    require(re.findall(r'^\s*(\d+) Error\(s\)\s*$', text, re.M) == ['0'], 'Expected one zero-error build summary')
    require(not re.search(r'\bwarning\s+[A-Z]+\d+\b', text, re.I), 'Build warning diagnostic found')


def inspect_discovery(discovery):
    require(discovery['schemaVersion'] == 1, 'Unknown discovery schema')
    uids = [t['uid'] for t in discovery['tests']]
    require(len(uids) == COUNT + len(EXPLICIT_UIDS), 'Wrong discovered census')
    require(len(set(uids)) == len(uids), 'Duplicate discovery UID')
    require(EXPLICIT_UIDS <= set(uids), 'Explicit process-worker set changed')
    expected = set(uids) - EXPLICIT_UIDS
    digest = hashlib.sha256(('\n'.join(sorted(expected)) + '\n').encode()).hexdigest()
    require(digest == EXECUTED_UID_SHA256, 'Frozen ordinary-suite UID fingerprint changed')
    return expected


def inspect_census(discovery, report, console, trx):
    expected = inspect_discovery(discovery)
    require(report['schemaVersion'] == 1 and report['assemblyName'] == 'SharpLink.UnitTests', 'Wrong report schema/assembly')
    tests = [t for group in report['groups'] for t in group['tests']]
    ids = [t['id'] for t in tests]
    require(len(ids) == COUNT and len(set(ids)) == COUNT, 'Wrong/duplicate executed census')
    require(set(ids) == expected, 'Discovered and executed UID sets differ')
    require(all(t['status'] == 'passed' for t in tests), 'Non-passing case found')
    wanted = dict(total=COUNT, passed=COUNT, failed=0, skipped=0, cancelled=0, timedOut=0, flaky=0)
    require(report['summary'] == wanted, 'Full outcome summary is not exact all-pass/no-retry census')
    for key, value in dict(total=COUNT, failed=0, succeeded=COUNT, skipped=0).items():
        require(re.findall(r'^\s*' + key + r':\s*(\d+)\s*$', console, re.M) == [str(value)], 'Console census mismatch: ' + key)
    root = ET.fromstring(trx)
    ns = {'t': 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
    counters = root.find('t:ResultSummary/t:Counters', ns)
    require(counters is not None, 'Missing TRX counters')
    for key in ('total', 'executed', 'passed'):
        require(counters.get(key) == str(COUNT), 'TRX census mismatch: ' + key)
    for key, value in counters.attrib.items():
        if key not in ('total', 'executed', 'passed'):
            require(value == '0', 'Nonzero TRX outcome: ' + key)
    results = root.findall('t:Results/t:UnitTestResult', ns)
    require(len(results) == COUNT, 'Wrong TRX result count')
    require(len({r.get('testId') for r in results}) == COUNT, 'Duplicate TRX test ID')
    require(all(r.get('outcome') == 'Passed' for r in results), 'Non-passing TRX result')
    return {'discovered': len(discovery['tests']), 'explicitProcessWorkersNotSelected': sorted(EXPLICIT_UIDS),
            'executed': COUNT, 'passed': COUNT, 'failed': 0, 'skipped': 0, 'uidSetEqual': True}
