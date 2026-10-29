#!/usr/bin/env python3
"""Local pure-tool negative controls: no build, product tests, IPC or network."""
import copy
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch, MagicMock

spec = importlib.util.spec_from_file_location('pending_validation', Path(__file__).with_name('run.py'))
gate = importlib.util.module_from_spec(spec)
spec.loader.exec_module(gate)


def fixture(scenario):
    r = dict(phase='complete', scenario=scenario, invariant=True)
    if scenario.startswith('deadline-'):
        r.update(sameReference=True, futureBefore=True, futureAfter=True, firstError='DeadlineExceeded',
                 oldReason='DeadlineExceeded', secondError=None, active=0, count=0, firstId=1, secondId=2)
    elif scenario.startswith('logger-'):
        r.update(policyAcquires=1, policyReports=1, loggerReports=1, countAfter=0, activeAfter=0,
                 connectionsOpened=0, completed=True, returned=True, nextSucceeded=True, escaped=None)
    else:
        r.update(escaped=None, positiveHits=0 if scenario == 'no-listener' else 1,
                 negativeHits=0 if scenario in ('no-listener', 'metric-plus') else 1,
                 hits=0 if scenario == 'no-listener' else (1 if scenario == 'metric-plus' else 2), active=0, count=0)
        if scenario == 'metric-plus':
            r.update(registered=1, ownerRegistered=1, countBefore=1, activeBefore=1, operationError='ConnectionClosed')
        else:
            r.update(operationError=None, nextSucceeded=True, nextError=None, ownerRegisteredBeforeNext=1,
                     countBefore=0, activeBefore=0)
    return r


class WitnessTests(unittest.TestCase):
    def test_all_nine_positive_controls(self):
        self.assertEqual(len(gate.SCENARIOS), 9)
        self.assertEqual(len({gate.worker_uid(s) for s in gate.SCENARIOS}), 2)
        for scenario in gate.SCENARIOS:
            gate.validate_witness(scenario, fixture(scenario))

    def test_every_required_witness_missing_is_rejected(self):
        for scenario in gate.SCENARIOS:
            original = fixture(scenario)
            for key in original:
                report = copy.deepcopy(original)
                del report[key]
                with self.subTest(scenario=scenario, missing=key), self.assertRaises(gate.GateError):
                    gate.validate_witness(scenario, report)

    def test_missing_metric_injection_cannot_pass(self):
        for scenario in ('metric-minus', 'metric-plus'):
            report = fixture(scenario)
            report.update(hits=0, positiveHits=0, negativeHits=0)
            with self.assertRaises(gate.GateError):
                gate.validate_witness(scenario, report)

    def test_no_admission_injection_or_leak_cannot_pass(self):
        for key in ('policyReports', 'loggerReports', 'countAfter', 'activeAfter', 'connectionsOpened'):
            report = fixture('logger-throw')
            report[key] = 1 if report[key] == 0 else 0
            with self.assertRaises(gate.GateError):
                gate.validate_witness('logger-throw', report)

    def test_wrong_scenario_and_bool_numeric_witness_rejected(self):
        report = fixture('metric-plus')
        report['scenario'] = 'metric-control'
        with self.assertRaises(gate.GateError):
            gate.validate_witness('metric-plus', report)
        report = fixture('metric-plus')
        report['registered'] = True
        with self.assertRaises(gate.GateError):
            gate.validate_witness('metric-plus', report)

    def test_stale_request_identity_rejected(self):
        report = fixture('deadline-response')
        report['secondId'] = report['firstId']
        with self.assertRaises(gate.GateError):
            gate.validate_witness('deadline-response', report)

    def test_direct_host_and_exact_uid_only(self):
        command = gate.worker_command('/private/dotnet', Path('/product/UnitTests.dll'), 'metric-plus', Path('/tmp/out'))
        self.assertEqual(command[:2], ['/private/dotnet', '/product/UnitTests.dll'])
        self.assertNotIn('run', command)
        self.assertEqual(command[command.index('--filter-uid') + 1], gate.worker_uid('metric-plus'))
        with self.assertRaises(gate.GateError):
            gate.worker_uid('unknown')

    def test_private_environment_has_no_github_reporter(self):
        env = gate.unit.private_test_environment({'GITHUB_ACTIONS': 'true', 'GITHUB_STEP_SUMMARY': '/raw', 'PATH': '/bin'}, '/private/dotnet')
        self.assertFalse(any(k.startswith('GITHUB_') for k in env))
        self.assertEqual(env['DOTNET_ROOT'], '/private')



