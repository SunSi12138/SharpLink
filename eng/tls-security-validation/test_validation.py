#!/usr/bin/env python3
"""Synthetic validation-only controls: no product tests, networking or IPC."""
import copy
import importlib.util
import json
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import MagicMock, patch

spec = importlib.util.spec_from_file_location('tls_validation', Path(__file__).with_name('run.py'))
gate = importlib.util.module_from_spec(spec)
spec.loader.exec_module(gate)
NAME = gate.METHODS[6]


def result():
    counts = dict(total=1, passed=1, failed=0, skipped=0, cancelled=0, timedOut=0, flaky=0)
    return dict(schemaVersion=1, publicationGeneration='a' * 32, assemblyName='SharpLink.IntegrationTests',
                tunitVersion='1.73.19.0', runtimeVersion='.NET 10.0.14', summary=counts,
                groups=[dict(summary=dict(counts), tests=[dict(id=gate.UIDS[NAME], status='passed')])])


def discovery():
    return dict(schemaVersion=1, tests=[dict(uid=uid, type=dict(namespace='SharpLink.IntegrationTests',
        typeName='TlsTransportIntegrationTests', methodName=name), location=dict(file='/source/' + gate.SOURCE_PATH))
        for name, uid in gate.UIDS.items()])


class ResultTests(unittest.TestCase):
    def test_positive_exact_one(self):
        gate.validate_result(NAME, result(), 0, False)

    def test_runtime_assembly_version_and_generation_fail_closed(self):
        for key, value in [('runtimeVersion', '.NET 10.0.2'), ('assemblyName', 'SharpLink.UnitTests'),
                           ('tunitVersion', 'old'), ('schemaVersion', True), ('publicationGeneration', None)]:
            report = result()
            report[key] = value
            with self.subTest(key=key), self.assertRaises(gate.GateError):
                gate.validate_result(NAME, report, 0, False)

    def test_skip_failure_and_retry_are_not_passes(self):
        for status in ('skipped', 'failed', 'cancelled', 'timedOut', 'inProgress'):
            report = result()
            report['groups'][0]['tests'][0]['status'] = status
            with self.subTest(status=status), self.assertRaises(gate.GateError):
                gate.validate_result(NAME, report, 0, False)
        for counter in ('flaky', 'skipped', 'failed', 'timedOut', 'cancelled', 'total', 'passed'):
            report = result()
            report['summary'][counter] += 1
            with self.subTest(counter=counter), self.assertRaises(gate.GateError):
                gate.validate_result(NAME, report, 0, False)

    def test_wrong_missing_extra_duplicate_uid_rejected(self):
        for case in ('wrong', 'missing', 'extra', 'duplicate'):
            report = result()
            tests = report['groups'][0]['tests']
            if case == 'wrong': tests[0]['id'] = gate.UIDS[gate.METHODS[0]]
            if case == 'missing': tests.clear()
            if case == 'extra': tests.append(dict(id='extra', status='passed'))
            if case == 'duplicate': tests.append(dict(tests[0]))
            with self.subTest(case=case), self.assertRaises(gate.GateError):
                gate.validate_result(NAME, report, 0, False)

    def test_failed_exit_and_process_timeout_rejected(self):
        for code, timeout in ((1, False), (-9, True), (0, True)):
            with self.subTest(code=code), self.assertRaises(gate.GateError):
                gate.validate_result(NAME, result(), code, timeout)

    def test_empty_or_bad_groups_rejected(self):
        for groups in (None, [], [{}], [dict(tests=[None])]):
            report = result()
            report['groups'] = groups
            with self.subTest(groups=groups), self.assertRaises(gate.GateError):
                gate.validate_result(NAME, report, 0, False)


class DiscoveryTests(unittest.TestCase):
    def test_all_eleven_required_including_three_mutual_cases(self):
        self.assertEqual(len(gate.METHODS), 11)
        self.assertEqual(sum(name.startswith('MutualTls') for name in gate.METHODS), 3)
        gate.validate_discovery(discovery())

    def test_missing_added_duplicate_wrong_location_or_method_fail(self):
        for case in ('missing', 'added', 'duplicate', 'location', 'method'):
            document = discovery()
            if case == 'missing': document['tests'].pop()
            if case == 'added':
                item = copy.deepcopy(document['tests'][0]); item['uid'] = 'new'; document['tests'].append(item)
            if case == 'duplicate': document['tests'].append(copy.deepcopy(document['tests'][0]))
            if case == 'location': document['tests'][0]['location']['file'] = '/source/other.cs'
            if case == 'method': document['tests'][0]['type']['methodName'] = 'Wrong'
            with self.subTest(case=case), self.assertRaises(gate.GateError):
                gate.validate_discovery(document)

    def test_other_integration_classes_are_not_run(self):
        document = discovery()
        document['tests'].append(dict(uid='other', type=dict(namespace='SharpLink.IntegrationTests', typeName='Other')))
        gate.validate_discovery(document)
        command = gate.test_command('/private/dotnet', Path('/source/tests.dll'), NAME, Path('/tmp/output'))
        self.assertEqual(command[:2], ['/private/dotnet', '/source/tests.dll'])
        self.assertNotIn('run', command)
        self.assertEqual(command[command.index('--filter-uid') + 1], gate.UIDS[NAME])


