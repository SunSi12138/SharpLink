#!/usr/bin/env python3
import copy
import os
import subprocess
import sys
from unittest.mock import patch, MagicMock
import json
from pathlib import Path
import tempfile
import unittest
import run as gate

UID = 'discovered-exact-uid-700'


def fixture(negative=False, interrupted=False):
    summary = dict(total=1, passed=int(not negative), failed=int(negative), skipped=0,
                   cancelled=0, timedOut=0, flaky=0)
    attempts = [dict(endpoint=e, phase='initial topology' if e != 'third' else 'concurrent topology/policy publication')
                for e in ['first', 'second', 'third']]
    if negative and not interrupted:
        attempts.append(dict(endpoint='first', phase='accepted stream drain'))
    events = []
    if negative:
        events = [dict(Endpoint=e, Category='SharpLink.Server.SharpLinkServer', EventId=1102,
                       Message='Client disconnected due to heartbeat timeout.', Phase='accepted stream drain', ElapsedMs=600)
                  for e in (['first', 'second'] if interrupted else ['first'])]
    error = None
    if negative:
        error = dict(type='SharpLink.Abstractions.SharpLinkException' if interrupted else 'System.Exception',
                     message='Transport closed.' if interrupted else
                     'resolver replacement requires exactly one new dial; policy publications must add none: expected total=3, first=1, second=1, third=1; actual total=4',
                     code='Unavailable' if interrupted else None)
    record = dict(schema=1, streamDelayMs=700, bodyCompleted=not negative, initialCountsVerified=True,
                  policyOnlyCountsVerified=True, phase='accepted stream drain' if interrupted else 'replacement readiness',
                  elapsedMs=2200, total=len(attempts), first=1 + int(negative and not interrupted), second=1, third=1,
                  attempts=attempts, serverEvents=events, error=error)
    if negative and not interrupted:
        error['message'] = gate.exact_dial_error(record)
    test = dict(id=UID, status='error' if negative else 'passed', startTime='start', endTime='end',
                output=gate.MARKER + json.dumps(record))
    if negative:
        test['exception'] = dict(type=error['type'], message=error['message'], stackTrace=gate.METHOD)
    report = dict(schemaVersion=1, assemblyName='SharpLink.IntegrationTests', tunitVersion='1.73.19.0',
                  runtimeVersion='.NET 10.0.14', operatingSystem='macOS 26.6.2', publicationGeneration='a' * 32,
                  summary=summary, groups=[dict(className=gate.CLASS, summary=copy.deepcopy(summary), tests=[test])])
    status = dict(completed=True, timed_out=False, process_group_quiescent=True, exit_code=2 if negative else 0)
    return report, status


def record_mutation(report, change):
    test = report['groups'][0]['tests'][0]
    record = json.loads(test['output'][len(gate.MARKER):])
    change(record)
    test['output'] = gate.MARKER + json.dumps(record)


