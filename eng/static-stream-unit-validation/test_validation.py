#!/usr/bin/env python3
"""Synthetic negative controls; no product tests, network, or report uploads."""
from __future__ import annotations

import copy
import contextlib
import io
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

import provenance
from run import require_pinned_sdk, require_test_runtime, verify_hosts, private_test_environment, verify_allocation_child_runtimes
from scope import explicit_workers, ordinary_uids, verify_explicit_sources
from validate import (GateError, check_summary, discovery_uids, read_report, strict_json,
                      uid_digest, unique_uids, validate_run)

UIDS = ['unit.A', 'unit.B']
SUMMARY = dict(total=2, passed=2, failed=0, skipped=0, cancelled=0, timedOut=0, flaky=0)


def report():
    return {'schemaVersion': 1, 'publicationGeneration': 'a' * 32,
            'assemblyName': 'SharpLink.UnitTests', 'tunitVersion': '1.73.19.0',
            'summary': dict(SUMMARY), 'groups': [{'summary': dict(SUMMARY), 'tests': [
                {'id': uid, 'displayName': uid, 'status': 'passed'} for uid in UIDS]}]}


class ValidationTests(unittest.TestCase):
    def test_pass(self):
        result = validate_run(UIDS, report(), 0)
        self.assertEqual(result['passed'], 2)
        self.assertEqual(result['uid_sha256'], uid_digest(UIDS))

    def test_discovery_schema_and_duplicates(self):
        self.assertEqual(discovery_uids({'schemaVersion': 1, 'tests': [{'uid': u} for u in reversed(UIDS)]}), UIDS)
        for value in ({}, {'schemaVersion': 2, 'tests': []}, {'schemaVersion': 1, 'tests': []},
                      {'schemaVersion': 1, 'tests': [{'uid': 'x'}, {'uid': 'x'}]},
                      {'schemaVersion': 1, 'tests': [{}]}):
            with self.subTest(value=value), self.assertRaises(GateError):
                discovery_uids(value)

    def test_every_nonpassing_status_rejected_even_if_summary_lies(self):
        for status in ('failed', 'skipped', 'cancelled', 'timedOut', 'timeout', 'inProgress', 'unknown'):
            value = report()
            value['groups'][0]['tests'][0]['status'] = status
            with self.subTest(status=status), self.assertRaises(GateError):
                validate_run(UIDS, value, 0)

    def test_no_uds_known_failure_exception(self):
        value = report()
        value['groups'][0]['tests'][0].update(status='failed', displayName='UdsConnectionShouldWork')
        with self.assertRaisesRegex(GateError, 'failed=1'):
            validate_run(UIDS, value, 2)

    def test_missing_extra_duplicate_and_wrong_identity(self):
        for mutation in ('missing', 'extra', 'duplicate', 'wrong'):
            value = report()
            tests = value['groups'][0]['tests']
            if mutation == 'missing': tests.pop()
            if mutation == 'extra': tests.append({'id': 'extra', 'status': 'passed'})
            if mutation == 'duplicate': tests.append(dict(tests[0]))
            if mutation == 'wrong': tests[0]['id'] = 'wrong'
            with self.subTest(mutation=mutation), self.assertRaises(GateError):
                validate_run(UIDS, value, 0)

    def test_missing_status(self):
        value = report()
        del value['groups'][0]['tests'][0]['status']
        with self.assertRaises(GateError): validate_run(UIDS, value, 0)

    def test_exit_codes_and_timeout(self):
        for code in (1, 2, 8, 9, -9):
            with self.subTest(code=code), self.assertRaises(GateError): validate_run(UIDS, report(), code)
        with self.assertRaises(GateError): validate_run(UIDS, report(), 0, True)

    def test_summary_counters_and_flaky_retries(self):
        for key in SUMMARY:
            value = report()
            value['summary'][key] += 1
            with self.subTest(key=key), self.assertRaises(GateError): validate_run(UIDS, value, 0)
        value = report()
        value['groups'][0]['summary']['total'] = 1
        with self.assertRaises(GateError): validate_run(UIDS, value, 0)

    def test_malformed_summary_types(self):
        for value in (None, {}, dict(SUMMARY, total=True), dict(SUMMARY, total=-1), dict(SUMMARY, extra=0)):
            with self.subTest(value=value), self.assertRaises(GateError): check_summary(value, 2, 'run')

    def test_schema_identity_generation(self):
        for key, bad in (('schemaVersion', 2), ('assemblyName', 'Other'), ('tunitVersion', '1.0'),
                         ('publicationGeneration', ''), ('groups', [])):
            value = report()
            value[key] = bad
            with self.subTest(key=key), self.assertRaises(GateError): validate_run(UIDS, value, 0)

    def test_json_truncation_duplicate_keys_nonfinite(self):
        for value in ('{', '{"x":1,"x":2}', '{"x":NaN}'):
            with self.subTest(value=value), self.assertRaises((GateError, ValueError)): strict_json(value)

    def test_missing_report(self):
        with tempfile.TemporaryDirectory() as temp:
            with self.assertRaises(OSError): read_report(Path(temp) / 'missing.json')

    def test_uid_digest_order_and_final_lf(self):
        import hashlib
        self.assertEqual(uid_digest(UIDS), hashlib.sha256(b'unit.A\nunit.B\n').hexdigest())
        self.assertEqual(uid_digest(list(reversed(UIDS))), uid_digest(UIDS))
        for values in ([], [''], ['x\ny'], ['x', 'x'], [None]):
            with self.assertRaises(GateError): unique_uids(values, 'test')

    def test_failure_summary_never_dumps_raw_trace(self):
        value = report()
        value['spans'] = 'RAW_PRIVATE_TRACE'
        value['groups'][0]['tests'][0].update(status='failed', stdout='RAW_PRIVATE_TRACE')
        with self.assertRaises(GateError) as caught: validate_run(UIDS, value, 2)
        self.assertNotIn('RAW_PRIVATE_TRACE', str(caught.exception))


class ProvenanceTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.request = dict(schema_version=1, source_sha='a' * 40, source_tree='b' * 40,
                            raw_discovered_count=3, raw_uid_sha256=uid_digest(UIDS + ['worker']),
                            ordinary_count=2, ordinary_uid_sha256=uid_digest(UIDS))
        path = self.root / provenance.REQUEST
        path.parent.mkdir(parents=True)
        path.write_text(json.dumps(self.request))
        path.with_name('expected-uids.json').write_text(json.dumps(UIDS + ['worker']))
        path.with_name('explicit-workers.json').write_text(json.dumps([dict(uid='worker',namespace='N',
            type_name='W',method_name='Run',source_path='test/W.cs',reason='isolated worker')]))
        self.answers = {('rev-parse', 'HEAD'): 'c' * 40,
                        ('status', '--porcelain', '--untracked-files=all'): '',
                        ('show', '-s', '--format=%P', 'HEAD'): 'a' * 40,
                        ('rev-parse', 'a' * 40 + '^{tree}'): 'b' * 40,
                        ('diff', '--name-status', '--no-renames', 'a' * 40, 'HEAD'): 'A\t' + provenance.REQUEST}

    def verify(self, ref=provenance.BRANCH):
        with patch('provenance.git', side_effect=lambda root, *args: self.answers[args]):
            return provenance.verify_release(self.root, 'c' * 40, ref)

    def test_valid_request_only_release(self): self.assertEqual(self.verify(), self.request)

    def test_wrong_branch(self):
        with self.assertRaises(GateError): self.verify('refs/heads/main')

    def test_head_dirty_parent_tree_and_delta_mismatch(self):
        for key, bad in ((('rev-parse', 'HEAD'), 'd' * 40),
                         (('status', '--porcelain', '--untracked-files=all'), ' M source.cs'),
                         (('show', '-s', '--format=%P', 'HEAD'), 'a' * 40 + ' ' + 'e' * 40),
                         (('rev-parse', 'a' * 40 + '^{tree}'), 'f' * 40),
                         (('diff', '--name-status', '--no-renames', 'a' * 40, 'HEAD'), 'M\tsource.cs')):
            original = self.answers[key]
            self.answers[key] = bad
            with self.subTest(key=key), self.assertRaises(GateError): self.verify()
            self.answers[key] = original

    def test_request_unknown_fields_count_hash_and_unsorted_corpus(self):
        for key, bad in (('raw_discovered_count', 4), ('raw_uid_sha256', '0' * 64),
                         ('ordinary_count', 3), ('ordinary_uid_sha256', '0' * 64), ('extra', 1)):
            value = dict(self.request)
            value[key] = bad
            (self.root / provenance.REQUEST).write_text(json.dumps(value))
            with self.subTest(key=key), self.assertRaises(GateError): self.verify()
        (self.root / provenance.REQUEST).write_text(json.dumps(self.request))
        (self.root / provenance.REQUEST).with_name('expected-uids.json').write_text(json.dumps(list(reversed(UIDS))))
        with self.assertRaises(GateError): self.verify()


