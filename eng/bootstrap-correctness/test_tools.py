#!/usr/bin/env python3
"""Offline fail-closed guard/census regression tests: no product execution."""
import copy
import hashlib
import unittest
from unittest.mock import patch

import census
import verify_request as guard


class RequestTests(unittest.TestCase):
    def setUp(self):
        self.env = dict(GITHUB_EVENT_NAME='push', GITHUB_REF=guard.BRANCH, GITHUB_RUN_ATTEMPT='1',
                        GITHUB_REPOSITORY='SunSi12138/SharpLink', GITHUB_SHA='a' * 40)
        self.data = dict(state='authorized', campaign='bootstrap-supervision-full-unit-v1',
                         reviewedToolCommit='b' * 40, reviewedToolTree='c' * 40,
                         candidate=guard.CANDIDATE, candidateTree=guard.CANDIDATE_TREE)
        self.kwargs = dict(head='a' * 40, parent='b' * 40, parent_tree='c' * 40,
                           changed=[guard.REQUEST], message=guard.MESSAGE, clean=True)

    def validate(self):
        guard.validate_request(self.data, self.env, **self.kwargs)

    def test_fixed_request_accepted(self):
        self.validate()

    def test_every_request_field_pinned(self):
        for key in self.data:
            with self.subTest(key=key), patch.dict(self.data, {key: 'wrong'}), self.assertRaises(ValueError):
                self.validate()
        with patch.dict(self.data, extra=True), self.assertRaises(ValueError):
            self.validate()

    def test_all_execution_context_is_bounded(self):
        for key in self.env:
            with self.subTest(key=key), patch.dict(self.env, {key: 'wrong'}), self.assertRaises(ValueError):
                self.validate()

    def test_rerun_rejected(self):
        with patch.dict(self.env, GITHUB_RUN_ATTEMPT='2'), self.assertRaises(ValueError):
            self.validate()

    def test_dirty_unreviewed_changed_tool_or_message_rejected(self):
        for key, value in [('head', 'd' * 40), ('parent', 'd' * 40), ('parent_tree', 'd' * 40),
                           ('changed', [guard.REQUEST, 'run.py']), ('changed', []),
                           ('message', 'almost ' + guard.MESSAGE), ('clean', False)]:
            with self.subTest(key=key, value=value), patch.dict(self.kwargs, {key: value}), self.assertRaises(ValueError):
                self.validate()


class CensusTests(unittest.TestCase):
    def setUp(self):
        self.ids = {'one', 'two', 'three'}
        digest = hashlib.sha256(('\n'.join(sorted(self.ids)) + '\n').encode()).hexdigest()
        self.constants = patch.multiple(census, COUNT=3, EXECUTED_UID_SHA256=digest)
        self.constants.start()
        self.addCleanup(self.constants.stop)
        self.discovery = dict(schemaVersion=1, tests=[dict(uid=x) for x in sorted(self.ids | census.EXPLICIT_UIDS)])
        self.report = dict(schemaVersion=1, assemblyName='SharpLink.UnitTests',
                           summary=dict(total=3, passed=3, failed=0, skipped=0, cancelled=0, timedOut=0, flaky=0),
                           groups=[dict(tests=[dict(id=x, status='passed') for x in sorted(self.ids)])])
        self.log = 'total: 3\nfailed: 0\nsucceeded: 3\nskipped: 0\n'
        self.trx = ('<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">'
                    '<ResultSummary><Counters total="3" executed="3" passed="3" failed="0" notExecuted="0" /></ResultSummary>'
                    '<Results>' + ''.join('<UnitTestResult testId="' + x + '" outcome="Passed" />' for x in sorted(self.ids)) +
                    '</Results></TestRun>')

    def validate(self):
        return census.inspect_census(self.discovery, self.report, self.log, self.trx)

    def test_all_pass_exact_set(self):
        self.assertEqual(self.validate()['executed'], 3)

    def test_discovery_missing_extra_duplicate_and_same_count_substitution_rejected(self):
        original = copy.deepcopy(self.discovery)
        for tests in (original['tests'][:-1], original['tests'] + [dict(uid='extra')],
                      original['tests'][:-1] + [original['tests'][0]],
                      [dict(uid='substituted') if t['uid'] == 'one' else t for t in original['tests']]):
            with self.subTest(tests=tests), patch.dict(self.discovery, tests=tests), self.assertRaises(ValueError):
                self.validate()

    def test_exact_explicit_allowlist_required(self):
        explicit = next(iter(census.EXPLICIT_UIDS))
        self.discovery['tests'] = [dict(uid='new-explicit') if t['uid'] == explicit else t for t in self.discovery['tests']]
        with self.assertRaises(ValueError):
            self.validate()

    def test_execution_missing_duplicate_unknown_rejected(self):
        original = copy.deepcopy(self.report['groups'][0]['tests'])
        for tests in (original[:-1], original[:-1] + [original[0]],
                      original[:-1] + [dict(id='unexpected', status='passed')]):
            with self.subTest(tests=tests), patch.dict(self.report['groups'][0], tests=tests), self.assertRaises(ValueError):
                self.validate()

    def test_all_nonpassing_outcomes_rejected(self):
        for status in ('failed', 'skipped', 'cancelled', 'timedOut', 'flaky', 'unknown'):
            with self.subTest(status=status), patch.dict(self.report['groups'][0]['tests'][0], status=status), self.assertRaises(ValueError):
                self.validate()

    def test_every_summary_counter_checked(self):
        for key in self.report['summary']:
            with self.subTest(key=key), patch.dict(self.report['summary'], {key: 100}), self.assertRaises(ValueError):
                self.validate()

    def test_console_missing_duplicate_and_wrong_counters_rejected(self):
        for log in ('', self.log + self.log, self.log.replace('skipped: 0', 'skipped: 1'), self.log.replace('total: 3', 'total: 4')):
            with self.subTest(log=log), self.assertRaises(ValueError):
                census.inspect_census(self.discovery, self.report, log, self.trx)

    def test_trx_counts_outcomes_and_duplicate_ids_rejected(self):
        for trx in (self.trx.replace('executed="3"', 'executed="2"'), self.trx.replace('failed="0"', 'failed="1"'),
                    self.trx.replace('outcome="Passed"', 'outcome="Failed"', 1),
                    self.trx.replace('testId="two"', 'testId="one"'), self.trx.replace('<UnitTestResult testId="one" outcome="Passed" />', '')):
            with self.subTest(trx=trx), self.assertRaises(ValueError):
                census.inspect_census(self.discovery, self.report, self.log, trx)

    def test_zero_warning_build(self):
        census.inspect_build('Build succeeded.\n    0 Warning(s)\n    0 Error(s)\n')
        for text in ('', '0 Warning(s)\n1 Error(s)\n', '1 Warning(s)\n0 Error(s)\n',
                     'warning NU1234: ignored\n0 Warning(s)\n0 Error(s)\n', '0 Warning(s)\n0 Warning(s)\n0 Error(s)\n'):
            with self.subTest(text=text), self.assertRaises(ValueError):
                census.inspect_build(text)


if __name__ == '__main__':
    unittest.main(verbosity=2)