class Controls(unittest.TestCase):
    def accept(self, report, status, negative=False):
        return gate.validate_report(report, UID, 700, negative, status)

    def test_positive_and_two_specific_causal_failure_paths(self):
        for negative, interrupted in [(False, False), (True, False), (True, True)]:
            self.accept(*fixture(negative, interrupted), negative=negative)

    def test_process_timeout_unfinished_bad_exit_and_incomplete_report_rejected(self):
        for change in [lambda r, s: s.update(completed=False), lambda r, s: s.update(timed_out=True), lambda r, s: s.update(process_group_quiescent=False),
                       lambda r, s: s.update(exit_code=0), lambda r, s: r.update(publicationGeneration=''),
                       lambda r, s: r['summary'].update(cancelled=1), lambda r, s: r.update(runtimeVersion='.NET 10.0.2'),
                       lambda r, s: r['groups'][0]['tests'][0].update(endTime=None),
                       lambda r, s: r['groups'][0]['tests'][0].update(retryAttempt=1),
                       lambda r, s: r['groups'][0]['tests'][0].update(id='other')]:
            report, status = fixture(True)
            change(report, status)
            with self.assertRaises(ValueError): self.accept(report, status, True)

    def test_generic_failure_or_missing_watchdog_witness_rejected(self):
        mutations = [lambda r: r.update(serverEvents=[]),
                     lambda r: r['serverEvents'][0].update(EventId=1201),
                     lambda r: r['serverEvents'][0].update(Endpoint='second'),
                     lambda r: r['serverEvents'][0].update(Message='Something failed.'),
                     lambda r: r.update(initialCountsVerified=False),
                     lambda r: r.update(policyOnlyCountsVerified=False),
                     lambda r: r.update(total=3), lambda r: r.update(bodyCompleted=True),
                     lambda r: r.update(phase='initial topology'),
                     lambda r: r['error'].update(message='generic failure')]
        for mutation in mutations:
            report, status = fixture(True)
            record_mutation(report, mutation)
            with self.assertRaises(ValueError): self.accept(report, status, True)

    def test_second_watchdog_requires_exact_category_and_message(self):
        for change in [lambda event: event.update(Category='unrelated'),
                       lambda event: event.update(Message='not a watchdog'),
                       lambda event: event.update(EventId=True)]:
            report, status = fixture(True, True)
            record_mutation(report, lambda record: change(record['serverEvents'][1]))
            with self.assertRaises(ValueError): self.accept(report, status, True)

    def test_extra_dial_error_requires_exact_counts_and_source_history(self):
        for change in [lambda message: message + 'junk',
                       lambda message: message.replace('actual total=4,', 'actual total=4junk,'),
                       lambda message: message.replace('actual total=4,', 'actual total=5,'),
                       lambda message: message.replace('first=2,', 'first=3,'),
                       lambda message: message.replace('first@accepted stream drain', 'third@accepted stream drain')]:
            report, status = fixture(True)
            test = report['groups'][0]['tests'][0]
            message = change(test['exception']['message'])
            test['exception']['message'] = message
            record_mutation(report, lambda record: record['error'].update(message=message))
            with self.assertRaises(ValueError): self.accept(report, status, True)

    def test_missing_duplicate_output_and_wrong_exception_rejected(self):
        for change in [lambda t: t.update(output=''), lambda t: t.update(output=t['output'] + '\n' + t['output']),
                       lambda t: t['exception'].update(type='System.TimeoutException'),
                       lambda t: t['exception'].update(stackTrace='SomeOtherTest'),
                       lambda t: t['exception'].update(message='unrelated failure')]:
            report, status = fixture(True)
            change(report['groups'][0]['tests'][0])
            with self.assertRaises(ValueError): self.accept(report, status, True)

    def test_positive_requires_three_dials_and_no_watchdog_failure(self):
        for change in [lambda r: r.update(first=2), lambda r: r.update(elapsedMs=100),
                       lambda r: r.update(bodyCompleted=False),
                       lambda r: r.update(serverEvents=[{'EventId': 1102}])]:
            report, status = fixture()
            record_mutation(report, change)
            with self.assertRaises(ValueError): self.accept(report, status)

    def test_discovery_uses_real_uid_and_exact_parameter_display(self):
        tests = [dict(uid='runtime-uid-' + str(delay), displayName=f'{gate.METHOD}({delay})',
                      type=dict(methodName=gate.METHOD, typeName=gate.CLASS, namespace='SharpLink.IntegrationTests',
                                parameterTypeFullNames=['System.Int32'])) for delay in (100, 700)]
        document = dict(schemaVersion=1, tests=tests)
        self.assertEqual(gate.select_cases(document), {100: 'runtime-uid-100', 700: 'runtime-uid-700'})
        for change in [lambda d: d['tests'].pop(), lambda d: d['tests'][1].update(uid='runtime-uid-100'),
                       lambda d: d['tests'][1].update(displayName=f'{gate.METHOD}(1700)'),
                       lambda d: d['tests'][1]['type'].update(typeName='OtherClass')]:
            mutated = copy.deepcopy(document); change(mutated)
            with self.assertRaises(ValueError): gate.select_cases(mutated)

    def test_duplicate_json_keys_rejected(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / 'record.json'; path.write_text('{"schema":1,"schema":2}')
            with self.assertRaises(ValueError): gate.read_json(path)


class ProcessIsolationControls(unittest.TestCase):
    def test_real_owned_child_of_successful_leader_is_joined_and_campaign_aborted(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            command = [sys.executable, '-c',
                       "import subprocess,sys; child=subprocess.Popen([sys.executable,'-c','import time; time.sleep(60)']); print(child.pid,flush=True)"]
            next_stages = []
            with self.assertRaises(gate.CampaignAborted):
                gate.execute(command, root, root, 'owned-child', 10)
                next_stages.append('must not start')
            self.assertEqual(next_stages, [])
            status = gate.read_json(root / 'owned-child.log.status.json')
            self.assertEqual(status['exit'], 0)
            self.assertTrue(status['descendantLeak'])
            self.assertTrue(status['processGroupQuiescent'])
            child = int((root / 'owned-child.log').read_text().strip())
            state = subprocess.run(['ps', '-o', 'stat=', '-p', str(child)], capture_output=True, text=True).stdout.strip()
            self.assertTrue(not state or state.startswith('Z'))
            outcome = gate.read_json(root / 'owned-child.status.json')
            self.assertIn('campaign_aborted', outcome)
            self.assertTrue(outcome['process_group_quiescent'])

    def test_real_timeout_joins_owned_group_but_stops_campaign(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            command = [sys.executable, '-c',
                       "import subprocess,sys,time; child=subprocess.Popen([sys.executable,'-c','import time; time.sleep(60)']); print(child.pid,flush=True); time.sleep(60)"]
            with self.assertRaises(gate.CampaignAborted): gate.execute(command, root, root, 'timeout', 0.2)
            status = gate.read_json(root / 'timeout.log.status.json')
            self.assertTrue(status['timed_out'])
            self.assertTrue(status['processGroupQuiescent'])
            child = int((root / 'timeout.log').read_text().strip())
            state = subprocess.run(['ps', '-o', 'stat=', '-p', str(child)], capture_output=True, text=True).stdout.strip()
            self.assertTrue(not state or state.startswith('Z'))

    def test_unknown_group_cleanup_is_fatal_and_retains_monitor_outcome(self):
        for kind in ('ps', 'kill', 'poll', 'recheck', 'wait', 'timeout-kill'):
            with self.subTest(kind=kind), tempfile.TemporaryDirectory() as tmp:
                root = Path(tmp)
                process = MagicMock(pid=1234)
                process.wait.return_value = 0
                error = OSError('owned-control-monitor-error')
                alive = [False, False]
                if kind == 'ps': alive = [error]
                if kind == 'kill': alive = [True]
                if kind == 'poll': alive = [True, error]
                if kind == 'recheck': alive = [False, error]
                if kind == 'wait': process.wait.side_effect = error
                if kind == 'timeout-kill': process.wait.side_effect = subprocess.TimeoutExpired(['host'], 1)
                with patch.object(gate.hosts.subprocess, 'Popen', return_value=process), \
                     patch.object(gate.hosts, 'group_alive', side_effect=alive), \
                     patch.object(gate.hosts.os, 'killpg', side_effect=error if kind in ('kill', 'timeout-kill') else None):
                    with self.assertRaises(gate.hosts.UnsafeProcessGroup): gate.execute(['host'], root, root, 'monitor', 1)
                status = gate.read_json(root / 'monitor.log.status.json')
                self.assertFalse(status['processGroupQuiescent'])
                self.assertEqual(status['monitorError']['type'], 'OSError')
                self.assertIn('unsafe_process_group', gate.read_json(root / 'monitor.status.json'))

    def test_zombies_are_not_executing_group_members(self):
        with patch.object(gate.hosts.subprocess, 'check_output', return_value='123 Z\n124 S\n'):
            self.assertFalse(gate.hosts.group_alive(123))
            self.assertTrue(gate.hosts.group_alive(124))

    def test_main_never_launches_later_case_or_arm_after_fatal_execution(self):
        for fatal in (gate.hosts.UnsafeProcessGroup('unknown group'), gate.CampaignAborted('timeout')):
            with self.subTest(fatal=type(fatal).__name__), tempfile.TemporaryDirectory() as tmp:
                root = Path(tmp)
                (root / 'positive').mkdir(); (root / 'negative').mkdir()
                calls = []
                def execute(argv, *args):
                    calls.append(argv)
                    if '--filter-uid' in argv: raise fatal
                    return dict(exit_code=0, completed=True, timed_out=False, process_group_quiescent=True)
                original = gate.read_json
                def read(path):
                    if Path(path).name == 'discovery.log': return {}
                    return original(path)
                argv = ['run.py', '--positive', str(root / 'positive'), '--negative', str(root / 'negative'),
                        '--output', str(root / 'reports')]
                with patch.object(sys, 'argv', argv), patch.object(gate, 'provenance'), \
                     patch.object(gate, 'execute', side_effect=execute), patch.object(gate, 'read_json', side_effect=read), \
                     patch.object(gate, 'select_cases', return_value={100: 'uid100', 700: 'uid700'}):
                    with self.assertRaises(type(fatal)): gate.main()
                self.assertEqual(len([args for args in calls if '--filter-uid' in args]), 1)
                self.assertFalse((root / 'reports' / 'negative').exists())
                oracle = gate.read_json(root / 'reports' / 'positive' / '100' / 'oracle.json')
                self.assertTrue(oracle['fatal_execution'])
                self.assertFalse(oracle['accepted'])
                self.assertTrue(any(row.get('fatal_execution') for row in gate.read_json(root / 'reports' / 'summary.json')))

    def test_helper_status_write_after_cleaned_timeout_or_leak_remains_fatal(self):
        for kind in ('timeout', 'leak'):
            with self.subTest(kind=kind), tempfile.TemporaryDirectory() as tmp:
                root = Path(tmp)
                process = MagicMock(pid=1234)
                process.wait.return_value = 0
                alive = [True, False, False] if kind == 'leak' else [False, False]
                if kind == 'timeout': process.wait.side_effect = [subprocess.TimeoutExpired(['host'], 1), -9]
                original = Path.open
                def opened(path, *args, **kwargs):
                    if path.name == 'inner.log.status.json': raise OSError('helper status write failed')
                    return original(path, *args, **kwargs)
                with patch.object(gate.hosts.subprocess, 'Popen', return_value=process), \
                     patch.object(gate.hosts, 'group_alive', side_effect=alive), \
                     patch.object(gate.hosts.os, 'killpg'), patch.object(Path, 'open', opened):
                    with self.assertRaises(gate.hosts.UnsafeProcessGroup): gate.execute(['host'], root, root, 'inner', 1)
                outcome = gate.read_json(root / 'inner.status.json')
                self.assertIn('unsafe_process_group', outcome)
                self.assertFalse(outcome['completed'])

    def test_fatal_status_write_failure_cannot_downgrade(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            with patch.object(gate.hosts, 'execute', side_effect=gate.hosts.UnsafeProcessGroup('not joined')), \
                 patch.object(gate, 'write_json', side_effect=OSError('status write failed')):
                with self.assertRaises(gate.hosts.UnsafeProcessGroup): gate.execute(['host'], root, root, 'fatal')


if __name__ == '__main__':
    unittest.main(verbosity=2)
