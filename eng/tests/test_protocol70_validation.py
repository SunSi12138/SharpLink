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


d = load('test_pure_runner', ROOT / 'eng/run-protocol70-validation.py')
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
    row = {'uid': uid, 'displayName': CANARY, 'type': {'namespace': ns, 'typeName': cls, 'methodName': method, 'assemblyFullName': d.ASSEMBLY_FULL_NAME, 'methodArity': 0, 'returnTypeFullName': d.METHOD_RETURN_TYPES[index], 'parameterTypeFullNames': []},
           'location': {'file': '/_/' + path, 'lineStart': d.ATTRIBUTE_LINES[index], 'lineEnd': end}}
    test = {'id': uid, 'displayName': CANARY, 'methodName': method, 'className': cls,
            'filePath': '/_/' + path, 'lineNumber': d.ATTRIBUTE_LINES[index], 'endLineNumber': end, 'status': status, 'durationMs': 1.0}
    if status != 'passed':
        test['exception'] = exception or {'type': 'Unknown.' + CANARY, 'message': CANARY, 'stackTrace': '/private/' + CANARY}
    native = {'schemaVersion': 1, 'publicationGeneration': 'a' * 32, 'assemblyName': 'SharpLink.UnitTests',
              'machineName': CANARY, 'timestamp': CANARY, 'tunitVersion': '1.73.19.0', 'operatingSystem': CANARY,
              'runtimeVersion': '.NET 10.0.14', 'totalDurationMs': 1.0,
              'groups': [{'namespace': ns, 'className': cls, 'tests': [test]}]}
    refresh(native)
    return (uid, row), native, test


