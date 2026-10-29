#!/usr/bin/env python3
"""Synthetic negative controls; no product tests, network, or report uploads."""
from __future__ import annotations

import copy
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

import provenance
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


if __name__ == '__main__':
    unittest.main(verbosity=2)
