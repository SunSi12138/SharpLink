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


d = load('test_pure_runner', ROOT / 'eng/run-pure-negotiation-validation.py')
e = load('test_pure_execution', BASE / 'eng/unit-execution-results.py')
c = load('test_pure_census', BASE / 'eng/run-unit-census.py')
o = load('test_pure_owner', BASE / 'eng/package-smoke-process.py')
r = e.census
ExecutionControls = load('test_core_execution', ROOT / 'eng/tests/test_unit_execution.py').Controls
RebaseControls = load('test_candidate_rebase', ROOT / 'eng/tests/test_three_fixture_census.py').Controls
CANARY = 'PRIVATE_CANARY_8fe5fca2'


def fixture(index=0, status='error', exception=None):
    path, ns, cls, method, start, end = d.CASES[index]
    uid = 'private-uid-' + str(index)
    row = {'uid': uid, 'displayName': d.CASES[index][3] + ('(True)' if index == 0 else '(False)') if index < 2 else CANARY, 'type': {'namespace': ns, 'typeName': cls, 'methodName': method, 'assemblyFullName': 'SharpLink.UnitTests, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null', 'methodArity': 0, 'returnTypeFullName': 'System.Threading.Tasks.Task' if index < 4 else 'System.Void', 'parameterTypeFullNames': ['System.Boolean'] if index < 2 else []},
           'location': {'file': '/_/' + path, 'lineStart': d.ATTRIBUTE_LINES[index], 'lineEnd': end}}
    test = {'id': uid, 'displayName': d.CASES[index][3] + ('(True)' if index == 0 else '(False)') if index < 2 else CANARY, 'methodName': method, 'className': cls,
            'filePath': '/_/' + path, 'lineNumber': d.ATTRIBUTE_LINES[index], 'endLineNumber': end, 'status': status, 'durationMs': 1.0}
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








    def test_historical_full_and_diagnostic_lanes_are_not_current_controls(self):
        import ast
        historical = {'.github/workflows/full-regression.yml': 'cd485975d8add22c5c869c4b660ab92eb6e3728f4461821d8cf997079d906253', '.github/workflows/stream-negotiation-diagnostic.yml': '3cf4e2c1197b427b307a0df5bd166d6e9cda2f407610ab69b82fb48d4f2ee015', 'eng/run-full-regression.py': 'a474a13dec376aaf6126531bb74c0662bfde596192c9cafc7bc09e511b57fa38', 'eng/run-stream-negotiation-diagnostic.py': 'b6704f8f87aa97bccb312b46101f63b253c71458d7d3a2bca00dce639cc5806a', 'eng/tests/test_full_regression.py': 'ced4ab1bf3272dabc38626a8f62ef4cf26af2f0bac4fd60d4bdbd20fb4dec9d8', 'eng/tests/test_stream_negotiation_diagnostic.py': '656933d73ab21eb22a098dc07483ba44947d9861ba46ce9263499747bc22e769'}
        for path,digest in historical.items():self.assertEqual(r.sha(ROOT/path),digest)
        for path in ('eng/run-full-regression.py','eng/run-stream-negotiation-diagnostic.py'):
            module=ast.parse((ROOT/path).read_text())
            helpers=next(ast.literal_eval(node.value) for node in module.body if isinstance(node,ast.Assign) and any(isinstance(target,ast.Name) and target.id=='HELPERS' for target in node.targets))
            self.assertNotEqual(helpers['eng/unit-census.json'],e.CATALOG_SHA256)
        self.assertNotIn('eng/tests/test_full_regression.py',d.HELPERS)
        self.assertNotIn('eng/tests/test_stream_negotiation_diagnostic.py',d.HELPERS)
        self.assertNotIn('eng/run-full-regression.py',d.HELPERS)
        self.assertIn('eng/tests/test_pure_negotiation_validation.py',d.HELPERS)
        self.assertEqual(e.CATALOG_SHA256,r.sha(ROOT/'eng/unit-census.json'))

    def test_complete_historical_full_and_sixteen_case_evidence_are_exact(self):
        fullpath=ROOT/'eng/pure-negotiation-history/full-ordinary-result.json'
        diagpath=ROOT/'eng/pure-negotiation-history/sixteen-case-diagnostic-result.json'
        self.assertEqual(r.sha(fullpath),'8b5c5a42210d118055655b75e456cf65f114b9e88fb63debbd8b122c55997e56')
        self.assertEqual(r.sha(diagpath),'57700796654908f5d3abb3064c04c746384253bad0668f5b4e9dab387373a55a')
        full=r.read_json(fullpath);diagnostic=r.read_json(diagpath)
        self.assertEqual((full['runId'],full['head']),(41962521900,'72d728bf226ef3d5c9a5ca4d547f798810147880'))
        execution=full['summary']['execution'];self.assertEqual((execution['observed'],execution['statusCounts']['passed'],execution['statusCounts']['failed']),(2879,2607,272))
        self.assertFalse(full['summary']['executionQualified']);self.assertEqual(execution['bodyExitCode'],2)
        self.assertEqual((diagnostic['runId'],diagnostic['head']),(41966659630,'aecea7afdfdd426a1bbbd5cf840bed945ef62e7b'))
        summary=diagnostic['summary'];self.assertTrue(summary['diagnosticComplete']);self.assertTrue(summary['infrastructureQualified']);self.assertFalse(summary['diagnosticQualified'])
        self.assertEqual([row['status'] for row in summary['cases']],['passed']*7+['error']*9)
        self.assertEqual([row['method'].rsplit('.',1)[1] for row in summary['cases']],[row[3] for row in d.CASES])
        self.assertEqual(summary['errorOccurrences'],27);self.assertTrue(all(row['ownerClosed'] for row in summary['cases']))
        self.assertEqual({row['argumentIdentity']['value'] for row in summary['cases'][:2]},{True,False})

    def test_current_helper_frames_and_stale_coordinates_are_exact(self):
        path=d.CASES[4][0];full='SharpLink.UnitTests.Protocol.ProtocolV2NegotiatorTests'
        for member,signature,line,stale in (('Ensure','Boolean condition, String message',738,624),
                ('CaptureSharpLinkException','Action action',695,581),
                ('CaptureArgumentException','Action action',708,594),
                ('CreatePolicy','ProtocolV2Capabilities supported, IReadOnlyList`1 providers, UInt16 minorVersion, Int32 maxFramePayloadBytes, Int32 streamReceiveWindowBytes, Int32 connectionReceiveWindowBytes',660,564)):
            exact=f'   at {full}.{member}({signature}) in /_/{path}:line {line}'
            wrong=exact.replace(f':line {line}',f':line {stale}')
            frames,unknown,omitted=d.source_frames(7,exact+'\n'+wrong)
            self.assertEqual(frames,[{'source':path,'member':member,'line':line}]);self.assertEqual((unknown,omitted),(1,0))
        self.assertIn('throw new Exception(message);',(ROOT/path).read_text().splitlines()[737])

    def test_candidate_source_tree_and_timed_file_remain_exact(self):
        g=load('current_source_proof',ROOT/'eng/rebase-three-fixture-census.py')
        self.assertEqual(g.tree_id(ROOT/'src'),'c3a94c896418a5d5ec62646461f17c67dc89bc96')
        self.assertEqual(g.tree_id(ROOT/'test'),'fb8f193f979ba936fcd3e51eff98562f27f06a50')
        self.assertEqual(r.sha(ROOT/d.CASES[0][0]),'bb0bf4e489c2bc58ca070c496cc33a0681c874331b574f40005ba2e1ef14ff15')
        self.assertEqual(d.ATTRIBUTE_LINES[:4],(185,185,17,54))
        self.assertEqual([(row[4],row[5]) for row in d.CASES[:4]],[(188,257),(188,257),(18,52),(55,93)])

    def test_every_case_and_each_report_must_pass_for_candidate_qualification(self):
        with tempfile.TemporaryDirectory() as temp:
            lane=self.make_lane(temp);self.run_mock_cases(lane,'all-pass')
            first_evidence=lane.evidence
            self.assertFalse((lane.evidence/'pure-negotiation-validation-summary.json').exists())
            self.assertEqual(lane.sequence,49);self.assertTrue(lane.candidate_complete)
            with contextlib.redirect_stdout(io.StringIO()) as output:self.assertEqual(d.finish(lane,c,e),0)
            summary=json.loads(output.getvalue());self.assertTrue(summary['candidateValidationQualified']);self.assertTrue(summary['allSelectedPassed'])
            self.assertEqual(summary['historicalSixteenCaseDiagnostic']['errors'],9);self.assertFalse(summary['historicalSixteenCaseDiagnostic']['qualified'])
            self.assertEqual(summary['historicalFullRun']['errors'],272);self.assertFalse(summary['fullSuiteQualified'])
            self.assertEqual(summary['historicalTimedFailureResolution'],'unresolved-no-timed-source-change')
        with tempfile.TemporaryDirectory() as temp:
            lane=self.make_lane(temp);self.run_mock_cases(lane,'all-pass')
            self.assertNotEqual(lane.evidence,first_evidence)
            self.assertFalse((lane.evidence/'pure-negotiation-validation-summary.json').exists())
            lane.case_facts[-1]['selectedPassed']=False
            with contextlib.redirect_stdout(io.StringIO()) as output:self.assertEqual(d.finish(lane,c,e),1)
            result=json.loads(output.getvalue());self.assertFalse(result['candidateValidationQualified']);self.assertFalse(result['allSelectedPassed'])

    def test_changed_source_rejected_before_diagnostics(self):
        with patch.object(r, 'sha', return_value='0' * 64): self.reject(lambda: d.verify_source(r, BASE))







    def test_pass_and_prior_control_are_bounded_diagnostic_only(self):
        for i in range(16):
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
        lane.validated_failure_receipts = []; lane.identity = {'binarySHA256': r.sha(dll)}; lane.case_facts = []; lane.candidate_complete = False; lane.preparation_complete = True; lane.sequence = 32
        return lane

    def run_mock_cases(self, lane, mode='mixed'):
        selections = [fixture(i)[0] for i in range(16)]; labels = []; originals = []
        def run(label, argv, *args):
            labels.append(label); lane.sequence += 1
            if label == 'pure-negotiation-python-controls':
                self.assertEqual(argv, [sys.executable, '-B', d.ROOT / 'eng/tests/test_pure_negotiation_validation.py'])
                self.assertEqual(args, (d.ROOT, 90))
                self.assertNotEqual(d.ROOT, lane.source)
                return None
            index = int(label.rsplit('-', 1)[1]) - 1
            passed = index >= 7 or mode == 'all-pass'
            selection, native, test = fixture(index, 'passed' if passed else ('timedOut' if mode == 'native-timeout' and index == 0 else 'error'))
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
        with patch.object(lane, 'run', side_effect=run), patch.object(d, 'verify_source'), patch.object(d, 'verify_helpers'):
            d.run_cases(lane, e, selections, d.time.monotonic() + 5100)
        return labels, originals

    def test_sixteen_fresh_processes_failure_preserved_without_retry(self):
        with tempfile.TemporaryDirectory() as temp:
            lane = self.make_lane(temp); labels, originals = self.run_mock_cases(lane)
            self.assertEqual(labels, ['pure-negotiation-python-controls'] + ['pure-negotiation-case-' + str(i) for i in range(1, 17)])
            self.assertEqual(len(lane.case_facts), 16); self.assertTrue(lane.candidate_complete)
            self.assertEqual([row['status'] for row in lane.case_facts], ['error'] * 7 + ['passed'] * 9)
            self.assertTrue(all(error in lane.errors for error in originals)); self.assertEqual(len(lane.seals), 16)
            with contextlib.redirect_stdout(io.StringIO()) as output: self.assertEqual(d.finish(lane, c, e), 1)
            summary = json.loads(output.getvalue()); self.assertFalse(summary['candidateValidationQualified']); self.assertTrue(summary['candidateValidationComplete']); self.assertFalse(summary['allSelectedPassed']); self.assertTrue(summary['infrastructureQualified'])
            self.assertFalse(summary['fullSuiteQualified']); self.assertNotIn(CANARY, output.getvalue())

    def test_unknown_timeout_survivor_cleanup_failure_cannot_parse_or_continue(self):
        for mode in ('owner-unknown', 'timeout', 'survivor', 'cleanup'):
            with self.subTest(mode=mode), tempfile.TemporaryDirectory() as temp:
                lane = self.make_lane(temp)
                with patch.object(d, 'classify') as classify:
                    self.reject(lambda: self.run_mock_cases(lane, mode)); classify.assert_not_called()
                self.assertFalse(lane.candidate_complete); self.assertEqual(len(lane.case_facts), 0); self.assertNotIn('pure-negotiation-case-2', lane.commands)
                self.assertTrue(lane.errors)

    def test_process_exit_mismatch_and_source_mutation_cannot_qualify(self):
        for mode in ('process-failure', 'mutated-source'):
            with self.subTest(mode=mode), tempfile.TemporaryDirectory() as temp:
                lane = self.make_lane(temp); self.reject(lambda: self.run_mock_cases(lane, mode))
                self.assertFalse(lane.candidate_complete); self.assertEqual(len(lane.case_facts), 0)


    def test_deadline_summary_writer_and_post_emit_failure_revoke(self):
        for mode in ('deadline', 'writer', 'post-emit'):
            with self.subTest(mode=mode), tempfile.TemporaryDirectory() as temp:
                lane = self.make_lane(temp); lane.candidate_complete = True; lane.case_facts = [self.classify(i, 'passed') for i in range(16)]
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
            ordinary = dict(fixture(i)[0] for i in range(16)); original = d.selected
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
                    self.reject(lambda: d.run_cases(lane, e, [fixture(i)[0] for i in range(16)], overall)); run.assert_not_called()
                self.assertEqual(lane.stop, before); self.assertFalse(lane.candidate_complete)

    def test_isolated_sdk_exact_directory_environment_and_symlink_guards(self):
        for mode in ('valid', 'wrong-root', 'wrong-env', 'multilevel', 'symlink'):
            with self.subTest(mode=mode), tempfile.TemporaryDirectory() as temp:
                lane = c.Lane(Path(temp), r, o)
                sdk = Path(temp) / 'pure-negotiation-dotnet'; sdk.mkdir(); dotnet = sdk / 'dotnet'; dotnet.write_bytes(b'never execute')
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
        script = 'import tempfile,runpy; tempfile.mkdtemp=lambda *a,**k: (_ for _ in ()).throw(OSError("' + CANARY + '")); runpy.run_path(' + repr(str(ROOT / 'eng/run-pure-negotiation-validation.py')) + ',run_name="__main__")'
        result = subprocess.run([sys.executable, '-B', '-c', script], capture_output=True, timeout=10)
        self.assertEqual(result.returncode, 1); self.assertEqual(result.stdout, b''); self.assertEqual(result.stderr, b'PURE_NEGOTIATION_VALIDATION_BOOTSTRAP_FAILED\n')

    def test_real_python_owner_nonzero_and_timeout_preserve_private_logs(self):
        o.enable_subreaper()
        for name, script, seconds in [('nonzero', 'import sys; print("' + CANARY + '"); sys.exit(7)', 2), ('timeout', 'import time; time.sleep(5)', .1)]:
            with self.subTest(name=name), tempfile.TemporaryDirectory() as temp:
                with self.assertRaises(o.CommandFailure) as context:
                    o.run_owned(name, [sys.executable, '-B', '-c', script], BASE, dict(os.environ), Path(temp), seconds)
                receipt = context.exception.outcome; self.assertEqual(receipt['remainingOwned'], {})
                self.assertEqual(receipt['timedOut'], name == 'timeout')
                self.assertTrue((Path(temp) / (name + '.stdout')).is_file())

    def test_catalog_and_all_sixteen_source_identities(self):
        catalog = r.read_json(ROOT / 'eng/unit-census.json')
        for index, (source, ns, cls, method, start, end) in enumerate(d.CASES):
            rows = [m for m in catalog['methods'] if (m['source'],m['namespace'],m['className'],m['methodName']) == (source,ns,cls,method)]
            self.assertEqual(len(rows),1)
            row=rows[0];self.assertEqual((row['signatureLine'],row['endLine'],row['attributeLine']),(start,end,d.ATTRIBUTE_LINES[index]))
            self.assertEqual(row['expectedCases'],2 if index < 2 else 1)
            self.assertEqual(row['parameters'],['System.Boolean'] if index < 2 else [])
        ordinary=dict(fixture(i)[0] for i in range(16))
        selections=d.selected(r,ordinary)
        self.assertEqual(len(selections),16)
        self.assertEqual({uid for uid,_ in selections},{uid for uid,_ in ordinary.items()})
        self.assertEqual(len({d.CASES[i][3] for i in range(16)}),15)
        self.assertEqual(len({d.CASES[i][3] for i in range(4,16)}),12)

    def test_selection_missing_duplicate_foreign_and_unsupported_bounds(self):
        good=dict(fixture(i)[0] for i in range(16))
        for mode in ('missing','duplicate','foreign','start','end','baduid'):
            ordinary=copy.deepcopy(good);uid,row=next(iter(ordinary.items()))
            if mode=='missing':del ordinary[uid]
            elif mode=='duplicate':ordinary['extra']={**row,'uid':'extra'}
            elif mode=='foreign':row['location']['file']='/private/'+CANARY
            elif mode=='start':row['location']['lineStart']=184
            elif mode=='end':row['location']['lineEnd']=258
            else:ordinary['-unsafe']={**ordinary.pop(uid),'uid':'-unsafe'}
            self.reject(lambda:d.selected(r,ordinary))
        for start in (185,):
            ordinary=copy.deepcopy(good)
            for i in (0,1):ordinary[fixture(i)[0][0]]['location']['lineStart']=start
            self.assertEqual(len(d.selected(r,ordinary)),16)

    def test_boolean_literal_identity_exact_metadata_not_uid_order(self):
        for index,value in ((0,True),(1,False)):
            selection=fixture(index)[0]
            result=d.argument_identity(index,selection,d.boolean_pair_proven([fixture(i)[0] for i in (0,1)]))
            self.assertEqual(result['value'],value);self.assertEqual(result['kind'],'source-literal-boolean')
            self.assertEqual(result['parameter'],'expiresDuringEmission')
            self.assertEqual(result['caseID'],d.hashlib.sha256(selection[0].encode()).hexdigest())
            for display in (CANARY,d.CASES[index][3]+'(true)',d.CASES[index][3]+'(False)'+CANARY):
                row=copy.deepcopy(selection[1]);row['displayName']=display
                result=d.argument_identity(index,(selection[0],row),True)
                self.assertIsNone(result['value']);self.assertEqual(result['limitation'],'boolean-value-not-proven')
                self.assertNotIn(CANARY,json.dumps(result))
        # Swapping UIDs preserves actual metadata-derived True/False labels.
        a=fixture(0)[0];b=fixture(1)[0]
        self.assertIs(d.argument_identity(0,(b[0],a[1]),True)['value'],True)
        self.assertIs(d.argument_identity(1,(a[0],b[1]),True)['value'],False)

    def test_exact_frames_have_no_raw_path_signature_or_message_projection(self):
        path,ns,cls,method,start,end=d.CASES[0]
        good=f'   at {ns}.{cls}.{method}(Boolean expiresDuringEmission) in /_/{path}:line 254'
        raw='\n'.join([good,good.replace('/_/','/private/'+CANARY+'/'),good.replace('254','999'),good+CANARY,CANARY])
        result=self.classify(exception={'type':'System.AggregateException','message':CANARY,'stackTrace':raw,'innerException':{'type':'System.TimeoutException','message':CANARY}})
        self.assertEqual(result['exceptions'][0]['sourceFrames'],[{'source':path,'member':method,'line':254}])
        self.assertEqual(result['exceptions'][0]['unrecognizedStackLineCount'],4)
        self.assertEqual([x['category'] for x in result['exceptions']],['aggregate','timeout'])
        self.assertEqual(result['exceptionEvidenceScope'],'serialized-innerException-chain-only')
        self.assertFalse(result['aggregateSiblingCausesObserved']);self.assertEqual(result['bodyVersusDisposal'],'inconclusive')
        self.assertNotIn(CANARY,json.dumps(result))

    def test_exact_message_tags_are_source_pinned_and_case_scoped(self):
        for indexes,typ,message,tag,path,line in d.EXCEPTION_TAGS:
            self.assertIn(message,(ROOT/path).read_text().splitlines()[line-1])
            index=indexes[0]
            row=self.classify(index,exception={'type':typ,'message':message})['exceptions'][0]
            self.assertEqual(row['assertionTag'],tag);self.assertNotIn(message,json.dumps(row))
            for changed in (message+CANARY,CANARY+message):
                self.assertEqual(self.classify(index,exception={'type':typ,'message':changed})['exceptions'][0]['assertionTag'],'unknown')
            other=4 if index<4 else 0
            self.assertEqual(self.classify(other,exception={'type':typ,'message':message})['exceptions'][0]['assertionTag'],'unknown')

    def test_latest_historical_failure_stays_exact_and_argument_unknown(self):
        h=r.read_json(ROOT/'eng/stream-negotiation-history/current-ordinary-result.json')
        self.assertEqual((h['runId'],h['head'],h['passed'],h['errors']),(41962521900,'72d728bf226ef3d5c9a5ca4d547f798810147880',2607,272))
        self.assertFalse(h['qualified']);self.assertEqual(h['argumentIdentity'],'not-present-in-historical-method-aggregation')
        self.assertEqual(sum(x['count'] for x in h['methodStatusCounts']),16)
        self.assertEqual(sum(x['count'] for x in h['methodExceptionCategoryCounts']),10)
        self.assertTrue(h['ownerClosed']);self.assertTrue(h['executionClosureObserved']);self.assertEqual(h['bodyExitCode'],2)

    def test_all_pass_does_not_resolve_historical_failure(self):
        with tempfile.TemporaryDirectory() as temp:
            lane=self.make_lane(temp);self.run_mock_cases(lane,'all-pass')
            with contextlib.redirect_stdout(io.StringIO()) as output:self.assertEqual(d.finish(lane,c,e),0)
            result=json.loads(output.getvalue())
            self.assertTrue(result['candidateValidationQualified']);self.assertFalse(result['fullSuiteQualified'])
            self.assertEqual(result['historicalFullRun']['errors'],272);self.assertFalse(result['historicalFullRun']['qualified'])
            self.assertFalse(result['historicalArgumentMappingAvailable'])

    def test_source_check_and_last_seal_cannot_cross_handoff_deadline(self):
        for mode in ('clock','seal'):
            with tempfile.TemporaryDirectory() as temp:
                lane=self.make_lane(temp);lane.preparation_complete=False;lane.stop=2.0;clock=[1.0]
                def source(*args):
                    if mode=='clock':clock[0]=3.0
                    else:lane.inputs[next(iter(lane.inputs))][-1]='0'*64
                with patch.object(d.time,'monotonic',side_effect=lambda:clock[0]),patch.object(d,'verify_helpers'),patch.object(d,'verify_source',side_effect=source),patch.object(lane,'run') as run:
                    self.reject(lambda:d.run_cases(lane,e,[],7200.0));run.assert_not_called()
                self.assertFalse(lane.preparation_complete);self.assertEqual(lane.stop,2.0)

    def test_workflow_has_pinned_sdk_and_no_preownership_dotnet_invocation(self):
        w=(ROOT/'.github/workflows/pure-negotiation-validation.yml').read_text()
        self.assertIn('actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1',w)
        self.assertIn('actions/setup-dotnet@a98b56852c35b8e3190ac28c8c2271da59106c68',w)
        self.assertIn('dotnet-version: \'10.0.114\'',w)
        self.assertIn('DOTNET_INSTALL_DIR: ${{ runner.temp }}/pure-negotiation-dotnet',w)
        self.assertIn('persist-credentials: false',w)
        self.assertNotIn('upload-artifact',w);self.assertNotIn('continue-on-error',w)
        self.assertNotIn('dotnet --',w);self.assertNotIn('dotnet test',w)
        self.assertIn('run: python3 eng/run-pure-negotiation-validation.py --configuration Release',w)

    def test_duplicate_or_unknown_boolean_pair_makes_both_identities_opaque(self):
        for mode in ('duplicate','unknown','missing'):
            selections=[fixture(i)[0] for i in (0,1)]
            if mode=='duplicate':selections[1][1]['displayName']=selections[0][1]['displayName']
            elif mode=='unknown':selections[1][1]['displayName']=CANARY
            else:selections=selections[:1]
            proven=d.boolean_pair_proven(selections)
            self.assertFalse(proven)
            for index,selection in enumerate(selections):
                identity=d.argument_identity(index,selection,proven)
                self.assertEqual(identity['kind'],'opaque-boolean-case');self.assertIsNone(identity['value'])
                self.assertNotIn(CANARY,json.dumps(identity))

    def test_native_display_name_mismatch_cannot_project_boolean(self):
        for name in (CANARY,d.CASES[0][3]+'(False)'):
            selection,native,test=fixture(0);test['displayName']=name
            self.reject(lambda:d.classify(e,native,junit(native),0,selection,True))

    def test_fixed_policy_and_cleanup_frames_keep_only_allowlisted_coordinates(self):
        for index,typ,member,signature,path,line in (
            (4,'SharpLink.Runtime.ProtocolV2NegotiationPolicy','Create','UInt16 minorVersion, ProtocolV2Capabilities supportedCapabilities, Int32 maxFramePayloadBytes, Int32 streamReceiveWindowBytes, Int32 connectionReceiveWindowBytes, IReadOnlyList`1 compressionProviders, Nullable`1 bindingLimits, Guid serverInstanceId, Guid registryEpoch','src/SharpLink.Runtime/ProtocolV2/ProtocolV2Negotiator.cs',68),
            (0,'SharpLink.Client.SharpLinkClient','ThrowStopCleanupFailures','List`1 failures','src/SharpLink.Client/SharpLinkClient.cs',261),
            (0,'SharpLink.Client.SharpLinkClient','StopCoreAsync','','src/SharpLink.Client/SharpLinkClient.cs',217)):
            frame=f'   at {typ}.{member}({signature}) in /_/{path}:line {line}'
            rows=self.classify(index,exception={'type':'System.AggregateException','message':CANARY,'stackTrace':frame+'\n'+frame+CANARY})['exceptions']
            self.assertEqual(rows[0]['sourceFrames'],[{'source':path,'member':member,'line':line}])
            self.assertEqual(rows[0]['unrecognizedStackLineCount'],1);self.assertNotIn(CANARY,json.dumps(rows))

    def test_genuine_test_failure_does_not_hide_independent_infrastructure_failure(self):
        with tempfile.TemporaryDirectory() as temp:
            lane=self.make_lane(temp);self.run_mock_cases(lane)
            self.assertTrue(d.infrastructure_qualified(lane,True))
            lane.record(r.Rejected('UC_SEAL'))
            self.assertFalse(d.infrastructure_qualified(lane,True))

    def test_parameterized_location_uses_exact_attributed_start(self):
        for start in (184,186,187,188,189):
            ordinary=dict(fixture(i)[0] for i in range(16))
            ordinary[fixture(0)[0][0]]['location']['lineStart']=start
            self.reject(lambda:d.selected(r,ordinary))

    def test_actual_seven_key_metadata_is_preserved_during_selection(self):
        ordinary=dict(fixture(i)[0] for i in range(16))
        self.assertEqual(set(next(iter(ordinary.values()))['type']),{'namespace','typeName','methodName','assemblyFullName','methodArity','returnTypeFullName','parameterTypeFullNames'})
        self.assertEqual(len(d.selected(r,ordinary)),16)

    def test_infrastructure_qualification_checks_receipt_identity_and_exception_graph(self):
        for mode in ('unrelated','unvalidated-receipt','cause','context','cleanup'):
            with tempfile.TemporaryDirectory() as temp:
                lane=self.make_lane(temp);self.run_mock_cases(lane)
                if mode=='unrelated':lane.record(RuntimeError(CANARY))
                elif mode=='unvalidated-receipt':lane.record(o.CommandFailure(copy.deepcopy(lane.validated_failure_receipts[0]),[]))
                elif mode=='cleanup':lane.record(BaseExceptionGroup(CANARY,[r.Rejected('PD_CASE_FAILED'),r.Rejected('UC_SEAL')]))
                else:
                    error=next(x for x in lane.errors if isinstance(x,o.CommandFailure))
                    if mode=='cause':error.__cause__=RuntimeError(CANARY)
                    else:error.__context__=RuntimeError(CANARY)
                self.assertFalse(d.infrastructure_qualified(lane,True))

    def test_physical_wrapper_category_and_source_frames_are_bounded(self):
        path='src/SharpLink.Runtime/FrameworkTaskSupervisor.cs'
        frame=f'   at SharpLink.Runtime.FrameworkTaskSupervisor.DrainCoreAsync(Task signal) in /_/{path}:line 434'
        ex={'type':'SharpLink.Runtime.PhysicalTaskFailureException','message':CANARY,'stackTrace':'\n'.join([frame]*70)}
        result=self.classify(exception=ex)['exceptions'][0]
        self.assertEqual(result['category'],'physical-task-failure');self.assertEqual(len(result['sourceFrames']),64)
        self.assertEqual(result['omittedRecognizedStackLineCount'],6);self.assertEqual(result['unrecognizedStackLineCount'],0)
        self.assertNotIn(CANARY,json.dumps(result))

    def test_official_default_boolean_proof_has_exact_verified_blobs(self):
        proof=r.read_json(ROOT/'eng/stream-negotiation-history/boolean-display-name-proof.json')
        self.assertEqual(proof['commit'],'ff2dc5186b05eddc8f6aea03a18784df3aa6e737')
        self.assertEqual([x['gitBlob'] for x in proof['files']],['d5e60c66961739830767e49182140d346f77d1e9','4826a5b76b6bfd77da3e12535e84030d3bc0bb60','57fad6205c7d81d4899bb7c11ee02399d472b4e9'])

    def test_native_timeout_and_cancelled_failures_remain_genuine_closed_results(self):
        for status in ('timedOut','cancelled'):
            result=self.classify(status=status,exception={'type':'System.TimeoutException','message':CANARY})
            self.assertFalse(result['selectedPassed']);self.assertEqual(result['status'],status)
        with tempfile.TemporaryDirectory() as temp:
            lane=self.make_lane(temp);labels,_=self.run_mock_cases(lane,'native-timeout')
            self.assertTrue(lane.candidate_complete);self.assertEqual(len(lane.case_facts),16)
            self.assertEqual(lane.case_facts[0]['status'],'timedOut');self.assertTrue(d.infrastructure_qualified(lane,True))


if __name__ == '__main__': unittest.main()
