"""Offline request/workflow negative controls; never launches a workflow or fixture."""
import copy
import json
from pathlib import Path
import unittest
from verify_request import (BRANCH, CAMPAIGN, DEV, DEV_TREE, DORMANT, MESSAGE,
                            OFFICIAL, REPOSITORY, REQUEST, strict_json, validate_request)


class RequestTests(unittest.TestCase):
    def setUp(self):
        self.head, self.parent, self.tree = 'a' * 40, 'b' * 40, 'c' * 40
        self.data = dict(state='authorized', campaign=CAMPAIGN,
                         reviewedToolCommit=self.parent, reviewedToolTree=self.tree,
                         reviewStatus='PASS', rootRelease='authorized', officialSource=OFFICIAL,
                         officialVersion='2.0.3', devSource=DEV, devTree=DEV_TREE)
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
                         changed=[REQUEST, '.github/workflows/official203-cleanup-diagnostic.yml'],
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

    def test_malformed_nested_push_objects_rejected(self):
        for field, value in [('commits', [None]), ('commits', ['bad']),
                             ('head_commit', None), ('head_commit', []),
                             ('repository', None), ('repository', 'bad')]:
            with self.subTest(field=field, value=value):
                arguments = copy.deepcopy(self.arguments)
                arguments['event'][field] = value
                with self.assertRaises(ValueError):
                    validate_request(**arguments)

    def test_bool_push_flags_require_exact_false(self):
        for field in ['deleted', 'forced']:
            for value in [None, 0, 'false']:
                arguments = copy.deepcopy(self.arguments)
                arguments['event'][field] = value
                with self.assertRaises(ValueError):
                    validate_request(**arguments)

    def test_workflow_has_no_timed_or_privileged_capture_path(self):
        workflow = (Path(__file__).resolve().parents[2] / '.github/workflows/official203-cleanup-diagnostic.yml').read_text()
        for forbidden in ['sudo', 'setcap', 'tcpdump', 'rawsocket', 'apt-get',
                          'eng/official-version-comparison/run.py', '--measure', '--warmup',
                          'workflow_dispatch', '48 timed']:
            self.assertNotIn(forbidden, workflow)
        self.assertEqual(workflow.count('python3 eng/official-cleanup-diagnostic/run.py'), 1)
        self.assertIn('--fixture-purpose cleanup-diagnostic', workflow)
        self.assertIn('test "$REVIEWED_PIN_OUTCOME" = success', workflow)
        self.assertIn('cp -R eng/official-cleanup-diagnostic "$DIAGNOSTIC_ROOT/reviewed-source/"', workflow)
        for folder in ['official203-cleanup-sdk', 'official203-cleanup-runtime', 'official203-cleanup-diagnostic']:
            self.assertIn('$RUNNER_TEMP/' + folder, workflow)
        self.assertIn('634326028645f38731c8c4e71a68356e4ca0d6b6607139a234ab760687f742063edaf33a1f20dda615742d2dc8300ca2ff857619d51fbb38090383b71334af89', workflow)
        self.assertIn('dd024fd2616926a555b75cbf7a836f6dce998199f0596d9d75676dca2b9d59c8ee8673c6ac23473c0f1a4bdd59eb5cd02fe9ab097eeaf2dd8c8bd117c85e7031', workflow)

    def test_campaign_protocol_is_only_six_untimed_original_cases(self):
        protocol = strict_json((Path(__file__).parent / 'protocol.json').read_text())
        self.assertEqual(protocol['campaign'], CAMPAIGN)
        self.assertEqual(protocol['officialSource'], OFFICIAL)
        self.assertEqual(protocol['devSource'], DEV)
        self.assertEqual(protocol['conditions'], ['static-none', 'static-cancellable', 'dynamic-none'])
        self.assertEqual(protocol['arms'], ['official', 'dev'])
        self.assertEqual(protocol['processes'], 6)
        self.assertEqual(protocol['processTimeoutSeconds'], 120)
        self.assertEqual(protocol['cleanupGuardSeconds'], 15)
        self.assertEqual(protocol['warmupSeconds'], 0)
        self.assertEqual(protocol['measureSeconds'], 0)
        self.assertEqual(protocol['priorRun'], 40747822819)
        self.assertEqual(protocol['priorAttempt'], 1)

    def test_job_environment_avoids_unavailable_runner_context(self):
        workflow = (Path(__file__).resolve().parents[2] / '.github/workflows/official203-cleanup-diagnostic.yml').read_text()
        job_env = workflow.split('    env:', 1)[1].split('    steps:', 1)[0]
        self.assertNotIn('runner.temp', job_env)
        self.assertIn('export DIAGNOSTIC_ROOT="$RUNNER_TEMP/official203-cleanup-diagnostic"', workflow)
        self.assertIn('>> "$GITHUB_ENV"', workflow)

    def test_workflow_is_narrow_and_contains_required_gates(self):
        path = Path(__file__).resolve().parents[2] / '.github/workflows/official203-cleanup-diagnostic.yml'
        text = path.read_text()
        self.assertIn('branches: [codex/official203-cleanup-diagnostic-validation]', text)
        self.assertIn("paths: ['eng/official-cleanup-diagnostic/REQUEST.json']", text)
        self.assertNotIn('workflow_dispatch', text)
        self.assertNotIn('pull_request:', text)
        self.assertIn("github.run_attempt == 1", text)
        self.assertIn(MESSAGE, text)
        self.assertIn('runs-on: ubuntu-24.04', text)
        self.assertIn('dotnet-version: 10.0.102', text)
        self.assertIn('git checkout --detach "$REVIEWED_TOOL_SHA"', text)
        self.assertIn('--reviewed-tool-sha "$REVIEWED_TOOL_SHA"', text)
        self.assertIn('--quiet-window-record "$DIAGNOSTIC_ROOT/quiet-window.json"', text)
        self.assertIn('contents: read', text)
        self.assertNotIn('contents: write', text)
        self.assertEqual(text.count('if: always()'), 2)
        for needle in ['verify_request.py', 'warm_cache.py', 'fetch_published.py', 'prepare.py',
                       'build-server shutdown', 'run.py', 'analyze.py', 'actions/upload-artifact@']:
            self.assertIn(needle, text)
        self.assertLess(text.index('verify_request.py'), text.index('git checkout --detach'))
        self.assertLess(text.index('prepare.py'), text.index('build-server shutdown'))
        self.assertLess(text.index('build-server shutdown'), text.index('diagnostic/run.py'))
        self.assertLess(text.index('diagnostic/run.py'), text.index('diagnostic/analyze.py'))


if __name__ == '__main__':
    unittest.main()
