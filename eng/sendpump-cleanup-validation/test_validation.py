#!/usr/bin/env python3
"""Pure-tool failure controls; no product execution, socket, pipe, or network."""
import copy
import importlib.util
import json
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import MagicMock, patch

spec = importlib.util.spec_from_file_location('cleanup_validation', Path(__file__).with_name('run.py'))
gate = importlib.util.module_from_spec(spec)
spec.loader.exec_module(gate)


def baseline_report():
    summary = dict(total=1, passed=0, failed=1, skipped=0, cancelled=0, timedOut=0, flaky=0)
    return dict(schemaVersion=1, assemblyName='SharpLink.UnitTests', tunitVersion='1.73.19.0', runtimeVersion='.NET 10.0.14',
        publicationGeneration='a' * 32, summary=summary, groups=[dict(summary=dict(summary), tests=[dict(id=gate.BASELINE_UID,
            status='failed', exception=dict(type='System.InvalidOperationException', message=gate.BASELINE_ASSERTION,
            stackTrace='SendPumpCleanupProgressTests.ThrowingBatchReturnMustNotReleaseRerentedOwnerOrAbandonEitherQueue()', innerException=None))])])


def tcp_log():
    rows = []
    for condition, termination in gate.TCP_CASES:
        rows.append('NEW_CORRECTNESS_CASE ' + condition + '/' + termination)
        rows.append(json.dumps(dict(suite='new-source-serverstream-cleanup', condition=condition, termination=termination,
            passed=True, failure=None, teardownFailures=[], expectedRuntimeVersion='10.0.14', runtimeVersion='10.0.14',
            frameworkDescription='.NET 10.0.14', historicalReplay=False)))
    rows.append('NEW_CORRECTNESS_RESULT cases=14 failures=[]')
    return '\n'.join(rows)


class BaselineTests(unittest.TestCase):
    def test_one_exact_assertion_failure_is_required(self):
        gate.validate_baseline(baseline_report(), 2, False)
        gate.validate_baseline(baseline_report(), 1, False)

    def test_exit_timeout_crash_and_unexpected_pass_inconclusive(self):
        for code, timed_out in ((0, False), (3, False), (-9, True), (2, True)):
            with self.subTest(code=code, timeout=timed_out), self.assertRaises(gate.GateError):
                gate.validate_baseline(baseline_report(), code, timed_out)

    def test_internal_timeout_and_old_generic_assertion_are_inconclusive(self):
        # The former test could swallow an internal WaitAsync timeout and then
        # emit this unrelated aggregate-membership assertion. Never accept it.
        for message, kind in (
            ('every distinct owner-cleanup failure must remain observable', 'System.InvalidOperationException'),
            ('The operation has timed out.', 'System.TimeoutException'),
            (gate.BASELINE_ASSERTION, 'System.TimeoutException'),
            ('cleanup operation did not complete', 'System.InvalidOperationException'),
            (gate.BASELINE_ASSERTION.replace('completed cleanup', 'incomplete cleanup'), 'System.InvalidOperationException'),
            (gate.BASELINE_ASSERTION.replace('abandoned later batch and queued owners', 'unrelated failure'), 'System.InvalidOperationException'),
        ):
            r = baseline_report(); error = r['groups'][0]['tests'][0]['exception']
            error.update(type=kind, message=message)
            with self.subTest(message=message, kind=kind), self.assertRaises(gate.GateError):
                gate.validate_baseline(r, 2, False)
        r = baseline_report()
        r['groups'][0]['tests'][0]['exception']['innerException'] = dict(type='System.TimeoutException')
        with self.assertRaises(gate.GateError):
            gate.validate_baseline(r, 2, False)

    def test_not_any_failed_test_is_causal(self):
        for mutation in ('uid', 'skip', 'type', 'message', 'stack', 'inner', 'retry', 'runtime', 'count', 'duplicate'):
            r = baseline_report(); t = r['groups'][0]['tests'][0]; e = t['exception']
            if mutation == 'uid': t['id'] = gate.NEW_UIDS[0]
            if mutation == 'skip': t['status'] = 'skipped'
            if mutation == 'type': e['type'] = 'System.TimeoutException'
            if mutation == 'message': e['message'] = 'unrelated assertion'
            if mutation == 'stack': e['stackTrace'] = 'infrastructure failure'
            if mutation == 'inner': e['innerException'] = dict(type='SocketException')
            if mutation == 'retry': t['retryAttempt'] = 1
            if mutation == 'runtime': r['runtimeVersion'] = '.NET 10.0.2'
            if mutation == 'count': r['summary']['failed'] = True
            if mutation == 'duplicate': r['groups'][0]['tests'].append(copy.deepcopy(t))
            with self.subTest(mutation=mutation), self.assertRaises(gate.GateError): gate.validate_baseline(r, 2, False)


