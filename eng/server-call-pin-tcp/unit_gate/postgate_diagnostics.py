"""Bounded observation only: never changes a gate, process, input, or raw report.

Only fixed stages, numeric/boolean fields, recognized version values and SHA256
reach stdout. Unknown paths are represented by a path digest, never raw names.
Diagnostic failure cannot replace the original gate's result.
"""
import hashlib
import json
import re
from functools import wraps
from unit_gate.validate import strict_json

STAGES = frozenset(('ORDINARY_RUNTIME', 'VALIDATION_RESULT_SCHEMA', 'VALIDATION_RESULT_UID',
    'VALIDATION_RESULT_TEST_STATUS', 'VALIDATION_RESULT_COUNTERS', 'VALIDATION_RESULT_EXIT',
    'CHILD_WITNESSES', 'ORDINARY_SNAPSHOT', 'ORDINARY_GRAPH', 'DYNAMIC_DISCOVERY_PROCESS',
    'DYNAMIC_DISCOVERY_EXIT', 'DYNAMIC_DISCOVERY_REPORT', 'DYNAMIC_DISCOVERY_UID',
    'DYNAMIC_SOURCE_MAPPING', 'DYNAMIC_EXECUTION_PROCESS', 'DYNAMIC_EXECUTION_EXIT',
    'DYNAMIC_RESULT_REPORT', 'DYNAMIC_RESULT', 'DYNAMIC_SNAPSHOT', 'DYNAMIC_GRAPH',
    'FINAL_SOURCE_IDENTITY', 'ARM_POSTGATE_PASS', 'NOT_ENTERED', 'FINAL_PREPARED_SNAPSHOTS',
    'FINAL_TOOL_IDENTITY', 'FINAL_RECORD_BINDING', 'COMPLETE_POSTGATE_PASS'))
LAST_STAGE = {}
COUNTERS = ('total', 'passed', 'failed', 'skipped', 'cancelled', 'timedOut', 'flaky')
SCENARIOS = ('SuccessfulSuspensionsShouldNotAllocateAfterWarmup',
             'RegisteredConsumerSuspensionsShouldNotAllocateAfterWarmup')
MARKER = 'READ_OWNERSHIP_ALLOCATION_VERIFIED|'
MAX_HASH_DIFFERENCES = 8


def bounded_int(value):
    return value if type(value) is int and -1_000_000 <= value <= 1_000_000 else None


def version(value, prefix=''):
    return value if isinstance(value, str) and re.fullmatch(re.escape(prefix) + r'[0-9]{1,3}\.[0-9]{1,3}\.[0-9]{1,6}(?:\.[0-9]{1,6})?', value) else 'invalid'


def sha(value):
    return value if isinstance(value, str) and re.fullmatch('[0-9a-f]{64}', value) else 'invalid'


def observe(function):
    @wraps(function)
    def wrapper(self, *args, **kwargs):
        try:
            return function(self, *args, **kwargs)
        except Exception:
            # Never reveal an exception message, object, output, path or credential.
            self.emit('diagnosticAvailable', True, False)
    return wrapper