class ExplicitScopeTests(unittest.TestCase):
    def test_workers_must_exist_and_never_count_as_passed(self):
        workers = [{'uid': 'worker'}]
        self.assertEqual(ordinary_uids(UIDS + ['worker'], workers), UIDS)
        with self.assertRaises(GateError): ordinary_uids(UIDS, workers)
        value = report()
        value['groups'][0]['tests'].append({'id': 'worker', 'status': 'passed'})
        with self.assertRaises(GateError): validate_run(UIDS, value, 0)
        value['groups'][0]['tests'][-1]['status'] = 'skipped'
        with self.assertRaises(GateError): validate_run(UIDS, value, 0)

    def test_missing_and_duplicate_worker_manifest(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory)
            worker = dict(uid='worker', namespace='N', type_name='W', method_name='Run',
                          source_path='test/SharpLink.UnitTests/W.cs', reason='isolated worker')
            for workers in ([], [worker, worker], [dict(worker, extra='not allowed')]):
                (path / 'explicit-workers.json').write_text(json.dumps(workers))
                with self.assertRaises(GateError): explicit_workers(path)

    def test_source_marker_and_identity_checked(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            path = root / 'test/SharpLink.UnitTests/W.cs'
            path.parent.mkdir(parents=True)
            path.write_text('[Explicit]\npublic sealed class W\n{}')
            worker = dict(uid='worker', namespace='N', type_name='W', method_name='Run',
                          source_path=path.relative_to(root).as_posix(), reason='isolated worker')
            test = dict(uid='worker', type=dict(namespace='N', typeName='W', methodName='Run'),
                        location=dict(file=str(path)))
            discovery = dict(schemaVersion=1, tests=[test])
            verify_explicit_sources(root, discovery, [worker])
            for field in ('namespace', 'typeName', 'methodName'):
                original = test['type'][field]
                test['type'][field] = 'WRONG'
                with self.assertRaises(GateError): verify_explicit_sources(root, discovery, [worker])
                test['type'][field] = original
            path.write_text('public sealed class W\n{}')
            with self.assertRaises(GateError): verify_explicit_sources(root, discovery, [worker])
            path.write_text('[Explicit]\npublic sealed class W\n{}')
            path.with_name('Unreviewed.cs').write_text('[Explicit]\npublic sealed class New\n{}')
            with self.assertRaises(GateError): verify_explicit_sources(root, discovery, [worker])


class SdkSelectionTests(unittest.TestCase):
    def test_exact_sdk_accepted_and_actual_logged(self):
        output = io.StringIO()
        with patch('run.subprocess.check_output', return_value='10.0.102\n') as invoke:
            with contextlib.redirect_stdout(output):
                self.assertEqual(require_pinned_sdk(Path('/candidate'), {'PATH': '/isolated'}), '10.0.102')
        invoke.assert_called_once_with(['dotnet', '--version'], cwd=Path('/candidate'),
                                       env={'PATH': '/isolated'}, text=True)
        self.assertIn('"actual": "10.0.102"', output.getvalue())

    def test_other_patch_and_banner_are_not_silently_accepted(self):
        for actual in ('10.0.114', '10.0.400', 'Welcome\n10.0.102'):
            output = io.StringIO()
            with patch('run.subprocess.check_output', return_value=actual):
                with contextlib.redirect_stdout(output), self.assertRaises(GateError) as caught:
                    require_pinned_sdk(Path('/candidate'), {})
            self.assertIn('expected=10.0.102', str(caught.exception))
            self.assertIn(json.dumps(actual), output.getvalue())

    def test_diagnostic_is_bounded_without_truncating_comparison(self):
        output = io.StringIO()
        with patch('run.subprocess.check_output', return_value='x' * 10000):
            with contextlib.redirect_stdout(output), self.assertRaises(GateError) as caught:
                require_pinned_sdk(Path('/candidate'), {})
        self.assertLess(len(output.getvalue()), 300)
        self.assertLess(len(str(caught.exception)), 250)
        self.assertIn('"truncated": true', output.getvalue())


class PinnedHostTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.sdk = self.root / 'sdk'
        self.runtime = self.root / 'private'
        for root in (self.sdk, self.runtime):
            root.mkdir()
            (root / 'dotnet').touch()
        self.env = {'SDK_DOTNET': str(self.sdk / 'dotnet'), 'PRIVATE_RUNTIME': str(self.runtime)}
        self.outputs = [
            '10.0.102\n', f'10.0.102 [{self.sdk / "sdk"}]\n',
            ''.join(f'{name} 10.0.2 [{self.sdk / "shared" / name}]\n'
                    for name in ('Microsoft.AspNetCore.App', 'Microsoft.NETCore.App')),
            '', ''.join(f'{name} 10.0.14 [{self.runtime / "shared" / name}]\n'
                        for name in ('Microsoft.AspNetCore.App', 'Microsoft.NETCore.App'))]

    def test_two_exact_separate_hosts(self):
        with patch('run.subprocess.check_output', side_effect=self.outputs) as invoke:
            with contextlib.redirect_stdout(io.StringIO()):
                self.assertEqual(verify_hosts(self.root, self.env),
                                 (str(self.sdk / 'dotnet'), str(self.runtime / 'dotnet')))
        self.assertEqual(invoke.call_args_list[0].args[0], [str(self.sdk / 'dotnet'), '--version'])
        self.assertEqual(invoke.call_args_list[-1].args[0], [str(self.runtime / 'dotnet'), '--list-runtimes'])

    def test_sdk_drift_extra_runtime_and_private_sdk_fail(self):
        for index, bad in ((0, '10.0.114'), (1, self.outputs[1] + '10.0.114 [/other]\n'),
                           (2, self.outputs[2].replace('10.0.2', '10.0.14')),
                           (3, '10.0.102 [/private/sdk]'),
                           (4, self.outputs[4].replace('10.0.14', '10.0.2')),
                           (4, self.outputs[4] + 'Microsoft.NETCore.App 10.0.15 [/extra]\n')):
            outputs = list(self.outputs)
            outputs[index] = bad
            with self.subTest(index=index, bad=bad), patch('run.subprocess.check_output', side_effect=outputs):
                with contextlib.redirect_stdout(io.StringIO()), self.assertRaises(GateError):
                    verify_hosts(self.root, self.env)

    def test_missing_relative_and_mixed_host_fail(self):
        for env in ({}, dict(self.env, SDK_DOTNET='dotnet'),
                    dict(self.env, PRIVATE_RUNTIME=str(self.sdk))):
            with self.assertRaises(GateError): verify_hosts(self.root, env)

    def test_children_inherit_private_root_and_host(self):
        env = dict(self.env, PATH='/build/bin', GITHUB_STEP_SUMMARY='/must-not-write')
        result = private_test_environment(env, str(self.runtime / 'dotnet'))
        self.assertEqual(result['DOTNET_ROOT'], str(self.runtime))
        self.assertEqual(result['DOTNET_ROOT_X64'], str(self.runtime))
        self.assertEqual(result['DOTNET_HOST_PATH'], str(self.runtime / 'dotnet'))
        self.assertTrue(result['PATH'].startswith(str(self.runtime) + ':'))
        self.assertNotIn('GITHUB_STEP_SUMMARY', result)

    def test_actual_test_runtime_must_be_14(self):
        with contextlib.redirect_stdout(io.StringIO()):
            self.assertEqual(require_test_runtime({'runtimeVersion': '.NET 10.0.14'}), '.NET 10.0.14')
        for value in (None, '.NET 10.0.2', '.NET 10.0.15', '10.0.14'):
            with contextlib.redirect_stdout(io.StringIO()), self.assertRaises(GateError):
                require_test_runtime({'runtimeVersion': value})


class AllocationChildRuntimeTests(unittest.TestCase):
    def records(self):
        tests = []
        for scenario in ('SuccessfulSuspensionsShouldNotAllocateAfterWarmup',
                         'RegisteredConsumerSuspensionsShouldNotAllocateAfterWarmup'):
            record = dict(scenario=scenario, runtime='10.0.14', architecture='X64', executed=1, passed=True,
                          privateTrace='must-not-appear')
            uid = ('SharpLink.UnitTests.Runtime.ReadOwnershipPipeReaderLifetimeTests.1.1.' + scenario
                   + '(System.Threading.CancellationToken).1.1.0')
            tests.append(dict(id=uid, status='passed', output='READ_OWNERSHIP_ALLOCATION_VERIFIED|'
                              + json.dumps(record) + '\n'))
        return dict(groups=[dict(tests=tests)])

    def test_only_bounded_witness_fields_returned(self):
        result = verify_allocation_child_runtimes(self.records())
        self.assertEqual(len(result), 2)
        self.assertEqual(set(result[0]), {'scenario', 'runtime', 'architecture', 'executed'})
        self.assertNotIn('must-not-appear', json.dumps(result))

    def test_missing_duplicate_wrong_runtime_and_identity_fail(self):
        for mutation in ('missing', 'duplicate', 'runtime', 'scenario', 'uid', 'status', 'executed'):
            value = self.records()
            tests = value['groups'][0]['tests']
            if mutation == 'missing': tests.pop()
            if mutation == 'duplicate': tests[0]['output'] *= 2
            if mutation == 'runtime': tests[0]['output'] = tests[0]['output'].replace('10.0.14', '10.0.2')
            if mutation == 'scenario': tests[0]['output'] = tests[0]['output'].replace('SuccessfulSuspensions', 'Other')
            if mutation == 'uid': tests[0]['id'] = 'unreviewed'
            if mutation == 'status': tests[0]['status'] = 'failed'
            if mutation == 'executed': tests[0]['output'] = tests[0]['output'].replace('"executed": 1', '"executed": 2')
            with self.subTest(mutation=mutation), self.assertRaises(GateError):
                verify_allocation_child_runtimes(value)


if __name__ == '__main__':
    unittest.main(verbosity=2)
