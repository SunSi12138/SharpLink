#!/usr/bin/env python3
"""Eleven unchanged parser/handshake diagnostic UIDs, once each; raw reports remain private.

Extends, never replaces, the reviewed single-build/double-discovery custody
lane. This diagnostic cannot qualify the historical failed full suite, D2, or performance.
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
SOURCE_TREES = {'src': 'c3a94c896418a5d5ec62646461f17c67dc89bc96',
                'test': '6052d7b2a52946f8d9048e76287167768f13c673'}
HELPERS = {
    'eng/run-unit-census.py': 'a445781ba97a6c6a963faa9ba4d7e0bb890a96df6395e482ddb3948f8d12e4bf',
    'eng/unit-census-results.py': 'cbf6ce2096576dfe027b8e196144e9cba57860ad99be48c1f89d7ba73a71414e',
    'eng/run-unit-execution.py': 'ede7580fa1a9e37fba16322ef0158a9db8905bc6983c217c8bdad6eb5b784acf',
    'eng/unit-execution-results.py': '8239004ceaecf6b34ea37b9c8b6d2959c7eee52bd42997714012f8b4a47bd97a',
    'eng/package-smoke-process.py': 'ba9f6ae43784ac752acdbfee1af60f33361ba76398a558948da721994449dda3',
    'eng/route59-results.py': 'eb95aaf6a6799b0c216ee3687d71e5e1aeb5651867d515e3a4db16561bfcda8b',
    'eng/retirement-results.py': '6774c8bdd8bd8b3f618c01aee34e429d8a3b8218cdb169e752cd81ab6a9e79eb',
    'eng/tests/test_unit_census.py': 'd12fbbc8e3634fac209aac866eab101d2938d4531ea27e04b9732fb1f7922da5',
    'eng/tests/test_unit_execution.py': 'c9fbae133b363f6ee0150036adfaba80528a3371ba73a5d6140423399672fa19',
    'eng/unit-census.json': '36749a0d854f5445c500523bba58a36827c83bd97ab65fe4d1a9dbdb567a9e20',
    'eng/rebase-three-fixture-census.py': 'c6d2f5ff33fda5b37d868616f147ad0274fede304ad60892751f8752a3b315c7',
    'eng/tests/test_three_fixture_census.py': 'b382a203d6ac21f907adf79bc774550211b15d3fc8041a1c03bb3686f7ae7357',
    'eng/tests/test_pure_failure_diagnostic.py': '8e1fc528ee0e31fa61e440dd0402e4fb0a1b0f75e5c3b51dd7236f1f674e5af7',
    'eng/three-fixture-census-rebase.json': '2c1d5a694fa66e02843e497986c629dd6437804d075672d0f005f6c145874e1e',
    'eng/fixture-migration-baseline/unit-census.json': '2c34614a0bc918c48b860e795f3f331d0243065f05f0c9530e6aa0d430faeb66',
    'eng/fixture-migration-baseline/test/SharpLink.UnitTests/Protocol/ProtocolV2Tests.cs.txt': 'd2f827e0f45adae15e78c9db4d9c468de1d247c34eb60ed5e01296b59b584e73',
    'eng/fixture-migration-baseline/test/SharpLink.UnitTests/Runtime/SendPumpProgressIsolationTests.cs.txt': '67ffae89508dc0aba7f25f968bec4eb6fa91f997c9acc32784e9defc96a536a9',
    'eng/fixture-migration-baseline/src/SharpLink.Runtime/OwnedFrame.cs.txt': '71948e8baa3494c086d368454460f0422bc635095f91234ea859a3b5eb4bf3d5',
    'eng/fixture-migration-baseline/test/SharpLink.UnitTests/Runtime/Stack13FrameClearTests.cs.txt': '3b4d5efdf3aba44ee1abc34199b517df615c768ec9721d5bc6ddc3c1b7076a3e',
    'eng/run-pure-failure-diagnostic.py': 'fdaf699eb2c010702d7bee82d72d9b48a797d9f289da6bb9bb1e64b676e1c323',
    'eng/tests/test_parser_handshake_diagnostic.py': 'c3d28e951c44293ed6810d0e906afb0aed552a0a9ccf09c1641f66413de49904',
}
PROTOCOL = 'test/SharpLink.UnitTests/Protocol/ProtocolV2Tests.cs'
NEGOTIATOR = 'test/SharpLink.UnitTests/Protocol/ProtocolV2NegotiatorTests.cs'
COMPATIBILITY = 'test/SharpLink.UnitTests/Protocol/ProtocolV2ErrorCompatibilityTests.cs'
GRAMMAR = 'test/SharpLink.UnitTests/Protocol/Protocol3FrameAndHandshakeTests.cs'
FRAME = 'src/SharpLink.Runtime/ProtocolV2/ProtocolV2FrameCodec.cs'
PAYLOAD = 'src/SharpLink.Runtime/ProtocolV2/ProtocolV2PayloadCodec.cs'
SOURCE_PINS = {
    'src/SharpLink.Runtime/ProtocolV2/ProtocolV2FrameCodec.cs': '737e171bed77f428e4e5d7a0128b8344ae0886cf70300775c9f7df823f588d0e',
    'src/SharpLink.Runtime/ProtocolV2/ProtocolV2PayloadCodec.cs': 'c3a94f11332f1026f10c72fe20b34666704ef0996e2c7dbdd9073ce43e94c732',
    'src/SharpLink.Abstractions/ProtocolV2.cs': '1e0b64dcba691ed274e66984fb8b566a10c68e57a7bfc2a5eefb5a506db994a2',
    'src/SharpLink.Runtime/ProtocolV2/ProtocolV2Negotiator.cs': 'db95fa43e92596680d5b026ba8981f648770587e437d0addfe880ff28347fb74',
    'src/SharpLink.Runtime/ProtocolViolationReason.cs': 'be29a593585933e97a233b9a5500affdee02a7faf27d2bedd8682b5c61630bc1',
    'test/SharpLink.UnitTests/Protocol/Protocol3FrameAndHandshakeTests.cs': '8804cf11f26fdd938c5ff786c154bf46fca05d6cca5af4e8a65d34d9e19f6fe5',
    'test/SharpLink.UnitTests/Protocol/ProtocolV2ErrorCompatibilityTests.cs': 'c2ab7b602ecc465b366775b8565df52b37a21b159259e188b0f7b0fdcb43f7c8',
    'test/SharpLink.UnitTests/Protocol/ProtocolV2NegotiatorTests.cs': 'b6f8d1b1130a10ee446e63a00b525f37e81a74c1846371b86363225e4a1cf3c8',
    'test/SharpLink.UnitTests/Protocol/ProtocolV2Tests.cs': 'a2aa71ed8ee7c5f7184c9f5ad9667973f7057cd73d5c139c81f6e92939f22d4e',
}
# Fixed source identities, never names or arguments copied from reports.
CASES = (
    ('test/SharpLink.UnitTests/Protocol/ProtocolV2Tests.cs', 'SharpLink.UnitTests.Protocol', 'ProtocolV2Tests', 'MultipleFramesFollowedByHalfFrameShouldPreserveRemainder', 773, 788),
    ('test/SharpLink.UnitTests/Protocol/ProtocolV2Tests.cs', 'SharpLink.UnitTests.Protocol', 'ProtocolV2Tests', 'InvalidLengthTypeFlagsAndRequestIdShouldBeRejected', 71, 94),
    ('test/SharpLink.UnitTests/Protocol/ProtocolV2Tests.cs', 'SharpLink.UnitTests.Protocol', 'ProtocolV2Tests', 'HealthControlFramesShouldRequireBoundedPayloadAndRequestId', 725, 770),
    ('test/SharpLink.UnitTests/Protocol/ProtocolV2Tests.cs', 'SharpLink.UnitTests.Protocol', 'ProtocolV2Tests', 'HandshakeRequestAndResponseShouldRoundTrip', 173, 210),
    ('test/SharpLink.UnitTests/Protocol/ProtocolV2NegotiatorTests.cs', 'SharpLink.UnitTests.Protocol', 'ProtocolV2NegotiatorTests', 'ClientOfferShouldContainOnlyPolicyAndAuthenticationInputs', 10, 42),
    ('test/SharpLink.UnitTests/Protocol/ProtocolV2NegotiatorTests.cs', 'SharpLink.UnitTests.Protocol', 'ProtocolV2NegotiatorTests', 'ImplementedCapabilitiesShouldRemainExplicitAndProtocolRecognized', 45, 61),
    ('test/SharpLink.UnitTests/Protocol/ProtocolV2ErrorCompatibilityTests.cs', 'SharpLink.UnitTests.Protocol', 'ProtocolV2ErrorCompatibilityTests', 'StructuredErrorShapeShouldRejectPublishedMinorThreePeers', 6, 51),
    ('test/SharpLink.UnitTests/Protocol/Protocol3FrameAndHandshakeTests.cs', 'SharpLink.UnitTests.Protocol', 'Protocol3FrameAndHandshakeTests', 'OnlyNewMagicAndPositive63CorrelationAreAccepted', 13, 24),
    ('test/SharpLink.UnitTests/Protocol/Protocol3FrameAndHandshakeTests.cs', 'SharpLink.UnitTests.Protocol', 'Protocol3FrameAndHandshakeTests', 'CancellationHasExactlyOneKnownReasonByte', 27, 33),
    ('test/SharpLink.UnitTests/Protocol/Protocol3FrameAndHandshakeTests.cs', 'SharpLink.UnitTests.Protocol', 'Protocol3FrameAndHandshakeTests', 'HandshakeRequestPinsNewOffsetsAndStrictRevisionCapabilitiesAndLengths', 76, 102),
    ('test/SharpLink.UnitTests/Protocol/Protocol3FrameAndHandshakeTests.cs', 'SharpLink.UnitTests.Protocol', 'Protocol3FrameAndHandshakeTests', 'SuccessHandshakeCarriesExactAuthorityAndNoDowngrade', 105, 125),
 )
# Exact known messages only. Rendered literals are never copied into summaries.
# Interpolated frame names are fixed source-reviewed enum constants, not report data.
# ArgumentException messages remain unknown; runtime-added parameter formatting is not a source literal.
EXCEPTION_TAGS = (
    (0, 'SharpLink.Runtime.SharpLinkProtocolViolationException', 'Cancel payload must contain exactly one reason byte.', 'cancel-payload-one-reason-byte', PAYLOAD, 390, 'Cancel payload must contain exactly one reason byte.'),
    (1, 'SharpLink.Runtime.SharpLinkProtocolViolationException', 'Connection-control frame Ping must use request ID 0.', 'ping-requires-zero-request-id', FRAME, 171, 'Connection-control frame {type} must use request ID 0.'),
    (2, 'SharpLink.Runtime.SharpLinkProtocolViolationException', 'Frame HealthCheck must use a positive 63-bit correlation ID.', 'health-check-positive-63-bit-id', FRAME, 173, 'Frame {type} must use a positive 63-bit correlation ID.'),
    (4, 'System.Exception', "the caller's required capabilities must remain explicit policy input", 'required-capabilities-policy-input', NEGOTIATOR, 33, "the caller's required capabilities must remain explicit policy input"),
    (5, 'System.Exception', 'endpoint implementation support must remain an explicit declaration', 'explicit-implemented-capabilities', NEGOTIATOR, 54, 'endpoint implementation support must remain an explicit declaration'),
    (6, 'System.Exception', '2.0 increments the published minor 3 once', 'legacy-minor-four-assertion', COMPATIBILITY, 8, '2.0 increments the published minor 3 once'),
) + tuple((index, 'System.InvalidOperationException', message, tag, GRAMMAR, line, message)
          for index in range(7, 11)
          for message, tag, line in (
              ('Expected Protocol3 structural violation.', 'expected-structural-violation', 246),
              ('Protocol3 source regression assertion failed.', 'protocol3-source-assertion', 251)))
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
              root == Path(temporary) / 'parser-handshake-dotnet' and dotnet.name == 'dotnet' and dotnet.is_file(), 'PD_SDK')
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
    r.require(len({uid for uid, _ in output}) == 11, 'PD_SELECTION')
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
        known = {(case, typ, message): (tag, path, line)
                 for case, typ, message, tag, path, line, _ in EXCEPTION_TAGS}
        match = known.get((index, current['type'], current['message']))
        if match:
            tag, path, line = match
            row.update(assertionTag=tag, exactMessageSource={'source': path, 'line': line})
        # Exact selected test-method frames and verified helper signatures only.
        # Unsupported paths, signatures, and line numbers are counted, never printed.
        frame_map = {}; full = namespace + '.' + cls
        for line in range(start, end + 1):
            frame_map[f'   at {full}.{method}() in /_/{source}:line {line}'] = (method, line)
        ensure_line = {PROTOCOL: 877, NEGOTIATOR: 620, COMPATIBILITY: 69}.get(source)
        if ensure_line is not None:
            frame_map[f'   at {full}.Ensure(Boolean condition, String message) in /_/{source}:line {ensure_line}'] = ('Ensure', ensure_line)
        if source == PROTOCOL and index in (0, 1, 2):
            frame_map[f'   at {full}.CreateFrame(ProtocolV2FrameType type, ProtocolV2FrameFlags flags, UInt64 requestId, Byte[] payload) in /_/{source}:line 809'] = ('CreateFrame', 809)
        if source == GRAMMAR:
            frame_map[f'   at {full}.Ensure(Boolean condition) in /_/{source}:line 251'] = ('Ensure', 251)
            frame_map[f'   at {full}.ExpectViolation(Action action) in /_/{source}:line 246'] = ('ExpectViolation', 246)
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
    lane.run('parser-handshake-python-controls', [sys.executable, '-B', ROOT / 'eng/tests/test_parser_handshake_diagnostic.py'], ROOT, 30)
    r.require(lane.sequence == 33, 'PD_COMMANDS')
    dotnet = next(path for path in lane.inputs if path.name == 'dotnet' and path.parent in lane.trees)
    dll = lane.source / 'test/SharpLink.UnitTests/bin/Release/net10.0/SharpLink.UnitTests.dll'
    lane.env['TUNIT_ENABLE_JUNIT_REPORTER'] = '1'
    for index, selection in enumerate(selections):
        lane.check(); verify_source(r, lane.source)
        r.require(r.sha(dll) == lane.identity['binarySHA256'], 'UC_INPUT_CHANGED')
        uid, _ = selection; label = 'parser-handshake-case-' + str(index + 1)
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
    r.require(lane.sequence == 44 and len(lane.case_facts) == 11, 'PD_COMMANDS')
    lane.diagnostic_complete = True


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
    complete = lane.diagnostic_complete and len(lane.case_facts) == 11
    qualified = bool(complete and not lane.errors and lane.durable and time.monotonic() < lane.stop)
    try:
        summary = {'schemaVersion': 1, 'scope': 'eleven-ordinary-parser-handshake-diagnostic',
            'identity': public_identity(r, lane.identity),
            'candidateCatalogSHA256': HELPERS['eng/unit-census.json'],
            'frozenCatalogSHA256': HELPERS['eng/fixture-migration-baseline/unit-census.json'],
            'sourceTrees': dict(SOURCE_TREES), 'requiredSdk': '10.0.114', 'requiredRuntime': '10.0.14',
            'preparationQualifiedAtHandoff': lane.preparation_complete,
            'diagnosticComplete': complete, 'diagnosticQualified': qualified,
            'allSelectedPassed': bool(complete and all(row['selectedPassed'] for row in lane.case_facts)),
            'sourceUnchangedFromHead': '5e021af675f9252a7bb2ac4023d78bf8bec0aa92',
            'fullSuiteQualified': False, 'historicalOrdinaryFailureCount': 290, 'requiredSelectedCaseCount': 11,
            'expectedOrdinaryMetadataCount': 2879, 'expectedExplicitMetadataCount': 5,
            'historicalFullRunId': 41940224644, 'historicalDiagnosticRunId': 41947099090,
            'historicalFirstMigrationRun': {'runId': 41950562552, 'head': '4209233576c2100e0d5dc625308f49b76d500169',
                'complete': True, 'passed': 5, 'nonpassing': 1, 'qualified': False,
                'case4': {'measuredBytes': 64, 'comparatorBytes': 56, 'shapeGuardsPassed': True}},
            'historicalPackingRun': {'runId': 41952557024, 'head': '3f2f1d03da65844fba6f367811545f195ccf5206',
                'complete': True, 'passed': 8, 'nonpassing': 0, 'qualified': True, 'fullSuiteQualified': False},
            'historicalThirteenCaseRun': {'runId': 41954983558, 'head': '5e021af675f9252a7bb2ac4023d78bf8bec0aa92',
                'complete': True, 'passed': 13, 'nonpassing': 0, 'qualified': True, 'fullSuiteQualified': False},
            'errorOccurrences': len(lane.errors), 'rejections': rejections(lane, execution),
            'evidenceDurable': lane.durable, 'cases': lane.case_facts, 'exit': 0 if qualified else 1}
        r.write_json(lane.evidence, 'parser-handshake-diagnostic-summary.json', summary); lane.extend_evidence(['parser-handshake-diagnostic-summary.json'])
        lane.check(); rendered = json.dumps(summary, sort_keys=True, allow_nan=False)
        r.require(time.monotonic() < lane.stop, 'PD_BUDGET')
        print(rendered, flush=True)
        r.require(time.monotonic() < lane.stop, 'PD_BUDGET')
        return 0 if qualified else 1
    except BaseException as error:
        lane.record(error)
        try: os.write(2, b'{"terminalDiagnostic":true,"diagnosticQualified":false,"fullSuiteQualified":false,"exit":1}\n')
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
        work = Path(tempfile.mkdtemp(prefix='slph-', dir='/tmp')); os.chmod(work, 0o700); ACTIVE['work'] = work
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
        lane.case_facts = []; lane.diagnostic_complete = False; lane.preparation_complete = False
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
        try: os.write(2, b'PARSER_HANDSHAKE_DIAGNOSTIC_BOOTSTRAP_FAILED\n')
        except BaseException as secondary: ACTIVE['errors'].append(secondary)
        return 1


def guarded_main():
    try: return main()
    except BaseException as error:
        ACTIVE['errors'].append(error)
        try: os.write(2, b'PARSER_HANDSHAKE_DIAGNOSTIC_OUTERMOST_FAILED\n')
        except BaseException as secondary: ACTIVE['errors'].append(secondary)
        return 1


if __name__ == '__main__': raise SystemExit(guarded_main())