class PostGateDiagnostics:
    def __init__(self, arm):
        self.arm = arm if arm in ('baseline', 'candidate', 'both') else 'invalid'
        self.current = 'NOT_ENTERED'
        LAST_STAGE[self.arm] = self.current

    def emit(self, field, expected, actual, **identifiers):
        print('UNIT_POSTGATE ' + json.dumps(dict(arm=self.arm, stage=self.current,
            field=field, expected=expected, actual=actual, **identifiers),
            ensure_ascii=True, separators=(',', ':')), flush=True)

    def stage(self, stage):
        self.current = stage if stage in STAGES else 'NOT_ENTERED'
        LAST_STAGE[self.arm] = self.current
        self.emit('entered', True, True)

    @observe
    def process(self, stage, code, timed):
        self.stage(stage)
        self.emit('exit', 0, bounded_int(code))
        self.emit('timedOut', False, timed if type(timed) is bool else None)

    @observe
    def report(self, stage, report, count, code, timed):
        self.stage(stage)
        self.emit('reportObject', True, isinstance(report, dict))
        if not isinstance(report, dict):
            return
        dynamic = stage == 'DYNAMIC_RESULT'
        self.emit('runtime', '.NET 10.0.14', version(report.get('runtimeVersion'), '.NET '))
        self.emit('schema', 1, bounded_int(report.get('schemaVersion')))
        self.emit('tunitVersion', '1.73.19.0', version(report.get('tunitVersion')))
        self.emit('assemblyMatches', True, report.get('assemblyName') ==
                  ('SharpLink.IntegrationTests' if dynamic else 'SharpLink.UnitTests'))
        generation = report.get('publicationGeneration')
        self.emit('generationValid', True, isinstance(generation, str) and
                  re.fullmatch('[0-9a-f]{32}', generation) is not None)
        self.emit('exit', 0, bounded_int(code))
        self.emit('timedOut', False, timed if type(timed) is bool else None)
        summary = report.get('summary')
        self.emit('summaryKeysValid', True, isinstance(summary, dict) and set(summary) == set(COUNTERS))
        for key in COUNTERS:
            self.emit('summary.' + key, count if key in ('total', 'passed') else 0,
                      bounded_int(summary.get(key)) if isinstance(summary, dict) else None)
        groups = report.get('groups')
        self.emit('groupsValid', True, isinstance(groups, list) and bool(groups))
        invalid_shape = invalid_keys = invalid_values = inconsistent = 0
        group_differences = []
        for group in groups if isinstance(groups, list) else []:
            if not isinstance(group, dict) or not isinstance(group.get('tests'), list):
                invalid_shape += 1
                continue
            counters = group.get('summary')
            if not isinstance(counters, dict) or set(counters) != set(COUNTERS):
                invalid_keys += 1
                continue
            if any(type(v) is not int or v < 0 for v in counters.values()):
                invalid_values += 1
            n = len(group['tests'])
            if any(counters[key] != (n if key in ('total', 'passed') else 0) for key in COUNTERS):
                inconsistent += 1
                if len(group_differences) < 8:
                    group_differences.append((n, counters))
        for name, value in [('groupInvalidShape', invalid_shape), ('groupInvalidKeys', invalid_keys),
                            ('groupInvalidValues', invalid_values), ('groupInconsistentCounters', inconsistent)]:
            self.emit(name, 0, bounded_int(value))
        for ordinal, (n, counters) in enumerate(group_differences):
            for key in COUNTERS:
                self.emit('group.' + key, bounded_int(n) if key in ('total', 'passed') else 0,
                          bounded_int(counters.get(key)), groupDifference=ordinal)
        if dynamic:
            from unit_gate.record import DYNAMIC_UID
            tests = [test for group in groups if isinstance(group, dict) and isinstance(group.get('tests'), list)
                     for test in group['tests']] if isinstance(groups, list) else []
            self.emit('dynamicTestCount', 1, bounded_int(len(tests)))
            self.emit('dynamicExactUID', True, len(tests) == 1 and isinstance(tests[0], dict) and tests[0].get('id') == DYNAMIC_UID)
            self.emit('dynamicPassed', True, len(tests) == 1 and isinstance(tests[0], dict) and tests[0].get('status') == 'passed')

    def validation(self, stage, report, count, code, timed):
        self.stage('VALIDATION_' + stage)

    @observe
    def children(self, report):
        self.stage('CHILD_WITNESSES')
        expected = {f'SharpLink.UnitTests.Runtime.ReadOwnershipPipeReaderLifetimeTests.1.1.{name}'
                    '(System.Threading.CancellationToken).1.1.0': index for index, name in enumerate(SCENARIOS)}
        matches = [[], []]
        nonstring = unknown = total = 0
        for group in report.get('groups', []):
            for test in group.get('tests', []):
                output = test.get('output', '')
                if not isinstance(output, str):
                    nonstring += 1
                    continue
                for line in output.splitlines():
                    if not line.startswith(MARKER):
                        continue
                    total += 1
                    index = expected.get(test.get('id'))
                    if index is None:
                        unknown += 1
                        continue
                    try:
                        value = strict_json(line[len(MARKER):])
                    except (ValueError, TypeError):
                        value = None
                    matches[index].append((test.get('status') == 'passed', value))
        self.emit('outputNonStringCount', 0, bounded_int(nonstring))
        self.emit('markerCount', 2, bounded_int(total))
        self.emit('unknownMarkerUIDCount', 0, bounded_int(unknown))
        for index, rows in enumerate(matches):
            self.emit('child.markerCount', 1, bounded_int(len(rows)), child=index)
            for ordinal, (passed, value) in enumerate(rows[:2]):
                self.emit('child.testPassed', True, passed, child=index, marker=ordinal)
                self.emit('child.recordObject', True, isinstance(value, dict), child=index, marker=ordinal)
                if not isinstance(value, dict):
                    continue
                for field, wanted, actual in [
                    ('scenarioMatches', True, value.get('scenario') == SCENARIOS[index]),
                    ('runtime', '10.0.14', version(value.get('runtime'))),
                    ('architectureMatches', True, value.get('architecture') == 'X64'),
                    ('executedIsInteger', True, type(value.get('executed')) is int),
                    ('executed', 1, bounded_int(value.get('executed'))),
                    ('passedIsTrue', True, value.get('passed') is True)]:
                    self.emit('child.' + field, wanted, actual, child=index, marker=ordinal)

    @observe
    def snapshot(self, stage, expected, actual):
        self.stage(stage)
        added = set(actual) - set(expected)
        removed = set(expected) - set(actual)
        changed = {path for path in set(expected) & set(actual) if expected[path] != actual[path]}
        self.emit('snapshotFileCount', bounded_int(len(expected)), bounded_int(len(actual)))
        for name, values in [('added', added), ('removed', removed), ('changed', changed)]:
            self.emit('snapshot.' + name, 0, bounded_int(len(values)))
        differences = sorted(added | removed | changed)
        self.emit('snapshot.omittedDifferences', 0, bounded_int(max(0, len(differences) - MAX_HASH_DIFFERENCES)))
        for path in differences[:MAX_HASH_DIFFERENCES]:
            self.emit('snapshot.fileSHA256', sha(expected.get(path)), sha(actual.get(path)),
                      pathSHA256=hashlib.sha256(path.encode('utf-8')).hexdigest())

    @observe
    def discovery(self, report, uid):
        self.stage('DYNAMIC_DISCOVERY_UID')
        self.emit('discoverySchema', 1, bounded_int(report.get('schemaVersion')))
        tests = report.get('tests')
        self.emit('discoveryTestsList', True, isinstance(tests, list))
        rows = tests if isinstance(tests, list) else []
        self.emit('discoveryMalformedTests', 0, sum(not isinstance(t, dict) for t in rows))
        ids = [t.get('uid') for t in rows if isinstance(t, dict)]
        valid = [x for x in ids if isinstance(x, str) and x and not any(c in x for c in '\r\n\x00')]
        self.emit('discoveryInvalidUIDs', 0, bounded_int(len(ids) - len(valid)))
        self.emit('discoveryDuplicateUIDs', 0, bounded_int(len(valid) - len(set(valid))))
        self.emit('dynamicUIDCount', 1, bounded_int(sum(x == uid for x in ids)))

    @observe
    def mapping(self, selected):
        self.stage('DYNAMIC_SOURCE_MAPPING')
        self.emit('selectedCount', 1, bounded_int(len(selected)))
        match = len(selected) == 1 and isinstance(selected[0], dict)
        location = selected[0].get('location') if match else None
        path = location.get('file') if isinstance(location, dict) else None
        self.emit('sourceSuffixMatches', True, isinstance(path, str) and path.replace('\\', '/').endswith(
            '/test/SharpLink.IntegrationTests/RuntimeAssemblyIntegrationTests.PhysicalOwnership.cs'))


def emit_stage_failure(arm, error_type):
    safe_arm = arm if arm in ('baseline', 'candidate', 'both') else 'invalid'
    safe_error = error_type if error_type in ('GateError', 'OSError', 'ValueError', 'TypeError', 'KeyError',
        'CalledProcessError', 'TimeoutExpired', 'UnsafeProcessGroup') else 'other'
    print('UNIT_POSTGATE_FAILURE ' + json.dumps(dict(arm=safe_arm,
        stage=LAST_STAGE.get(safe_arm, 'NOT_ENTERED'), errorType=safe_error),
        separators=(',', ':')), flush=True)
