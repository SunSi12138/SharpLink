"""Offline request/workflow negative controls; never launches a workflow or fixture."""
import copy
import json
from pathlib import Path
import unittest
from verify_request import (BRANCH, CAMPAIGN, BASELINE, BASELINE_TREE, DORMANT, MESSAGE,
                            REPOSITORY, REQUEST, strict_json, validate_request)


class RequestTests(unittest.TestCase):
    def setUp(self):
        self.head, self.parent, self.tree = 'a' * 40, 'b' * 40, 'c' * 40
        self.data = dict(state='authorized', campaign=CAMPAIGN,
                         reviewedToolCommit=self.parent, reviewedToolTree=self.tree,
                         reviewStatus='PASS', rootRelease='authorized', baselineSource=BASELINE,
                         baselineTree=BASELINE_TREE)
        self.environment = dict(GITHUB_EVENT_NAME='push', GITHUB_REF=BRANCH,
                                GITHUB_RUN_ATTEMPT='1', GITHUB_REPOSITORY=REPOSITORY,
                                GITHUB_SHA=self.head)
        self.event = dict(before=self.parent, after=self.head, ref=BRANCH,
                          deleted=False, forced=False, commits=[{'id': self.head}],
                          head_commit={'id': self.head, 'message': MESSAGE},
                          repository={'full_name': REPOSITORY})
        self.arguments = dict(data=self.data, environment=self.environment, head=self.head,
                              parent=self.parent, parent_tree=self.tree, changed=[REQUEST],
                              message=MESSAGE, clean=True, parent_request=DORMANT, event=self.event)

    def test_only_exact_authorized_request_passes(self):
        validate_request(**self.arguments)

    def test_dormant_file_never_authorizes(self):
        actual = strict_json((Path(__file__).parent / 'REQUEST.json').read_text())
        self.assertEqual(actual, DORMANT)
        self.arguments['data'] = actual
        with self.assertRaises(ValueError):
            validate_request(**self.arguments)

    def test_all_request_fields_fail_closed(self):
        for key in self.data:
            with self.subTest(key=key):
                arguments = copy.deepcopy(self.arguments)
                arguments['data'][key] = 'wrong'
                with self.assertRaises(ValueError):
                    validate_request(**arguments)
        for missing in self.data:
            arguments = copy.deepcopy(self.arguments)
            del arguments['data'][missing]
            with self.assertRaises(ValueError):
                validate_request(**arguments)
        self.data['extra'] = 'not-allowed'
        with self.assertRaises(ValueError):
            validate_request(**self.arguments)

    def test_environment_mutations_rejected(self):
        for key in self.environment:
            for value in ['wrong', '', '2']:
                with self.subTest(key=key, value=value):
                    arguments = copy.deepcopy(self.arguments)
                    arguments['environment'][key] = value
                    with self.assertRaises(ValueError):
                        validate_request(**arguments)

    def test_checkout_identity_and_scope_rejected(self):
        mutations = dict(head='abcd', parent='B' * 40, parent_tree='abcd', clean=False,
                         changed=[REQUEST, '.github/workflows/baseline-diagnostics.yml'],
                         message=MESSAGE + '\nextra body', parent_request=self.data)
        for key, value in mutations.items():
            with self.subTest(key=key):
                arguments = copy.deepcopy(self.arguments)
                arguments[key] = value
                with self.assertRaises(ValueError):
                    validate_request(**arguments)
        for changed in [[], ['wrong'], [REQUEST, REQUEST]]:
            arguments = copy.deepcopy(self.arguments)
            arguments['changed'] = changed
            with self.assertRaises(ValueError):
                validate_request(**arguments)

    def test_push_must_be_one_nonforced_child_of_reviewed_parent(self):
        mutations = dict(before='0' * 40, after='d' * 40, ref='refs/heads/main',
                         deleted=True, forced=True, commits=[],
                         head_commit={'id': self.head, 'message': 'wrong'},
                         repository={'full_name': 'other/repo'})
        for key, value in mutations.items():
            with self.subTest(key=key):
                arguments = copy.deepcopy(self.arguments)
                arguments['event'][key] = value
                with self.assertRaises(ValueError):
                    validate_request(**arguments)
        for commits in [[{'id': self.head}, {'id': 'd' * 40}], [{'id': 'd' * 40}]]:
            arguments = copy.deepcopy(self.arguments)
            arguments['event']['commits'] = commits
            with self.assertRaises(ValueError):
                validate_request(**arguments)

    def test_missing_push_fields_rejected(self):
        for key in self.event:
            arguments = copy.deepcopy(self.arguments)
            del arguments['event'][key]
            with self.assertRaises(ValueError):
                validate_request(**arguments)

    def test_duplicate_and_nonfinite_json_rejected(self):
        for text in ['{"state":"review-required","state":"authorized"}',
                     '{"x":NaN}', '{"x":Infinity}', '{"x":-Infinity}']:
            with self.assertRaises(ValueError):
                strict_json(text)

    def test_job_environment_avoids_unavailable_runner_context(self):
        workflow = (Path(__file__).resolve().parents[2] / '.github/workflows/baseline-diagnostics.yml').read_text()
        job_env = workflow.split('    env:', 1)[1].split('    steps:', 1)[0]
        self.assertNotIn('runner.temp', job_env)
        self.assertIn('export DIAGNOSTIC_ROOT="$RUNNER_TEMP/baselinediag-$GITHUB_RUN_ID-$GITHUB_RUN_ATTEMPT"', workflow)
        self.assertIn('>> "$GITHUB_ENV"', workflow)

    def test_workflow_is_narrow_and_no_uploads(self):
        text = (Path(__file__).resolve().parents[2] / '.github/workflows/baseline-diagnostics.yml').read_text()
        self.assertIn('validation/baseline-eventpipe-no-evidence-20261029', text)
        self.assertIn('eng/baseline-diagnostics/REQUEST.json', text)
        self.assertIn('github.run_attempt == 1', text)
        for value in ('workflow_dispatch', 'upload-artifact@', 'collect-linux', 'sudo ', 'pull_request:'):
            self.assertNotIn(value, text)

if __name__ == '__main__': unittest.main()