class IsolationTests(unittest.TestCase):
    def test_watchdog_kills_whole_process_group_including_disposal(self):
        process = MagicMock(pid=1234, returncode=-9)
        process.wait.side_effect = [subprocess.TimeoutExpired(['test'], 120), -9]
        with tempfile.TemporaryDirectory() as directory, patch.object(gate.unit.subprocess, 'Popen', return_value=process) as start, \
             patch.object(gate.unit.os, 'killpg') as kill:
            code, timed_out = gate.unit.execute(['/private/dotnet', 'tests.dll'], Path(directory), {}, 120, Path(directory) / 'raw.log')
        self.assertEqual((code, timed_out), (-9, True))
        self.assertTrue(start.call_args.kwargs['start_new_session'])
        kill.assert_called_once_with(1234, gate.unit.signal.SIGKILL)

    def test_raw_github_reporting_environment_removed(self):
        env = gate.unit.private_test_environment(dict(GITHUB_ACTIONS='true', GITHUB_STEP_SUMMARY='raw', PATH='/bin'), '/private/dotnet')
        self.assertFalse(any(k.startswith('GITHUB_') for k in env))
        self.assertEqual(env['DOTNET_ROOT'], '/private')

    def test_no_upload_performance_or_credentials_retained(self):
        workflow = (Path(__file__).resolve().parents[2] / '.github/workflows/tls-security-no-upload.yml').read_text()
        for text in ('actions/upload-artifact@', 'actions/cache@', 'security-events: write', 'sudo ', 'performance.py'):
            self.assertNotIn(text, workflow)
        for text in ('contents: read', 'persist-credentials: false', 'fetch-depth: 4', 'timeout-minutes: 50'):
            self.assertIn(text, workflow)


class ProvenanceTests(unittest.TestCase):
    def source_answers(self):
        return {
            ('rev-parse', 'HEAD'): 'a' * 40,
            ('rev-parse', 'HEAD^{tree}'): gate.SOURCE_TREE,
            ('status', '--porcelain', '--untracked-files=all'): '',
            ('show', '-s', '--format=%P', 'HEAD'): gate.BASE_SHA,
            ('rev-parse', gate.BASE_SHA + '^{tree}'): gate.BASE_TREE,
            ('diff', '--name-status', '--no-renames', gate.BASE_SHA, 'HEAD'): 'M\t' + gate.SOURCE_PATH,
        }

    def test_source_sha_reviewed_tree_parent_and_single_test_change(self):
        answers = self.source_answers()
        with patch.object(gate, 'git', side_effect=lambda _, *args: answers[args]):
            gate.verify_source(Path('/source'), 'a' * 40)
            for key in answers:
                original = answers[key]; answers[key] = 'wrong'
                with self.subTest(key=key), self.assertRaises(gate.GateError):
                    gate.verify_source(Path('/source'), 'a' * 40)
                answers[key] = original

    def test_request_binds_actual_source_and_tools_sha_but_never_changes_reviewed_source_tree(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            path = root / gate.REQUEST
            path.parent.mkdir(parents=True)
            request = dict(schema_version=1, source_sha='a' * 40, tools_sha='b' * 40, tools_tree='c' * 40)
            path.write_text(json.dumps(request))
            answers = {
                ('rev-parse', 'HEAD'): 'd' * 40,
                ('status', '--porcelain', '--untracked-files=all'): '',
                ('show', '-s', '--format=%P', 'HEAD'): 'b' * 40,
                ('rev-parse', 'b' * 40 + '^{tree}'): 'c' * 40,
                ('diff', '--name-status', '--no-renames', 'b' * 40, 'HEAD'): 'A\t' + gate.REQUEST,
                ('show', '-s', '--format=%P', 'b' * 40): 'a' * 40,
                ('rev-parse', 'a' * 40 + '^{tree}'): gate.SOURCE_TREE,
                ('show', '-s', '--format=%P', 'a' * 40): gate.BASE_SHA,
                ('rev-parse', gate.BASE_SHA + '^{tree}'): gate.BASE_TREE,
                ('diff', '--name-status', '--no-renames', gate.BASE_SHA, 'a' * 40): 'M\t' + gate.SOURCE_PATH,
                ('diff', '--name-status', '--no-renames', 'a' * 40, 'b' * 40): '\n'.join(sorted('A\t' + p for p in gate.TOOL_FILES)),
            }
            with patch.object(gate, 'git', side_effect=lambda _, *args: answers[args]):
                gate.verify_release(root, 'd' * 40, gate.BRANCH)
                with self.assertRaises(gate.GateError): gate.verify_release(root, 'd' * 40, 'refs/heads/main')
                for key in answers:
                    original = answers[key]; answers[key] = 'wrong'
                    with self.subTest(key=key), self.assertRaises(gate.GateError):
                        gate.verify_release(root, 'd' * 40, gate.BRANCH)
                    answers[key] = original
                path.write_text(json.dumps(dict(request, source_tree='untrusted')))
                with self.assertRaises(gate.GateError): gate.verify_release(root, 'd' * 40, gate.BRANCH)


if __name__ == '__main__':
    unittest.main(verbosity=2)
