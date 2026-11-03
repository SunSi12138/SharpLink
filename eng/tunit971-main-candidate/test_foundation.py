"""Pure Python mocks only: never launch a child process, SDK or platform signal."""
import hashlib
import json
import os
from pathlib import Path
import signal
import subprocess
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch
import candidate
import hosted_process as h


class FakeProcess:
    pid=4321
    def __init__(self,code=0):self.code=code;self.kills=0;self.waits=0
    def poll(self):return self.code
    def wait(self,timeout):
        self.waits+=1
        if self.code is None:raise subprocess.TimeoutExpired(['PRIVATE'],timeout)
        return self.code
    def kill(self):self.kills+=1;self.code=-9


class Adapter(unittest.TestCase):
    def invoke(self,root,process,platform='darwin',expected=0,timeout=1,**overrides):
        tick=[0];calls=[]
        def clock():tick[0]+=.2;return tick[0]
        def tree(group,sig):calls.append(('killpg',group,sig));process.kill()
        def taskkill(argv,**kwargs):calls.append(('taskkill',argv,kwargs));process.kill();return SimpleNamespace(returncode=0)
        with patch.object(h.subprocess,'Popen',side_effect=overrides.get('spawn',lambda *a,**k:process)) as spawn,patch.object(h.subprocess,'run',side_effect=overrides.get('taskkill',taskkill)),patch.object(h.os,'killpg',side_effect=overrides.get('killpg',tree)),patch.object(h.time,'monotonic',side_effect=clock),patch.object(h.time,'sleep'),patch.object(h.signal,'getsignal',return_value=signal.SIG_DFL),patch.object(h.signal,'signal',side_effect=overrides.get('signal',lambda *a:None)),patch.dict(os.environ,{'SystemRoot':'C:/Windows'}):
            result=h.run(['PRIVATE-EXEC','PRIVATE-ARG'],cwd=root,output=root/'command',label='body',timeout=timeout,expected_exit=expected,platform=platform)
            return result,calls,spawn.call_args
    def test_normal_exit_proves_only_direct_process(self):
        with tempfile.TemporaryDirectory() as t:
            result,calls,spawn=self.invoke(Path(t),FakeProcess())
            self.assertTrue(result['directProcessTerminal']);self.assertFalse(result['descendantJoinProven']);self.assertNotIn('remainingOwned',result)
            self.assertEqual(calls,[]);self.assertTrue(spawn.kwargs['start_new_session']);self.assertFalse(spawn.kwargs['shell'])
    def test_expected_two_is_not_a_test_pass(self):
        with tempfile.TemporaryDirectory() as t:
            result,_,_=self.invoke(Path(t),FakeProcess(2),expected=2)
            self.assertEqual(result['exitCode'],2);self.assertNotIn('classification',result)
    def test_wrong_exit_preserved_full_private_graph(self):
        with tempfile.TemporaryDirectory() as t:
            with self.assertRaises(h.HostedCommandFailure) as e:self.invoke(Path(t),FakeProcess(7))
            self.assertIsInstance(e.exception.causes[0],ValueError);self.assertTrue(e.exception.outcome['directProcessTerminal'])
            raw=json.loads((Path(t)/'command/COMMAND.json').read_text());self.assertEqual(raw['argv'],['PRIVATE-EXEC','PRIVATE-ARG']);self.assertEqual(raw['errors'][0]['type'],'builtins.ValueError')
    def test_mac_timeout_attempts_group_cleanup_and_waits_direct_child(self):
        with tempfile.TemporaryDirectory() as t:
            process=FakeProcess(None)
            with self.assertRaises(h.HostedCommandFailure) as e:self.invoke(Path(t),process)
            self.assertIsInstance(e.exception.causes[0],h.CommandDeadline);self.assertTrue(e.exception.outcome['timedOut']);self.assertTrue(e.exception.outcome['directProcessTerminal']);self.assertFalse(e.exception.outcome['descendantJoinProven']);self.assertGreater(process.waits,0)
    def test_windows_timeout_bounded_taskkill_then_direct_wait(self):
        with tempfile.TemporaryDirectory() as t:
            process=FakeProcess(None);seen=[]
            def taskkill(argv,**kwargs):seen.append((argv,kwargs));process.kill();return SimpleNamespace(returncode=0)
            with self.assertRaises(h.HostedCommandFailure):self.invoke(Path(t),process,platform='win32',taskkill=taskkill)
            self.assertEqual(seen[0][0][1:],['/PID','4321','/T','/F']);self.assertEqual(seen[0][1]['timeout'],5);self.assertGreater(process.waits,0)
    def test_cleanup_failure_after_timeout_and_direct_kill_fallback(self):
        with tempfile.TemporaryDirectory() as t:
            process=FakeProcess(None)
            def fail(*a,**k):raise OSError('tree failure')
            with self.assertRaises(h.HostedCommandFailure) as e:self.invoke(Path(t),process,killpg=fail)
            self.assertIsInstance(e.exception.causes[0],h.CommandDeadline);self.assertTrue(any(isinstance(x,OSError) for x in e.exception.causes));self.assertGreater(process.kills,0)
    def test_signal_cancellation_is_never_expected_success(self):
        with tempfile.TemporaryDirectory() as t:
            handlers={};process=FakeProcess(None)
            def registration(n,fn):handlers[n]=fn
            def spawn(*a,**k):handlers[signal.SIGTERM](signal.SIGTERM,None);return process
            with self.assertRaises(h.HostedCommandFailure) as e:self.invoke(Path(t),process,spawn=spawn,signal=registration)
            self.assertTrue(e.exception.outcome['cancelled']);self.assertIsInstance(e.exception.causes[0],h.CancelledCommand)
    def test_spawn_failure_is_retained_without_fake_terminal(self):
        with tempfile.TemporaryDirectory() as t:
            def fail(*a,**k):raise OSError('spawn')
            with self.assertRaises(h.HostedCommandFailure) as e:self.invoke(Path(t),FakeProcess(),spawn=fail)
            self.assertFalse(e.exception.outcome['directProcessTerminal']);self.assertIsInstance(e.exception.causes[0],OSError)
    def test_existing_output_symlink_and_unknown_platform_rejected(self):
        with tempfile.TemporaryDirectory() as t:
            p=Path(t);(p/'command').mkdir()
            with self.assertRaises(FileExistsError):self.invoke(p,FakeProcess())
            with self.assertRaises(ValueError):h.run(['x'],cwd=p,output=p/'other',label='body',timeout=1,expected_exit=0,platform='linux')
    def test_report_failure_does_not_mask_body(self):
        with tempfile.TemporaryDirectory() as t:
            original=Path.open
            class BrokenWriter:
                def __init__(self,file):self.file=file
                def fileno(self):return self.file.fileno()
                def write(self,value):raise OSError('report write')
                def flush(self):self.file.flush()
                def close(self):self.file.close()
            def opened(path,*a,**k):
                real=original(path,*a,**k)
                return BrokenWriter(real) if path.name=='COMMAND.json' else real
            with patch.object(Path,'open',opened):
                with self.assertRaises(h.HostedCommandFailure) as e:self.invoke(Path(t),FakeProcess(7))
            self.assertIsInstance(e.exception.causes[0],ValueError);self.assertIsInstance(e.exception.causes[-1],OSError)
    def test_private_error_graph_group_cause_and_bad_formatter(self):
        class Bad(Exception):
            def __str__(self):raise RuntimeError('formatter')
        child=ValueError('original');root=BaseExceptionGroup('group',[child,Bad()]);root.__cause__=OSError('cause');formatting=[]
        graph=h.private_graph(root,formatting_errors=formatting)
        self.assertEqual(graph['exceptions'][0]['message'],'original');self.assertEqual(graph['cause']['message'],'cause');self.assertTrue(formatting)
    def test_invalid_budgets_and_labels_fail_before_spawn(self):
        with tempfile.TemporaryDirectory() as t:
            for timeout in (True,0,601):
                with self.assertRaises(ValueError):h.run(['x'],cwd=t,output=Path(t)/'x',label='body',timeout=timeout,expected_exit=0,platform='darwin')
            with self.assertRaises(ValueError):h.run(['x'],cwd=t,output=Path(t)/'x',label='../escape',timeout=1,expected_exit=0,platform='darwin')
    def test_poll_failure_never_masks_original_and_forces_cleanup(self):
        class PollFailure(FakeProcess):
            calls=0
            def poll(self):
                self.calls+=1
                if self.calls==1:raise OSError('poll failed')
                return self.code
        with tempfile.TemporaryDirectory() as t:
            process=PollFailure(None)
            with self.assertRaises(h.HostedCommandFailure) as e:self.invoke(Path(t),process)
            self.assertIsInstance(e.exception.causes[0],OSError);self.assertGreater(process.kills,0)
    def test_cancellation_at_terminal_boundary_cannot_pass(self):
        handlers={}
        class Boundary(FakeProcess):
            calls=0
            def poll(self):
                self.calls+=1
                if self.calls==1:handlers[signal.SIGTERM](signal.SIGTERM,None)
                return 0
        with tempfile.TemporaryDirectory() as t:
            with self.assertRaises(h.HostedCommandFailure) as e:self.invoke(Path(t),Boundary(),signal=lambda n,h:handlers.update({n:h}))
            self.assertTrue(e.exception.outcome['cancelled']);self.assertTrue(e.exception.outcome['directProcessTerminal'])
    def test_capture_replacement_rejects_after_primary_terminal(self):
        with tempfile.TemporaryDirectory() as t:
            root=Path(t);other=root/'other';other.write_text('private')
            def spawn(*args,**kwargs):
                target=Path(kwargs['stdout'].name);target.unlink();target.symlink_to(other);return FakeProcess()
            with self.assertRaises(h.HostedCommandFailure) as e:self.invoke(root,FakeProcess(),spawn=spawn)
            self.assertTrue(any(isinstance(x,ValueError) for x in e.exception.causes))
    def test_reparse_and_fifo_directory_guards_fail_closed(self):
        with tempfile.TemporaryDirectory() as t:
            root=Path(t);link=root/'link';link.symlink_to(root,target_is_directory=True)
            with self.assertRaises(ValueError):h.ordinary_directory(link)
            fifo=root/'fifo';os.mkfifo(fifo)
            with self.assertRaises(ValueError):h.ordinary_directory(fifo)
            info=SimpleNamespace(st_mode=0o40700,st_file_attributes=0x400)
            with patch.object(Path,'lstat',return_value=info):
                with self.assertRaises(ValueError):h.ordinary_directory(root)
    def test_windows_failed_tree_cleanup_falls_back_without_green(self):
        with tempfile.TemporaryDirectory() as t:
            process=FakeProcess(None)
            with self.assertRaises(h.HostedCommandFailure) as e:self.invoke(Path(t),process,platform='win32',taskkill=lambda *a,**k:SimpleNamespace(returncode=5))
            self.assertTrue(e.exception.outcome['timedOut']);self.assertTrue(e.exception.outcome['cleanupErrors']);self.assertTrue(e.exception.outcome['directProcessTerminal']);self.assertGreater(process.kills,0)
    def test_late_cancel_during_handler_restore_cannot_return_success(self):
        with tempfile.TemporaryDirectory() as t:
            installed={};delivered=[]
            def registration(number,handler):
                if callable(handler):installed[number]=handler
                elif not delivered:
                    delivered.append(True);installed[signal.SIGTERM](signal.SIGTERM,None)
            with self.assertRaises(h.HostedCommandFailure) as e:self.invoke(Path(t),FakeProcess(),signal=registration)
            self.assertTrue(e.exception.outcome['cancelled']);self.assertIsInstance(e.exception.causes[0],h.CancelledCommand)
            receipt=json.loads((Path(t)/'command/COMMAND.json').read_text());self.assertTrue(receipt['outcome']['cancelled'])
    def test_directory_replacement_never_writes_private_receipt_to_target(self):
        with tempfile.TemporaryDirectory() as t:
            root=Path(t);outside=root/'substitute';outside.mkdir()
            def spawn(*a,**k):
                command=root/'command';command.rename(root/'original');command.symlink_to(outside,target_is_directory=True);return FakeProcess(7)
            with self.assertRaises(h.HostedCommandFailure) as e:self.invoke(root,FakeProcess(),spawn=spawn)
            self.assertIsInstance(e.exception.causes[0],ValueError);self.assertFalse((outside/'COMMAND.json').exists())
            self.assertEqual((root/'original/COMMAND.json').read_text(),'')
    def test_receipt_replacement_never_writes_rejected_file(self):
        with tempfile.TemporaryDirectory() as t:
            root=Path(t);outside=root/'substitute';outside.write_text('untouched')
            def spawn(*a,**k):
                target=root/'command/COMMAND.json';target.unlink();target.symlink_to(outside);return FakeProcess(7)
            with self.assertRaises(h.HostedCommandFailure):self.invoke(root,FakeProcess(),spawn=spawn)
            self.assertEqual(outside.read_text(),'untouched')
    def test_receipt_open_failure_prevents_child_start(self):
        with tempfile.TemporaryDirectory() as t:
            original=Path.open;started=[]
            def opened(path,*a,**k):
                if path.name=='COMMAND.json':raise OSError('receipt open')
                return original(path,*a,**k)
            with patch.object(Path,'open',opened):
                with self.assertRaises(h.HostedCommandFailure) as e:self.invoke(Path(t),FakeProcess(),spawn=lambda *a,**k:started.append(True))
            self.assertEqual(started,[]);self.assertIsInstance(e.exception.causes[0],OSError)
    def test_receipt_close_failure_preserves_body_and_fails(self):
        with tempfile.TemporaryDirectory() as t:
            original=Path.open
            class BrokenClose:
                def __init__(self,file):self.file=file
                def fileno(self):return self.file.fileno()
                def write(self,v):return self.file.write(v)
                def flush(self):return self.file.flush()
                def close(self):self.file.close();raise OSError('receipt close')
            def opened(path,*a,**k):
                real=original(path,*a,**k);return BrokenClose(real) if path.name=='COMMAND.json' else real
            with patch.object(Path,'open',opened):
                with self.assertRaises(h.HostedCommandFailure) as e:self.invoke(Path(t),FakeProcess(7))
            self.assertIsInstance(e.exception.causes[0],ValueError);self.assertIsInstance(e.exception.causes[-1],OSError)
    def test_receipt_formatter_failure_preserves_actual_spawn_error(self):
        class Bad(Exception):
            def __str__(self):raise RuntimeError('formatter failed')
        original=Bad()
        def spawn(*a,**k):raise original
        with tempfile.TemporaryDirectory() as t:
            with self.assertRaises(h.HostedCommandFailure) as e:self.invoke(Path(t),FakeProcess(),spawn=spawn)
            self.assertIs(e.exception.causes[0],original);self.assertTrue(any(isinstance(x,RuntimeError) for x in e.exception.causes[1:]))
            receipt=json.loads((Path(t)/'command/COMMAND.json').read_text());self.assertTrue(receipt['errors'][0]['messageUnavailable'])


