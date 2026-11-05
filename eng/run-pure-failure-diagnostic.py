#!/usr/bin/env python3
"""Six ordinary candidate UIDs, once each; raw reports remain private.

Extends, never replaces, the reviewed single-build/double-discovery custody
lane. This candidate validation cannot qualify the historical failed full suite, D2, or performance.
"""
import hashlib
import importlib.util
import json
import math
import os
from pathlib import Path
import re
import sys
import tempfile
import time
import xml.etree.ElementTree as ET

sys.dont_write_bytecode = True
ROOT = Path(__file__).resolve().parents[1]
ACTIVE = {'lane': None, 'errors': [], 'work': None}
SOURCE_TREES = {'src': 'bfc211191d91b5a77c6a8f0085a0c00a459c5583',
                'test': '585fa834a57e1aaf48f007eba23d7d37c7cf0705'}
HELPERS = {
    'eng/run-unit-census.py': 'a445781ba97a6c6a963faa9ba4d7e0bb890a96df6395e482ddb3948f8d12e4bf',
    'eng/unit-census-results.py': 'cbf6ce2096576dfe027b8e196144e9cba57860ad99be48c1f89d7ba73a71414e',
    'eng/run-unit-execution.py': 'ede7580fa1a9e37fba16322ef0158a9db8905bc6983c217c8bdad6eb5b784acf',
    'eng/unit-execution-results.py': '5fa888b3cc6dbc8d23b1282f4ce75440b65d4732927f08a8cfb3fa140b3d4dce',
    'eng/package-smoke-process.py': 'ba9f6ae43784ac752acdbfee1af60f33361ba76398a558948da721994449dda3',
    'eng/route59-results.py': 'eb95aaf6a6799b0c216ee3687d71e5e1aeb5651867d515e3a4db16561bfcda8b',
    'eng/retirement-results.py': '6774c8bdd8bd8b3f618c01aee34e429d8a3b8218cdb169e752cd81ab6a9e79eb',
    'eng/tests/test_unit_census.py': 'd12fbbc8e3634fac209aac866eab101d2938d4531ea27e04b9732fb1f7922da5',
    'eng/tests/test_unit_execution.py': 'c9fbae133b363f6ee0150036adfaba80528a3371ba73a5d6140423399672fa19',
    'eng/unit-census.json': 'ef9b5d58a7e4d2879782a93f431da18127860f8ba66bd68955a1681772b97cb5',
    'eng/rebase-three-fixture-census.py': 'e02e8d3d885d7be2735622679f1a0e9c5325b6aa13581ff7d929704e0055dbae',
    'eng/tests/test_three_fixture_census.py': '8a460a122010446f4136aa4fc61eefd59f4b8dd8d899f32cfe7b0def21a6ad4c',
    'eng/tests/test_pure_failure_diagnostic.py': 'e094542281f2c05d64ea0ed204f93accb6ef25ba210990550240c6c380ec8ec9',
    'eng/three-fixture-census-rebase.json': '38973de6106365868610b4509560ea5dab6a321dedfa2d084c24d1ac8bc11400',
    'eng/fixture-migration-baseline/unit-census.json': '2c34614a0bc918c48b860e795f3f331d0243065f05f0c9530e6aa0d430faeb66',
    'eng/fixture-migration-baseline/test/SharpLink.UnitTests/Protocol/ProtocolV2Tests.cs.txt': 'd2f827e0f45adae15e78c9db4d9c468de1d247c34eb60ed5e01296b59b584e73',
    'eng/fixture-migration-baseline/test/SharpLink.UnitTests/Runtime/SendPumpProgressIsolationTests.cs.txt': '67ffae89508dc0aba7f25f968bec4eb6fa91f997c9acc32784e9defc96a536a9',
}
PROTOCOL = 'test/SharpLink.UnitTests/Protocol/ProtocolV2Tests.cs'
PROGRESS = 'test/SharpLink.UnitTests/Runtime/SendPumpProgressIsolationTests.cs'
GRAMMAR = 'test/SharpLink.UnitTests/Protocol/Protocol3FrameAndHandshakeTests.cs'
FRAME = 'src/SharpLink.Runtime/ProtocolV2/ProtocolV2FrameCodec.cs'
PAYLOAD = 'src/SharpLink.Runtime/ProtocolV2/ProtocolV2PayloadCodec.cs'
SOURCE_PINS = {
    PROTOCOL: '736cce73a637487608d83a7db64c678473be51c8e79c978fdec05ddf3b77e384',
    PROGRESS: '0e125fe63685ff1c8f4c4d2a1226d8de434e30290e657104ee3389292f50dffb',
    GRAMMAR: '8804cf11f26fdd938c5ff786c154bf46fca05d6cca5af4e8a65d34d9e19f6fe5',
    FRAME: '737e171bed77f428e4e5d7a0128b8344ae0886cf70300775c9f7df823f588d0e',
    PAYLOAD: 'c3a94f11332f1026f10c72fe20b34666704ef0996e2c7dbdd9073ce43e94c732',
    'src/SharpLink.Runtime/ProtocolViolationReason.cs': 'be29a593585933e97a233b9a5500affdee02a7faf27d2bedd8682b5c61630bc1',
}
# Fixed source identities, never names or argument values copied from reports.
CASES = (
    (PROTOCOL, 'SharpLink.UnitTests.Protocol', 'ProtocolV2Tests', 'WriterAndParserShouldRoundTripFrame', 13, 28),
    (PROTOCOL, 'SharpLink.UnitTests.Protocol', 'ProtocolV2Tests', 'HeaderWithEveryByteInSeparateSegmentShouldParse', 50, 68),
    (PROTOCOL, 'SharpLink.UnitTests.Protocol', 'ProtocolV2Tests', 'RawFrameWriterAndTokenShouldRemainInternalImplementationDetails', 31, 35),
    (PROGRESS, 'SharpLink.UnitTests.Runtime', 'SendPumpProgressIsolationTests', 'ProgressFlagDoesNotGrowOwnedFrame', 656, 678),
    (GRAMMAR, 'SharpLink.UnitTests.Protocol', 'Protocol3FrameAndHandshakeTests', 'OnlyNewMagicAndPositive63CorrelationAreAccepted', 13, 24),
    (GRAMMAR, 'SharpLink.UnitTests.Protocol', 'Protocol3FrameAndHandshakeTests', 'CancellationHasExactlyOneKnownReasonByte', 27, 33),
)
# Candidate-only literal vocabulary. Each exact source line is verified by controls.
ASSERTION_LITERALS = {
    0: (('frame parsed', 'frame-parsed', PROTOCOL, 22),
        ('frame type', 'frame-type', PROTOCOL, 23),
        ('frame flags', 'frame-flags', PROTOCOL, 24),
        ('maximum positive 63-bit request ID', 'maximum-positive-63-bit-request-id', PROTOCOL, 25),
        ('payload length', 'payload-length', PROTOCOL, 26),
        ('frame consumed', 'frame-consumed', PROTOCOL, 27)),
    1: (('frame spans separate byte segments', 'separate-byte-segments', PROTOCOL, 59),
        ('segmented header', 'segmented-header', PROTOCOL, 60),
        ('segmented frame type', 'segmented-frame-type', PROTOCOL, 61),
        ('segmented frame flags', 'segmented-frame-flags', PROTOCOL, 62),
        ('segmented request ID', 'segmented-request-id', PROTOCOL, 63),
        ('segmented cancellation reason length', 'segmented-reason-length', PROTOCOL, 64),
        ('segmented cancellation reason', 'segmented-reason', PROTOCOL, 66),
        ('segmented frame consumed', 'segmented-frame-consumed', PROTOCOL, 67)),
    3: (('the comparison must omit exactly the protocol-progress bool field', 'comparison-omits-only-progress-bool', PROGRESS, 664),
        ('the comparison must mirror every current field in declaration order except the progress flag', 'comparison-mirrors-current-field-order', PROGRESS, 672)),
}
EXCEPTION_CATEGORIES = {
    'System.Exception': 'exception',
    'SharpLink.Runtime.SharpLinkProtocolViolationException': 'protocol-violation',
    'SharpLink.Abstractions.SharpLinkException': 'sharplink-exception',
    'System.InvalidOperationException': 'invalid-operation',
    'System.ArgumentException': 'argument',
    'System.ArgumentNullException': 'argument-null',
    'System.ArgumentOutOfRangeException': 'argument-range',
    'System.NullReferenceException': 'null-reference',
    'System.TypeInitializationException': 'type-initialization',
    'System.TypeLoadException': 'type-load',
    'System.MissingMethodException': 'missing-method',
    'System.MethodAccessException': 'method-access',
    'System.IO.FileNotFoundException': 'file-not-found',
    'System.IO.FileLoadException': 'file-load',
    'System.BadImageFormatException': 'bad-image',
    'System.AggregateException': 'aggregate',
    'TUnit.Assertions.Exceptions.AssertionException': 'assertion',
}
CODES = frozenset(('PD_HELPERS', 'PD_SOURCE', 'PD_SDK', 'PD_RUNTIME', 'PD_SELECTION',
    'PD_PREPARATION', 'PD_COMMANDS', 'PD_ARGV', 'PD_SHAPE', 'PD_LIMIT', 'PD_EXCEPTION',
    'PD_JUNIT', 'PD_REPORTS', 'PD_OWNER', 'PD_EXIT', 'PD_RETRY', 'PD_CASE_FAILED',
    'PD_INCOMPLETE', 'PD_BUDGET', 'PD_PUBLIC'))
