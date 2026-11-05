"""Python-only controls; never invokes a .NET executable."""
import contextlib
import copy
import importlib.util
import io
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch
import xml.etree.ElementTree as ET
sys.dont_write_bytecode = True
ROOT = Path(__file__).resolve().parents[2]
BASE = ROOT
if len(sys.argv) == 3 and sys.argv[1] == '--reviewed-base':
    BASE = Path(sys.argv[2]); sys.argv[1:] = []


def load(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    obj = importlib.util.module_from_spec(spec); spec.loader.exec_module(obj); return obj


d = load('test_pure_runner', ROOT / 'eng/run-parser-handshake-validation.py')
e = load('test_pure_execution', BASE / 'eng/unit-execution-results.py')
c = load('test_pure_census', BASE / 'eng/run-unit-census.py')
o = load('test_pure_owner', BASE / 'eng/package-smoke-process.py')
r = e.census
RebaseControls = load('test_candidate_rebase', ROOT / 'eng/tests/test_three_fixture_census.py').Controls
CANARY = 'PRIVATE_CANARY_8fe5fca2'


def fixture(index=0, status='error', exception=None):
    path, ns, cls, method, start, end = d.CASES[index]
    if index in d.PARAMETERIZED_CASE_INDICES: start = d.PARAMETERIZED_ATTRIBUTE_LINE
    uid = 'private-uid-' + str(index)
    row = {'uid': uid, 'displayName': CANARY, 'type': {'namespace': ns, 'typeName': cls, 'methodName': method},
           'location': {'file': '/_/' + path, 'lineStart': start, 'lineEnd': end}}
    test = {'id': uid, 'displayName': CANARY, 'methodName': method, 'className': cls,
            'filePath': '/_/' + path, 'lineNumber': start, 'endLineNumber': end, 'status': status, 'durationMs': 1.0}
    if status != 'passed':
        test['exception'] = exception or {'type': 'Unknown.' + CANARY, 'message': CANARY, 'stackTrace': '/private/' + CANARY}
    native = {'schemaVersion': 1, 'publicationGeneration': 'a' * 32, 'assemblyName': 'SharpLink.UnitTests',
              'machineName': CANARY, 'timestamp': CANARY, 'tunitVersion': '1.73.19.0', 'operatingSystem': CANARY,
              'runtimeVersion': '.NET 10.0.14', 'totalDurationMs': 1.0,
              'groups': [{'namespace': ns, 'className': cls, 'tests': [test]}]}
    refresh(native)
    return (uid, row), native, test


def refresh(native):
    all_tests = []
    for group in native['groups']:
        group['summary'] = e.summary(group['tests']); all_tests += group['tests']
    native['summary'] = e.summary(all_tests)


def junit(native):
    tests = [t for g in native['groups'] for t in g['tests']]
    statuses = [t['status'] for t in tests]
    attrs = {'name': 'SharpLink.UnitTests', 'tests': str(len(tests)), 'failures': str(statuses.count('failed')),
             'errors': str(sum(s in ('error', 'timedOut', 'cancelled') for s in statuses)), 'skipped': str(statuses.count('skipped')),
             'time': '0.001', 'timestamp': CANARY}
    root = ET.Element('testsuites', attrs); suite = ET.SubElement(root, 'testsuite', {**attrs, 'hostname': CANARY})
    props = ET.SubElement(suite, 'properties')
    for name in ('framework', 'platform', 'runtime'):
        ET.SubElement(props, 'property', {'name': name, 'value': CANARY})
    for test in tests:
        case = ET.SubElement(suite, 'testcase', {'name': e.xml_name(test['displayName']), 'classname': test['className'], 'time': '0.001'})
        if test['status'] != 'passed':
            kind = {'failed': 'failure', 'error': 'error', 'timedOut': 'error', 'cancelled': 'error', 'skipped': 'skipped'}[test['status']]
            ex = test.get('exception', {'type': 'unknown', 'message': 'unknown'})
            node = ET.SubElement(case, kind, {'message': e.xml_name(ex['message']), 'type': ex['type']})
            node.text = CANARY + '\nDifferent from native stack by contract'
    return ET.tostring(root, encoding='unicode')


class Controls(unittest.TestCase):
    def reject(self, action):
        with self.assertRaises(Exception): action()

    def classify(self, index=0, status='error', exception=None):
        selection, native, test = fixture(index, status, exception)
        return d.classify(e, native, junit(native), index, selection)

    def test_pins_match_reviewed_helpers_and_source(self):
        for name, expected in d.HELPERS.items(): self.assertEqual(r.sha(BASE / name), expected)
        d.verify_source(r, BASE)

    def test_candidate_tags_have_exact_pinned_source_literals(self):
        for index, rows in d.ASSERTION_LITERALS.items():
            for message, tag, path, line in rows:
                self.assertIn('"' + message + '"', (BASE / path).read_text().splitlines()[line - 1])
                result = self.classify(index, exception={'type': 'System.InvalidOperationException' if path in (d.STACK, d.GRAMMAR) else 'System.Exception', 'message': message})
                self.assertEqual(result['exceptions'][0]['assertionTag'], tag)
                self.assertEqual(result['exceptions'][0]['exactMessageSource'], {'source': path, 'line': line})
        self.assertEqual(self.classify(0, exception={'type': 'System.Exception', 'message': 'unsigned request ID'})['exceptions'][0]['assertionTag'], 'unknown')
        self.assertIn('Ensure(Unsafe.SizeOf<OwnedFrame>()', (BASE / d.PROGRESS).read_text().splitlines()[681])

    def test_selected_spans_match_rebound_catalog(self):
        catalog = r.read_json(BASE / 'eng/unit-census.json')
        for index, (path, namespace, cls, method, start, end) in enumerate(d.CASES):
            matches = [m for m in catalog['methods'] if (m['source'], m['namespace'], m['className'], m['methodName']) == (path, namespace, cls, method)]
            self.assertEqual(len(matches), 1)
            self.assertEqual((matches[0]['signatureLine'], matches[0]['endLine']), (start, end))
            self.assertEqual(matches[0]['initialEligibility'], 'ordinary')
            self.assertEqual(matches[0]['expectedCases'], 3 if index in d.PARAMETERIZED_CASE_INDICES else 1)
        self.assertEqual(len(d.CASES), 23)
        self.assertEqual(e.CATALOG_SHA256, r.sha(BASE / 'eng/unit-census.json'))


    def test_historical_lanes_preserved_but_not_counted_as_current_controls(self):
        import ast
        historical = {'.github/workflows/pure-failure-diagnostic.yml': 'ba7cfd5620a4f99922f957b346a6db4de7123c7cff8ebf14d71dae785b3d1466', '.github/workflows/parser-handshake-diagnostic.yml': '5622a4bdd55f5bf782caa3c228ebedc5c646c34934848ead62ca6065d6723f2e', 'eng/run-pure-failure-diagnostic.py': 'fdaf699eb2c010702d7bee82d72d9b48a797d9f289da6bb9bb1e64b676e1c323', 'eng/run-parser-handshake-diagnostic.py': 'f2c3c59b51a56d911fdd7fce211c195b68763a2a868406961820398511aff707', 'eng/tests/test_pure_failure_diagnostic.py': '8e1fc528ee0e31fa61e440dd0402e4fb0a1b0f75e5c3b51dd7236f1f674e5af7', 'eng/tests/test_parser_handshake_diagnostic.py': 'c3d28e951c44293ed6810d0e906afb0aed552a0a9ccf09c1641f66413de49904'}
        for path, expected in historical.items(): self.assertEqual(r.sha(ROOT / path), expected)
        for path in ('eng/run-pure-failure-diagnostic.py', 'eng/run-parser-handshake-diagnostic.py'):
            module = ast.parse((ROOT / path).read_text())
            helpers = next(ast.literal_eval(node.value) for node in module.body if isinstance(node, ast.Assign) and any(isinstance(target, ast.Name) and target.id == 'HELPERS' for target in node.targets))
            self.assertNotEqual(helpers['eng/unit-census.json'], e.CATALOG_SHA256)
        self.assertNotIn('eng/tests/test_pure_failure_diagnostic.py', d.HELPERS)
        self.assertNotIn('eng/tests/test_parser_handshake_diagnostic.py', d.HELPERS)
        self.assertIn('eng/tests/test_parser_handshake_validation.py', d.HELPERS)

    def test_seven_diagnostic_failures_and_all_counters_are_preserved_exactly(self):
        path = 'eng/fixture-migration-baseline/parser-handshake-diagnostic-result.json'
        self.assertEqual(r.sha(ROOT / path), 'f2f09025a1cc611049c32ec83ad6be2ef2cd8a6351efb9191588a02d761424e1')
        evidence = r.read_json(ROOT / path); summary = evidence['summary']
        self.assertEqual((evidence['runId'], evidence['jobId'], evidence['head']), (41957258310, 127015025107, 'd6c4d41bca201b2bd82e2e917cfd93d701976b5a'))
        self.assertTrue(summary['diagnosticComplete']); self.assertFalse(summary['diagnosticQualified'])
        self.assertEqual(summary['errorOccurrences'], 21)
        self.assertEqual(summary['rejections'], {'codes': ['PD_CASE_FAILED'], 'unclassifiedExceptionNodes': 7})
        self.assertEqual(len(evidence['stages']), 37)
        self.assertEqual([row['status'] for row in summary['cases']], ['error'] * 7 + ['passed'] * 4)
        self.assertEqual([row['bodyExitCode'] for row in summary['cases']], [2] * 7 + [0] * 4)
        self.assertTrue(all(row['ownerClosed'] for row in summary['cases']))
        self.assertEqual([row['method'].rsplit('.', 1)[1] for row in summary['cases'][:7]], [row[3] for row in d.CASES[13:20]])
        self.assertEqual([row['method'].rsplit('.', 1)[1] for row in summary['cases'][7:]], [d.CASES[i][3] for i in (4, 5, 20, 21)])

    def test_replaced_messages_and_argument_runtime_suffix_remain_unknown(self):
        for index, typ, message in (
            (17, 'System.Exception', "the caller's required capabilities must remain explicit policy input"),
            (19, 'System.Exception', '2.0 increments the published minor 3 once'),
            (16, 'System.ArgumentException', 'Protocol3 requires flow control, cancellation reasons, and contract manifests.'),
            (16, 'System.ArgumentException', "Protocol3 requires flow control, cancellation reasons, and contract manifests. (Parameter 'request')")):
            row = self.classify(index, exception={'type': typ, 'message': message})['exceptions'][0]
            self.assertEqual(row['assertionTag'], 'unknown'); self.assertNotIn('exactMessageSource', row)
        for index in range(13, 20):
            for message, tag, path, line in d.ASSERTION_LITERALS[index]:
                for typ, value in (('System.InvalidOperationException', message), ('System.Exception', message + CANARY)):
                    self.assertEqual(self.classify(index, exception={'type': typ, 'message': value})['exceptions'][0]['assertionTag'], 'unknown')

    def test_shifted_current_helpers_are_exact_and_old_coordinates_unknown(self):
        for index, line, old_line in ((13, 903, 877), (17, 624, 620), (19, 74, 69)):
            source, namespace, cls, _, _, _ = d.CASES[index]
            exact = f'   at {namespace}.{cls}.Ensure(Boolean condition, String message) in /_/{source}:line {line}'
            stale = exact.replace(f':line {line}', f':line {old_line}')
            row = self.classify(index, exception={'type': 'System.Exception', 'message': CANARY, 'stackTrace': exact + '\n' + stale})['exceptions'][0]
            self.assertEqual(row['sourceFrames'], [{'source': source, 'member': 'Ensure', 'line': line}])
            self.assertEqual(row['unrecognizedStackLineCount'], 1)
            self.assertIn('throw new Exception(message);', (ROOT / source).read_text().splitlines()[line - 1])
        self.assertIn('var token = ProtocolV2FrameWriter.BeginFrame', (ROOT / d.PROTOCOL).read_text().splitlines()[834])

    def test_control_budget_extension_keeps_phase_and_each_case_bounded(self):
        with tempfile.TemporaryDirectory() as temp, patch.object(d.time, 'monotonic', return_value=1000.0):
            lane = self.make_lane(temp); lane.stop = 2000.0
            self.run_mock_cases(lane, 'all-pass')
            self.assertEqual(lane.stop, 2500.0)
            self.assertEqual(lane.sequence, 56)

    def test_changed_source_rejected_before_diagnostics(self):
        with patch.object(r, 'sha', return_value='0' * 64): self.reject(lambda: d.verify_source(r, BASE))

    def test_twenty_three_exact_source_method_identities(self):
        ordinary = dict(fixture(i)[0] for i in range(23))
        chosen = d.selected(r, ordinary)
        self.assertEqual(len(chosen), 23)
        for i, (uid, row) in enumerate(chosen): self.assertEqual(row['type']['methodName'], d.CASES[i][3])
        for mode in ('missing', 'duplicate', 'wrong-method', 'qualified-alias', 'wrong-namespace', 'wrong-path', 'wrong-start', 'wrong-end', 'unsafe-uid'):
            changed = copy.deepcopy(ordinary)
            uid = chosen[0][0]
            if mode == 'missing': del changed[uid]
            elif mode == 'duplicate': changed['duplicate'] = copy.deepcopy(changed[uid])
            elif mode == 'wrong-method': changed[uid]['type']['methodName'] += 'Extra'
            elif mode == 'qualified-alias': changed[uid]['type']['typeName'] = changed[uid]['type']['namespace'] + '.' + changed[uid]['type']['typeName']
            elif mode == 'wrong-namespace': changed[uid]['type']['namespace'] += '.Other'
            elif mode == 'wrong-path': changed[uid]['location']['file'] = '/private/' + CANARY
            elif mode == 'wrong-start': changed[uid]['location']['lineStart'] = True
            elif mode == 'wrong-end': changed[uid]['location']['lineEnd'] += 1
            else: changed['--injected'] = changed.pop(uid); changed['--injected']['uid'] = '--injected'
            with self.subTest(mode=mode): self.reject(lambda: d.selected(r, changed))

    def test_original_full_run_summary_names_bind_exactly_to_selection(self):
        # Verbatim selected method/status rows from run41940224644/job126958823032,
        # dcc3edbb. Failure rows are in EXECUTION-RESULT.json; the control pass
        # is independently retained in the original parsed summary. These are identity
        # evidence, NOT fabricated discovered UIDs or a replacement result.
        observed = [
            {'className': 'ProtocolV2Tests', 'count': 1, 'methodName': 'HeaderWithEveryByteInSeparateSegmentShouldParse', 'namespace': 'SharpLink.UnitTests.Protocol', 'source': 'test/SharpLink.UnitTests/Protocol/ProtocolV2Tests.cs', 'status': 'error'},
            {'className': 'ProtocolV2Tests', 'count': 1, 'methodName': 'RawFrameWriterAndTokenShouldRemainInternalImplementationDetails', 'namespace': 'SharpLink.UnitTests.Protocol', 'source': 'test/SharpLink.UnitTests/Protocol/ProtocolV2Tests.cs', 'status': 'passed'},
            {'className': 'ProtocolV2Tests', 'count': 1, 'methodName': 'WriterAndParserShouldRoundTripFrame', 'namespace': 'SharpLink.UnitTests.Protocol', 'source': 'test/SharpLink.UnitTests/Protocol/ProtocolV2Tests.cs', 'status': 'error'},
            {'className': 'SendPumpProgressIsolationTests', 'count': 1, 'methodName': 'ProgressFlagDoesNotGrowOwnedFrame', 'namespace': 'SharpLink.UnitTests.Runtime', 'source': 'test/SharpLink.UnitTests/Runtime/SendPumpProgressIsolationTests.cs', 'status': 'error'},
        ]
        observed += [
            {'className': 'Protocol3FrameAndHandshakeTests', 'count': 1, 'methodName': method, 'namespace': 'SharpLink.UnitTests.Protocol', 'source': d.GRAMMAR, 'status': 'passed'}
            for method in ('OnlyNewMagicAndPositive63CorrelationAreAccepted', 'CancellationHasExactlyOneKnownReasonByte')]
        ordinary = dict(fixture(i)[0] for i in range(23))
        observed += [
            {'className': 'SendPumpProgressIsolationTests', 'count': 1, 'methodName': 'StreamCompleteNeverOvertakesQueuedStreamData', 'namespace': 'SharpLink.UnitTests.Runtime', 'source': d.PROGRESS, 'status': 'passed'},
            {'className': 'FlushOperationStressTests', 'count': 1, 'methodName': 'SessionStopShouldCompleteEveryForceFlushWaiter', 'namespace': 'SharpLink.UnitTests.Runtime', 'source': d.FLUSH, 'status': 'passed'},
        ]
        observed += [
            {'className': 'ProtocolV2Tests', 'count': 1, 'methodName': 'ZeroThroughFourteenHeaderBytesShouldRemainPartial', 'namespace': 'SharpLink.UnitTests.Protocol', 'source': d.PROTOCOL, 'status': 'error'},
            {'className': 'Stack13FrameClearTests', 'count': 3, 'methodName': 'FrameInitializationShouldTouchOnlyItsOwnFifteenBytes', 'namespace': 'SharpLink.UnitTests.Runtime', 'source': d.STACK, 'status': 'error'},
            {'className': 'Stack13FrameClearTests', 'count': 1, 'methodName': 'ExactWriterLimitAndDisposedLeaseMustRemainEnforced', 'namespace': 'SharpLink.UnitTests.Runtime', 'source': d.STACK, 'status': 'error'},
        ]
        observed += [{'className': 'Protocol3FrameAndHandshakeTests', 'count': 1, 'methodName': 'HandshakeRequestPinsNewOffsetsAndStrictRevisionCapabilitiesAndLengths', 'namespace': 'SharpLink.UnitTests.Protocol', 'source': 'test/SharpLink.UnitTests/Protocol/Protocol3FrameAndHandshakeTests.cs', 'status': 'passed'}, {'className': 'Protocol3FrameAndHandshakeTests', 'count': 1, 'methodName': 'SuccessHandshakeCarriesExactAuthorityAndNoDowngrade', 'namespace': 'SharpLink.UnitTests.Protocol', 'source': 'test/SharpLink.UnitTests/Protocol/Protocol3FrameAndHandshakeTests.cs', 'status': 'passed'}, {'className': 'ProtocolV2ErrorCompatibilityTests', 'count': 1, 'methodName': 'StructuredErrorShapeShouldRejectPublishedMinorThreePeers', 'namespace': 'SharpLink.UnitTests.Protocol', 'source': 'test/SharpLink.UnitTests/Protocol/ProtocolV2ErrorCompatibilityTests.cs', 'status': 'error'}, {'className': 'ProtocolV2NegotiatorTests', 'count': 1, 'methodName': 'ClientOfferShouldContainOnlyPolicyAndAuthenticationInputs', 'namespace': 'SharpLink.UnitTests.Protocol', 'source': 'test/SharpLink.UnitTests/Protocol/ProtocolV2NegotiatorTests.cs', 'status': 'error'}, {'className': 'ProtocolV2NegotiatorTests', 'count': 1, 'methodName': 'ImplementedCapabilitiesShouldRemainExplicitAndProtocolRecognized', 'namespace': 'SharpLink.UnitTests.Protocol', 'source': 'test/SharpLink.UnitTests/Protocol/ProtocolV2NegotiatorTests.cs', 'status': 'error'}, {'className': 'ProtocolV2Tests', 'count': 1, 'methodName': 'HandshakeRequestAndResponseShouldRoundTrip', 'namespace': 'SharpLink.UnitTests.Protocol', 'source': 'test/SharpLink.UnitTests/Protocol/ProtocolV2Tests.cs', 'status': 'error'}, {'className': 'ProtocolV2Tests', 'count': 1, 'methodName': 'HealthControlFramesShouldRequireBoundedPayloadAndRequestId', 'namespace': 'SharpLink.UnitTests.Protocol', 'source': 'test/SharpLink.UnitTests/Protocol/ProtocolV2Tests.cs', 'status': 'error'}, {'className': 'ProtocolV2Tests', 'count': 1, 'methodName': 'InvalidLengthTypeFlagsAndRequestIdShouldBeRejected', 'namespace': 'SharpLink.UnitTests.Protocol', 'source': 'test/SharpLink.UnitTests/Protocol/ProtocolV2Tests.cs', 'status': 'error'}, {'className': 'ProtocolV2Tests', 'count': 1, 'methodName': 'MultipleFramesFollowedByHalfFrameShouldPreserveRemainder', 'namespace': 'SharpLink.UnitTests.Protocol', 'source': 'test/SharpLink.UnitTests/Protocol/ProtocolV2Tests.cs', 'status': 'error'}, {'className': 'ProtocolV2Tests', 'count': 1, 'methodName': 'ParserViolationsShouldCarryFixedLowCardinalityClassification', 'namespace': 'SharpLink.UnitTests.Protocol', 'source': 'test/SharpLink.UnitTests/Protocol/ProtocolV2Tests.cs', 'status': 'passed'}]
        self.assertEqual(len(observed), 21)
        self.assertEqual(sum(row['count'] for row in observed), 23)
        for evidence in observed:
            match = [row for row in ordinary.values() if row['type']['methodName'] == evidence['methodName']]
            self.assertEqual(len(match), evidence['count'])
            row = match[0]
            self.assertEqual(row['type']['typeName'], evidence['className'])
            self.assertEqual(row['type']['namespace'], evidence['namespace'])
            self.assertEqual(row['location']['file'], '/_/' + evidence['source'])
        self.assertEqual(len(d.selected(r, ordinary)), 23)
        for index in range(23):
            selection, native, test = fixture(index, 'passed')
            self.assertEqual(selection[1]['type']['typeName'], d.CASES[index][2])
            self.assertEqual(native['groups'][0]['className'], d.CASES[index][2])
            self.assertEqual(test['className'], d.CASES[index][2])
            self.assertTrue(d.classify(e, native, junit(native), index, selection)['selectedPassed'])

    def test_supported_attribute_or_signature_start_not_fuzzy(self):
        ordinary = dict(fixture(i)[0] for i in range(23))
        for index, row in enumerate(ordinary.values()):
            if index not in d.PARAMETERIZED_CASE_INDICES: row['location']['lineStart'] -= 1
        self.assertEqual(len(d.selected(r, ordinary)), 23)
        next(iter(ordinary.values()))['location']['lineStart'] -= 1
        self.reject(lambda: d.selected(r, ordinary))

    def test_parameterized_selection_requires_exact_three_distinct_opaque_uids(self):
        ordinary = dict(fixture(i)[0] for i in range(23))
        expected = sorted(fixture(i)[0][0] for i in d.PARAMETERIZED_CASE_INDICES)
        chosen = d.selected(r, dict(reversed(list(ordinary.items()))))
        self.assertEqual([chosen[i][0] for i in sorted(d.PARAMETERIZED_CASE_INDICES)], expected)
        self.assertEqual(len({uid for uid, _ in chosen}), 23)
        for mode in ('missing', 'extra', 'duplicate-native-uid', 'wrong-source', 'wrong-end', 'fuzzy-name', 'wrong-type'):
            changed = copy.deepcopy(ordinary); uid = fixture(9)[0][0]
            if mode == 'missing': del changed[uid]
            elif mode == 'extra': changed['extra-case'] = copy.deepcopy(changed[uid]); changed['extra-case']['uid'] = 'extra-case'
            elif mode == 'duplicate-native-uid': changed[uid]['uid'] = fixture(10)[0][0]
            elif mode == 'wrong-source': changed[uid]['location']['file'] = '/_/' + d.PROTOCOL
            elif mode == 'wrong-end': changed[uid]['location']['lineEnd'] += 1
            elif mode == 'fuzzy-name': changed[uid]['type']['methodName'] += 'Extra'
            else: changed[uid]['type']['typeName'] = 'OtherClass'
            with self.subTest(mode=mode): self.reject(lambda: d.selected(r, changed))

    def test_parameterized_location_is_exact_pinned_attributed_method_start(self):
        ordinary = dict(fixture(i)[0] for i in range(23))
        self.assertEqual([d.selected(r, ordinary)[i][1]['location']['lineStart'] for i in sorted(d.PARAMETERIZED_CASE_INDICES)], [9, 9, 9])
        for start in (8, 10, 11, 12, 13, 14, True, 9.0):
            changed = copy.deepcopy(ordinary); changed[fixture(9)[0][0]]['location']['lineStart'] = start
            with self.subTest(start=start): self.reject(lambda: d.selected(r, changed))
        catalog = r.read_json(BASE / 'eng/unit-census.json')
        method = next(m for m in catalog['methods'] if m['source'] == d.STACK and m['methodName'] == d.CASES[9][3])
        self.assertEqual((method['attributeLine'], method['signatureLine'], method['endLine']), (9, 13, 28))
        self.assertEqual(method['sourceProvenance']['attributes'], ['[Test]', '[Arguments(32)]', '[Arguments(1024)]', '[Arguments(65536)]'])

    def test_parameterized_argument_values_are_never_inferred_from_private_labels(self):
        ordinary = dict(fixture(i)[0] for i in range(23))
        for index in d.PARAMETERIZED_CASE_INDICES:
            row = ordinary[fixture(index)[0][0]]
            row['displayName'] = CANARY + '(wrong capacity)'
        chosen = d.selected(r, ordinary)
        self.assertEqual(len(chosen), 23)
        for index in d.PARAMETERIZED_CASE_INDICES:
            result = self.classify(index, 'passed')
            self.assertNotIn(CANARY, json.dumps(result))
            self.assertNotIn('capacity', result)

    def test_pass_and_prior_control_are_bounded_diagnostic_only(self):
        for i in range(23):
            result = self.classify(i, 'passed'); self.assertTrue(result['selectedPassed']); self.assertEqual(result['exceptions'], [])
            self.assertNotIn(CANARY, json.dumps(result))

    def test_unknown_category_and_all_raw_optional_canaries_do_not_escape(self):
        selection, native, test = fixture()
        native.update({key: CANARY for key in ('filter', 'artifactUrl', 'commitSha', 'branch', 'pullRequestNumber', 'repositorySlug')})
        native['sourceLinks'] = {key: CANARY for key in ('lineUrl', 'rangeUrl', 'rawUrl')}
        test.update({key: CANARY for key in ('startTime', 'endTime', 'output', 'errorOutput', 'sourceRelativePath', 'traceId', 'spanId')})
        test['categories'] = [CANARY]; test['additionalTraceIds'] = [CANARY]; test['customProperties'] = [{'key': CANARY, 'value': CANARY}]
        native['spans'] = [{'traceId': CANARY, 'spanId': CANARY, 'parentSpanId': CANARY, 'name': CANARY, 'spanType': CANARY,
            'source': CANARY, 'kind': CANARY, 'startTimeMs': 1, 'durationMs': 2, 'status': CANARY, 'statusMessage': CANARY,
            'tags': [{'key': CANARY, 'value': CANARY}], 'events': [{'name': CANARY, 'timestampMs': 1, 'tags': [{'key': CANARY, 'value': CANARY}]}],
            'links': [{'traceId': CANARY, 'spanId': CANARY}]}]
        result = d.classify(e, native, junit(native), 0, selection)
        self.assertNotIn(CANARY, json.dumps(result)); self.assertEqual(result['exceptions'][0]['category'], 'unknown')
        self.assertEqual(result['exceptions'][0]['assertionTag'], 'unknown')
        self.assertFalse(result['selectedPassed'])

    def test_exact_protocol_message_tags_and_no_near_matches(self):
        cases = [(0, 'Frame Request must use a positive 63-bit correlation ID.', 'request-id-positive-63-bit'),
                 (1, 'Cancel payload must contain exactly one reason byte.', 'cancel-payload-one-reason-byte')]
        for index, message, tag in cases:
            ex = {'type': 'SharpLink.Runtime.SharpLinkProtocolViolationException', 'message': message}
            result = self.classify(index, exception=ex)
            self.assertEqual(result['exceptions'][0]['assertionTag'], tag); self.assertNotIn(message, json.dumps(result))
            for changed in (message + CANARY, message.lower(), CANARY + message):
                ex['message'] = changed
                self.assertEqual(self.classify(index, exception=ex)['exceptions'][0]['assertionTag'], 'unknown')
            ex['message'] = message; ex['type'] = 'Unknown'
            self.assertEqual(self.classify(index, exception=ex)['exceptions'][0]['assertionTag'], 'unknown')

    def test_ensure_tags_only_for_corresponding_case_and_type(self):
        self.assertEqual(self.classify(0, exception={'type': 'System.Exception', 'message': 'frame parsed'})['exceptions'][0]['assertionTag'], 'frame-parsed')
        self.assertEqual(self.classify(1, exception={'type': 'System.Exception', 'message': 'frame parsed'})['exceptions'][0]['assertionTag'], 'unknown')
        self.assertEqual(self.classify(0, exception={'type': 'System.InvalidOperationException', 'message': 'frame parsed'})['exceptions'][0]['assertionTag'], 'unknown')

    def test_numeric_owned_frame_template_is_bounded_not_raw(self):
        ex = {'type': 'System.Exception', 'message': 'OwnedFrame must not grow with the progress flag (measured 80 vs 48)'}
        row = self.classify(3, exception=ex)['exceptions'][0]
        self.assertEqual((row['measuredBytes'], row['comparatorBytes']), (80, 48))
        self.assertEqual(row['exactMessageSource'], {'source': d.PROGRESS, 'line': 684})
        self.assertIn('(measured {Unsafe.SizeOf<OwnedFrame>()}', (BASE / d.PROGRESS).read_text().splitlines()[683])
        for message in ('OwnedFrame must not grow with the progress flag (measured 9999 vs 48)',
                        'OwnedFrame must not grow with the progress flag (measured 080 vs 48)', ex['message'] + CANARY):
            row = self.classify(3, exception={**ex, 'message': message})['exceptions'][0]
            self.assertEqual(row['assertionTag'], 'unknown'); self.assertNotIn('measuredBytes', row)

    def test_stack_only_exact_verified_literal_source_frame(self):
        source, namespace, cls, method, start, end = d.CASES[0]
        good = f'   at {namespace}.{cls}.{method}() in /_/{source}:line 22'
        lines = [good, good.replace('/_/', '/private/' + CANARY + '/'), good.replace(':line 22', ':line 999'), good + CANARY,
                 f'   at {CANARY}({CANARY}) in /private/{CANARY}:line 1']
        result = self.classify(exception={'type': 'System.Exception', 'message': CANARY, 'stackTrace': '\n'.join(lines)})
        row = result['exceptions'][0]; self.assertEqual(len(row['sourceFrames']), 1); self.assertEqual(row['unrecognizedStackLineCount'], 4)
        self.assertEqual(row['sourceFrames'][0]['line'], 22); self.assertNotIn(CANARY, json.dumps(result))

    def test_inner_exception_chain_preserved_as_safe_categories(self):
        ex = {'type': 'System.TypeInitializationException', 'message': CANARY, 'innerException': {'type': 'System.MissingMethodException', 'message': CANARY}}
        rows = self.classify(exception=ex)['exceptions']; self.assertEqual([row['category'] for row in rows], ['type-initialization', 'missing-method'])
        self.assertEqual([row['depth'] for row in rows], [0, 1])
        for _ in range(16): ex = {'type': 'System.Exception', 'message': CANARY, 'innerException': ex}
        self.reject(lambda: self.classify(exception=ex))

    def test_changed_schema_and_unknown_fields_rejected(self):
        for target in ('root', 'group', 'test', 'exception'):
            selection, native, test = fixture()
            obj = {'root': native, 'group': native['groups'][0], 'test': test, 'exception': test['exception']}[target]
            obj[CANARY] = CANARY
            with self.subTest(target=target): self.reject(lambda: d.classify(e, native, junit(native), 0, selection))
        for bad in (None, [], {'type': CANARY}, {'type': CANARY, 'message': 4}, {'type': CANARY, 'message': CANARY, 'innerException': []}):
            selection, native, test = fixture(); test['exception'] = bad
            self.reject(lambda: d.native_shape(r, native))
        selection, native, test = fixture(); native['schemaVersion'] = 2
        self.reject(lambda: d.classify(e, native, junit(native), 0, selection))

    def test_missing_duplicate_foreign_explicit_and_retried_cases_rejected(self):
        for mode in ('missing', 'duplicate', 'foreign', 'other-selected', 'retry', 'attempts', 'status', 'missing-exception', 'pass-exception'):
            selection, native, test = fixture()
            if mode == 'missing': native['groups'][0]['tests'] = []
            elif mode == 'duplicate': native['groups'][0]['tests'].append(copy.deepcopy(test))
            elif mode == 'foreign': test['id'] = 'private-explicit-or-foreign'
            elif mode == 'other-selected': test['id'] = fixture(1)[0][0]
            elif mode == 'retry': test['retryAttempt'] = 0
            elif mode == 'attempts': test['attempts'] = []
            elif mode == 'status': test['status'] = 'skipped'
            elif mode == 'missing-exception': del test['exception']
            else: test['status'] = 'passed'
            refresh(native)
            with self.subTest(mode=mode): self.reject(lambda: d.classify(e, native, junit(native), 0, selection))

    def test_changed_junit_unknown_fields_or_exception_mismatch_rejected(self):
        for mode in ('type', 'message', 'attribute', 'element', 'identity', 'count', 'entity'):
            selection, native, test = fixture(); xml = junit(native); root = ET.fromstring(xml); case = root[0].find('testcase')
            if mode in ('type', 'message'): case[0].set(mode, 'mismatch')
            elif mode == 'attribute': case.set(CANARY, CANARY)
            elif mode == 'element': ET.SubElement(case[0], CANARY)
            elif mode == 'identity': case.set('name', 'wrong')
            elif mode == 'count': root.set('tests', '0')
            xml = ET.tostring(root, encoding='unicode')
            if mode == 'entity': xml = '<!DOCTYPE testsuites>' + xml
            with self.subTest(mode=mode): self.reject(lambda: d.classify(e, native, xml, 0, selection))

    def test_native_duplicate_json_keys_and_nonfinite_rejected(self):
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / 'native.json'
            for text in ('{"groups":[],"groups":[]}', '{"value":NaN}'):
                path.write_text(text); self.reject(lambda: r.read_json(path))
        selection, native, test = fixture(); test['durationMs'] = float('nan')
        self.reject(lambda: d.classify(e, native, junit(native), 0, selection))

    def make_lane(self, temp):
        lane = c.Lane(Path(temp), r, o); lane.source = Path(temp) / 'source'
        dll = lane.source / 'test/SharpLink.UnitTests/bin/Release/net10.0/SharpLink.UnitTests.dll'; dll.parent.mkdir(parents=True); dll.write_bytes(b'fixture-not-executable')
        sdk = Path(temp) / 'sdk'; sdk.mkdir(); dotnet = sdk / 'dotnet'; dotnet.write_bytes(b'fixture-not-executable')
        lane.inputs[dotnet] = r.file_identity(dotnet); lane.trees[sdk] = r.snapshot(sdk)
        lane.identity = {'binarySHA256': r.sha(dll)}; lane.case_facts = []; lane.candidate_complete = False; lane.preparation_complete = True; lane.sequence = 32
        return lane

    def run_mock_cases(self, lane, mode='mixed'):
        selections = [fixture(i)[0] for i in range(23)]; labels = []; originals = []
        def run(label, argv, *args):
            labels.append(label); lane.sequence += 1
            if label == 'parser-handshake-validation-python-controls':
                self.assertEqual(argv, [sys.executable, '-B', d.ROOT / 'eng/tests/test_parser_handshake_validation.py'])
                self.assertEqual(args, (d.ROOT, 60))
                self.assertNotEqual(d.ROOT, lane.source)
                return None
            self.assertEqual(args, (lane.source, 180))
            self.assertEqual(argv[argv.index('--timeout') + 1], '60s')
            index = int(label.rsplit('-', 1)[1]) - 1
            passed = index in (2, 4, 5, 6, 7) or mode == 'all-pass'
            selection, native, test = fixture(index, 'passed' if passed else 'error')
            report = Path(argv[argv.index('--results-directory') + 1]); (report / 'session.html').write_text(CANARY)
            (report / 'session.tunit-report.json').write_text(json.dumps(native)); (report / 'junit.xml').write_text(junit(native))
            receipt = {'stage': label, 'argv': argv, 'exitCode': 0 if passed else 2, 'timedOut': False,
                       'timeoutSeconds': 180, 'remainingOwned': {}, 'errors': [], 'cleanupErrors': [], 'reportingErrors': []}
            if mode == 'timeout': receipt['timedOut'] = True
            if mode == 'survivor': receipt['remainingOwned'] = {'123': CANARY}
            if mode == 'cleanup': receipt['cleanupErrors'] = [CANARY]
            if mode == 'process-failure': receipt['exitCode'] = 99
            if mode == 'mutated-source': lane.inputs[next(iter(lane.inputs))][-1] = '0' * 64
            lane.commands[label] = {'receipt': receipt, 'argv': argv, 'seconds': 180,
                                    'ownerProvenance': 'validated-original-success' if passed else 'original-owner-failure'}
            if mode == 'owner-unknown': lane.commands[label]['ownerProvenance'] = None
            if not passed:
                original = o.CommandFailure(receipt, []); originals.append(original); lane.record(original); raise original
        with patch.object(lane, 'run', side_effect=run), patch.object(d, 'verify_source'):
            d.run_cases(lane, e, selections, d.time.monotonic() + 5100)
        return labels, originals

    def test_twenty_three_fresh_processes_failure_preserved_without_retry(self):
        with tempfile.TemporaryDirectory() as temp:
            lane = self.make_lane(temp); labels, originals = self.run_mock_cases(lane)
            self.assertEqual(labels, ['parser-handshake-validation-python-controls'] + ['parser-handshake-validation-case-' + str(i) for i in range(1, 24)])
            self.assertEqual(len(lane.case_facts), 23); self.assertTrue(lane.candidate_complete)
            self.assertEqual([row['status'] for row in lane.case_facts], ['passed' if i in (2, 4, 5, 6, 7) else 'error' for i in range(23)])
            self.assertTrue(all(error in lane.errors for error in originals)); self.assertEqual(len(lane.seals), 23)
            with contextlib.redirect_stdout(io.StringIO()) as output: self.assertEqual(d.finish(lane, c, e), 1)
            summary = json.loads(output.getvalue()); self.assertFalse(summary['candidateValidationQualified']); self.assertTrue(summary['candidateValidationComplete'])
            self.assertFalse(summary['fullSuiteQualified']); self.assertNotIn(CANARY, output.getvalue())

    def test_unknown_timeout_survivor_cleanup_failure_cannot_parse_or_continue(self):
        for mode in ('owner-unknown', 'timeout', 'survivor', 'cleanup'):
            with self.subTest(mode=mode), tempfile.TemporaryDirectory() as temp:
                lane = self.make_lane(temp)
                with patch.object(d, 'classify') as classify:
                    self.reject(lambda: self.run_mock_cases(lane, mode)); classify.assert_not_called()
                self.assertFalse(lane.candidate_complete); self.assertEqual(len(lane.case_facts), 0); self.assertNotIn('parser-handshake-validation-case-2', lane.commands)
                self.assertTrue(lane.errors)

    def test_process_exit_mismatch_and_source_mutation_cannot_qualify(self):
        for mode in ('process-failure', 'mutated-source'):
            with self.subTest(mode=mode), tempfile.TemporaryDirectory() as temp:
                lane = self.make_lane(temp); self.reject(lambda: self.run_mock_cases(lane, mode))
                self.assertFalse(lane.candidate_complete); self.assertEqual(len(lane.case_facts), 0)

    def test_all_pass_still_never_qualifies_historical_full_suite(self):
        with tempfile.TemporaryDirectory() as temp:
            lane = self.make_lane(temp); self.run_mock_cases(lane, 'all-pass')
            with contextlib.redirect_stdout(io.StringIO()) as output: self.assertEqual(d.finish(lane, c, e), 0)
            summary = json.loads(output.getvalue()); self.assertTrue(summary['candidateValidationQualified']); self.assertFalse(summary['fullSuiteQualified'])
            self.assertEqual(summary['historicalOrdinaryFailureCount'], 290)
            self.assertEqual(summary['candidateCatalogSHA256'], e.CATALOG_SHA256)
            self.assertEqual(summary['sourceTrees'], d.SOURCE_TREES)
            self.assertEqual(summary['requiredSelectedCaseCount'], 23)

    def test_deadline_summary_writer_and_post_emit_failure_revoke(self):
        for mode in ('deadline', 'writer', 'post-emit'):
            with self.subTest(mode=mode), tempfile.TemporaryDirectory() as temp:
                lane = self.make_lane(temp); lane.candidate_complete = True; lane.case_facts = [self.classify(i, 'passed') for i in range(23)]
                writes = []; clock = [1.0]; lane.stop = 2.0
                def emit(*args, **kwargs): clock[0] = 3.0
                patches = [patch.object(d.os, 'write', side_effect=lambda fd, data: writes.append(data) or len(data))]
                if mode == 'deadline': lane.stop = 0
                if mode == 'writer': patches.append(patch.object(r, 'write_json', side_effect=OSError(CANARY)))
                if mode == 'post-emit': patches.extend([patch.object(d.time, 'monotonic', side_effect=lambda: clock[0]), patch('builtins.print', side_effect=emit)])
                with contextlib.ExitStack() as stack:
                    for item in patches: stack.enter_context(item)
                    stack.enter_context(contextlib.redirect_stdout(io.StringIO()))
                    self.assertEqual(d.finish(lane, c, e), 1)
                self.assertNotIn(CANARY.encode(), b''.join(writes)); self.assertTrue(lane.errors)

    def test_sdk_runtime_exact_pin_hook_rejects_other_versions(self):
        Lane = d.lane_type(c, e)
        for label, value in [('sdk-version', '10.0.115'), ('sdk-runtimes', 'Microsoft.NETCore.App 10.0.15 [/sdk/shared/Microsoft.NETCore.App]')]:
            with tempfile.TemporaryDirectory() as temp:
                lane = Lane(Path(temp), r, o); path = Path(temp) / 'output'; path.write_text(value)
                with patch.object(c.Lane, 'run', return_value=path): self.reject(lambda: lane.run(label, ['/sdk/dotnet']))

    def test_selection_crossing_original_preparation_deadline_never_hands_off(self):
        with tempfile.TemporaryDirectory() as temp:
            lane = self.make_lane(temp); lane.preparation_complete = False
            lane.identity.update(sdk='10.0.114', installedRuntimes=d.RUNTIMES)
            ordinary = dict(fixture(i)[0] for i in range(23)); original = d.selected
            clock = [1.0]; lane.stop = 2.0
            def slow_selection(*args):
                result = original(*args); clock[0] = 3.0; return result
            with patch.object(d, 'selected', side_effect=slow_selection), patch.object(d.time, 'monotonic', side_effect=lambda: clock[0]), patch.object(lane, 'run') as run:
                self.reject(lambda: d.handoff_selections(lane, ordinary, set())); run.assert_not_called()
            self.assertFalse(lane.preparation_complete); self.assertEqual(lane.stop, 2.0)

    def test_run_cases_cannot_revive_an_expired_preparation_or_overall_budget(self):
        for expired in ('preparation', 'overall'):
            with self.subTest(expired=expired), tempfile.TemporaryDirectory() as temp:
                lane = self.make_lane(temp); lane.stop = 0 if expired == 'preparation' else d.time.monotonic() + 3600
                before = lane.stop; overall = 0 if expired == 'overall' else d.time.monotonic() + 5100
                with patch.object(lane, 'run') as run:
                    self.reject(lambda: d.run_cases(lane, e, [fixture(i)[0] for i in range(23)], overall)); run.assert_not_called()
                self.assertEqual(lane.stop, before); self.assertFalse(lane.candidate_complete)

    def test_isolated_sdk_exact_directory_environment_and_symlink_guards(self):
        for mode in ('valid', 'wrong-root', 'wrong-env', 'multilevel', 'symlink'):
            with self.subTest(mode=mode), tempfile.TemporaryDirectory() as temp:
                lane = c.Lane(Path(temp), r, o)
                sdk = Path(temp) / 'parser-handshake-fixtures-dotnet'; sdk.mkdir(); dotnet = sdk / 'dotnet'; dotnet.write_bytes(b'never execute')
                lane.env.update(DOTNET_ROOT=str(sdk), DOTNET_MULTILEVEL_LOOKUP='0')
                if mode == 'wrong-env': lane.env['DOTNET_ROOT_X64'] = '/other-sdk'
                if mode == 'multilevel': lane.env['DOTNET_MULTILEVEL_LOOKUP'] = '1'
                runner_temp = temp if mode != 'wrong-root' else '/other-temp'
                if mode == 'symlink':
                    actual = Path(temp) / 'actual'; sdk.rename(actual); sdk.symlink_to(actual, target_is_directory=True)
                with patch.dict(os.environ, RUNNER_TEMP=runner_temp), patch.object(c.shutil, 'which', return_value=str(dotnet)):
                    if mode == 'valid': d.verify_isolated_sdk(lane, c)
                    else: self.reject(lambda: d.verify_isolated_sdk(lane, c))

    def test_public_identity_never_copies_arbitrary_strings(self):
        safe = d.public_identity(r, {'head': 'a' * 40, 'tree': 'b' * 40, 'unknown': CANARY})
        self.assertNotIn(CANARY, json.dumps(safe)); self.assertNotIn('unknown', safe)
        self.reject(lambda: d.public_identity(r, {'head': CANARY}))

    def test_bootstrap_failure_is_literal_and_original_retained(self):
        original = RuntimeError(CANARY)
        with patch.object(d.tempfile, 'mkdtemp', side_effect=original), patch.object(d.os, 'write', return_value=1):
            self.assertEqual(d.guarded_main(), 1)
        self.assertIs(d.ACTIVE['errors'][0], original)
        script = 'import tempfile,runpy; tempfile.mkdtemp=lambda *a,**k: (_ for _ in ()).throw(OSError("' + CANARY + '")); runpy.run_path(' + repr(str(ROOT / 'eng/run-parser-handshake-validation.py')) + ',run_name="__main__")'
        result = subprocess.run([sys.executable, '-B', '-c', script], capture_output=True, timeout=10)
        self.assertEqual(result.returncode, 1); self.assertEqual(result.stdout, b''); self.assertEqual(result.stderr, b'PARSER_HANDSHAKE_VALIDATION_BOOTSTRAP_FAILED\n')

    def test_real_python_owner_nonzero_and_timeout_preserve_private_logs(self):
        o.enable_subreaper()
        for name, script, seconds in [('nonzero', 'import sys; print("' + CANARY + '"); sys.exit(7)', 2), ('timeout', 'import time; time.sleep(5)', .1)]:
            with self.subTest(name=name), tempfile.TemporaryDirectory() as temp:
                with self.assertRaises(o.CommandFailure) as context:
                    o.run_owned(name, [sys.executable, '-B', '-c', script], BASE, dict(os.environ), Path(temp), seconds)
                receipt = context.exception.outcome; self.assertEqual(receipt['remainingOwned'], {})
                self.assertEqual(receipt['timedOut'], name == 'timeout')
                self.assertTrue((Path(temp) / (name + '.stdout')).is_file())


if __name__ == '__main__': unittest.main()