class CensusAndTcpTests(unittest.TestCase):
    def test_new_manifest_counts_four_methods_six_cases(self):
        self.assertEqual(len(gate.NEW_UIDS), 6)
        self.assertEqual(sum('TerminalCleanup' in u for u in gate.NEW_UIDS), 3)
        self.assertIn(gate.BASELINE_UID, gate.NEW_UIDS)
        root = Path(__file__).resolve().parents[2]
        original = json.loads((root / 'eng/static-stream-unit-validation/expected-uids.json').read_text())
        workers = gate.explicit_workers(root / 'eng/static-stream-unit-validation')
        self.assertEqual(len(original), 2310)
        self.assertEqual(len(workers), 5)
        self.assertEqual(len(gate.ordinary_uids(sorted(original + gate.NEW_UIDS), workers)), 2311)

    def test_full_and_focused_commands_preserve_existing_gate(self):
        full = gate.command('/private/dotnet', Path('/product/tests.dll'), Path('/tmp/o'))
        self.assertEqual(full[full.index('--minimum-expected-tests') + 1], '2311')
        self.assertEqual(full[full.index('--maximum-parallel-tests') + 1], '4')
        self.assertIn('20m', full)
        focus = gate.command('/private/dotnet', Path('/product/tests.dll'), Path('/tmp/o'), gate.BASELINE_UID)
        self.assertEqual(focus[:2], ['/private/dotnet', '/product/tests.dll'])
        self.assertEqual(focus[focus.index('--filter-uid') + 1], gate.BASELINE_UID)
        self.assertNotIn('run', focus)

    def test_tcp14_exact_runtime_and_case_census(self):
        gate.validate_tcp(tcp_log(), 0, False)
        for mutation in ('runtime', 'missing', 'duplicate', 'failed', 'teardown', 'final'):
            log = tcp_log()
            if mutation == 'runtime': log = log.replace('10.0.14', '10.0.2')
            if mutation == 'missing': log = '\n'.join(log.splitlines()[2:])
            if mutation == 'duplicate': log += '\n' + log.splitlines()[1]
            if mutation == 'failed': log = log.replace('"passed": true', '"passed": false', 1)
            if mutation == 'teardown': log = log.replace('"teardownFailures": []', '"teardownFailures": ["failure"]', 1)
            if mutation == 'final': log = log.replace('NEW_CORRECTNESS_RESULT cases=14 failures=[]', '')
            with self.subTest(mutation=mutation), self.assertRaises(gate.GateError): gate.validate_tcp(log, 0, False)
        with self.assertRaises(gate.GateError): gate.validate_tcp(tcp_log(), 1, False)
        with self.assertRaises(gate.GateError): gate.validate_tcp(tcp_log(), 0, True)

    def test_external_watchdog_kills_entire_tree(self):
        process = MagicMock(pid=1234, returncode=-9)
        process.wait.side_effect = [subprocess.TimeoutExpired(['test'], 120), -9]
        with tempfile.TemporaryDirectory() as directory, patch.object(gate.unit.subprocess, 'Popen', return_value=process) as start, patch.object(gate.unit.os, 'killpg') as kill:
            self.assertEqual(gate.unit.execute(['host', 'tests.dll'], Path(directory), {}, 120, Path(directory) / 'log'), (-9, True))
        self.assertTrue(start.call_args.kwargs['start_new_session']); kill.assert_called_once_with(1234, gate.unit.signal.SIGKILL)


