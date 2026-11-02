"""Fail-closed classifiers for pinned TUnit 1.73.19 / MTP 2.5.1 schemas.
No SDK invocation. Event/collection evidence is the reviewed fixture control flow
plus exact count assertion and source stack, never invented Task sender telemetry.
"""
import json
from pathlib import Path
import re
import xml.etree.ElementTree as ET

NS = 'SharpLink.UnitTests.Server'
REGULAR = 'ServerTerminalSignalObservationTests'
POSITIVE = 'ServerTerminalSignalObservationPositiveControl'
START = 'FailedStartAndDisposeWithoutTerminalWaitShouldLeaveNoUnobservedSignal'
STOP = 'FailedStopAndDisposeWithoutTerminalWaitShouldLeaveNoUnobservedSignal'
LATE_START = 'FailedStartShouldPreserveFailureForLateWaiters'
LATE_STOP = 'FailedStopShouldPreserveEverySiblingForLateWaiters'
SUCCESS = 'SuccessfulStopShouldStillCompleteTerminalSignalSuccessfully'
POS_METHOD = 'UnobservedFaultMustBeCollectedAndReported'
EXPECTED = {START: 1, STOP: 1, LATE_START: 2, LATE_STOP: 2, SUCCESS: 1}
COUNTER = 'Expected 0 matching unobserved failure(s), received 1.'


def require(value, label):
    if not value:
        raise ValueError(label)


def unique_object(pairs):
    result = {}
    for key, value in pairs:
        require(key not in result, 'Duplicate JSON key')
        result[key] = value
    return result


def read_json(path):
    return json.loads(Path(path).read_text(encoding='utf-8-sig'), object_pairs_hook=unique_object,
                      parse_constant=lambda _: (_ for _ in ()).throw(ValueError('Nonfinite JSON number')))


def discovery(document, scope, source_file):
    require(document.get('schemaVersion') == 1, 'Unexpected discovery schema')
    tests = document.get('tests')
    require(isinstance(tests, list), 'Discovery tests missing')
    cls = POSITIVE if scope == 'positive' else REGULAR
    counts = {POS_METHOD: 1} if scope == 'positive' else ({scope: 1} if scope in (START, STOP) else EXPECTED)
    require(len(tests) == sum(counts.values()), 'Wrong discovery count')
    ledger = {}
    by_method = {}
    for test in tests:
        uid = test.get('uid')
        require(isinstance(uid, str) and uid and uid not in ledger, 'Invalid/duplicate actual UID')
        meta = test.get('type', {})
        require(meta.get('namespace') == NS and meta.get('typeName') in (cls, NS + '.' + cls), 'Wrong discovered class')
        require(meta.get('assemblyFullName') == 'SharpLink.UnitTests, Version=2.0.4.0, Culture=neutral, PublicKeyToken=null', 'Wrong discovered assembly')
        method = meta.get('methodName')
        require(method in counts and meta.get('methodArity') == 0, 'Unexpected discovered method')
        boolean = method in (LATE_START, LATE_STOP)
        require(meta.get('parameterTypeFullNames') == (['System.Boolean'] if boolean else []), 'Wrong discovered parameters')
        expected_return = 'System.Threading.Tasks.Task' if boolean or method == SUCCESS else 'System.Void'
        require(meta.get('returnTypeFullName') == expected_return, 'Wrong discovered return type')
        display = test.get('displayName')
        require(isinstance(display, str), 'Missing display name')
        prefixes = (method, cls + '.' + method, NS + '.' + cls + '.' + method)
        if boolean:
            require(display in tuple(prefix + '(' + value + ')' for prefix in prefixes for value in ('False', 'True')), 'Wrong boolean display value')
        else:
            require(display in prefixes, 'Wrong singleton display name')
        location = test.get('location', {})
        require(location.get('file') == str(source_file), 'Wrong discovered source file')
        require(type(location.get('lineStart')) is int and location['lineStart'] > 0, 'Wrong source line')
        require(type(location.get('lineEnd')) is int and location['lineEnd'] >= location['lineStart'], 'Wrong source span')
        ledger[uid] = test
        by_method.setdefault(method, []).append(display)
    require({m: len(v) for m, v in by_method.items()} == counts, 'Wrong method distribution')
    for method in (LATE_START, LATE_STOP):
        if method in by_method:
            require({re.search(r'\((False|True)\)$', display).group(1) for display in by_method[method]} == {'False', 'True'}, 'Missing boolean row')
    return ledger


