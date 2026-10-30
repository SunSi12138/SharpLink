import json, subprocess, tempfile, unittest
from pathlib import Path
from unittest.mock import Mock, patch
from process import execute, group_alive

class ProcessTests(unittest.TestCase):
    def run_mock(self, process, alive, kill=None):
        with tempfile.TemporaryDirectory() as temp, patch('process.subprocess.Popen',return_value=process) as launch, patch('process.group_alive',side_effect=alive), patch('process.os.killpg',side_effect=kill) as killer:
            root=Path(temp);host=execute(['mock-dotnet'],root,'slot',{'K':'V'})
            self.assertEqual(json.loads((root/'slot.host.json').read_text()),host)
            self.assertTrue(launch.call_args.kwargs['start_new_session'])
            self.assertEqual(launch.call_args.kwargs['cwd'],root)
            return host,killer.call_count
    def test_success(self):
        p=Mock(pid=42);p.wait.return_value=0;p.poll.return_value=0
        host,kills=self.run_mock(p,[False]);self.assertEqual(host['status'],'success');self.assertTrue(host['processGroupQuiescent']);self.assertEqual(kills,0);p.wait.assert_called_once_with(timeout=90)
    def test_nonzero_stops(self):
        p=Mock(pid=42);p.wait.return_value=7;p.poll.return_value=7
        host,_=self.run_mock(p,[False]);self.assertEqual(host['status'],'failed');self.assertEqual(host['exitCode'],7)
    def test_timeout_bounded_kill_and_receipt(self):
        p=Mock(pid=42);p.wait.side_effect=[subprocess.TimeoutExpired('mock',90),-9];p.poll.return_value=None
        host,kills=self.run_mock(p,[True,False]);self.assertTrue(host['timedOut']);self.assertEqual(host['status'],'failed');self.assertTrue(host['processGroupQuiescent']);self.assertEqual(kills,1);self.assertEqual([c.kwargs['timeout'] for c in p.wait.call_args_list],[90,5])
    def test_zero_exit_descendant_is_failure_even_if_cleaned(self):
        p=Mock(pid=42);p.wait.return_value=0;p.poll.return_value=0
        host,kills=self.run_mock(p,[True,False]);self.assertTrue(host['descendantLeak']);self.assertEqual(host['status'],'failed');self.assertEqual(kills,1)
    def test_ps_uncertainty_cannot_claim_quiescence(self):
        p=Mock(pid=42);p.wait.return_value=0
        host,kills=self.run_mock(p,[subprocess.TimeoutExpired('ps',5)]);self.assertFalse(host['processGroupQuiescent']);self.assertIn('cleanupError',host);self.assertEqual(host['status'],'failed');self.assertEqual(kills,1);self.assertEqual(p.wait.call_args.kwargs['timeout'],5)
    def test_cleanup_wait_timeout_is_retained_and_bounded(self):
        p=Mock(pid=42);p.wait.side_effect=[subprocess.TimeoutExpired('mock',90),subprocess.TimeoutExpired('mock',5),subprocess.TimeoutExpired('mock',5)];p.poll.return_value=None
        host,_=self.run_mock(p,[True]);self.assertIn('cleanupWaitError',host);self.assertFalse(host['processGroupQuiescent']);self.assertEqual([c.kwargs['timeout'] for c in p.wait.call_args_list],[90,5,5])
    def test_kill_error_stops_uncertain(self):
        p=Mock(pid=42);p.wait.return_value=0;p.poll.return_value=0
        host,_=self.run_mock(p,[True],PermissionError('mock kill denied'));self.assertFalse(host['processGroupQuiescent']);self.assertEqual(host['status'],'failed')
    def test_group_deadline_is_finite(self):
        p=Mock(pid=42);p.wait.return_value=0;p.poll.return_value=0
        with patch('process.time.monotonic',side_effect=[0,1,7,8]):
            host,_=self.run_mock(p,[True])
        self.assertFalse(host['processGroupQuiescent']);self.assertIn('cleanupError',host)
        self.assertTrue(all(c.kwargs.get('timeout') in (90,5) for c in p.wait.call_args_list))
    def test_start_failure_persists_receipt(self):
        with tempfile.TemporaryDirectory() as temp, patch('process.subprocess.Popen',side_effect=FileNotFoundError('mock')):
            root=Path(temp);host=execute(['mock-dotnet'],root,'slot',{})
            self.assertIsNone(host['pid']);self.assertFalse(host['processGroupQuiescent']);self.assertEqual(host['status'],'failed');self.assertTrue((root/'slot.host.json').exists())
    def test_ps_is_bounded_and_zombies_excluded(self):
        with patch('process.subprocess.check_output',return_value=' 42 Z\n 45 S\n') as check:
            self.assertFalse(group_alive(42));self.assertEqual(check.call_args.kwargs['timeout'],5)
        with patch('process.subprocess.check_output',return_value=' 42 S\n') as check:
            self.assertTrue(group_alive(42,timeout=.5));self.assertEqual(check.call_args.kwargs['timeout'],.5)
