"""Offline finite-plan and launch-failure controls; no fixture process is started."""
import copy
from pathlib import Path
import signal
import subprocess
import tempfile
import unittest
from unittest.mock import patch, MagicMock
from common import CONTRACT, gate_schedule, schedule, strict_json
from run import launch_plan, command_for, execute


class RunTests(unittest.TestCase):
    def test_exact_finite_phases_and_identity(self):
        plan = launch_plan()
        self.assertEqual(list(range(60)), [s['launchIndex'] for s in plan])
        self.assertEqual(['strict-cleanup'] * 4 + ['normal-validate'] * 8 + ['normal-measure'] * 48,
                         [s['phase'] for s in plan])
        self.assertEqual({'dev'}, {s['arm'] for s in plan[:4]})
        self.assertEqual(8, len({(s['mode'], s['items'], s['arm']) for s in plan[4:12]}))
        self.assertEqual('3d76a3a8b55a37e24c62a073fe588cff3d3867be', CONTRACT['devSource'])
        self.assertEqual('1c24da3af090acd4d6cdbfb005a9e3bd95304e3a', CONTRACT['devTree'])
        self.assertEqual((4, 5, '10.0.14'), (CONTRACT['officialApi'], CONTRACT['devApi'], CONTRACT['runtime']))
        self.assertEqual(40753260956, CONTRACT['knownNegative']['run'])
        with self.assertRaises(ValueError): gate_schedule('validate')

    def test_phase_command_budget(self):
        for slot in launch_plan():
            command, folder = command_for(slot, Path('/prepared'), Path('/runtime/dotnet'), Path('/run'), [0, 2])
            self.assertEqual(['taskset', '-c', '0,2', '/runtime/dotnet'], command[:4])
            self.assertEqual(f"/prepared/{slot['arm']}/PhysicalTcpProbe.dll", command[4])
            self.assertEqual('1', command[7])
            self.assertEqual(slot['phase'], command[11])
            self.assertEqual(['3', '6'] if slot['phase'] == 'normal-measure' else ['0', '0'], command[8:10])
            self.assertEqual(13 if slot['phase'] == 'normal-measure' else 12, len(command))
            if slot['phase'] != 'normal-measure':
                self.assertNotIn('/ticks/', ' '.join(command))

    def execute_fake(self, process, groups):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            slot = launch_plan()[0]
            with patch('run.subprocess.Popen', return_value=process) as popen, \
                 patch('run.observation', return_value={}), \
                 patch('run.group_alive', side_effect=groups), patch('run.os.killpg') as kill:
                host = execute(['never-executed'], root, slot, {})
            stored = strict_json((root / (slot['id'] + '.host.json')).read_text())
            self.assertEqual(host, stored)
            self.assertEqual(1, popen.call_count)
            self.assertTrue(popen.call_args.kwargs['start_new_session'])
            return host, kill

    def test_successful_parent_with_descendant_is_failure(self):
        process = MagicMock(pid=123); process.wait.return_value = 0
        host, kill = self.execute_fake(process, [True, False, False])
        self.assertEqual('failed', host['status'])
        self.assertTrue(host['descendantLeak'])
        self.assertTrue(host['processGroupQuiescent'])
        kill.assert_called_once_with(123, signal.SIGKILL)

    def test_timeout_retained_and_never_retried(self):
        process = MagicMock(pid=123)
        process.wait.side_effect = [subprocess.TimeoutExpired('fake', 120), -9]
        host, kill = self.execute_fake(process, [False, False])
        self.assertEqual('failed', host['status'])
        self.assertEqual(-9, host['exitCode'])
        self.assertTrue(host['timedOut'])
        kill.assert_called_once_with(123, signal.SIGKILL)

    def test_nonzero_exit_not_promoted(self):
        process = MagicMock(pid=123); process.wait.return_value = 1
        host, kill = self.execute_fake(process, [False, False])
        self.assertEqual('failed', host['status']); self.assertEqual(1, host['exitCode'])
        self.assertTrue(host['processGroupQuiescent']); kill.assert_not_called()

    def test_source_does_not_reintroduce_mixed_preflight_or_raw_stdout(self):
        root = Path(__file__).parent
        program = (root / 'fixture/probe/Program.cs').read_text()
        self.assertNotIn('Console.WriteLine(json)', program)
        self.assertEqual(1, program.count('ColdChecks.Run('))
        self.assertIn('phase == "strict-cleanup"', program)
        workflow = (root.parents[1] / '.github/workflows/official-normal-comparison.yml').read_text()
        for forbidden in ('actions/upload-artifact@', '| tee', 'continue-on-error:', 'rm -rf', 'workflow_dispatch:'):
            self.assertNotIn(forbidden, workflow)
        self.assertIn('if: always()', workflow)
        self.assertNotIn('40753260956', (root / 'run.py').read_text())


if __name__ == '__main__':
    unittest.main()