class Candidate(unittest.TestCase):
    def test_exact_fixture_and_central_upgrade(self):
        source=json.loads((candidate.PAYLOAD/'SOURCE.json').read_text());raw=(candidate.PAYLOAD/'package-preimage.props').read_bytes()
        after=candidate.central_after(raw,source);self.assertEqual(candidate.blob(after),source['centralAfterGitBlob']);self.assertEqual(candidate.sha(candidate.PAYLOAD/'ServerTerminalSignalObservationTests.cs'),'f2bf615cc01cab31a45382825138cf47aac5de7b87da3ccf8e38ba4a437e0d2a')
        for bad in (raw+b' ',after):
            with self.assertRaises(ValueError):candidate.central_after(bad,source)
    def test_real_unit_project_not_focused_adapter(self):
        source=json.loads((candidate.PAYLOAD/'SOURCE.json').read_text());pin=source['mainPins']['test/SharpLink.UnitTests/SharpLink.UnitTests.csproj']
        self.assertEqual(pin['gitBlob'],'b56e80738fbf240ef7f8ce8997fb68af74471457')
        self.assertEqual(source['mainPins']['test/SharpLink.UnitTests/GlobalUsings.cs']['gitBlob'],'e8f7c62511473c4cb4e79a70f30739c81ce0fc60')
        self.assertEqual(candidate.FIXTURE,'test/SharpLink.UnitTests/Server/ServerTerminalSignalObservationTests.cs')
        self.assertEqual(source['regressionExpectedCases'],{'regular':7,'explicit':1})
        self.assertEqual(source['mainPins']['src/SharpLink.Server/SharpLink.Server.csproj']['gitBlob'],'e552c01e2ead42052a76ea086e032e269167827e')

if __name__=='__main__':unittest.main(verbosity=2)