class ProvenanceTests(unittest.TestCase):
    def test_candidate_and_baseline_have_distinct_frozen_scope(self):
        for baseline in (False, True):
            changes = ([] if baseline else ['M\t' + gate.PRODUCT_PATH]) + ['A\t' + gate.TEST_PATH]
            answers = {
                ('rev-parse', 'HEAD'): 'a' * 40,
                ('rev-parse', 'HEAD^{tree}'): gate.OVERLAY_TREE if baseline else gate.SOURCE_TREE,
                ('status', '--porcelain', '--untracked-files=all'): '',
                ('show', '-s', '--format=%P', 'HEAD'): gate.BASE_SHA,
                ('rev-parse', gate.BASE_SHA + '^{tree}'): gate.BASE_TREE,
                ('diff', '--name-status', '--no-renames', gate.BASE_SHA, 'HEAD'): '\n'.join(changes),
                ('rev-parse', 'HEAD:' + gate.TEST_PATH): gate.TEST_BLOB,
            }
            with patch.object(gate, 'git', side_effect=lambda _, *args: answers[args]):
                gate.verify_source(Path('/product'), 'a' * 40, baseline)
                for key in answers:
                    original = answers[key]; answers[key] = 'wrong'
                    with self.subTest(baseline=baseline, key=key), self.assertRaises(gate.GateError):
                        gate.verify_source(Path('/product'), 'a' * 40, baseline)
                    answers[key] = original

    def test_request_only_and_tool_source_parent_bindings(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory); path = root / gate.REQUEST; path.parent.mkdir(parents=True)
            path.write_text(json.dumps(dict(schema_version=1, source_sha='a' * 40, baseline_sha='b' * 40, tools_sha='c' * 40, tools_tree='d' * 40)))
            answers = {
                ('rev-parse', 'HEAD'): 'e' * 40,
                ('status', '--porcelain', '--untracked-files=all'): '',
                ('show', '-s', '--format=%P', 'HEAD'): 'c' * 40,
                ('rev-parse', 'c' * 40 + '^{tree}'): 'd' * 40,
                ('diff', '--name-status', '--no-renames', 'c' * 40, 'HEAD'): 'A\t' + gate.REQUEST,
                ('show', '-s', '--format=%P', 'c' * 40): 'a' * 40,
                ('rev-parse', 'a' * 40 + '^{tree}'): gate.SOURCE_TREE,
                ('show', '-s', '--format=%P', 'a' * 40): gate.BASE_SHA,
                ('rev-parse', gate.BASE_SHA + '^{tree}'): gate.BASE_TREE,
                ('diff', '--name-status', '--no-renames', gate.BASE_SHA, 'a' * 40): '\n'.join(['M\t' + gate.PRODUCT_PATH, 'A\t' + gate.TEST_PATH]),
                ('rev-parse', 'a' * 40 + ':' + gate.TEST_PATH): gate.TEST_BLOB,
                ('diff', '--name-status', '--no-renames', 'a' * 40, 'c' * 40): '\n'.join(sorted('A\t' + p for p in gate.TOOL_FILES)),
            }
            with patch.object(gate, 'git', side_effect=lambda _, *args: answers[args]):
                gate.verify_release(root, 'e' * 40, gate.BRANCH)
                for key in answers:
                    original = answers[key]; answers[key] = 'wrong'
                    with self.subTest(key=key), self.assertRaises(gate.GateError): gate.verify_release(root, 'e' * 40, gate.BRANCH)
                    answers[key] = original
                with self.assertRaises(gate.GateError): gate.verify_release(root, 'e' * 40, 'refs/heads/main')

    def test_workflow_keeps_candidate_independent_no_upload_or_perf(self):
        workflow = (Path(__file__).resolve().parents[2] / '.github/workflows/sendpump-cleanup-no-upload.yml').read_text()
        for value in ('actions/upload-artifact@', 'actions/cache@', 'sudo ', 'security-events: write', 'run-performance-matrix'):
            self.assertNotIn(value, workflow)
        self.assertIn('needs: baseline', workflow)
        self.assertIn("!cancelled() && github.event_name", workflow)
        self.assertIn('persist-credentials: false', workflow)
        self.assertIn('contents: read', workflow)


if __name__ == '__main__': unittest.main(verbosity=2)