def summary(total, failed):
    return dict(total=total, passed=total-failed, failed=failed, skipped=0, cancelled=0, timedOut=0, flaky=0)


def exact_summary(actual, expected):
    require(isinstance(actual, dict) and set(actual) == set(expected), 'Wrong summary fields')
    require(all(type(v) is int for v in actual.values()) and actual == expected, 'Wrong summary counts')


def source_lines(source):
    lines = Path(source).read_text().splitlines()
    def one(text):
        indices = [i+1 for i, line in enumerate(lines) if text in line]
        require(len(indices) == 1, 'Source counter/control location ambiguous')
        return indices[0]
    return [one('throw new Exception($"Expected {expected} matching unobserved failure(s), received {actual}.");'),
            one('observation.RequireCount(0);'),
            one('=> RequireNoUnobservedSignal(failDuringStart: true);'),
            one('=> RequireNoUnobservedSignal(failDuringStart: false);')]


def check_optional_filter(native, selector):
    if 'filter' in native:
        require(native['filter'] == selector, 'Present native filter differs from executed filter')


def check_publication(generation, seen):
    require(isinstance(generation, str) and re.fullmatch('[0-9a-f]{32}', generation) is not None, 'Invalid publication generation')
    require(generation not in seen, 'Repeated publication generation across isolated bodies')