class ExecutionTests(unittest.TestCase):
    def report(self):
        summary = dict(total=1, passed=1, failed=0, skipped=0, cancelled=0, timedOut=0, flaky=0)
        return dict(schemaVersion=1, publicationGeneration='a' * 32, assemblyName='SharpLink.UnitTests',
                    tunitVersion='1.73.19.0', runtimeVersion='.NET 10.0.14', summary=summary,
                    groups=[dict(summary=dict(summary), tests=[dict(id=gate.worker_uid('metric-plus'),
                                 displayName='metric-plus', status='passed')])])

    def test_runtime_test_uid_and_counts_are_required(self):
        gate.validate_worker_report('metric-plus', self.report(), 0)
        for mutation in ('runtime', 'uid', 'skip', 'count', 'flaky', 'exit'):
            r = self.report()
            if mutation == 'runtime': r['runtimeVersion'] = '.NET 10.0.2'
            if mutation == 'uid': r['groups'][0]['tests'][0]['id'] = 'wrong'
            if mutation == 'skip': r['groups'][0]['tests'][0]['status'] = 'skipped'
            if mutation == 'count': r['summary']['total'] = 0
            if mutation == 'flaky': r['summary']['flaky'] = 1
            with self.subTest(mutation=mutation), self.assertRaises(gate.GateError):
                gate.validate_worker_report('metric-plus', r, 1 if mutation == 'exit' else 0)

    def test_whole_process_watchdog_kills_group_and_never_passes(self):
        with tempfile.TemporaryDirectory() as directory:
            process = MagicMock(pid=12345, returncode=-9)
            process.poll.side_effect = [None, -9]
            with patch.object(gate.subprocess, 'Popen', return_value=process), \
                 patch.object(gate.time, 'monotonic', side_effect=[0, 121, 122]), \
                 patch.object(gate.os, 'killpg') as kill, self.assertRaises(gate.GateError):
                gate.run_probe(Path('/product'), '/private/dotnet', Path('/product/tests.dll'), {},
                               'metric-plus', Path(directory) / 'case')
            kill.assert_called_once_with(12345, gate.signal.SIGKILL)
            result = json.loads((Path(directory) / 'case/execution.json').read_text())
            self.assertEqual(result['watchdog'], 'process-120s')

    def test_disposal_watchdog_uses_published_phase_and_never_passes(self):
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / 'case'
            process = MagicMock(pid=12345, returncode=-9)
            process.poll.side_effect = [None, -9]
            def start(*args, **kwargs):
                (output / 'scenario.json').write_text(json.dumps(dict(phase='dispose-enter')))
                return process
            with patch.object(gate.subprocess, 'Popen', side_effect=start), \
                 patch.object(gate.time, 'monotonic', side_effect=[0, 1, 17, 18]), \
                 patch.object(gate.os, 'killpg') as kill, self.assertRaises(gate.GateError):
                gate.run_probe(Path('/product'), '/private/dotnet', Path('/product/tests.dll'), {},
                               'metric-plus', output)
            kill.assert_called_once_with(12345, gate.signal.SIGKILL)
            result = json.loads((output / 'execution.json').read_text())
            self.assertEqual(result['watchdog'], 'dispose-15s')

class ProvenanceTests(unittest.TestCase):
    def test_request_parent_tree_and_delta_are_fail_closed(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / gate.REQUEST).parent.mkdir(parents=True)
            (root / gate.REQUEST).write_text(json.dumps({'schema_version': 1, 'tools_sha': 'a' * 40, 'tools_tree': 'b' * 40}))
            answers = {
                ('rev-parse', 'HEAD'): 'c' * 40,
                ('status', '--porcelain', '--untracked-files=all'): '',
                ('show', '-s', '--format=%P', 'HEAD'): 'a' * 40,
                ('rev-parse', 'a' * 40 + '^{tree}'): 'b' * 40,
                ('diff', '--name-status', '--no-renames', 'a' * 40, 'HEAD'): 'A\t' + gate.REQUEST,
                ('show', '-s', '--format=%P', 'a' * 40): gate.SOURCE_SHA,
                ('rev-parse', gate.SOURCE_SHA + '^{tree}'): gate.SOURCE_TREE,
                ('diff', '--name-status', '--no-renames', gate.SOURCE_SHA, 'a' * 40): '\n'.join(sorted('A\t' + f for f in gate.TOOL_FILES)),
            }
            with patch.object(gate, 'git', side_effect=lambda _, *args: answers[args]):
                gate.verify_release(root, 'c' * 40, gate.BRANCH)
                with self.assertRaises(gate.GateError):
                    gate.verify_release(root, 'c' * 40, 'refs/heads/main')
                for key in answers:
                    original = answers[key]
                    answers[key] = 'wrong'
                    with self.subTest(check=key), self.assertRaises(gate.GateError):
                        gate.verify_release(root, 'c' * 40, gate.BRANCH)
                    answers[key] = original

    def test_source_identity_and_dirty_checkout_rejected(self):
        values = [gate.SOURCE_SHA, gate.SOURCE_TREE, '']
        with patch.object(gate, 'git', side_effect=values):
            gate.verify_source(Path('/product'))
        for bad in (['wrong'], [gate.SOURCE_SHA, 'wrong'], [gate.SOURCE_SHA, gate.SOURCE_TREE, ' M product.cs']):
            with patch.object(gate, 'git', side_effect=bad), self.assertRaises(gate.GateError):
                gate.verify_source(Path('/product'))

    def test_workflow_is_read_only_without_upload_cache_or_perf(self):
        workflow = (Path(__file__).resolve().parents[2] / '.github/workflows/pending-lifecycle-no-upload.yml').read_text()
        for forbidden in ('actions/upload-artifact@', 'actions/cache@', 'security-events: write', 'sudo ', 'validate-codec-semantics.py', 'validate-datetimeoffset-performance.py'):
            self.assertNotIn(forbidden, workflow)
        self.assertIn('contents: read', workflow)
        self.assertIn('persist-credentials: false', workflow)
        self.assertIn('fetch-depth: 3', workflow)


if __name__ == '__main__':
    unittest.main(verbosity=2)
