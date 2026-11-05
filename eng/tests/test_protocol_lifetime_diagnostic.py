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


d = load('test_pure_runner', ROOT / 'eng/run-protocol-lifetime-diagnostic.py')
e = load('test_pure_execution', BASE / 'eng/unit-execution-results.py')
c = load('test_pure_census', BASE / 'eng/run-unit-census.py')
o = load('test_pure_owner', BASE / 'eng/package-smoke-process.py')
r = e.census
FullRegressionControls = load('test_latest_full_lane', ROOT / 'eng/tests/test_pure_negotiation_full_regression.py').Controls
RebaseControls = load('test_candidate_rebase', ROOT / 'eng/tests/test_three_fixture_census.py').Controls
CANARY = 'PRIVATE_CANARY_8fe5fca2'


def fixture(index=0, status='error', exception=None):
    path, ns, cls, method, start, end = d.CASES[index]
    uid = 'private-uid-' + str(index)
    group=d.group_for(index)
    displays=list(d.expected_displays(group))
    display=displays[index-group['first']] if displays else CANARY
    row = {'uid': uid, 'displayName': display,
           'type': {'namespace': ns, 'typeName': cls, 'methodName': method,
                    'assemblyFullName': 'SharpLink.UnitTests, Version=3.0.0.0, Culture=neutral, PublicKeyToken=null',
                    'methodArity': 0, 'returnTypeFullName': d.RETURN_TYPES[index], 'parameterTypeFullNames': d.PARAMETER_TYPES[index]},
           'location': {'file': '/_/' + path, 'lineStart': d.ATTRIBUTE_LINES[index], 'lineEnd': end}}
    test = {'id': uid, 'displayName': display, 'methodName': method, 'className': cls,
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







    def test_changed_source_rejected_before_diagnostics(self):
        with patch.object(r, 'sha', return_value='0' * 64): self.reject(lambda: d.verify_source(r, BASE))







    def test_pass_and_prior_control_are_bounded_diagnostic_only(self):
        for i in range(21):
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
        for _ in range(21): ex = {'type': 'System.Exception', 'message': CANARY, 'innerException': ex}
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
        lane.validated_failure_receipts = []; lane.identity = {'binarySHA256': r.sha(dll)}; lane.case_facts = []; lane.diagnostic_complete = False; lane.preparation_complete = True; lane.sequence = 32
        return lane

    def run_mock_cases(self, lane, mode='mixed'):
        selections = [fixture(i)[0] for i in range(21)]; labels = []; originals = []
        def run(label, argv, *args):
            labels.append(label); lane.sequence += 1
            if label == 'protocol-lifetime-python-controls':
                self.assertEqual(argv, [sys.executable, '-B', d.ROOT / 'eng/tests/test_protocol_lifetime_diagnostic.py'])
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
            d.run_cases(lane, e, selections, d.time.monotonic() + 8400)
        return labels, originals

    def test_twenty_one_fresh_processes_failure_preserved_without_retry(self):
        with tempfile.TemporaryDirectory() as temp:
            lane = self.make_lane(temp); labels, originals = self.run_mock_cases(lane)
            self.assertEqual(labels, ['protocol-lifetime-python-controls'] + ['protocol-lifetime-case-' + str(i) for i in range(1, 22)])
            self.assertEqual(len(lane.case_facts), 21); self.assertTrue(lane.diagnostic_complete)
            self.assertEqual([row['status'] for row in lane.case_facts], ['error'] * 7 + ['passed'] * 14)
            self.assertTrue(all(error in lane.errors for error in originals)); self.assertEqual(len(lane.seals), 21)
            with contextlib.redirect_stdout(io.StringIO()) as output: self.assertEqual(d.finish(lane, c, e), 1)
            summary = json.loads(output.getvalue()); self.assertFalse(summary['diagnosticQualified']); self.assertTrue(summary['diagnosticComplete']); self.assertFalse(summary['allSelectedPassed']); self.assertTrue(summary['infrastructureQualified'])
            self.assertFalse(summary['fullSuiteQualified']); self.assertNotIn(CANARY, output.getvalue())

    def test_unknown_timeout_survivor_cleanup_failure_cannot_parse_or_continue(self):
        for mode in ('owner-unknown', 'timeout', 'survivor', 'cleanup'):
            with self.subTest(mode=mode), tempfile.TemporaryDirectory() as temp:
                lane = self.make_lane(temp)
                with patch.object(d, 'classify') as classify:
                    self.reject(lambda: self.run_mock_cases(lane, mode)); classify.assert_not_called()
                self.assertFalse(lane.diagnostic_complete); self.assertEqual(len(lane.case_facts), 0); self.assertNotIn('protocol-lifetime-case-2', lane.commands)
                self.assertTrue(lane.errors)

    def test_process_exit_mismatch_and_source_mutation_cannot_qualify(self):
        for mode in ('process-failure', 'mutated-source'):
            with self.subTest(mode=mode), tempfile.TemporaryDirectory() as temp:
                lane = self.make_lane(temp); self.reject(lambda: self.run_mock_cases(lane, mode))
                self.assertFalse(lane.diagnostic_complete); self.assertEqual(len(lane.case_facts), 0)


    def test_deadline_summary_writer_and_post_emit_failure_revoke(self):
        for mode in ('deadline', 'writer', 'post-emit'):
            with self.subTest(mode=mode), tempfile.TemporaryDirectory() as temp:
                lane = self.make_lane(temp); lane.diagnostic_complete = True; lane.case_facts = [self.classify(i, 'passed') for i in range(21)]
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
            ordinary = dict(fixture(i)[0] for i in range(21)); original = d.selected
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
                before = lane.stop; overall = 0 if expired == 'overall' else d.time.monotonic() + 8400
                with patch.object(lane, 'run') as run:
                    self.reject(lambda: d.run_cases(lane, e, [fixture(i)[0] for i in range(21)], overall)); run.assert_not_called()
                self.assertEqual(lane.stop, before); self.assertFalse(lane.diagnostic_complete)

    def test_isolated_sdk_exact_directory_environment_and_symlink_guards(self):
        for mode in ('valid', 'wrong-root', 'wrong-env', 'multilevel', 'symlink'):
            with self.subTest(mode=mode), tempfile.TemporaryDirectory() as temp:
                lane = c.Lane(Path(temp), r, o)
                sdk = Path(temp) / 'protocol-lifetime-dotnet'; sdk.mkdir(); dotnet = sdk / 'dotnet'; dotnet.write_bytes(b'never execute')
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
        script = 'import tempfile,runpy; tempfile.mkdtemp=lambda *a,**k: (_ for _ in ()).throw(OSError("' + CANARY + '")); runpy.run_path(' + repr(str(ROOT / 'eng/run-protocol-lifetime-diagnostic.py')) + ',run_name="__main__")'
        result = subprocess.run([sys.executable, '-B', '-c', script], capture_output=True, timeout=10)
        self.assertEqual(result.returncode, 1); self.assertEqual(result.stdout, b''); self.assertEqual(result.stderr, b'PROTOCOL_LIFETIME_DIAGNOSTIC_BOOTSTRAP_FAILED\n')

    def test_real_python_owner_nonzero_and_timeout_preserve_private_logs(self):
        o.enable_subreaper()
        for name, script, seconds in [('nonzero', 'import sys; print("' + CANARY + '"); sys.exit(7)', 2), ('timeout', 'import time; time.sleep(5)', .1)]:
            with self.subTest(name=name), tempfile.TemporaryDirectory() as temp:
                with self.assertRaises(o.CommandFailure) as context:
                    o.run_owned(name, [sys.executable, '-B', '-c', script], BASE, dict(os.environ), Path(temp), seconds)
                receipt = context.exception.outcome; self.assertEqual(receipt['remainingOwned'], {})
                self.assertEqual(receipt['timedOut'], name == 'timeout')
                self.assertTrue((Path(temp) / (name + '.stdout')).is_file())







    def test_all_pass_does_not_resolve_historical_failure(self):
        with tempfile.TemporaryDirectory() as temp:
            lane=self.make_lane(temp);self.run_mock_cases(lane,'all-pass')
            with contextlib.redirect_stdout(io.StringIO()) as output:self.assertEqual(d.finish(lane,c,e),0)
            result=json.loads(output.getvalue())
            self.assertTrue(result['diagnosticQualified']);self.assertFalse(result['fullSuiteQualified'])
            self.assertEqual(result['historicalFullRun']['errors'],265);self.assertFalse(result['historicalFullRun']['qualified'])
            self.assertFalse(result['historicalArgumentMappingAvailable'])

    def test_source_check_and_last_seal_cannot_cross_handoff_deadline(self):
        for mode in ('clock','seal'):
            with tempfile.TemporaryDirectory() as temp:
                lane=self.make_lane(temp);lane.preparation_complete=False;lane.stop=2.0;clock=[1.0]
                def source(*args):
                    if mode=='clock':clock[0]=3.0
                    else:lane.inputs[next(iter(lane.inputs))][-1]='0'*64
                with patch.object(d.time,'monotonic',side_effect=lambda:clock[0]),patch.object(d,'verify_helpers'),patch.object(d,'verify_source',side_effect=source),patch.object(lane,'run') as run:
                    self.reject(lambda:d.run_cases(lane,e,[],8400.0));run.assert_not_called()
                self.assertFalse(lane.preparation_complete);self.assertEqual(lane.stop,2.0)

    def test_workflow_has_pinned_sdk_and_no_preownership_dotnet_invocation(self):
        w=(ROOT/'.github/workflows/protocol-lifetime-diagnostic.yml').read_text()
        self.assertIn('actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1',w)
        self.assertIn('actions/setup-dotnet@a98b56852c35b8e3190ac28c8c2271da59106c68',w)
        self.assertIn('dotnet-version: \'10.0.114\'',w)
        self.assertIn('DOTNET_INSTALL_DIR: ${{ runner.temp }}/protocol-lifetime-dotnet',w)
        self.assertIn('persist-credentials: false',w)
        self.assertNotIn('upload-artifact',w);self.assertNotIn('continue-on-error',w)
        self.assertNotIn('dotnet --',w);self.assertNotIn('dotnet test',w)
        self.assertIn('run: python3 eng/run-protocol-lifetime-diagnostic.py --configuration Release',w)




    def test_genuine_test_failure_does_not_hide_independent_infrastructure_failure(self):
        with tempfile.TemporaryDirectory() as temp:
            lane=self.make_lane(temp);self.run_mock_cases(lane)
            self.assertTrue(d.infrastructure_qualified(lane,True))
            lane.record(r.Rejected('UC_SEAL'))
            self.assertFalse(d.infrastructure_qualified(lane,True))


    def test_actual_seven_key_metadata_is_preserved_during_selection(self):
        ordinary=dict(fixture(i)[0] for i in range(21))
        self.assertEqual(set(next(iter(ordinary.values()))['type']),{'namespace','typeName','methodName','assemblyFullName','methodArity','returnTypeFullName','parameterTypeFullNames'})
        self.assertEqual(len(d.selected(r,ordinary)),21)

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
            self.assertTrue(lane.diagnostic_complete);self.assertEqual(len(lane.case_facts),21)
            self.assertEqual(lane.case_facts[0]['status'],'timedOut');self.assertTrue(d.infrastructure_qualified(lane,True))



    def test_exact_twenty_one_inventory_and_protocol_first(self):
        catalog=r.read_json(ROOT/'eng/unit-census.json')
        self.assertEqual((len(d.CASES), d.REQUIRED_CASES, d.BODY_COMMAND_COUNT),(21,21,54))
        self.assertEqual([d.group_for(i)['kind'] for i in range(21)],['protocol']*7+['response-lease']*4+['timer']*3+['prebuild']*7)
        for index,(source,ns,cls,method,start,end) in enumerate(d.CASES):
            rows=[m for m in catalog['methods'] if (m['source'],m['namespace'],m['className'],m['methodName'])==(source,ns,cls,method)]
            self.assertEqual(len(rows),1);row=rows[0]
            self.assertEqual((row['signatureLine'],row['endLine'],row['attributeLine']),(start,end,d.ATTRIBUTE_LINES[index]))
            self.assertEqual(row['parameters'],d.PARAMETER_TYPES[index]);self.assertEqual(row['expectedCases'],d.group_for(index)['count'])
            self.assertEqual(row['initialEligibility'],'ordinary')
        self.assertEqual(len(d.selected(r,dict(fixture(i)[0] for i in range(21)))),21)
        self.assertEqual(len(set(c[3] for c in d.CASES)),15)

    def test_exact_seven_key_metadata_and_all_attributed_starts(self):
        good=dict(fixture(i)[0] for i in range(21))
        for index in range(21):
            for mode in ('start','end','file','params','return','assembly','arity','extra','missing','duplicate','uid'):
                ordinary=copy.deepcopy(good);uid=fixture(index)[0][0];row=ordinary[uid]
                if mode=='start':row['location']['lineStart']+=1
                elif mode=='end':row['location']['lineEnd']+=1
                elif mode=='file':row['location']['file']='/private/'+CANARY
                elif mode=='params':row['type']['parameterTypeFullNames']=['unknown']
                elif mode=='return':row['type']['returnTypeFullName']='unknown'
                elif mode=='assembly':row['type']['assemblyFullName']='unknown'
                elif mode=='arity':row['type']['methodArity']=False
                elif mode=='extra':row['type']['extra']=CANARY
                elif mode=='missing':del ordinary[uid]
                elif mode=='duplicate':ordinary['extra']={**row,'uid':'extra'}
                else:ordinary['-unsafe']={**ordinary.pop(uid),'uid':'-unsafe'}
                with self.subTest(index=index,mode=mode):self.reject(lambda:d.selected(r,ordinary))
        for start in (24,25,26,51,52,53,54,55,56,57):
            index=7 if start<50 else 14;ordinary=copy.deepcopy(good)
            ordinary[fixture(index)[0][0]]['location']['lineStart']=start
            self.reject(lambda:d.selected(r,ordinary))

    def test_boolean_and_stage_complete_unique_source_literal_sets(self):
        selections=[fixture(i)[0] for i in range(21)];proofs=d.argument_proofs(selections)
        self.assertEqual(proofs,{7:True,14:True})
        for i in list(range(7,9))+list(range(14,20)):
            out=d.argument_identity(i,selections[i],proofs);group=d.group_for(i)
            expected=list(d.expected_displays(group).values())[i-group['first']]
            self.assertEqual((out['value'],out['literalSource']['line']),expected)
            literal=(ROOT/d.CASES[i][0]).read_text().splitlines()[expected[1]-1]
            self.assertIn('[Arguments('+('true' if expected[0] is True else 'false' if expected[0] is False else '"'+expected[0]+'"')+')]',literal)
            self.assertEqual(out['caseID'],d.hashlib.sha256(selections[i][0].encode()).hexdigest())
        # UID order is intentionally reversed: labels must follow exact names.
        for first,count in ((7,2),(14,6)):
            shuffled=copy.deepcopy(selections);shuffled[first:first+count]=list(reversed(shuffled[first:first+count]))
            for i in range(first,first+count):
                value=d.argument_identity(i,shuffled[i],d.argument_proofs(shuffled))['value']
                self.assertEqual(value,d.expected_displays(d.group_for(i))[shuffled[i][1]['displayName']][0])

    def test_duplicate_or_unknown_name_downgrades_whole_parameter_group(self):
        for first,count in ((7,2),(14,6)):
            for mode in ('duplicate','unknown','case','quotes','suffix','dot'):
                selections=[fixture(i)[0] for i in range(21)]
                a=selections[first][1];b=selections[first+1][1]
                if mode=='duplicate':b['displayName']=a['displayName']
                elif mode=='unknown':b['displayName']=CANARY
                elif mode=='case':b['displayName']=b['displayName'].lower()
                elif mode=='quotes':b['displayName']=b['displayName'].replace('(', '("').replace(')', '")')
                elif mode=='suffix':b['displayName']+=CANARY
                else:b['displayName']=b['displayName'].replace('(', '(private.')
                proofs=d.argument_proofs(selections);self.assertFalse(proofs[first])
                for i in range(first,first+count):
                    out=d.argument_identity(i,selections[i],proofs)
                    self.assertEqual(out['kind'],'opaque-parameter-case');self.assertIsNone(out['value']);self.assertNotIn(CANARY,json.dumps(out))

    def test_native_display_mismatch_cannot_project_argument(self):
        selections=[fixture(i)[0] for i in range(21)]
        for i in list(range(7,9))+list(range(14,20)):
            selection,native,test=fixture(i)
            test['displayName']=CANARY
            self.reject(lambda:d.classify(e,native,junit(native),i,selection,d.argument_proofs(selections)))

    def test_all_fixed_message_tags_source_scoped_and_never_partial(self):
        self.assertEqual(len(d.EXCEPTION_TAGS),70)
        for indexes,typ,message,tag,path,line in d.EXCEPTION_TAGS:
            text=(ROOT/path).read_text().splitlines()[line-1]
            if message.startswith('R23 exact '):
                self.assertEqual(line,69);self.assertIn('"R23 exact " + stage + " prebuild marker"',text)
                self.assertIn(message.split()[2],('targets','buckets','providers','state','signal'))
            elif message=='Missing actual diagnostic field _error':
                self.assertIn('Missing actual diagnostic field {field}',text)
                self.assertIn('"_error"',(ROOT/path).read_text().splitlines()[41])
            else:self.assertIn(message,text)
            i=indexes[0];out=self.classify(i,exception={'type':typ,'message':message})['exceptions'][0]
            self.assertEqual(out['assertionTag'],tag);self.assertNotIn(message,json.dumps(out))
            for changed in (message+CANARY,CANARY+message):
                self.assertEqual(self.classify(i,exception={'type':typ,'message':changed})['exceptions'][0]['assertionTag'],'unknown')
            other=next(i for i in range(21) if i not in indexes)
            self.assertEqual(self.classify(other,exception={'type':typ,'message':message})['exceptions'][0]['assertionTag'],'unknown')

    def test_fixed_frames_match_only_full_source_bound_signature(self):
        for indexes,path,typ,member,signature,first,last in d.EXTRA_FRAMES:
            self.assertIn(path,d.SOURCE_PINS)
            self.assertLessEqual(first,last);self.assertLessEqual(last,len((ROOT/path).read_text().splitlines()))
            frame=f'   at {typ}.{member}({signature}) in /_/{path}:line {last}'
            raw='\n'.join((frame,frame+CANARY,frame.replace('/_/','/private/'),frame.replace(':line '+str(last),':line 9999')))
            out=self.classify(indexes[0],exception={'type':'System.AggregateException','message':CANARY,'stackTrace':raw})
            self.assertEqual(out['exceptions'][0]['sourceFrames'],[{'source':path,'member':member,'line':last}])
            self.assertEqual(out['exceptions'][0]['unrecognizedStackLineCount'],3);self.assertNotIn(CANARY,json.dumps(out))
            self.assertEqual(out['exceptionEvidenceScope'],'serialized-innerException-chain-only');self.assertFalse(out['aggregateSiblingCausesObserved'])

    def test_lambda_frames_require_exact_known_family_and_bounded_ordinals(self):
        for i in (7,9,10,14,20):
            path,ns,cls,method,start,end=d.CASES[i]
            signature='Fixture fixture' if i<11 else 'FailureFixture fixture'
            prefix=f'   at {ns}.{cls}.<>c__DisplayClass12_0.'
            direct=f'{prefix}<{method}>b__0({signature}) in /_/{path}:line {start+3}'
            state=f'{prefix}<<{method}>b__0>d.MoveNext() in /_/{path}:line {start+3}'
            for good in (direct,state):
                negatives=[good+CANARY,CANARY+good,good.replace('12_0','1234_0'),good.replace('b__0','b__1234'),good.replace(method,'Unknown'),good.replace('/_/','/private/'),good.replace(':line '+str(start+3),':line '+str(end+1)),good.replace(cls,'UnknownClass'),good.replace('fixture','privateValue') if 'fixture' in good else good.replace('MoveNext()', 'MoveNext(privateValue)'),good.replace('MoveNext()', 'MoveNext('+CANARY+')') if 'MoveNext' in good else good.replace(signature,signature+CANARY)]
                out=self.classify(i,exception={'type':'System.AggregateException','message':CANARY,'stackTrace':'\n'.join([good]+negatives)})
                self.assertEqual(out['exceptions'][0]['sourceFrames'],[{'source':path,'member':method+'-body-lambda','line':start+3}])
                self.assertEqual(out['exceptions'][0]['unrecognizedStackLineCount'],len(negatives));self.assertEqual(out['bodyVersusCleanup'],'inconclusive')
                self.assertNotIn(CANARY,json.dumps(out));self.assertNotIn('DisplayClass',json.dumps(out))

    def test_bounded_frames_chain_and_private_route_failure_arrays(self):
        i=11;path,ns,cls,method,start,end=d.CASES[i]
        frame=f'   at {ns}.{cls}.{method}() in /_/{path}:line 201'
        out=self.classify(i,exception={'type':'System.TimeoutException','message':CANARY,'stackTrace':'\n'.join([frame]*70)})
        row=out['exceptions'][0];self.assertEqual((len(row['sourceFrames']),row['omittedRecognizedStackLineCount'],row['unrecognizedStackLineCount']),(64,6,0))
        self.reject(lambda:self.classify(i,exception={'type':'System.Exception','message':CANARY,'stackTrace':'\n'.join([frame]*4097)}))
        for typ,category in (('SharpLink.Client.SharpLinkClient+ContractRouteFailure','contract-route-failure'),('SharpLink.Client.SharpLinkClient.ContractRouteFailure','unknown'),('System.Threading.Tasks.TaskCanceledException','task-cancelled'),('System.OperationCanceledException','operation-cancelled')):
            out=self.classify(14,exception={'type':typ,'message':CANARY})
            self.assertEqual(out['exceptions'][0]['category'],category);self.assertFalse(out['privateRouteFailurePayloadObserved']);self.assertNotIn(CANARY,json.dumps(out))
        for key in ('InnerExceptions','innerExceptions','ErrorAt','StageAt','errors','stages'):
            ex={'type':'SharpLink.Client.SharpLinkClient+ContractRouteFailure','message':CANARY,key:[CANARY]}
            self.reject(lambda:self.classify(14,exception=ex))

    def test_aggregate_title_not_treated_as_complete_message(self):
        for i in (7,14):
            out=self.classify(i,exception={'type':'System.AggregateException','message':'Body first, then independent original owner failures. ('+CANARY+')','innerException':{'type':'System.TimeoutException','message':CANARY}})
            self.assertEqual([x['category'] for x in out['exceptions']],['aggregate','timeout']);self.assertEqual(out['exceptions'][0]['assertionTag'],'unknown')
            self.assertFalse(out['aggregateSiblingCausesObserved']);self.assertEqual(out['bodyVersusCleanup'],'inconclusive');self.assertNotIn(CANARY,json.dumps(out))

    def test_current_history_immutable_and_not_argument_attributed(self):
        h=r.read_json(ROOT/'eng/protocol-lifetime-history/current-full-result.json')
        self.assertEqual((h['runId'],h['head'],h['tree'],h['passed'],h['errors']),(41970862705,'1f5315ca23753258c002ebd6bed294f0226af603','4217a22cf7fc0d804d0e3ad50e2bf54c33c04ba4',2614,265))
        self.assertEqual(h['checkpointSHA256'],'073a199d62f9add8222789e3f4fec936377e64e2897cd5145497f046d0c3c8d6')
        self.assertFalse(h['qualified']);self.assertEqual(h['argumentIdentity'],'not-present-in-historical-method-aggregation')
        self.assertEqual(sum(x['count'] for x in h['methodStatusCounts']),21)
        self.assertEqual(sum(x['count'] for x in h['methodExceptionCategoryCounts']),8)
        self.assertEqual(sum(x['count'] for x in h['methodStatusCounts'] if x['status']=='passed'),13)
        self.assertEqual(next(x['category'] for x in h['methodExceptionCategoryCounts'] if x['methodName'].startswith('Overlapping')),'unknown')

    def test_official_string_formatter_proof_and_approved_budgets(self):
        p=r.read_json(ROOT/'eng/protocol-lifetime-history/argument-display-name-proof.json')
        self.assertEqual(p['commit'],'ff2dc5186b05eddc8f6aea03a18784df3aa6e737')
        self.assertIn('str.Replace',json.dumps(p));self.assertIn('4826a5b76b6bfd77da3e12535e84030d3bc0bb60',json.dumps(p))
        source=(ROOT/'eng/run-protocol-lifetime-diagnostic.py').read_text()
        self.assertIn('started + 3600',source);self.assertIn('now + 4800',source);self.assertIn('started + 8400',source)
        self.assertIn("lane.source, 180",source);self.assertIn("'60s'",source)
        workflow=(ROOT/'.github/workflows/protocol-lifetime-diagnostic.yml').read_text();self.assertIn('timeout-minutes: 165',workflow)

if __name__ == '__main__': unittest.main()