def ordinary_metadata():
    result=dict(fixture(i)[0] for i in range(70))
    for i in range(70,2879):result['other-'+str(i)]={'uid':'other-'+str(i),'type':{'namespace':'Other'}}
    return result


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
        self.assertIn('Seventy existing parameterless protocol cases',d.__doc__)
        self.assertNotIn('timed controls',d.__doc__)
        for name, expected in d.HELPERS.items(): self.assertEqual(r.sha(BASE / name), expected)
        d.verify_source(r, BASE)













    def test_changed_source_rejected_before_diagnostics(self):
        with patch.object(r, 'sha', return_value='0' * 64): self.reject(lambda: d.verify_source(r, BASE))







    def test_pass_and_prior_control_are_bounded_diagnostic_only(self):
        for i in range(70):
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
        for _ in range(70): ex = {'type': 'System.Exception', 'message': CANARY, 'innerException': ex}
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
        selections = [fixture(i)[0] for i in range(70)]; labels = []; originals = []
        def run(label, argv, *args):
            labels.append(label); lane.sequence += 1
            if label == 'protocol70-python-controls':
                self.assertEqual(argv, [sys.executable, '-B', d.ROOT / 'eng/tests/test_protocol70_validation.py'])
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
            if mode == 'phase-budget' and index == 4: lane.stop = 0
            lane.commands[label] = {'receipt': receipt, 'argv': argv, 'seconds': 180,
                                    'ownerProvenance': 'validated-original-success' if passed else 'original-owner-failure'}
            if mode == 'owner-unknown': lane.commands[label]['ownerProvenance'] = None
            if not passed:
                original = o.CommandFailure(receipt, []); originals.append(original); lane.record(original); raise original
        with patch.object(lane, 'run', side_effect=run), patch.object(d, 'verify_source'), patch.object(d, 'verify_helpers'):
            d.run_cases(lane, e, selections, d.time.monotonic() + 5100)
        return labels, originals

    def test_seventy_fresh_processes_failure_preserved_without_retry(self):
        with tempfile.TemporaryDirectory() as temp:
            lane = self.make_lane(temp); labels, originals = self.run_mock_cases(lane)
            self.assertEqual(labels, ['protocol70-python-controls'] + ['protocol70-case-' + str(i) for i in range(1, 71)])
            self.assertEqual(len(lane.case_facts), 70); self.assertTrue(lane.candidate_complete)
            self.assertEqual([row['status'] for row in lane.case_facts], ['error'] * 7 + ['passed'] * 63)
            self.assertTrue(all(error in lane.errors for error in originals)); self.assertEqual(len(lane.seals), 70)
            with contextlib.redirect_stdout(io.StringIO()) as output: self.assertEqual(d.finish(lane, c, e), 1)
            summary = json.loads(output.getvalue()); self.assertFalse(summary['candidateValidationQualified']); self.assertTrue(summary['candidateValidationComplete']); self.assertFalse(summary['allSelectedPassed']); self.assertTrue(summary['infrastructureQualified'])
            self.assertFalse(summary['fullSuiteQualified']); self.assertNotIn(CANARY, output.getvalue())

    def test_unknown_timeout_survivor_cleanup_failure_cannot_parse_or_continue(self):
        for mode in ('owner-unknown', 'timeout', 'survivor', 'cleanup'):
            with self.subTest(mode=mode), tempfile.TemporaryDirectory() as temp:
                lane = self.make_lane(temp)
                with patch.object(d, 'classify') as classify:
                    self.reject(lambda: self.run_mock_cases(lane, mode)); classify.assert_not_called()
                self.assertFalse(lane.candidate_complete); self.assertEqual(len(lane.case_facts), 0); self.assertNotIn('protocol70-case-2', lane.commands)
                self.assertTrue(lane.errors)

    def test_process_exit_mismatch_and_source_mutation_cannot_qualify(self):
        for mode in ('process-failure', 'mutated-source'):
            with self.subTest(mode=mode), tempfile.TemporaryDirectory() as temp:
                lane = self.make_lane(temp); self.reject(lambda: self.run_mock_cases(lane, mode))
                self.assertFalse(lane.candidate_complete); self.assertEqual(len(lane.case_facts), 0)


    def test_deadline_summary_writer_and_post_emit_failure_revoke(self):
        for mode in ('deadline', 'writer', 'post-emit'):
            with self.subTest(mode=mode), tempfile.TemporaryDirectory() as temp:
                lane = self.make_lane(temp); lane.candidate_complete = True; lane.case_facts = [self.classify(i, 'passed') for i in range(70)]
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
            ordinary = ordinary_metadata(); original = d.selected
            clock = [1.0]; lane.stop = 2.0
            def slow_selection(*args):
                result = original(*args); clock[0] = 3.0; return result
            with patch.object(d, 'selected', side_effect=slow_selection), patch.object(d.time, 'monotonic', side_effect=lambda: clock[0]), patch.object(lane, 'run') as run:
                self.reject(lambda: d.handoff_selections(lane, ordinary, {'explicit-'+str(i) for i in range(5)})); run.assert_not_called()
            self.assertFalse(lane.preparation_complete); self.assertEqual(lane.stop, 2.0)

    def test_run_cases_cannot_revive_an_expired_preparation_or_overall_budget(self):
        for expired in ('preparation', 'overall'):
            with self.subTest(expired=expired), tempfile.TemporaryDirectory() as temp:
                lane = self.make_lane(temp); lane.stop = 0 if expired == 'preparation' else d.time.monotonic() + 3600
                before = lane.stop; overall = 0 if expired == 'overall' else d.time.monotonic() + 5100
                with patch.object(lane, 'run') as run:
                    self.reject(lambda: d.run_cases(lane, e, [fixture(i)[0] for i in range(70)], overall)); run.assert_not_called()
                self.assertEqual(lane.stop, before); self.assertFalse(lane.candidate_complete)

    def test_isolated_sdk_exact_directory_environment_and_symlink_guards(self):
        for mode in ('valid', 'wrong-root', 'wrong-env', 'multilevel', 'symlink'):
            with self.subTest(mode=mode), tempfile.TemporaryDirectory() as temp:
                lane = c.Lane(Path(temp), r, o)
                sdk = Path(temp) / 'protocol70-dotnet'; sdk.mkdir(); dotnet = sdk / 'dotnet'; dotnet.write_bytes(b'never execute')
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
        script = 'import tempfile,runpy; tempfile.mkdtemp=lambda *a,**k: (_ for _ in ()).throw(OSError("' + CANARY + '")); runpy.run_path(' + repr(str(ROOT / 'eng/run-protocol70-validation.py')) + ',run_name="__main__")'
        result = subprocess.run([sys.executable, '-B', '-c', script], capture_output=True, timeout=10)
        self.assertEqual(result.returncode, 1); self.assertEqual(result.stdout, b''); self.assertEqual(result.stderr, b'PROTOCOL70_VALIDATION_BOOTSTRAP_FAILED\n')

    def test_real_python_owner_nonzero_and_timeout_preserve_private_logs(self):
        o.enable_subreaper()
        for name, script, seconds in [('nonzero', 'import sys; print("' + CANARY + '"); sys.exit(7)', 2), ('timeout', 'import time; time.sleep(5)', .1)]:
            with self.subTest(name=name), tempfile.TemporaryDirectory() as temp:
                with self.assertRaises(o.CommandFailure) as context:
                    o.run_owned(name, [sys.executable, '-B', '-c', script], BASE, dict(os.environ), Path(temp), seconds)
                receipt = context.exception.outcome; self.assertEqual(receipt['remainingOwned'], {})
                self.assertEqual(receipt['timedOut'], name == 'timeout')
                self.assertTrue((Path(temp) / (name + '.stdout')).is_file())








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
        w=(ROOT/'.github/workflows/protocol70-validation.yml').read_text()
        self.assertIn('actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1',w)
        self.assertIn('actions/setup-dotnet@a98b56852c35b8e3190ac28c8c2271da59106c68',w)
        self.assertIn('dotnet-version: \'10.0.114\'',w)
        self.assertIn('DOTNET_INSTALL_DIR: ${{ runner.temp }}/protocol70-dotnet',w)
        self.assertIn('persist-credentials: false',w)
        self.assertNotIn('upload-artifact',w);self.assertNotIn('continue-on-error',w)
        self.assertNotIn('dotnet --',w);self.assertNotIn('dotnet test',w)
        self.assertIn('run: python3 eng/run-protocol70-validation.py --configuration Release',w)




    def test_genuine_test_failure_does_not_hide_independent_infrastructure_failure(self):
        with tempfile.TemporaryDirectory() as temp:
            lane=self.make_lane(temp);self.run_mock_cases(lane)
            self.assertTrue(d.infrastructure_qualified(lane,True))
            lane.record(r.Rejected('UC_SEAL'))
            self.assertFalse(d.infrastructure_qualified(lane,True))



    def test_infrastructure_qualification_checks_receipt_identity_and_exception_graph(self):
        with tempfile.TemporaryDirectory() as temp:
            lane=self.make_lane(temp);self.run_mock_cases(lane)
            errors_list=lane.errors;baseline_errors=list(lane.errors)
            baseline_receipts=list(lane.validated_failure_receipts)
            original=next(error for error in lane.errors if isinstance(error,o.CommandFailure))
            cause,context,suppressed=original.__cause__,original.__context__,original.__suppress_context__
            for mode in ('unrelated','unvalidated-receipt','cause','context','cleanup'):
                with self.subTest(mode=mode):
                    self.assertIs(lane.errors,errors_list);self.assertEqual(lane.errors,baseline_errors)
                    self.assertTrue(d.infrastructure_qualified(lane,True))
                    self.assertIs(original.outcome,baseline_receipts[0])
                    try:
                        if mode=='unrelated':lane.errors.append(RuntimeError(CANARY))
                        elif mode=='unvalidated-receipt':
                            foreign=copy.deepcopy(baseline_receipts[0]);self.assertIsNot(foreign,baseline_receipts[0])
                            lane.errors.append(o.CommandFailure(foreign,[]))
                        elif mode=='cleanup':lane.errors.append(BaseExceptionGroup(CANARY,[r.Rejected('PD_CASE_FAILED'),r.Rejected('UC_SEAL')]))
                        elif mode=='cause':original.__cause__=RuntimeError(CANARY)
                        else:original.__context__=RuntimeError(CANARY)
                        self.assertFalse(d.infrastructure_qualified(lane,True))
                    finally:
                        lane.errors[:]=baseline_errors
                        original.__cause__=cause;original.__context__=context;original.__suppress_context__=suppressed
                    self.assertIs(lane.errors,errors_list);self.assertEqual(lane.errors,baseline_errors)
                    self.assertIs(original.__cause__,cause);self.assertIs(original.__context__,context)
                    self.assertTrue(all(a is b for a,b in zip(lane.validated_failure_receipts,baseline_receipts)))
                    self.assertTrue(d.infrastructure_qualified(lane,True))



    def test_native_timeout_and_cancelled_failures_remain_genuine_closed_results(self):
        for status in ('timedOut','cancelled'):
            result=self.classify(status=status,exception={'type':'System.TimeoutException','message':CANARY})
            self.assertFalse(result['selectedPassed']);self.assertEqual(result['status'],status)
        with tempfile.TemporaryDirectory() as temp:
            lane=self.make_lane(temp);labels,_=self.run_mock_cases(lane,'native-timeout')
            self.assertTrue(lane.candidate_complete);self.assertEqual(len(lane.case_facts),70)
            self.assertEqual(lane.case_facts[0]['status'],'timedOut');self.assertTrue(d.infrastructure_qualified(lane,True))



    def test_historical_lanes_are_exact_and_not_imported_as_current_controls(self):
        import ast
        expected={'.github/workflows/pure-negotiation-full-regression.yml': '76a9eb49a3db1c930db6a57d3eb77cb11f0a44447978f5e4082bae4ad5a819a8', '.github/workflows/protocol-lifetime-diagnostic.yml': '9c0bceeb70fe833464f5bf9ea5bb414e3c55b082f4aa485dc1e94a359f928540', 'eng/run-pure-negotiation-full-regression.py': 'd708aef5417b425f21deb6a0c2a1281af1790af6a636a5ec42da20c5041165f6', 'eng/run-protocol-lifetime-diagnostic.py': 'bd179a2a314c618b2400ddc6061d4ef2cf3e06a5b065dbdd87ff4d2231a4a4a0', 'eng/tests/test_pure_negotiation_full_regression.py': 'b5045fe69f50d59b48642ca1fc2cb591111f00d3b09a71ffc918e13f705d55c7', 'eng/tests/test_protocol_lifetime_diagnostic.py': '88cfeee33501b84e95b18be25d97c8b72b2c3953a86f09518511a57a95421622', 'eng/pure-negotiation-full-history/sixteen-case-success.json': 'b6ba59c1cdf4cccc61db90a31226f342286ba4cdb8f8bfc323e7f5f6127fd90d'}
        for path,digest in expected.items():self.assertEqual(r.sha(ROOT/path),digest)
        for path in ('eng/run-pure-negotiation-full-regression.py','eng/run-protocol-lifetime-diagnostic.py'):
            m=ast.parse((ROOT/path).read_text());helpers=next(ast.literal_eval(n.value) for n in m.body if isinstance(n,ast.Assign) and any(isinstance(t,ast.Name) and t.id=='HELPERS' for t in n.targets))
            self.assertNotEqual(helpers['eng/unit-census.json'],e.CATALOG_SHA256)
        for path in ('eng/tests/test_pure_negotiation_full_regression.py','eng/tests/test_protocol_lifetime_diagnostic.py','eng/tests/test_pure_negotiation_validation.py'):
            self.assertNotIn(path,d.HELPERS)
        self.assertIn('eng/tests/test_protocol70_validation.py',d.HELPERS)

    def test_exact_full265_and_twenty_one_case_history_are_not_normalized(self):
        full=r.read_json(ROOT/'eng/protocol70-history/full-ordinary-result.json')
        self.assertEqual(r.sha(ROOT/'eng/protocol70-history/full-ordinary-result.json'),'073a199d62f9add8222789e3f4fec936377e64e2897cd5145497f046d0c3c8d6')
        self.assertEqual(full['head'],'1f5315ca23753258c002ebd6bed294f0226af603')
        facts=full['summary']['execution'];self.assertEqual((facts['observed'],facts['statusCounts']['passed'],facts['statusCounts']['failed']),(2879,2614,265))
        rows=[x for x in facts['methodStatusCounts'] if x['namespace']=='SharpLink.UnitTests.Protocol']
        self.assertEqual(len(rows),70);self.assertTrue(all(x['count']==1 for x in rows));self.assertEqual(sum(x['status']=='passed' for x in rows),66)
        self.assertEqual({(x['source'],x['className'],x['methodName']) for x in rows},{(x[0],x[2],x[3]) for x in d.CASES})
        result=r.read_json(ROOT/'eng/protocol70-history/twenty-one-case-diagnostic-result.json')
        self.assertEqual(r.sha(ROOT/'eng/protocol70-history/twenty-one-case-diagnostic-result.json'),'e6916265942d76dd66761a46d979014603c6f51035ec085687bb7b3af43fb5d4')
        self.assertEqual(result['head'],'0de69b451c744182f6962d253a1749899fdc29af')
        summary=result['summary'];self.assertTrue(summary['diagnosticComplete']);self.assertTrue(summary['infrastructureQualified']);self.assertFalse(summary['diagnosticQualified'])
        self.assertEqual(len(summary['cases']),21);self.assertEqual(sum(row['status']=='passed' for row in summary['cases']),17)
        self.assertEqual(summary['errorOccurrences'],12)

    def test_catalog_binds_exact_seventy_parameterless_ordinary_source_identities(self):
        catalog=r.read_json(ROOT/'eng/unit-census.json')
        rows=sorted((m for m in catalog['methods'] if m['namespace']=='SharpLink.UnitTests.Protocol'),key=lambda m:(m['source'],m['className'],m['methodName']))
        self.assertEqual(len(rows),70);self.assertEqual(len(d.CASES),70)
        for i,m in enumerate(rows):
            self.assertEqual(d.CASES[i],(m['source'],m['namespace'],m['className'],m['methodName'],m['signatureLine'],m['endLine']))
            self.assertEqual(d.ATTRIBUTE_LINES[i],m['attributeLine']);self.assertEqual(d.METHOD_RETURN_TYPES[i],m['return'])
            self.assertEqual((m['expectedCases'],m['arity'],m['parameters'],m['initialEligibility']),(1,0,[],'ordinary'));self.assertIn('Release',m['configurations'])
        self.assertEqual(sum(x=='System.Threading.Tasks.Task' for x in d.METHOD_RETURN_TYPES),16)
        selected=d.selected(r,ordinary_metadata());self.assertEqual(len(selected),70)
        self.assertEqual(len({uid for uid,_ in selected}),70)

    def test_selection_rejects_missing_extra_duplicate_fuzzy_and_wrong_type_or_span(self):
        for mode in ('short','missing','extra','duplicate','namespace-prefix','method-prefix','source','attribute','end','arity','parameters','return','assembly','type-extra','uid','cli'):
            ordinary=ordinary_metadata();uid,row=next(iter(ordinary.items()))
            if mode=='short':ordinary.pop('other-70')
            elif mode=='missing':row['type']['namespace']='Other'
            elif mode=='extra':ordinary['other-70']=copy.deepcopy(row);ordinary['other-70']['uid']='other-70';ordinary['other-70']['type']['methodName']='Extra'
            elif mode=='duplicate':ordinary[fixture(1)[0][0]]['type']=copy.deepcopy(row['type'])
            elif mode=='namespace-prefix':row['type']['namespace']+='Extra'
            elif mode=='method-prefix':row['type']['methodName']+='Extra'
            elif mode=='source':row['location']['file']='/private/'+CANARY
            elif mode=='attribute':row['location']['lineStart']=d.CASES[0][4]
            elif mode=='end':row['location']['lineEnd']+=1
            elif mode=='arity':row['type']['methodArity']=False
            elif mode=='parameters':row['type']['parameterTypeFullNames']=['System.Boolean']
            elif mode=='return':row['type']['returnTypeFullName']='Other'
            elif mode=='assembly':row['type']['assemblyFullName']='Other'
            elif mode=='type-extra':row['type']['private']=CANARY
            elif mode=='uid':row['uid']='wrong'
            else:ordinary['-unsafe']={**ordinary.pop(uid),'uid':'-unsafe'}
            with self.subTest(mode=mode):self.reject(lambda:d.selected(r,ordinary))

    def test_exact_helper_frames_have_no_raw_path_or_unrecognized_signature(self):
        for i,case in enumerate(d.CASES):
            source,namespace,cls,method,start,end=case
            known=f'   at {namespace}.{cls}.{method}() in /_/{source}:line {start}'
            bad=known.replace('()','(PRIVATE argument)')
            frames,unknown,omitted=d.source_frames(i,known+'\n'+bad)
            self.assertEqual(frames,[{'source':source,'member':method,'line':start}]);self.assertEqual((unknown,omitted),(1,0))
        for path,specs in d.HELPER_FRAMES.items():
            i=next(i for i,c in enumerate(d.CASES) if c[0]==path);_,ns,cls,_,_,_=d.CASES[i]
            for method,signature,start,end in specs:
                self.assertIn(method+'(',(ROOT/path).read_text().splitlines()[start-1])
                raw=f'   at {ns}.{cls}.{method}({signature}) in /_/{path}:line {end}'
                self.assertEqual(d.source_frames(i,raw)[0],[{'source':path,'member':method,'line':end}])

    def test_recognized_frames_and_exception_depth_have_fixed_limits(self):
        path,ns,cls,method,start,end=d.CASES[0];frame=f'   at {ns}.{cls}.{method}() in /_/{path}:line {start}'
        value=self.classify(exception={'type':'System.Exception','message':CANARY,'stackTrace':'\n'.join([frame]*70)})['exceptions'][0]
        self.assertEqual(len(value['sourceFrames']),64);self.assertEqual(value['omittedRecognizedStackLineCount'],6)
        self.assertNotIn(CANARY,json.dumps(value));self.reject(lambda:d.source_frames(0,'\n'.join([frame]*4097)))

    def test_fixed_cancel_literal_is_bound_without_unknown_message_suffixes(self):
        for indexes,typ,message,tag,path,line in d.EXCEPTION_TAGS:
            self.assertIn(message,(ROOT/path).read_text().splitlines()[line-1]);self.assertIn(path,d.SOURCE_PINS)
            for i in indexes:
                value=self.classify(i,exception={'type':typ,'message':message})['exceptions'][0]
                self.assertEqual(value['assertionTag'],tag)
            value=self.classify(indexes[0],exception={'type':typ,'message':message+CANARY})['exceptions'][0]
            self.assertEqual(value['assertionTag'],'unknown');self.assertNotIn(CANARY,json.dumps(value))

    def test_parameterless_identity_never_uses_display_name(self):
        for i in range(70):
            selection=fixture(i)[0];value=d.argument_identity(i,selection)
            self.assertEqual(value['kind'],'parameterless');self.assertIsNone(value['value']);self.assertIsNone(value['parameter'])
            self.assertEqual(value['caseID'],d.hashlib.sha256(selection[0].encode()).hexdigest());self.assertNotIn(CANARY,json.dumps(value))

    def test_all_seventy_passes_required_and_targeted_pass_does_not_resolve_history(self):
        for bad in (False,True):
            with tempfile.TemporaryDirectory() as temp:
                lane=self.make_lane(temp);self.run_mock_cases(lane,'all-pass');self.assertEqual(lane.sequence,103)
                if bad:lane.case_facts[-1]['selectedPassed']=False
                self.assertFalse((lane.evidence/'protocol70-validation-summary.json').exists())
                with contextlib.redirect_stdout(io.StringIO()) as output:self.assertEqual(d.finish(lane,c,e),1 if bad else 0)
                summary=json.loads(output.getvalue());self.assertEqual(summary['candidateValidationQualified'],not bad)
                self.assertFalse(summary['fullSuiteQualified']);self.assertEqual(summary['historicalFullRun']['errors'],265)
                self.assertEqual(summary['protocolSliceQualified'],not bad);self.assertEqual(summary['maximumParallelTestsOption'],1)
                self.assertEqual(summary['plannedCommandCount'],103);self.assertEqual(summary['casePhaseBudgetSeconds'],3600)
                self.assertEqual(summary['previousFullRun']['errors'],272);self.assertEqual(summary['originalFullRun']['errors'],290)
                self.assertEqual(summary['historicalTwentyOneCaseDiagnostic']['errors'],4)
                self.assertEqual(summary['historicalLifetimeFailuresResolution'],'unresolved-despite-isolated-pass')

    def test_partial_duplicate_or_budget_exhausted_case_set_cannot_pass(self):
        for mode in ('partial','duplicate','budget'):
            with tempfile.TemporaryDirectory() as temp:
                lane=self.make_lane(temp);lane.candidate_complete=True;lane.case_facts=[self.classify(i,'passed') for i in range(70)]
                if mode=='partial':lane.case_facts.pop()
                elif mode=='duplicate':lane.case_facts[-1]['case']=1
                else:lane.stop=0
                with contextlib.redirect_stdout(io.StringIO()),patch.object(d.os,'write',return_value=1):self.assertEqual(d.finish(lane,c,e),1)
        self.assertGreater(70*180,3600)

    def test_mid_sequence_aggregate_budget_stops_incomplete_without_retry(self):
        with tempfile.TemporaryDirectory() as temp:
            lane=self.make_lane(temp)
            self.reject(lambda:self.run_mock_cases(lane,'phase-budget'))
            self.assertFalse(lane.candidate_complete);self.assertEqual(len(lane.case_facts),4)
            self.assertIn('protocol70-case-5',lane.commands);self.assertNotIn('protocol70-case-6',lane.commands)
            self.assertEqual(lane.stop,0);self.assertFalse(d.infrastructure_qualified(lane,False))
            self.assertTrue(lane.errors)
            with contextlib.redirect_stdout(io.StringIO()),patch.object(d.os,'write',return_value=1):self.assertEqual(d.finish(lane,c,e),1)

    def test_explicit_intersection_never_hands_off(self):
        with tempfile.TemporaryDirectory() as temp:
            lane=self.make_lane(temp);lane.identity.update(sdk='10.0.114',installedRuntimes=d.RUNTIMES)
            ordinary=ordinary_metadata();explicit={next(iter(ordinary)),*['explicit-'+str(i) for i in range(4)]}
            self.reject(lambda:d.handoff_selections(lane,ordinary,explicit))


if __name__ == '__main__': unittest.main()