def classify(native, junit, ledger, scope, source_file, runtime=None, reported_source_file=None):
    """scope baseline-start/baseline-stop/positive/fixed; caller separately gates exit/cleanup."""
    require(scope in ('baseline-start', 'baseline-stop', 'positive', 'fixed'), 'Unknown execution scope')
    reported_source_file = source_file if reported_source_file is None else reported_source_file
    baseline = scope.startswith('baseline-')
    require(len(ledger) == (7 if scope == 'fixed' else 1), 'Wrong scope ledger size')
    methods = [row['type']['methodName'] for row in ledger.values()]
    if scope == 'positive':
        require(methods == [POS_METHOD], 'Wrong positive target')
    if scope == 'fixed':
        require({m: methods.count(m) for m in methods} == EXPECTED, 'Wrong fixed method distribution')
    require(native.get('schemaVersion') == 1, 'Unexpected native schema')
    require(native.get('tunitVersion') == '1.73.19.0', 'Wrong actual TUnit version')
    require(native.get('assemblyName') == 'SharpLink.UnitTests', 'Wrong native assembly')
    actual_runtime = native.get('runtimeVersion', '')
    require(re.fullmatch(r'\.NET 10\.0\.\d+', actual_runtime) is not None, 'Unexpected actual runtime')
    require(runtime is None or runtime == actual_runtime, 'Variants used different actual runtime')
    require(re.fullmatch('[0-9a-f]{32}', native.get('publicationGeneration', '')) is not None, 'Missing publication identity')
    expected_summary = summary(len(ledger), 1 if baseline else 0)
    exact_summary(native.get('summary'), expected_summary)
    groups = native.get('groups')
    require(isinstance(groups, list) and len(groups) == 1, 'Unexpected native groups')
    group = groups[0]
    exact_summary(group.get('summary'), expected_summary)
    require(group.get('namespace') == NS, 'Wrong native group namespace')
    tests = group.get('tests')
    require(isinstance(tests, list) and len(tests) == len(ledger), 'Wrong actual test count')
    require(len({t.get('id') for t in tests}) == len(tests) and {t.get('id') for t in tests} == set(ledger), 'Wrong actual UID set')
    root = ET.fromstring(junit)
    require(root.tag == 'testsuites' and len(root) == 1 and root[0].tag == 'testsuite', 'Wrong JUnit root/suite')
    suite = root[0]
    cases = suite.findall('testcase')
    require(len(cases) == len(ledger), 'Wrong JUnit case count')
    require(all(c.tag in ('properties', 'testcase') for c in suite), 'Unexpected suite child')
    by_display = {case.get('name'): case for case in cases}
    require(len(by_display) == len(cases), 'Duplicate JUnit case')
    status_counts = {'failures': 0, 'errors': 0}
    for test in tests:
        row = ledger[test['id']]
        meta = row['type']
        require(test.get('displayName') == row['displayName'] and test.get('methodName') == meta['methodName'], 'Native test identity mismatch')
        require(test.get('className') == meta['typeName'] == group.get('className'), 'Native class mismatch')
        require(test.get('filePath') == str(reported_source_file) and test.get('lineNumber') == row['location']['lineStart'], 'Native source identity mismatch')
        require('skipReason' not in test and 'retryAttempt' not in test and 'attempts' not in test, 'Skip/retry field forbidden')
        case = by_display.get(row['displayName'])
        require(case is not None and case.get('classname') == meta['typeName'], 'JUnit identity mismatch')
        if baseline:
            method = START if scope == 'baseline-start' else STOP
            require(len(ledger) == 1 and test.get('methodName') == method, 'Wrong baseline target')
            require(test.get('status') in ('error', 'failed'), 'Baseline must fail, not pass/skip/timeout')
            tag = 'error' if test['status'] == 'error' else 'failure'
            require(len(case) == 1 and case[0].tag == tag, 'Wrong JUnit failure mapping')
            status_counts['errors' if tag == 'error' else 'failures'] += 1
            failure = test.get('exception')
            require(isinstance(failure, dict) and set(failure) == {'type', 'message', 'stackTrace'}, 'Unexpected full exception graph')
            require(failure['type'] == 'System.Exception' and failure['message'] == COUNTER, 'Not target counter1 failure')
            trace = failure['stackTrace']
            require(isinstance(trace, str), 'Missing actual source stack')
            frames = [line.strip() for line in trace.splitlines() if line.strip().startswith('at ')]
            numbers = source_lines(source_file)
            expected_frames = [NS + '.TerminalSignalObservation.RequireCount(Int32 expected)',
                               NS + '.' + REGULAR + '.RequireNoUnobservedSignal(Boolean failDuringStart)',
                               NS + '.' + REGULAR + '.' + method + '()']
            required_lines = [numbers[0], numbers[1], numbers[2 if scope == 'baseline-start' else 3]]
            require(len(frames) >= 3, 'Incomplete source stack')
            for frame, name, line in zip(frames[:3], expected_frames, required_lines):
                require(frame == f'at {name} in {reported_source_file}:line {line}', 'Wrong counter/collection source stack')
            require('INCONCLUSIVE' not in trace and '--->' not in trace, 'Unexpected exception topology')
            child = case[0]
            require(not list(child) and child.attrib == {'message': COUNTER, 'type': 'System.Exception'}, 'Wrong JUnit exception fields')
            expected_text = 'System.Exception: ' + COUNTER + '\n' + trace
            require((child.text or '').replace('\r\n', '\n') == expected_text.replace('\r\n', '\n'), 'Native/JUnit exception mismatch')
        else:
            require(test.get('status') == 'passed' and 'exception' not in test, 'Not an exception-free success')
            require(len(case) == 0, 'Passed JUnit case has error/skip/unknown children')
    for element in (root, suite):
        expected = {'tests': str(len(ledger)), 'failures': str(status_counts['failures']),
                    'errors': str(status_counts['errors']), 'skipped': '0', 'name': 'SharpLink.UnitTests'}
        require(all(element.get(k) == v for k, v in expected.items()), 'JUnit summary mismatch')
    return {'scope': scope, 'classification': 'EXPECTED_BASELINE_FAILURE' if baseline else ('CALIBRATION_PASS' if scope == 'positive' else 'FIXED_PASS'),
            'tests': len(ledger), 'eventCount': 1 if baseline or scope == 'positive' else None,
            'collectionEvidence': 'reviewed-source-control-flow+exact-counter-stack' if baseline else ('reviewed-source-success-path' if scope == 'positive' else 'reviewed-test-success-path'),
            'runtime': actual_runtime}
