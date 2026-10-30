"""Offline request/workflow negative controls; never launches a workflow or fixture."""
import copy
import json
from pathlib import Path
import unittest
from verify_request import (BRANCH, CAMPAIGN, DEV, DEV_TREE, DORMANT, MESSAGE,
                            REPOSITORY, REQUEST, strict_json, validate_request)


class RequestTests(unittest.TestCase):
    def setUp(self):
        self.head, self.parent, self.tree = 'a' * 40, 'b' * 40, 'c' * 40
        self.data = dict(state='authorized', campaign=CAMPAIGN,
                         reviewedToolCommit=self.parent, reviewedToolTree=self.tree,
                         reviewStatus='PASS', rootRelease='authorized', devSource=DEV, devTree=DEV_TREE)
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
        actual = copy.deepcopy(DORMANT)
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
                         changed=[REQUEST, '.github/workflows/codeql-sarif-evidence.yml'],
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

    def test_workflow_is_narrow(self):
        text=(Path(__file__).resolve().parents[2]/'.github/workflows/codeql-sarif-evidence.yml').read_text()
        for required in [BRANCH.removeprefix('refs/heads/'), REQUEST, MESSAGE, 'github.run_attempt == 1', 'contents: read', 'retention-days: 3', 'verify_request.py', 'upload-database:', 'never']:
            self.assertIn(required,text)
        for forbidden in ['contents: write','continue-on-error','workflow_dispatch','pull_request:', 'overwrite: true']:
            self.assertNotIn(forbidden,text)
        self.assertEqual(text.count('if: always()'),8)
        self.assertIn('build-mode: manual',text)
        self.assertIn('dotnet-version: 10.0.114',text)
        self.assertIn('CODEQL_EXTRACTOR_CSHARP_OPTION_LOGGING_VERBOSITY: info',text)
        self.assertIn('process_host.py" restore',text)
        self.assertIn('process_host.py" build',text)
        self.assertIn('steps.lifecycle.outputs.safe == \'true\'',text)
        self.assertIn("!cancelled()",text)
        self.assertIn("steps.lifecycle.outcome == 'success'",text)
        self.assertIn("--build-outcome '${{ steps.build.outcome }}'",text)
        self.assertNotIn('dotnet test',text)
        self.assertNotIn('NuGetAudit=false',text)
        self.assertIn('timeout-minutes: 60',text)
        self.assertIn('source-root: .',text)
        self.assertIn('checkout_path: ${{ github.workspace }}\n',text)
        self.assertNotIn('path: tools',text)
        self.assertNotIn('path: source',text)
        self.assertLess(text.index('Preserve only reviewed hash-bound tools'),text.index('Clean checkout exact product'))
        self.assertIn('cql6-tools/eng/codeql-sarif-evidence/evidence.py',text)
        self.assertLess(text.index('Build bounded BCL-only NuGet reader'),text.index('Initialize default C# queries'))
        self.assertLess(text.index('Analyze once without'),text.index('Observe structured compilation'))
        self.assertLess(text.index('Retain exact package bytes'),text.index('Observe structured compilation'))
        self.assertLess(text.index('Check the separate forensic QL'),text.index('Restore the complete solution'))
        self.assertIn("steps.compilation_preflight.outcome == 'success'",text)
        self.assertIn("steps.package_evidence.outputs.safe == 'true'",text)
        self.assertIn("steps.package_evidence.outcome == 'success'",text)
        self.assertIn("steps.helper_build.outcome == 'success'",text)
        preflight=text.split('      - name: Check the separate forensic QL',1)[1].split('      - name:',1)[0]
        restore=text.split('      - name: Restore the complete solution',1)[1].split('      - name:',1)[0]
        build=text.split('      - name: Build the complete Release solution',1)[1].split('      - name:',1)[0]
        for gated_by_default_success in (preflight,restore,build):
            self.assertNotIn('if:',gated_by_default_success)
        package=text.split('      - name: Retain exact package bytes',1)[1].split('      - name:',1)[0]
        forensic=text.split('      - name: Observe structured compilation',1)[1].split('      - name:',1)[0]
        for step in (package,forensic):
            self.assertIn("!cancelled()",step)
            self.assertIn("steps.lifecycle.outcome == 'success'",step)
            self.assertIn("steps.lifecycle.outputs.safe == 'true'",step)
        for guard in ("steps.package_evidence.outcome == 'success'", "steps.package_evidence.outputs.safe == 'true'", "steps.compilation_preflight.outcome == 'success'"):
            self.assertIn(guard,forensic)
        self.assertNotIn('steps.compilation_evidence',package)
        self.assertIn('compilation-preflight.json',text)
        self.assertIn('process-compilation-compile.json',text)
        self.assertIn('package-evidence/tunit.core.1.73.19.nupkg',text)
        self.assertNotIn('**',text)

if __name__ == '__main__':
    unittest.main()
