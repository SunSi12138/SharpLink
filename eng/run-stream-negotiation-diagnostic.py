#!/usr/bin/env python3
"""Sixteen unchanged cases: timed-stream first, then pure negotiation.

Single build, double source-bound discovery, one fresh owned process per case.
Only fixed vocabulary, source-bound frames, and opaque UID hashes are public.
Historical full-suite failure stays separate and can never be qualified here.
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
SOURCE_TREES = {'src': 'c3a94c896418a5d5ec62646461f17c67dc89bc96', 'test': '226e6404e69879868bc2f98037624ec357529f49'}
HELPERS = {'eng/stream-negotiation-history/boolean-display-name-proof.json': '1917416924ccc1f506b7922aff4872d8b7bac20bfa2147da12347129c4cfed6e',
 'eng/run-unit-census.py': 'a445781ba97a6c6a963faa9ba4d7e0bb890a96df6395e482ddb3948f8d12e4bf',
 'eng/unit-census-results.py': 'cbf6ce2096576dfe027b8e196144e9cba57860ad99be48c1f89d7ba73a71414e',
 'eng/run-unit-execution.py': 'ede7580fa1a9e37fba16322ef0158a9db8905bc6983c217c8bdad6eb5b784acf',
 'eng/unit-execution-results.py': '5c2f5108f0770bbc582c8f6d208fb66934a2ca3e02f180f3996c05ec31474cb7',
 'eng/package-smoke-process.py': 'ba9f6ae43784ac752acdbfee1af60f33361ba76398a558948da721994449dda3',
 'eng/route59-results.py': 'eb95aaf6a6799b0c216ee3687d71e5e1aeb5651867d515e3a4db16561bfcda8b',
 'eng/retirement-results.py': '6774c8bdd8bd8b3f618c01aee34e429d8a3b8218cdb169e752cd81ab6a9e79eb',
 'eng/tests/test_unit_census.py': 'd12fbbc8e3634fac209aac866eab101d2938d4531ea27e04b9732fb1f7922da5',
 'eng/tests/test_unit_execution.py': 'c9fbae133b363f6ee0150036adfaba80528a3371ba73a5d6140423399672fa19',
 'eng/unit-census.json': '4b4f7cd616a746b153aee47a2fdd9f2685fdeee18a17ee0693743610642209a0',
 'eng/rebase-three-fixture-census.py': '92af1a4c420d5ccaa47d8f5298ef8b6ce8e4befef3b46cc6572912ccb28a12ec',
 'eng/tests/test_three_fixture_census.py': '34669c538d218a34effb86dc9f4e086749320b0e09e493b807e626fd7377b71b',
 'eng/three-fixture-census-rebase.json': 'cd1e92af4898bdc25466bc4caa1a887ff7fba7dea06e3e9f38965ee177b8d179',
 'eng/fixture-migration-baseline/unit-census.json': '2c34614a0bc918c48b860e795f3f331d0243065f05f0c9530e6aa0d430faeb66',
 'eng/fixture-migration-baseline/test/SharpLink.UnitTests/Protocol/ProtocolV2Tests.cs.txt': 'd2f827e0f45adae15e78c9db4d9c468de1d247c34eb60ed5e01296b59b584e73',
 'eng/fixture-migration-baseline/test/SharpLink.UnitTests/Runtime/SendPumpProgressIsolationTests.cs.txt': '67ffae89508dc0aba7f25f968bec4eb6fa91f997c9acc32784e9defc96a536a9',
 'eng/fixture-migration-baseline/src/SharpLink.Runtime/OwnedFrame.cs.txt': '71948e8baa3494c086d368454460f0422bc635095f91234ea859a3b5eb4bf3d5',
 'eng/fixture-migration-baseline/test/SharpLink.UnitTests/Runtime/Stack13FrameClearTests.cs.txt': '3b4d5efdf3aba44ee1abc34199b517df615c768ec9721d5bc6ddc3c1b7076a3e',
 'eng/fixture-migration-baseline/parser-handshake-diagnostic-result.json': 'f2f09025a1cc611049c32ec83ad6be2ef2cd8a6351efb9191588a02d761424e1',
 'eng/fixture-migration-baseline/test/SharpLink.UnitTests/Protocol/ProtocolV2NegotiatorTests.cs.txt': 'b6f8d1b1130a10ee446e63a00b525f37e81a74c1846371b86363225e4a1cf3c8',
 'eng/fixture-migration-baseline/test/SharpLink.UnitTests/Protocol/ProtocolV2ErrorCompatibilityTests.cs.txt': 'c2ab7b602ecc465b366775b8565df52b37a21b159259e188b0f7b0fdcb43f7c8',
 'eng/tests/test_full_regression.py': 'ced4ab1bf3272dabc38626a8f62ef4cf26af2f0bac4fd60d4bdbd20fb4dec9d8',
 'eng/full-regression-history/original-ordinary-result.json': 'dae32c0724ad78dca30234d7d109e45e56566efe900aa854b6461edb4a50f214',
 'eng/full-regression-history/twenty-three-case-result.json': '363a314514e08f19abee140e4ea9c2e692e9836aaeebf02d317036b012fc07af',
 'eng/run-full-regression.py': 'a474a13dec376aaf6126531bb74c0662bfde596192c9cafc7bc09e511b57fa38',
 'eng/stream-negotiation-history/current-ordinary-result.json': 'b53cffd3018fdcd0cced7ea119d3a1268d8d96363f3645343bb14566953285fc',
 'eng/tests/test_stream_negotiation_diagnostic.py': '656933d73ab21eb22a098dc07483ba44947d9861ba46ce9263499747bc22e769'}
SOURCE_PINS = {'test/SharpLink.UnitTests/Client/SharpLinkClientTimeBudgetTests.cs': 'bb0bf4e489c2bc58ca070c496cc33a0681c874331b574f40005ba2e1ef14ff15',
 'test/SharpLink.UnitTests/Protocol/ProtocolV2NegotiatorTests.cs': 'd40c522f8bc8805860c9a29288109221545c69c0d598ef5ce6c206efe13e6f57',
 'src/SharpLink.Runtime/ProtocolV2/ProtocolV2Negotiator.cs': 'db95fa43e92596680d5b026ba8981f648770587e437d0addfe880ff28347fb74'}
CASES = (('test/SharpLink.UnitTests/Client/SharpLinkClientTimeBudgetTests.cs',
  'SharpLink.UnitTests.Client',
  'SharpLinkClientTimeBudgetTests',
  'TimedClientStreamShouldPublishCreationTimeBudgetAndFailThroughPendingDeadline',
  188,
  257),
 ('test/SharpLink.UnitTests/Client/SharpLinkClientTimeBudgetTests.cs',
  'SharpLink.UnitTests.Client',
  'SharpLinkClientTimeBudgetTests',
  'TimedClientStreamShouldPublishCreationTimeBudgetAndFailThroughPendingDeadline',
  188,
  257),
 ('test/SharpLink.UnitTests/Client/SharpLinkClientTimeBudgetTests.cs',
  'SharpLink.UnitTests.Client',
  'SharpLinkClientTimeBudgetTests',
  'ExplicitMethodTimeoutShouldOverrideClientDefaultTimeBudget',
  18,
  52),
 ('test/SharpLink.UnitTests/Client/SharpLinkClientTimeBudgetTests.cs',
  'SharpLink.UnitTests.Client',
  'SharpLinkClientTimeBudgetTests',
  'InheritedTimeBudgetShouldCapSelectedMethodPolicyWithoutRestartingIt',
  55,
  93),
 ('test/SharpLink.UnitTests/Protocol/ProtocolV2NegotiatorTests.cs',
  'SharpLink.UnitTests.Protocol',
  'ProtocolV2NegotiatorTests',
  'ClientOfferShouldContainOnlyPolicyAndAuthenticationInputs',
  10,
  45),
 ('test/SharpLink.UnitTests/Protocol/ProtocolV2NegotiatorTests.cs',
  'SharpLink.UnitTests.Protocol',
  'ProtocolV2NegotiatorTests',
  'ImplementedCapabilitiesShouldRemainExplicitAndProtocolRecognized',
  48,
  65),
 ('test/SharpLink.UnitTests/Protocol/ProtocolV2NegotiatorTests.cs',
  'SharpLink.UnitTests.Protocol',
  'ProtocolV2NegotiatorTests',
  'PolicyConstructionShouldRejectEveryIllegalLocalState',
  68,
  98),
 ('test/SharpLink.UnitTests/Protocol/ProtocolV2NegotiatorTests.cs',
  'SharpLink.UnitTests.Protocol',
  'ProtocolV2NegotiatorTests',
  'CapabilityMatrixShouldProduceSymmetricServerAndClientResults',
  101,
  161),
 ('test/SharpLink.UnitTests/Protocol/ProtocolV2NegotiatorTests.cs',
  'SharpLink.UnitTests.Protocol',
  'ProtocolV2NegotiatorTests',
  'UnsupportedRequiredCapabilityShouldReturnUnimplemented',
  164,
  188),
 ('test/SharpLink.UnitTests/Protocol/ProtocolV2NegotiatorTests.cs',
  'SharpLink.UnitTests.Protocol',
  'ProtocolV2NegotiatorTests',
  'ServerNegotiationShouldIntersectLimitsAtCurrentMinorBoundaries',
  191,
  254),
 ('test/SharpLink.UnitTests/Protocol/ProtocolV2NegotiatorTests.cs',
  'SharpLink.UnitTests.Protocol',
  'ProtocolV2NegotiatorTests',
  'ClientValidationShouldRejectOutOfOfferVersionCapabilitiesAndLimits',
  257,
  298),
 ('test/SharpLink.UnitTests/Protocol/ProtocolV2NegotiatorTests.cs',
  'SharpLink.UnitTests.Protocol',
  'ProtocolV2NegotiatorTests',
  'CompressionMatrixShouldHonorOptionalRequiredAndServerPreference',
  301,
  350),
 ('test/SharpLink.UnitTests/Protocol/ProtocolV2NegotiatorTests.cs',
  'SharpLink.UnitTests.Protocol',
  'ProtocolV2NegotiatorTests',
  'MalformedOfferMatrixShouldReturnProtocolViolation',
  353,
  404),
 ('test/SharpLink.UnitTests/Protocol/ProtocolV2NegotiatorTests.cs',
  'SharpLink.UnitTests.Protocol',
  'ProtocolV2NegotiatorTests',
  'MalformedResponseMatrixShouldReturnStructuredFailure',
  407,
  447),
 ('test/SharpLink.UnitTests/Protocol/ProtocolV2NegotiatorTests.cs',
  'SharpLink.UnitTests.Protocol',
  'ProtocolV2NegotiatorTests',
  'ServerResultShouldValidateToEquivalentClientSnapshot',
  450,
  496),
 ('test/SharpLink.UnitTests/Protocol/ProtocolV2NegotiatorTests.cs',
  'SharpLink.UnitTests.Protocol',
  'ProtocolV2NegotiatorTests',
  'RepeatedInputsShouldProduceEquivalentResultsAndErrors',
  499,
  540))
ATTRIBUTE_LINES = (185, 185, 17, 54, 9, 47, 67, 100, 163, 190, 256, 300, 352, 406, 449, 498)
RUNTIMES = [{'framework': 'Microsoft.AspNetCore.App', 'version': '10.0.14'}, {'framework': 'Microsoft.NETCore.App', 'version': '10.0.14'}]
EXCEPTION_CATEGORIES = {'System.Exception': 'exception',
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
 'System.TimeoutException': 'timeout',
 'SharpLink.Runtime.PhysicalTaskFailureException': 'physical-task-failure',
 'System.OperationCanceledException': 'operation-cancelled',
 'System.Threading.Tasks.TaskCanceledException': 'task-cancelled'}
EXCEPTION_TAGS = (((0, 1),
  'System.Exception',
  'the wire TimeBudget must be the remaining sampled once when the frame is created',
  'wire-budget-sample',
  'test/SharpLink.UnitTests/Client/SharpLinkClientTimeBudgetTests.cs',
  231),
 ((0, 1),
  'System.Exception',
  'the producer must belong to the exact emitted head',
  'producer-head-identity',
  'test/SharpLink.UnitTests/Client/SharpLinkClientTimeBudgetTests.cs',
  241),
 ((0, 1),
  'System.Exception',
  'producer entry must not complete the pending call',
  'premature-pending-completion',
  'test/SharpLink.UnitTests/Client/SharpLinkClientTimeBudgetTests.cs',
  242),
 ((0, 1),
  'System.Exception',
  'an emitted client-stream Request whose budget elapsed must fail through its pending deadline',
  'deadline-code',
  'test/SharpLink.UnitTests/Client/SharpLinkClientTimeBudgetTests.cs',
  248),
 ((0, 1),
  'System.Exception',
  'an expired head must never enter the producer writer',
  'expired-producer-start',
  'test/SharpLink.UnitTests/Client/SharpLinkClientTimeBudgetTests.cs',
  250),
 ((0, 1),
  'System.Exception',
  'expected SharpLinkException',
  'missing-expected-sharplink-error',
  'test/SharpLink.UnitTests/Client/SharpLinkClientTimeBudgetTests.cs',
  664),
 ((0, 1),
  'System.Exception',
  'expected exactly one ready connection',
  'ready-connection-count',
  'test/SharpLink.UnitTests/Client/SharpLinkClientTimeBudgetTests.cs',
  650),
 ((4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15),
  'SharpLink.Runtime.SharpLinkProtocolViolationException',
  'Peer binding limits are invalid.',
  'peer-binding-limits-invalid',
  'src/SharpLink.Runtime/ProtocolV2/ProtocolV2Negotiator.cs',
  352))
CODES = frozenset(('PD_HELPERS','PD_SOURCE','PD_SDK','PD_RUNTIME','PD_SELECTION','PD_PREPARATION','PD_COMMANDS','PD_ARGV','PD_SHAPE','PD_LIMIT','PD_EXCEPTION','PD_JUNIT','PD_REPORTS','PD_OWNER','PD_EXIT','PD_RETRY','PD_CASE_FAILED','PD_INCOMPLETE','PD_BUDGET','PD_PUBLIC'))



def load(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    obj = importlib.util.module_from_spec(spec); spec.loader.exec_module(obj)
    return obj


def verify_source(r, root):
    catalog = r.read_json(root / 'eng/unit-census.json')
    r.require(r.sha(root / 'eng/unit-census.json') == HELPERS['eng/unit-census.json'], 'PD_SOURCE')
    for name, digest in catalog['sourcePins'].items():
        r.require(r.sha(root / name) == digest, 'PD_SOURCE')
    for name, digest in SOURCE_PINS.items():
        r.require(r.sha(root / name) == digest, 'PD_SOURCE')


def verify_isolated_sdk(lane, census):
    r = lane.r
    dotnet = Path(census.shutil.which('dotnet', path=lane.env['PATH']) or '').resolve()
    root = dotnet.parent
    temporary = os.environ.get('RUNNER_TEMP')
    r.require(type(temporary) is str and Path(temporary).is_absolute() and
              root == Path(temporary) / 'stream-negotiation-dotnet' and dotnet.name == 'dotnet' and dotnet.is_file(), 'PD_SDK')
    r.legacy.directory_identity_strict(root)
    for name in ('DOTNET_ROOT', 'DOTNET_ROOT_X64'):
        if name in lane.env: r.require(Path(lane.env[name]) == root, 'PD_SDK')
    r.require(lane.env.get('DOTNET_MULTILEVEL_LOOKUP') == '0', 'PD_SDK')


def verify_helpers():
    for name, expected in HELPERS.items():
        path = ROOT / name
        if path.is_symlink() or hashlib.sha256(path.read_bytes()).hexdigest() != expected:
            raise RuntimeError('PD_HELPERS')


def selected(r, ordinary):
    output = []
    for index, (source, namespace, cls, method, start, end) in enumerate(CASES):
        if index == 1: continue
        matches = [(uid, row) for uid, row in ordinary.items()
                   if row['type']['namespace'] == namespace and row['type']['typeName'] == cls and
                   row['type']['methodName'] == method]
        r.require(len(matches) == (2 if index == 0 else 1), 'PD_SELECTION')
        # Hash order is solely stable case identity, NEVER a boolean-value inference.
        matches.sort(key=lambda pair: hashlib.sha256(pair[0].encode()).hexdigest())
        for uid, row in matches:
            r.require(row['uid'] == uid and type(uid) is str and uid and
                      not uid.startswith(('-', '@')) and len(uid) <= 4096 and
                      not any(ord(c) < 32 for c in uid), 'PD_SELECTION')
            loc = row['location']
            r.require(set(loc) == {'file', 'lineStart', 'lineEnd'} and loc['file'] == '/_/' + source and
                      type(loc['lineStart']) is int and loc['lineStart'] == ATTRIBUTE_LINES[index] and
                      type(loc['lineEnd']) is int and loc['lineEnd'] == end, 'PD_SELECTION')
            output.append((uid, row))
    r.require(len(output) == 16 and len({uid for uid, _ in output}) == 16, 'PD_SELECTION')
    return output


def boolean_pair_proven(selections):
    if len(selections) < 2: return False
    names = [row['displayName'] for _, row in selections[:2]]
    return set(names) == {CASES[0][3] + '(True)', CASES[0][3] + '(False)'}


def argument_identity(index, selection, pair_proven=False):
    uid, row = selection
    result = {'caseID': hashlib.sha256(uid.encode()).hexdigest(),
              'kind': 'opaque-boolean-case' if index < 2 else 'parameterless',
              'parameter': 'expiresDuringEmission' if index < 2 else None,
              'value': None,
              'limitation': 'boolean-value-not-proven' if index < 2 else 'none'}
    # Pinned TUnit default formatting is conditional: a changed/custom name
    # stays opaque. No substring, UID-order or attribute-order inference.
    if index < 2 and pair_proven:
        exact = {CASES[index][3] + '(True)': True, CASES[index][3] + '(False)': False}
        if row['displayName'] in exact:
            result.update(kind='source-literal-boolean', value=exact[row['displayName']],
                          limitation='none', proof='pinned-default-display-name-exact-match')
    return result


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
    r.require(test['status'] in ('passed', 'failed', 'error', 'timedOut', 'cancelled'), 'PD_EXCEPTION')
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


def source_frames(index, stack):
    source, namespace, cls, method, start, end = CASES[index]
    frame_map = {}; full = namespace + '.' + cls
    signature = 'Boolean expiresDuringEmission' if index < 2 else ''
    for line in range(start, end + 1):
        frame_map[f'   at {full}.{method}({signature}) in /_/{source}:line {line}'] = (source, method, line)
    helpers = (('Ensure', 'Boolean condition, String message', 759, 763),
               ('CaptureSharpLinkExceptionAsync', 'Task operation', 654, 665),
               ('GetOnlyReadyConnection', 'SharpLinkClient client', 644, 652)) if index < 4 else (
               ('Ensure', 'Boolean condition, String message', 621, 625),
               ('CaptureSharpLinkException', 'Action action', 576, 587),
               ('CaptureArgumentException', 'Action action', 589, 600),
               ('CreatePolicy', 'ProtocolV2Capabilities supported, IReadOnlyList`1 providers, UInt16 minorVersion, Int32 maxFramePayloadBytes, Int32 streamReceiveWindowBytes, Int32 connectionReceiveWindowBytes', 556, 569))
    for member, signature, first, last in helpers:
        for line in range(first, last + 1):
            frame_map[f'   at {full}.{member}({signature}) in /_/{source}:line {line}'] = (source, member, line)
    if index < 4:
        product = 'src/SharpLink.Client/SharpLinkClient.cs'
        for member, signature, first, last in (
                ('DisposeAsync', '', 139, 139),
                ('StopAsync', 'CancellationToken cancellationToken', 141, 159),
                ('StopCoreAsync', '', 161, 218),
                ('ThrowStopCleanupFailures', 'List`1 failures', 255, 262)):
            for line in range(first, last + 1):
                frame_map[f'   at SharpLink.Client.SharpLinkClient.{member}({signature}) in /_/{product}:line {line}'] = (product, member, line)
    if index >= 4:
        product = 'src/SharpLink.Runtime/ProtocolV2/ProtocolV2Negotiator.cs'
        specs = (
            ('SharpLink.Runtime.ProtocolV2NegotiationPolicy', 'Create', 'UInt16 minorVersion, ProtocolV2Capabilities supportedCapabilities, Int32 maxFramePayloadBytes, Int32 streamReceiveWindowBytes, Int32 connectionReceiveWindowBytes, IReadOnlyList`1 compressionProviders, Nullable`1 bindingLimits, Guid serverInstanceId, Guid registryEpoch', 46, 130),
            ('SharpLink.Runtime.ProtocolV2Negotiator', 'ValidatePeerOffer', 'ProtocolV2HandshakeRequest& offer', 345, 378))
        for typ, member, signature, first, last in specs:
            for line in range(first, last + 1):
                frame_map[f'   at {typ}.{member}({signature}) in /_/{product}:line {line}'] = (product, member, line)
    if index < 4:
        physical = (
            ('src/SharpLink.Client/ClientStreamPublicationGroup.cs', 'SharpLink.Client.ClientStreamPublicationGroup', 'CompleteCleanupAsync', 'TaskCompletionSource completion, RpcDeadline deadline, Exception setupFailure', 87, 130),
            ('src/SharpLink.Client/ClientStreamPublicationGroup.cs', 'SharpLink.Client.ClientStreamPublicationGroup', 'PublishCleanupAsync', 'SharpLinkException error', 132, 147),
            ('src/SharpLink.Client/ClientStreamPublicationGroup.cs', 'SharpLink.Client.ClientStreamPublicationGroup', 'FinishAsync', 'Exception publicationFailure, Boolean startupFailureOwnedByCaller', 245, 360),
            ('src/SharpLink.Runtime/FrameworkTaskSupervisor.cs', 'SharpLink.Runtime.FrameworkTaskSupervisor', 'DrainCoreAsync', 'Task signal', 409, 436),
            ('src/SharpLink.Runtime/RpcSession.StreamPublicationFence.cs', 'SharpLink.Runtime.RpcSession', 'CompleteStreamFenceCoreAsync', 'RpcStreamPublication group, RpcFramePublicationRole role', 28, 75))
        for path, typ, member, signature, first, last in physical:
            for line in range(first, last + 1):
                frame_map[f'   at {typ}.{member}({signature}) in /_/{path}:line {line}'] = (path, member, line)
    lines = stack.splitlines()
    if len(lines) > 4096: raise ValueError('PD_LIMIT')
    output = [dict(zip(('source', 'member', 'line'), frame_map[raw])) for raw in lines if raw in frame_map]
    return output[:64], len(lines) - len(output), max(0, len(output) - 64)


def projected_exception(r, index, exception):
    exception_shape(r, exception)
    result = []; current = exception
    while current is not None:
        frames, unknown, omitted = source_frames(index, current.get('stackTrace', ''))
        row = {'depth': len(result), 'category': EXCEPTION_CATEGORIES.get(current['type'], 'unknown'),
               'assertionTag': 'unknown', 'sourceFrames': frames, 'unrecognizedStackLineCount': unknown, 'omittedRecognizedStackLineCount': omitted}
        for indexes, typ, message, tag, path, line in EXCEPTION_TAGS:
            if index in indexes and (current['type'], current['message']) == (typ, message):
                row.update(assertionTag=tag, exactMessageSource={'source': path, 'line': line})
        result.append(row); current = current.get('innerException')
    return result


def classify(execution, native, junit, index, selection, pair_proven=False):
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
            'argumentIdentity': argument_identity(index, selection, pair_proven),
            'exceptionEvidenceScope': 'serialized-innerException-chain-only',
            'aggregateSiblingCausesObserved': False,
            'bodyVersusDisposal': 'inconclusive' if index < 4 and test['status'] != 'passed' else 'not-applicable',
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


def prepare_sdk_metadata(lane,sdkroot):
    # Fresh pinned SDKs create this empty directory during Build. Prepare only
    # that exact absent directory, then seal it independently before any SDK
    # command. Neither workload markers nor arbitrary preexisting content pass.
    r=lane.r;sdkroot=Path(sdkroot)
    parent_identity=r.legacy.directory_identity_strict(sdkroot)
    metadata=sdkroot/'metadata'
    r.require(not os.path.lexists(metadata),'UE_SDK_METADATA')
    metadata.mkdir(mode=0o755)
    r.legacy.verify_directory(sdkroot,parent_identity)
    identity=r.legacy.directory_identity_strict(metadata)
    r.legacy.verify_directory(metadata,identity,empty=True)
    lane.trees[metadata]=r.snapshot(metadata);lane.seal_domains[metadata]=('sdk',None)
    return metadata


def prepare(lane,census,results):
    r=lane.r
    verify_isolated_sdk(lane,census)
    verify_helpers()
    r.require(r.sha(ROOT/'eng/unit-census.json')==results.CATALOG_SHA256,'UE_CATALOG')
    dotnet=Path(census.shutil.which('dotnet',path=lane.env['PATH']) or '').resolve()
    r.require(dotnet.is_file(),'UC_SDK_MISSING')
    prepare_sdk_metadata(lane,dotnet.parent)
    census.execute(lane)
    lane.check()
    first=r.read_json(lane.evidence/'ledger1'/'ledger.json')
    second=r.read_json(lane.evidence/'ledger2'/'ledger.json')
    ordinary,explicit=results.expected_ordinary(first,second)
    lane.check()
    r.require(lane.stable and lane.unfiltered and lane.durable and not lane.errors and time.monotonic()<lane.stop,'UE_PREPARATION')
    r.require(lane.sequence==32,'UE_COMMANDS')
    r.require(lane.identity['sdk']=='10.0.114' and sorted(lane.identity['installedRuntimes'],key=lambda row:row['framework'])==RUNTIMES,'PD_RUNTIME')
    certificate={'identity':lane.identity,'metadataQualified':True,'ordinaryCount':len(ordinary),'explicitCount':len(explicit),
        'canonicalLedgerSHA256':r.digest(first['actual']),'preparationDeadlineMonotonic':lane.stop}
    r.write_json(lane.evidence,'preparation.json',certificate);lane.extend_evidence(['preparation.json'])
    lane.check()
    r.require(time.monotonic()<lane.stop,'UE_PREPARATION')
    return ordinary,explicit


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
    verify_helpers(); verify_source(r, lane.source)
    lane.check()
    now = time.monotonic()
    r.require(now < lane.stop and now < overall_stop, 'PD_BUDGET')
    lane.preparation_complete = True
    lane.stop = min(overall_stop, now + 3600)
    # The original checkout remains an independently sealed, unbuilt input.
    # Rebase controls must use it, not the clone containing generated bin/obj.
    lane.run('stream-negotiation-python-controls', [sys.executable, '-B', ROOT / 'eng/tests/test_stream_negotiation_diagnostic.py'], ROOT, 90)
    r.require(lane.sequence == 33, 'PD_COMMANDS')
    dotnet = next(path for path in lane.inputs if path.name == 'dotnet' and path.parent in lane.trees)
    dll = lane.source / 'test/SharpLink.UnitTests/bin/Release/net10.0/SharpLink.UnitTests.dll'
    lane.env['TUNIT_ENABLE_JUNIT_REPORTER'] = '1'
    pair_proven = boolean_pair_proven(selections)
    for index, selection in enumerate(selections):
        lane.check(); verify_source(r, lane.source)
        r.require(r.sha(dll) == lane.identity['binarySHA256'], 'UC_INPUT_CHANGED')
        uid, _ = selection; label = 'stream-negotiation-case-' + str(index + 1)
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
        result = classify(execution, native, junit, index, selection, pair_proven)
        exit_code = state['receipt']['exitCode']
        r.require(type(exit_code) is int and exit_code == (0 if result['selectedPassed'] else 2), 'PD_EXIT')
        result.update(ownerClosed=True, bodyExitCode=exit_code,
                      nativeFileSHA256=r.sha(report / 'session.tunit-report.json'), junitFileSHA256=r.sha(report / 'junit.xml'))
        if not result['selectedPassed']:
            lane.validated_failure_receipts.append(state['receipt'])
        lane.case_facts.append(result)
        lane.check()
        if not result['selectedPassed']: lane.record(r.Rejected('PD_CASE_FAILED'))
    r.require(lane.sequence == 49 and len(lane.case_facts) == 16, 'PD_COMMANDS')
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


def infrastructure_qualified(lane, complete):
    if not complete or not lane.durable or time.monotonic() >= lane.stop: return False
    queue = list(lane.errors); seen = set()
    while queue:
        error = queue.pop()
        if id(error) in seen: continue
        seen.add(id(error))
        if isinstance(error, BaseExceptionGroup): queue.extend(error.exceptions)
        elif type(error) is lane.r.Rejected and error.code == 'PD_CASE_FAILED': pass
        elif isinstance(error, lane.owner.CommandFailure) and any(error.outcome is receipt for receipt in lane.validated_failure_receipts): pass
        else: return False
        if error.__cause__ is not None: queue.append(error.__cause__)
        if error.__context__ is not None: queue.append(error.__context__)
    return True


def finish(lane, census, execution):
    r = lane.r
    try: lane.check()
    except BaseException as error: lane.record(error)
    complete = lane.diagnostic_complete and len(lane.case_facts) == 16
    qualified = bool(complete and not lane.errors and lane.durable and time.monotonic() < lane.stop)
    try:
        summary = {'schemaVersion': 1, 'scope': 'sixteen-ordinary-timed-stream-negotiation-diagnostic',
            'identity': public_identity(r, lane.identity), 'sourceTrees': dict(SOURCE_TREES),
            'candidateCatalogSHA256': HELPERS['eng/unit-census.json'],
            'sourceUnchangedFromHead': '72d728bf226ef3d5c9a5ca4d547f798810147880',
            'requiredSdk': '10.0.114', 'requiredRuntime': '10.0.14',
            'preparationQualifiedAtHandoff': lane.preparation_complete,
            'diagnosticComplete': complete, 'diagnosticQualified': qualified,
            'infrastructureQualified': infrastructure_qualified(lane, complete),
            'observedNonpassingCaseCount': sum(not row['selectedPassed'] for row in lane.case_facts),
            'allSelectedPassed': bool(complete and all(row['selectedPassed'] for row in lane.case_facts)),
            'fullSuiteQualified': False, 'requiredSelectedCaseCount': 16,
            'expectedOrdinaryMetadataCount': 2879, 'expectedExplicitMetadataCount': 5,
            'historicalFullRun': {'runId': 41962521900, 'passed': 2607, 'errors': 272, 'qualified': False,
                'evidenceSHA256': HELPERS['eng/stream-negotiation-history/current-ordinary-result.json']},
            'originalFullRun': {'runId': 41940224644, 'passed': 2589, 'errors': 290, 'qualified': False},
            'historicalArgumentMappingAvailable': False,
            'exceptionEvidenceScope': 'serialized-innerException-chain-only',
            'aggregateSiblingCausesObserved': False,
            'bodyVersusDisposalInference': 'known-frames-only-no-causal-inference',
            'errorOccurrences': len(lane.errors), 'rejections': rejections(lane, execution),
            'evidenceDurable': lane.durable, 'cases': lane.case_facts, 'exit': 0 if qualified else 1}
        r.write_json(lane.evidence, 'stream-negotiation-diagnostic-summary.json', summary)
        lane.extend_evidence(['stream-negotiation-diagnostic-summary.json'])
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
        work = Path(tempfile.mkdtemp(prefix='slsn-', dir='/tmp')); os.chmod(work, 0o700); ACTIVE['work'] = work
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
        lane.validated_failure_receipts = []; lane.case_facts = []; lane.diagnostic_complete = False; lane.preparation_complete = False
        lane.stop = min(lane.stop, started + 3600)
        try:
            for name, expected in HELPERS.items(): lane.r.require(lane.r.sha(ROOT / name) == expected, 'PD_HELPERS')
            verify_isolated_sdk(lane, census)
            ordinary, explicit = prepare(lane, census, execution)
            selections = handoff_selections(lane, ordinary, explicit)
            run_cases(lane, execution, selections, started + 7200)
        except BaseException as error: lane.record(error)
        return finish(lane, census, execution)
    except BaseException as error:
        if lane is not None: lane.record(error)
        else:
            ACTIVE['errors'].append(error)
            if census is not None and ACTIVE['work'] is not None: census.persist(ACTIVE['work'], ACTIVE['errors'], 999)
            elif execution_runner is not None: execution_runner.bootstrap_persist(ACTIVE['work'], ACTIVE['errors'])
        try: os.write(2, b'STREAM_NEGOTIATION_DIAGNOSTIC_BOOTSTRAP_FAILED\n')
        except BaseException as secondary: ACTIVE['errors'].append(secondary)
        return 1


def guarded_main():
    try: return main()
    except BaseException as error:
        ACTIVE['errors'].append(error)
        try: os.write(2, b'STREAM_NEGOTIATION_DIAGNOSTIC_OUTERMOST_FAILED\n')
        except BaseException as secondary: ACTIVE['errors'].append(secondary)
        return 1


if __name__ == '__main__': raise SystemExit(guarded_main())
