"""Offline finite-plan and launch-failure controls; no fixture process is started."""
import copy
from pathlib import Path
import signal
import subprocess
import tempfile
import unittest
from unittest.mock import patch, MagicMock
from common import CONTRACT, gate_schedule, schedule, strict_json
from run import launch_plan, command_for, execute, main
from test_protocol import synthetic_unit_record
from common import ROOT, write, source_inventory


class RunTests(unittest.TestCase):
    def test_exact_finite_phases_and_identity(self):
        plan = launch_plan()
        self.assertEqual(list(range(48)), [s['launchIndex'] for s in plan])
        self.assertEqual(['strict-cleanup'] * 8 + ['normal-validate'] * 8 + ['normal-measure'] * 32,
                         [s['phase'] for s in plan])
        self.assertEqual({'baseline','candidate'}, {s['arm'] for s in plan[:8]})
        self.assertEqual(8, len({(s['mode'], s['items'], s['arm']) for s in plan[8:16]}))
        self.assertEqual('fbfd5590372ca0cad9766c3cf7e509055c3874f1', CONTRACT['candidateSource'])
        self.assertEqual('2a833a52cc04346c41dcdcaf627766dc2beba280', CONTRACT['candidateTree'])
        self.assertEqual((5, 5, '10.0.14'), (CONTRACT['baselineApi'], CONTRACT['candidateApi'], CONTRACT['runtime']))
        self.assertNotIn('knownNegative',CONTRACT)
        with self.assertRaises(ValueError): gate_schedule('validate')

    def test_unit_gate_missing_or_mutated_prevents_any_runtime_or_probe_launch(self):
        with tempfile.TemporaryDirectory() as td:
            base=Path(td); prepared=base/'prepared'; prepared.mkdir()
            manifest=dict(contract=CONTRACT, toolRevision='a'*40,
                          toolSourceHashes=source_inventory(ROOT), fixturePurpose='outbound-lease-comparison',
                          fixtureSourceHashes=source_inventory(ROOT/'fixture'))
            write(prepared/'prepared.json',manifest)
            gate=base/'unit-gate.json'
            argv=['run.py','--prepared',str(prepared),'--runtime-dotnet',str(base/'absent-runtime'),
                  '--output',str(base/'output'),'--reviewed-tool-sha','a'*40,
                  '--quiet-window-record',str(base/'absent-quiet'), '--runtime-archives',str(base/'absent-archives'),
                  '--unit-gate-record',str(gate)]
            for mutation in [None, ('candidateSource',CONTRACT['baselineSource']), ('toolRevision','b'*40),
                             ('runtime','10.0.12'), ('executedUidSha256','0'*64), ('passedCount',2322)]:
                if mutation is not None:
                    record=synthetic_unit_record(); record[mutation[0]]=mutation[1];write(gate,record)
                with self.subTest(mutation=mutation), patch('sys.argv',argv), \
                     patch('run.subprocess.check_output',side_effect=['a'*40,'']) as commands, \
                     patch('run.execute') as execute_probe:
                    with self.assertRaises((OSError,ValueError)): main()
                    self.assertEqual(2,commands.call_count) # Only tool git identity, no CLR.
                    execute_probe.assert_not_called()
                    self.assertFalse((base/'output').exists())

    def test_valid_unit_gate_is_copied_before_any_probe_launch(self):
        with tempfile.TemporaryDirectory() as td:
            base=Path(td); prepared=base/'prepared'; prepared.mkdir()
            for arm in ('baseline','candidate'): (prepared/arm).mkdir()
            manifest=dict(contract=CONTRACT, toolRevision='a'*40,
                          toolSourceHashes=source_inventory(ROOT), fixturePurpose='outbound-lease-comparison',
                          fixtureSourceHashes=source_inventory(ROOT/'fixture'),
                          arms={arm:dict(snapshot={}) for arm in ('baseline','candidate')})
            write(prepared/'prepared.json',manifest)
            gate=base/'unit-gate.json'; record=synthetic_unit_record(); write(gate,record)
            runtime=base/'dotnet';runtime.write_text('not an executable; all subprocesses mocked')
            archives=base/'archives';archives.mkdir()
            quiet=base/'quiet.json';write(quiet,dict(noOtherClrBuildTest=True,reviewedToolCommit='a'*40,campaign=CONTRACT['campaign']))
            output=base/'output'
            argv=['run.py','--prepared',str(prepared),'--runtime-dotnet',str(runtime),
                  '--output',str(output),'--reviewed-tool-sha','a'*40,
                  '--quiet-window-record',str(quiet),'--runtime-archives',str(archives),'--unit-gate-record',str(gate)]
            listing='\n'.join(f'{name} 10.0.14 [{base / "shared" / name}]' for name in
                               ('Microsoft.AspNetCore.App','Microsoft.NETCore.App'))
            original_read=Path.read_text
            def read(path,*args,**kwargs):
                if path == Path('/etc/os-release'):return 'ID=ubuntu\nVERSION_ID="24.04"\n'
                return original_read(path,*args,**kwargs)
            with patch('sys.argv',argv), patch('run.subprocess.check_output',side_effect=['a'*40,'',listing,'','0,0,0,Y\n2,1,0,Y\n']), \
                 patch('run.framework_policy',return_value={}),patch('run.archive_framework_policy',return_value={}), \
                 patch.dict('os.environ',{'GITHUB_ACTIONS':'true'}),patch('run.os.sched_getaffinity',return_value={0,2}), \
                 patch.object(Path,'read_text',read),patch('run.observation',return_value={}), \
                 patch('run.command_for',side_effect=RuntimeError('offline stop before probe command')),patch('run.execute') as execute_probe:
                with self.assertRaisesRegex(RuntimeError,'offline stop'):main()
                execute_probe.assert_not_called()
            self.assertEqual(record,strict_json((output/'unit-gate.json').read_text()))

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
        workflow = (root.parents[1] / '.github/workflows/outbound-lease-comparison.yml').read_text()
        for forbidden in ('actions/upload-artifact@', '| tee', 'continue-on-error:', 'rm -rf', 'workflow_dispatch:'):
            self.assertNotIn(forbidden, workflow)
        self.assertIn('if: always()', workflow)
        self.assertNotIn('40753260956', (root / 'run.py').read_text())


if __name__ == '__main__':
    unittest.main()