RUNTIMES = [{'framework': 'Microsoft.AspNetCore.App', 'version': '10.0.14'},
            {'framework': 'Microsoft.NETCore.App', 'version': '10.0.14'}]


def load(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    obj = importlib.util.module_from_spec(spec); spec.loader.exec_module(obj)
    return obj


def verify_source(r, root):
    for name, digest in SOURCE_PINS.items():
        r.require(r.sha(root / name) == digest, 'PD_SOURCE')


def verify_isolated_sdk(lane, census):
    r = lane.r
    dotnet = Path(census.shutil.which('dotnet', path=lane.env['PATH']) or '').resolve()
    root = dotnet.parent
    temporary = os.environ.get('RUNNER_TEMP')
    r.require(type(temporary) is str and Path(temporary).is_absolute() and
              root == Path(temporary) / 'three-fixture-dotnet' and dotnet.name == 'dotnet' and dotnet.is_file(), 'PD_SDK')
    r.legacy.directory_identity_strict(root)
    for name in ('DOTNET_ROOT', 'DOTNET_ROOT_X64'):
        if name in lane.env: r.require(Path(lane.env[name]) == root, 'PD_SDK')
    r.require(lane.env.get('DOTNET_MULTILEVEL_LOOKUP') == '0', 'PD_SDK')


def selected(r, ordinary):
    output = []
    for source, namespace, cls, method, start, end in CASES:
        matches = [(uid, row) for uid, row in ordinary.items()
                   if row['type']['namespace'] == namespace and
                   row['type']['typeName'] == cls and
                   row['type']['methodName'] == method]
        r.require(len(matches) == 1, 'PD_SELECTION')
        uid, row = matches[0]
        r.require(row['uid'] == uid and type(uid) is str and uid and
                  not uid.startswith(('-', '@')) and len(uid) <= 4096 and
                  not any(ord(c) < 32 for c in uid), 'PD_SELECTION')
        loc = row['location']
        r.require(set(loc) == {'file', 'lineStart', 'lineEnd'} and loc['file'] == '/_/' + source and
                  type(loc['lineStart']) is int and start - 1 <= loc['lineStart'] <= start and
                  type(loc['lineEnd']) is int and loc['lineEnd'] == end, 'PD_SELECTION')
        output.append((uid, row))
    r.require(len({uid for uid, _ in output}) == 6, 'PD_SELECTION')
    return output


def keys(r, obj, required, optional=()):
    r.require(type(obj) is dict and set(required) <= set(obj) <= set(required) | set(optional), 'PD_SHAPE')


def string(r, value):
    r.require(type(value) is str and len(value) <= 1024 * 1024, 'PD_SHAPE')


def number(r, value):
    r.require(type(value) in (int, float) and math.isfinite(value), 'PD_SHAPE')


def strings(r, values):
    r.require(type(values) is list and len(values) <= 1024, 'PD_LIMIT')
    for value in values: string(r, value)


def pairs(r, values):
    r.require(type(values) is list and len(values) <= 1024, 'PD_LIMIT')
    for value in values:
        keys(r, value, ('key', 'value'))
        for item in value.values(): string(r, item)


def exception_shape(r, exception, depth=0):
    r.require(depth < 16, 'PD_LIMIT')
    keys(r, exception, ('type', 'message'), ('stackTrace', 'innerException'))
    for name in ('type', 'message', 'stackTrace'):
        if name in exception: string(r, exception[name])
    if 'innerException' in exception: exception_shape(r, exception['innerException'], depth + 1)


def native_shape(r, native):
    required = ('schemaVersion', 'publicationGeneration', 'assemblyName', 'machineName', 'timestamp',
                'tunitVersion', 'operatingSystem', 'runtimeVersion', 'totalDurationMs', 'summary', 'groups')
    optional = ('filter', 'artifactUrl', 'commitSha', 'branch', 'pullRequestNumber', 'repositorySlug', 'sourceLinks', 'spans')
    keys(r, native, required, optional)
    for name in set(required + optional) - {'schemaVersion', 'totalDurationMs', 'summary', 'groups', 'sourceLinks', 'spans'}:
        if name in native: string(r, native[name])
    number(r, native['totalDurationMs'])
    if 'sourceLinks' in native:
        keys(r, native['sourceLinks'], ('lineUrl', 'rangeUrl'), ('rawUrl',))
        for value in native['sourceLinks'].values(): string(r, value)
    # Exact pinned optional tracing schema; these fields are never projected.
    spans = native.get('spans', [])
    r.require(type(spans) is list and len(spans) <= 4096, 'PD_LIMIT')
    for span in spans:
        keys(r, span, ('traceId', 'spanId', 'name', 'source', 'kind', 'startTimeMs', 'durationMs', 'status'),
             ('parentSpanId', 'spanType', 'statusMessage', 'tags', 'events', 'links'))
        for name, value in span.items():
            if name in ('startTimeMs', 'durationMs'): number(r, value)
            elif name == 'tags': pairs(r, value)
            elif name not in ('events', 'links'): string(r, value)
        for field in ('events', 'links'):
            values = span.get(field, []); r.require(type(values) is list and len(values) <= 1024, 'PD_LIMIT')
            for value in values:
                if field == 'links':
                    keys(r, value, ('traceId', 'spanId'))
                    for item in value.values(): string(r, item)
                else:
                    keys(r, value, ('name', 'timestampMs'), ('tags',))
                    string(r, value['name']); number(r, value['timestampMs'])
                    if 'tags' in value: pairs(r, value['tags'])
    r.require(type(native['groups']) is list and len(native['groups']) == 1, 'PD_SHAPE')
    group = native['groups'][0]; keys(r, group, ('namespace', 'className', 'summary', 'tests'))
    r.require(type(group['tests']) is list and len(group['tests']) == 1, 'PD_SHAPE')
    test = group['tests'][0]
    required = ('id', 'displayName', 'methodName', 'className', 'status', 'durationMs', 'filePath', 'lineNumber', 'endLineNumber')
    optional = ('startTime', 'endTime', 'exception', 'output', 'errorOutput', 'categories', 'customProperties',
                'sourceRelativePath', 'skipReason', 'retryAttempt', 'attempts', 'traceId', 'spanId', 'additionalTraceIds')
    keys(r, test, required, optional)
    r.require(not {'retryAttempt', 'attempts'} & set(test), 'PD_RETRY')
    for name in set(required + optional) - {'durationMs', 'lineNumber', 'endLineNumber', 'exception', 'categories', 'customProperties', 'additionalTraceIds', 'retryAttempt', 'attempts'}:
        if name in test: string(r, test[name])
    for name in ('categories', 'additionalTraceIds'):
        if name in test: strings(r, test[name])
    if 'customProperties' in test: pairs(r, test['customProperties'])
    if 'exception' in test: exception_shape(r, test['exception'])
    r.require(test['status'] in ('passed', 'failed', 'error'), 'PD_EXCEPTION')
    r.require(('exception' in test) == (test['status'] != 'passed') and 'skipReason' not in test, 'PD_EXCEPTION')
    return test


def junit_shape(r, execution, text, test):
    # Existing classifier checks totals, exact discovery identities, multiplicity
    # and state mapping first. This adds the pinned writer's attribute boundary.
    root = ET.fromstring(text); suite = root[0]
    attrs = {'name', 'tests', 'failures', 'errors', 'skipped', 'time', 'timestamp'}
    r.require(set(root.attrib) == attrs and set(suite.attrib) == attrs | {'hostname'}, 'PD_JUNIT')
    properties = suite.findall('properties')
    r.require(len(properties) == 1 and not properties[0].attrib, 'PD_JUNIT')
    seen = set()
    for item in properties[0]:
        r.require(item.tag == 'property' and set(item.attrib) == {'name', 'value'} and len(item) == 0, 'PD_JUNIT')
        name = item.get('name'); r.require(name in ('framework', 'platform', 'runtime', 'filter') and name not in seen, 'PD_JUNIT'); seen.add(name)
    r.require({'framework', 'platform', 'runtime'} <= seen, 'PD_JUNIT')
    cases = suite.findall('testcase'); r.require(len(cases) == 1 and set(cases[0].attrib) == {'name', 'classname', 'time'}, 'PD_JUNIT')
    if test['status'] != 'passed':
        child = cases[0][0]; exception = test['exception']
        r.require(set(child.attrib) == {'message', 'type'} and len(child) == 0, 'PD_JUNIT')
        r.require(child.get('message') == execution.xml_name(exception['message']) and child.get('type') == exception['type'], 'PD_JUNIT')
        # JUnit text is Exception.ToString(), not native stackTrace. It remains
        # original private evidence and is deliberately never copied/rendered.


def projected_exception(r, index, exception):
    """Only fixed vocabulary, bounded numerics and verified source literals."""
    exception_shape(r, exception)
    source, namespace, cls, method, start, end = CASES[index]
    result = []; depth = 0
    current = exception
    while current is not None:
        category = EXCEPTION_CATEGORIES.get(current['type'], 'unknown')
        row = {'depth': depth, 'category': category, 'assertionTag': 'unknown', 'sourceFrames': []}
        known = {message: (tag, path, line) for message, tag, path, line in ASSERTION_LITERALS.get(index, ())}
        if category == 'exception' and current['message'] in known:
            tag, path, line = known[current['message']]
            row.update(assertionTag=tag, exactMessageSource={'source': path, 'line': line})
        protocol_messages = {
            (0, 'Frame Request must use a positive 63-bit correlation ID.'): ('request-id-positive-63-bit', FRAME, 173),
            (1, 'Cancel payload must contain exactly one reason byte.'): ('cancel-payload-one-reason-byte', PAYLOAD, 390),
        }
        match = protocol_messages.get((index, current['message'])) if category == 'protocol-violation' else None
        if match:
            tag, path, line = match
            row.update(assertionTag=tag, exactMessageSource={'source': path, 'line': line})
        # The assertion text is interpolated in public source. Only its exact
        # fixed template plus two bounded decimal sizes yields numeric facts.
        if index == 3 and category == 'exception':
            match = re.fullmatch(r'OwnedFrame must not grow with the progress flag \(measured ([1-9][0-9]{0,3}) vs ([1-9][0-9]{0,3})\)', current['message'])
            if match and all(1 <= int(value) <= 4096 for value in match.groups()):
                row.update(assertionTag='owned-frame-size-comparison', measuredBytes=int(match[1]), comparatorBytes=int(match[2]),
                           exactMessageSource={'source': PROGRESS, 'line': 675})
        # Exact test-method frames only. No arbitrary signature, path or line is
        # returned. Unsupported frames are counted, never stringified.
        frame_map = {}
        full = namespace + '.' + cls
        for line in range(start, end + 1):
            frame_map[f'   at {full}.{method}() in /_/{source}:line {line}'] = (method, line)
        ensure_line = {PROTOCOL: 877, PROGRESS: 839}.get(source)
        if ensure_line is not None:
            frame_map[f'   at {full}.Ensure(Boolean condition, String message) in /_/{source}:line {ensure_line}'] = ('Ensure', ensure_line)
        lines = current.get('stackTrace', '').splitlines()
        r.require(len(lines) <= 4096, 'PD_LIMIT')
        for raw in lines:
            if raw in frame_map:
                member, line = frame_map[raw]
                row['sourceFrames'].append({'source': source, 'member': member, 'line': line})
        row['unrecognizedStackLineCount'] = len(lines) - len(row['sourceFrames'])
        result.append(row); current = current.get('innerException'); depth += 1
    return result


def classify(execution, native, junit, index, selection):
    r = execution.census
    test = native_shape(r, native)
    uid, row = selection
    facts = {}
    passed = execution.classify(native, junit, {uid: row}, set(), facts, RUNTIMES)
    junit_shape(r, execution, junit, test)
    r.require(native['runtimeVersion'] == '.NET 10.0.14' and facts['retriesObserved'] is False, 'PD_RUNTIME')
    source, namespace, cls, method, _, _ = CASES[index]
    return {'case': index + 1, 'source': source, 'method': namespace + '.' + cls + '.' + method,
            'status': test['status'], 'selectedPassed': passed,
            'nativeCanonicalSHA256': r.digest(native), 'junitDecodedTextSHA256': hashlib.sha256(junit.encode()).hexdigest(),
            'resultUIDSetSHA256': facts['resultUIDSetSHA256'],
            'exceptions': projected_exception(r, index, test['exception']) if 'exception' in test else []}


def case_argv(execution, dotnet, dll, uid, report):
    return execution.census.legacy.case_argv(dotnet, dll, uid, report, '60s')


def lane_type(census, execution):
    class DiagnosticLane(census.Lane):
        def run(self, label, argv, cwd=None, seconds=20):
            output = super().run(label, argv, cwd, seconds)
            if label == 'clone-head':
                for name, expected in SOURCE_TREES.items():
                    self.r.require(self.git('diagnostic-' + name + '-tree', 'rev-parse', 'HEAD:' + name, cwd=self.source) == expected, 'PD_SOURCE')
                verify_source(self.r, self.source)
            if label == 'sdk-version':
                self.r.require(self.r.stable_read(output)[0].decode().strip() == '10.0.114', 'PD_SDK')
            if label == 'sdk-runtimes':
                dotnet = Path(argv[0]); text = self.r.stable_read(output)[0].decode()
                expected = {f"{row['framework']} {row['version']} [{dotnet.parent / 'shared' / row['framework']}]" for row in RUNTIMES}
                self.r.require(set(text.splitlines()) == expected and len(text.splitlines()) == 2, 'PD_RUNTIME')
            return output
    return DiagnosticLane


def handoff_selections(lane, ordinary, explicit):
    r = lane.r
    r.require(lane.identity['sdk'] == '10.0.114' and
              sorted(lane.identity['installedRuntimes'], key=lambda x: x['framework']) == RUNTIMES, 'PD_RUNTIME')
    selections = selected(r, ordinary)
    r.require(not ({uid for uid, _ in selections} & explicit), 'PD_SELECTION')
    r.require(lane.sequence == 32, 'PD_COMMANDS')
    # Selection and every independent seal must finish under the ORIGINAL
    # preparation deadline. The body phase cannot revive expired preparation.
    lane.check()
    return selections


def run_cases(lane, execution, selections, overall_stop):
    r = lane.r
    lane.check()
    now = time.monotonic()
    r.require(now < lane.stop and now < overall_stop, 'PD_BUDGET')
    lane.stop = min(overall_stop, now + 1500)
    # The original checkout remains an independently sealed, unbuilt input.
    # Rebase controls must use it, not the clone containing generated bin/obj.
    lane.run('fixture-python-controls', [sys.executable, '-B', ROOT / 'eng/tests/test_pure_failure_diagnostic.py'], ROOT, 30)
    r.require(lane.sequence == 33, 'PD_COMMANDS')
    dotnet = next(path for path in lane.inputs if path.name == 'dotnet' and path.parent in lane.trees)
    dll = lane.source / 'test/SharpLink.UnitTests/bin/Release/net10.0/SharpLink.UnitTests.dll'
    lane.env['TUNIT_ENABLE_JUNIT_REPORTER'] = '1'
    for index, selection in enumerate(selections):
        lane.check(); verify_source(r, lane.source)
        r.require(r.sha(dll) == lane.identity['binarySHA256'], 'UC_INPUT_CHANGED')
        uid, _ = selection; label = 'fixture-case-' + str(index + 1)
        report = lane.work / (label + '-results'); report.mkdir(mode=0o700)
        report_identity = r.legacy.directory_identity_strict(report); r.legacy.verify_directory(report, report_identity, empty=True)
        argv = case_argv(execution, dotnet, dll, uid, report)
        r.require(argv == [str(x) for x in (dotnet, dll, '--filter-uid', uid, '--maximum-parallel-tests', '1', '--timeout', '60s',
                  '--results-directory', report, '--report-html', '--report-html-filename', report / 'session.html', '--junit-output-path', report / 'junit.xml')], 'PD_ARGV')
        execution.verify_body_inputs(execution.body_argv(dotnet, dll, report), dotnet, dll, report, lane.env)
        try: lane.run(label, argv, lane.source, 180)
        except BaseException as error: lane.record(error)
        state = lane.commands.get(label)
        # A timeout/unknown owner is not a closed result domain, even if files exist.
        r.require(execution.owner_closed(state, label, argv), 'PD_OWNER')
        snapshot = execution.report_snapshot(report, report_identity)
        r.require(set(snapshot) == {'.'} | execution.PARENT_REPORTS, 'PD_REPORTS')
        lane.seals[report] = snapshot; lane.seal_domains[report] = ('unknown', None)
        lane.check()
        native = r.read_json(report / 'session.tunit-report.json')
        junit = r.stable_read(report / 'junit.xml')[0].decode('utf-8-sig')
        result = classify(execution, native, junit, index, selection)
        exit_code = state['receipt']['exitCode']
        r.require(type(exit_code) is int and exit_code == (0 if result['selectedPassed'] else 2), 'PD_EXIT')
        result.update(ownerClosed=True, bodyExitCode=exit_code,
                      nativeFileSHA256=r.sha(report / 'session.tunit-report.json'), junitFileSHA256=r.sha(report / 'junit.xml'))
        lane.case_facts.append(result)
        lane.check()
        if not result['selectedPassed']: lane.record(r.Rejected('PD_CASE_FAILED'))
    r.require(lane.sequence == 39 and len(lane.case_facts) == 6, 'PD_COMMANDS')
    lane.candidate_complete = True


def rejections(lane, execution):
    allowed = CODES | execution.CODES | lane.r.PUBLIC_REJECTION_CODES
    queue = list(lane.errors); seen = set(); codes = set(); unknown = 0
    while queue:
        error = queue.pop()
        if not isinstance(error, BaseException) or id(error) in seen: continue
        seen.add(id(error))
        if type(error) is lane.r.Rejected and type(error.code) is str and error.code in allowed: codes.add(error.code)
        elif not isinstance(error, BaseExceptionGroup): unknown += 1
        if isinstance(error, BaseExceptionGroup): queue.extend(error.exceptions)
        if error.__cause__ is not None: queue.append(error.__cause__)
        if error.__context__ is not None: queue.append(error.__context__)
    return {'codes': sorted(codes), 'unclassifiedExceptionNodes': unknown}


def public_identity(r, identity):
    output = {}
    for name in ('head', 'tree', 'binarySHA256', 'sourceManifestSHA256', 'sdkManifestSHA256', 'packageManifestSHA256', 'providerProofSHA256'):
        value = identity.get(name)
        if value is not None:
            r.require(type(value) is str and re.fullmatch('[0-9a-f]{' + ('40' if name in ('head', 'tree') else '64') + '}', value), 'PD_PUBLIC')
            output[name] = value
    return output


def finish(lane, census, execution):
    r = lane.r
    try: lane.check()
    except BaseException as error: lane.record(error)
    complete = lane.candidate_complete and len(lane.case_facts) == 6
    qualified = bool(complete and not lane.errors and lane.durable and time.monotonic() < lane.stop)
    try:
        summary = {'schemaVersion': 1, 'scope': 'six-ordinary-three-fixture-candidate-validation',
            'identity': public_identity(r, lane.identity),
            'candidateCatalogSHA256': HELPERS['eng/unit-census.json'],
            'frozenCatalogSHA256': HELPERS['eng/fixture-migration-baseline/unit-census.json'],
            'sourceTrees': dict(SOURCE_TREES), 'requiredSdk': '10.0.114', 'requiredRuntime': '10.0.14',
            'preparationQualifiedAtHandoff': lane.preparation_complete,
            'candidateValidationComplete': complete, 'candidateValidationQualified': qualified,
            'fullSuiteQualified': False, 'historicalOrdinaryFailureCount': 290, 'requiredSelectedCaseCount': 6,
            'expectedOrdinaryMetadataCount': 2879, 'expectedExplicitMetadataCount': 5,
            'historicalFullRunId': 41940224644, 'historicalDiagnosticRunId': 41947099090,
            'errorOccurrences': len(lane.errors), 'rejections': rejections(lane, execution),
            'evidenceDurable': lane.durable, 'cases': lane.case_facts, 'exit': 0 if qualified else 1}
        r.write_json(lane.evidence, 'three-fixture-validation-summary.json', summary); lane.extend_evidence(['three-fixture-validation-summary.json'])
        lane.check(); rendered = json.dumps(summary, sort_keys=True, allow_nan=False)
        r.require(time.monotonic() < lane.stop, 'PD_BUDGET')
        print(rendered, flush=True)
        r.require(time.monotonic() < lane.stop, 'PD_BUDGET')
        return 0 if qualified else 1
    except BaseException as error:
        lane.record(error)
        try: os.write(2, b'{"terminalDiagnostic":true,"candidateValidationQualified":false,"fullSuiteQualified":false,"exit":1}\n')
        except BaseException as secondary: lane.record(secondary)
        try: os.write(2, census.terminal_diagnostic(lane, 'guard'))
        except BaseException as secondary: lane.record(secondary)
        return 1


def main():
    global ACTIVE
    lane = None; census = None; execution_runner = None
    ACTIVE = {'lane': None, 'errors': [], 'work': None}
    try:
        started = time.monotonic()
        work = Path(tempfile.mkdtemp(prefix='slfv-', dir='/tmp')); os.chmod(work, 0o700); ACTIVE['work'] = work
        # Fixed reviewed helper hashes before importing executable helper content.
        for name, expected in HELPERS.items():
            path = ROOT / name
            if path.is_symlink() or hashlib.sha256(path.read_bytes()).hexdigest() != expected:
                raise RuntimeError('PD_HELPERS')
        census = load('pure_census', ROOT / 'eng/run-unit-census.py')
        execution = load('pure_execution_results', ROOT / 'eng/unit-execution-results.py')
        execution_runner = load('pure_execution_runner', ROOT / 'eng/run-unit-execution.py')
        owner = load('pure_owner', ROOT / 'eng/package-smoke-process.py'); owner.enable_subreaper()
        lane = lane_type(census, execution)(work, execution.census, owner)
        ACTIVE['lane'] = lane; ACTIVE['errors'] = lane.errors
        lane.case_facts = []; lane.candidate_complete = False; lane.preparation_complete = False
        lane.stop = min(lane.stop, started + 3600)
        try:
            for name, expected in HELPERS.items(): lane.r.require(lane.r.sha(ROOT / name) == expected, 'PD_HELPERS')
            verify_isolated_sdk(lane, census)
            ordinary, explicit = execution_runner.prepare(lane, census, execution)
            selections = handoff_selections(lane, ordinary, explicit)
            lane.preparation_complete = True
            run_cases(lane, execution, selections, started + 5100)
        except BaseException as error: lane.record(error)
        return finish(lane, census, execution)
    except BaseException as error:
        if lane is not None: lane.record(error)
        else:
            ACTIVE['errors'].append(error)
            if census is not None and ACTIVE['work'] is not None: census.persist(ACTIVE['work'], ACTIVE['errors'], 999)
            elif execution_runner is not None: execution_runner.bootstrap_persist(ACTIVE['work'], ACTIVE['errors'])
        try: os.write(2, b'FIXTURE_VALIDATION_BOOTSTRAP_FAILED\n')
        except BaseException as secondary: ACTIVE['errors'].append(secondary)
        return 1


def guarded_main():
    try: return main()
    except BaseException as error:
        ACTIVE['errors'].append(error)
        try: os.write(2, b'FIXTURE_VALIDATION_OUTERMOST_FAILED\n')
        except BaseException as secondary: ACTIVE['errors'].append(secondary)
        return 1


if __name__ == '__main__': raise SystemExit(guarded_main())
