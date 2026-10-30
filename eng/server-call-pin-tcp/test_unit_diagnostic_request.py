"""Offline-only request and no-TCP workflow controls."""
import ast
import copy
import json
from pathlib import Path
import unittest
import verify_request as original
import verify_unit_diagnostic_request as diagnostic

ROOT = Path(__file__).resolve().parents[2]
WORKFLOW = ROOT / '.github/workflows/server-call-pin-unit-postgate-diagnostic.yml'


class UnitDiagnosticRequestTests(unittest.TestCase):
    def arguments(self):
        head, parent, tree = 'a' * 40, 'b' * 40, 'c' * 40
        return dict(data=dict(state='authorized', campaign=diagnostic.CAMPAIGN,
            reviewedToolCommit=parent, reviewedToolTree=tree, reviewStatus='PASS', rootRelease='authorized',
            baselineSource=diagnostic.BASELINE, baselineTree=diagnostic.BASELINE_TREE,
            candidateSource=diagnostic.CANDIDATE, candidateTree=diagnostic.CANDIDATE_TREE),
            environment=dict(GITHUB_EVENT_NAME='push', GITHUB_REF=diagnostic.BRANCH, GITHUB_RUN_ATTEMPT='1',
                GITHUB_REPOSITORY=diagnostic.REPOSITORY, GITHUB_SHA=head), head=head, parent=parent,
            parent_tree=tree, changed=[diagnostic.REQUEST], message=diagnostic.MESSAGE, clean=True,
            parent_request=diagnostic.DORMANT, event=dict(before=parent, after=head, ref=diagnostic.BRANCH,
                deleted=False, forced=False, commits=[{'id': head}],
                head_commit={'id': head, 'message': diagnostic.MESSAGE},
                repository={'full_name': diagnostic.REPOSITORY}))

    def test_dedicated_request_passes_but_old_request_never_does(self):
        diagnostic.validate_request(**self.arguments())
        self.assertNotEqual(diagnostic.REQUEST, original.REQUEST)
        self.assertNotEqual(diagnostic.BRANCH, original.BRANCH)
        self.assertNotEqual(diagnostic.MESSAGE, original.MESSAGE)
        self.assertNotEqual(diagnostic.CAMPAIGN, original.CAMPAIGN)
        for key in ('campaign', 'state', 'reviewStatus', 'rootRelease', 'reviewedToolCommit', 'reviewedToolTree',
                    'baselineSource', 'baselineTree', 'candidateSource', 'candidateTree'):
            args = self.arguments(); args['data'][key] = 'wrong'
            with self.subTest(key=key), self.assertRaises(ValueError): diagnostic.validate_request(**args)
        with self.assertRaises(ValueError): original.validate_request(**self.arguments())

    def test_authorization_constraints_are_identical_functions(self):
        # Only fixed request/campaign/branch/message constants differ. Every function
        # body, including CLI sole-parent/unique-A/clean/tree/event checks is identical.
        def functions(module):
            return [ast.dump(node, include_attributes=False) for node in ast.parse(Path(module.__file__).read_text()).body
                    if isinstance(node, (ast.FunctionDef, ast.ClassDef))]
        self.assertEqual(functions(original), functions(diagnostic))

    def test_new_template_dormant_and_real_requests_absent(self):
        self.assertEqual(json.loads((ROOT / diagnostic.TEMPLATE).read_text()), diagnostic.DORMANT)
        self.assertFalse((ROOT / diagnostic.REQUEST).exists()); self.assertFalse((ROOT / original.REQUEST).exists())
        diagnostic.validate_request_addition('', 'A\t' + diagnostic.REQUEST)
        for parent, change in [('exists', 'A\t' + diagnostic.REQUEST), ('', 'M\t' + diagnostic.REQUEST),
                               ('', 'A\t' + original.REQUEST), ('', 'A\t' + diagnostic.REQUEST + '\nM\tanything')]:
            with self.assertRaises(ValueError): diagnostic.validate_request_addition(parent, change)

    def test_no_force_retry_wrong_head_or_extra_file(self):
        for field, value in [('forced', True), ('before', 'd' * 40), ('commits', [])]:
            args = self.arguments(); args['event'][field] = value
            with self.assertRaises(ValueError): diagnostic.validate_request(**args)
        args = self.arguments(); args['environment']['GITHUB_RUN_ATTEMPT'] = '2'
        with self.assertRaises(ValueError): diagnostic.validate_request(**args)
        args = self.arguments(); args['changed'].append(original.REQUEST)
        with self.assertRaises(ValueError): diagnostic.validate_request(**args)

    def test_workflow_is_unit_only_and_separate(self):
        text = WORKFLOW.read_text()
        self.assertIn('branches: [' + diagnostic.BRANCH.removeprefix('refs/heads/') + ']', text)
        self.assertIn("paths: ['" + diagnostic.REQUEST + "']", text)
        self.assertIn(diagnostic.MESSAGE, text)
        self.assertIn('verify_unit_diagnostic_request.py', text)
        self.assertIn('unit_gate/run.py', text)
        self.assertIn('prepare.py', text)
        self.assertIn('steps.unit.outcome', text)
        self.assertIn('POSTGATE_DIAG_NOT_REPRODUCED', text)
        self.assertIn('originalFailureUnchanged=true TCP=0 acceptedMetrics=0', text)
        self.assertNotIn('server-call-pin-tcp/run.py', text)
        self.assertNotIn('server-call-pin-tcp/validate.py', text)
        for value in ('workflow_dispatch:', 'workflow_run:', 'repository_dispatch:', 'actions: write',
            'contents: write', 'actions/upload-artifact@', 'actions/cache@', 'continue-on-error:',
            'normal-measure', 'strict-cleanup', '--unit-gate-record', '--quiet-window-record'):
            self.assertNotIn(value, text)
        self.assertIn('contents: read', text)
        self.assertIn('github.run_attempt == 1', text)

    def test_preparation_and_execution_environment_preserved(self):
        text = WORKFLOW.read_text()
        previous = (ROOT / '.github/workflows/server-call-pin-tcp.yml').read_text()
        for start, end in [('      - name: Install exact separate SDK102', '      - name: Exercise offline'),
                           ('      - name: Build each arm once', '      - name: Complete both')]:
            expected = previous[previous.index(start):previous.index(end)]
            self.assertIn(expected, text)
        original_command = previous[previous.index('          python3 tools/eng/server-call-pin-tcp/unit_gate/run.py'):]
        original_command = original_command.split('      - name:', 1)[0]
        self.assertIn(original_command, text)


if __name__ == '__main__': unittest.main()
