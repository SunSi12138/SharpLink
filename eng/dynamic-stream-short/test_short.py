import ast
import copy
import hashlib
import json
import os
from pathlib import Path
import signal
import tempfile
import unittest
from unittest import mock
import xml.etree.ElementTree as ET
import prepare
import process_owner as owner
import owner_probe
import results as r
import run

KIT=Path(__file__).parent


def rows():
    return {'schemaVersion':1,'tests':[{'uid':'actual-'+m,'displayName':r.METHOD+'('+m+')',
        'type':{'namespace':r.NS,'typeName':r.CLASS,'methodName':r.METHOD,'methodArity':0,
                'parameterTypeFullNames':['System.String'],'returnTypeFullName':'System.Threading.Tasks.Task',
                'assemblyFullName':r.ASSEMBLY+', Version=2.0.4.0, Culture=neutral, PublicKeyToken=null'},
        'location':{'file':r.REPORTED,'lineStart':338,'lineEnd':362}} for m in r.ALL_MODES]}


def sample():
    row=rows()['tests'][3];summary=dict(total=1,passed=1,failed=0,skipped=0,cancelled=0,timedOut=0,flaky=0)
    test={'id':row['uid'],'displayName':row['displayName'],'methodName':r.METHOD,'className':r.CLASS,
          'filePath':r.REPORTED,'lineNumber':338,'endLineNumber':362,'status':'passed','durationMs':1}
    native={'schemaVersion':1,'assemblyName':r.ASSEMBLY,'tunitVersion':'1.73.19.0','runtimeVersion':'.NET 10.0.14',
        'publicationGeneration':'a'*32,'summary':summary,'groups':[{'namespace':r.NS,'className':r.CLASS,'summary':copy.deepcopy(summary),'tests':[test]}]}
    attr=dict(name=r.ASSEMBLY,tests='1',failures='0',errors='0',skipped='0');root=ET.Element('testsuites',attr)
    suite=ET.SubElement(root,'testsuite',attr);ET.SubElement(suite,'testcase',{'name':row['displayName'],'classname':r.CLASS})
    return native,root,row


class SourceControls(unittest.TestCase):
    def test_marker_has_no_stored_references(self):
        text=(KIT/'StreamExitStage.cs').read_text()
        self.assertNotIn('static readonly',text);self.assertNotIn('Task<',text)
        self.assertIn('Stopwatch.GetTimestamp()',text);self.assertIn('EntryPoint = "write"',text)
        self.assertNotIn('File.',text);self.assertNotIn('Console.',text)
    def test_transform_preserves_original_value_task_consumption(self):
        # These preimages are test inputs copied from exact main, not generated postimages.
        root=KIT/'test_preimage'
        for name in json.loads((KIT/'SOURCE.json').read_text())['instrumented']:
            old=(root/name).read_text();new=prepare.transform(name,old)
            without='\n'.join(line for line in new.split('\n') if 'StreamExitStage(' not in line)
            self.assertEqual(old,without)
            self.assertEqual(old.count('await using'),new.count('await using'))
            self.assertEqual(old.count('.DisposeAsync('),new.count('.DisposeAsync('))
            self.assertEqual(old.count('MoveNextAsync('),new.count('MoveNextAsync('))
    def test_independent_span(self):
        s=prepare.transform('RuntimeAssemblyIntegrationTests.RegistrationAndReplacement.cs',
            (KIT/'test_preimage/RuntimeAssemblyIntegrationTests.RegistrationAndReplacement.cs').read_text()).splitlines()
        self.assertEqual(s[337].strip(),'[Test]');self.assertIn('public async Task '+r.METHOD,s[344]);self.assertEqual(s[361],'    }')
        self.assertEqual(json.loads((KIT/'SOURCE.json').read_text())['span'],[338,362])
    def test_exact_preimage_required(self):
        with self.assertRaises(ValueError):prepare.replace_once('aa','a','b')
    def test_no_new_caller_cancellation(self):
        text=(KIT/'test_preimage/RuntimeAssemblyIntegrationTests.cs').read_text();new=prepare.transform('RuntimeAssemblyIntegrationTests.cs',text)
        self.assertEqual(text.count('CancellationToken.None'),new.count('CancellationToken.None'))
        self.assertEqual(text.count('cancellation.Cancel();'),new.count('cancellation.Cancel();'))
    def test_budget_and_scope(self):
        self.assertEqual(len(run.LIMITS),9);self.assertEqual(sum(s for _,s in run.LIMITS),1820)
        self.assertEqual(r.MODES,('consumer-break','normal','cancellation-mid-stream'))
        self.assertLess(1820+9*16,2100)
    def test_exact_uid_argv(self):
        args=run.report_args(Path('/dotnet'),Path('/dll'),'actual',Path('/report'))
        self.assertEqual(args.count('--filter-uid'),1);self.assertEqual(args[args.index('--filter-uid')+1],'actual')
        self.assertEqual(args[args.index('--timeout')+1],'120s');self.assertNotIn('--retry',args)


class SchemaControls(unittest.TestCase):
    def test_discovery_bijection(self):self.assertEqual(tuple(r.discovery(rows(),[338,362])),r.MODES)
    def test_discovery_rejections(self):
        mutations=[lambda d:d['tests'].pop(),lambda d:d['tests'].append(copy.deepcopy(d['tests'][0])),
            lambda d:d['tests'][0].update(uid=d['tests'][1]['uid']),lambda d:d['tests'][0].update(uid='--all'),
            lambda d:d['tests'][0]['location'].update(file='/unknown/'+r.REL),lambda d:d['tests'][0]['location'].update(lineStart=345),
            lambda d:d['tests'][0]['location'].update(lineEnd=True),lambda d:d['tests'][0].update(displayName=r.METHOD+'(other)'),
            lambda d:d['tests'][0]['type'].update(parameterTypeFullNames=[]),lambda d:d.update(schemaVersion=True)]
        for mutate in mutations:
            with self.subTest(mutate=mutate):
                d=rows();mutate(d)
                with self.assertRaises((ValueError,TypeError)):r.discovery(d,[338,362])
    def test_pass(self):
        n,x,row=sample();self.assertEqual(r.passed(n,ET.tostring(x,encoding='unicode'),row,set(),None)['runtime'],'.NET 10.0.14')
    def test_filter_never_authority(self):
        for value in (None,{},'unrelated',False):
            n,x,row=sample();n['filter']=value;r.passed(n,ET.tostring(x,encoding='unicode'),row,set(),None)
            n['groups'][0]['tests'][0]['id']='wrong'
            with self.assertRaises(ValueError):r.passed(n,ET.tostring(x,encoding='unicode'),row,set(),None)
    def test_native_negative_controls(self):
        mutations=[lambda n:n.update(tunitVersion='1.78.0.0'),lambda n:n.update(unobservedTaskExceptions=[]),
            lambda n:n['summary'].update(total=True),lambda n:n['summary'].update(skipped=1),
            lambda n:n['groups'][0]['tests'][0].update(id='wrong'),lambda n:n['groups'][0]['tests'][0].update(status='skipped'),
            lambda n:n['groups'][0]['tests'][0].update(endLineNumber=None),lambda n:n['groups'][0]['tests'][0].update(lineNumber=345),
            lambda n:n['groups'][0]['tests'][0].update(exception=None),lambda n:n['groups'][0]['tests'][0].update(skipReason=None),
            lambda n:n['groups'][0]['tests'][0].update(retryAttempt=0),lambda n:n['groups'][0]['tests'][0].update(attempts=[]),
            lambda n:n['groups'][0]['tests'][0].update(durationMs=True),lambda n:n['groups'].append(copy.deepcopy(n['groups'][0]))]
        for mutate in mutations:
            n,x,row=sample();mutate(n)
            with self.assertRaises(ValueError):r.passed(n,ET.tostring(x,encoding='unicode'),row,set(),None)
    def test_junit_negative_controls(self):
        for tag in ('error','failure','skipped','system-out','unknown'):
            n,x,row=sample();ET.SubElement(x[0][0],tag)
            with self.assertRaises(ValueError):r.passed(n,ET.tostring(x,encoding='unicode'),row,set(),None)
    def test_generation_runtime(self):
        n,x,row=sample()
        for seen,runtime in [({'a'*32},None),(set(),'.NET 10.0.2')]:
            with self.assertRaises(ValueError):r.passed(n,ET.tostring(x,encoding='unicode'),row,seen,runtime)
    def test_json_duplicate_nonfinite(self):
        with tempfile.TemporaryDirectory() as t:
            p=Path(t)/'x'
            for raw in ('{"a":1,"a":2}','{"a":NaN}'):
                p.write_text(raw)
                with self.assertRaises(ValueError):r.read_json(p)


class StageControls(unittest.TestCase):
    def test_complete_modes(self):
        for mode in r.MODES:
            ids=[10,11,12]+([20,21] if mode=='normal' else [30,31,50])+[51,60,61,70,71,72,73,79,90,91,92,93,94,95,100,101,102,103,104]
            raw=b''.join(f'{s} {i}\n'.encode() for i,s in enumerate(ids));r.stage_records(raw,True,mode)
    def test_incomplete_not_pass(self):
        raw=b'10 1\n11 2\n12 3\n30 4\n';self.assertEqual(r.stage_records(raw)[-1][0],30)
        with self.assertRaises(ValueError):r.stage_records(raw,True,'consumer-break')
    def test_stage_rejections(self):
        for raw in (b'plugin 1\n',b'999 1\n',b'10 2\n11 1\n',b'10 1 extra\n',b'10 1\n'*4000):
            with self.assertRaises(ValueError):r.stage_records(raw)
    def test_no_pass_without_gc_terminal(self):
        with self.assertRaises(ValueError):r.stage_records(b'104 1\n',True,'normal')


class FakeProcess:
    pid=99999999
    def __init__(self,*a,**kw):self.code=0
    def poll(self):return self.code
    def wait(self,timeout):return self.code


class OwnerControls(unittest.TestCase):
    def invoke(self,factory=FakeProcess,**patches):
        with tempfile.TemporaryDirectory() as t, mock.patch.object(owner.subprocess,'Popen',factory),mock.patch.object(owner,'process_table',return_value={}), mock.patch.object(owner.os,'pidfd_open',side_effect=lambda _:os.open('/dev/null',os.O_RDONLY)):
            return owner.run_owned('case',['fake'],Path(t),{},Path(t),1)
    def test_normal_mock(self):self.assertEqual(self.invoke()['remainingOwned'],{})
    def test_nonzero_not_green(self):
        class Failed(FakeProcess):
            def __init__(self,*a,**kw):self.code=2
        with self.assertRaises(owner.CommandFailure) as cm:self.invoke(Failed)
        self.assertEqual(cm.exception.outcome['exitCode'],2)
    def test_launch_failure_preserved(self):
        failure=RuntimeError('private')
        with self.assertRaises(owner.CommandFailure) as cm:self.invoke(mock.Mock(side_effect=failure))
        self.assertIs(cm.exception.causes[0],failure)
    def test_timeout_not_green_even_joined(self):
        class Hung(FakeProcess):
            def poll(self):return None
        ticks=iter(range(1000))
        with mock.patch.object(owner.time,'monotonic',side_effect=lambda:next(ticks)),mock.patch.object(owner.time,'sleep'):
            with self.assertRaises(owner.CommandFailure) as cm:self.invoke(Hung)
        self.assertTrue(cm.exception.outcome['timedOut']);self.assertEqual(cm.exception.outcome['remainingOwned'],{})
    def test_late_cancel_after_handler_restore(self):
        real=signal.signal; installed={};injected=False
        def setter(sig,handler):
            nonlocal injected
            if callable(handler) and getattr(handler,'__name__','')=='interrupted':installed[sig]=handler
            elif sig in installed and not injected:
                injected=True;installed[sig](sig,None)
            return real(sig,handler)
        with mock.patch.object(owner.signal,'signal',side_effect=setter):
            with self.assertRaises(owner.CommandFailure) as cm:self.invoke()
        self.assertTrue(cm.exception.outcome['cancelled'])
    def test_directory_replaced_no_external_receipt(self):
        with tempfile.TemporaryDirectory() as t:
            root=Path(t);logs=root/'logs';logs.mkdir();outside=root/'outside';outside.mkdir()
            class Replace(FakeProcess):
                def __init__(self,*a,**kw):
                    super().__init__();logs.rename(root/'moved');logs.symlink_to(outside,target_is_directory=True)
            with mock.patch.object(owner.subprocess,'Popen',Replace),mock.patch.object(owner,'process_table',return_value={}):
                with self.assertRaises(owner.CommandFailure):owner.run_owned('case',['fake'],root,{},logs,1)
            self.assertEqual(list(outside.iterdir()),[])
    def test_no_shell_inherited_only_stage_fd(self):
        def factory(*a,**kw):
            self.assertTrue(kw['start_new_session']);self.assertEqual(kw['pass_fds'],());self.assertNotIn('shell',kw)
            return FakeProcess()
        self.invoke(factory)
    def test_receipt_failure_cannot_replace_body(self):
        failure=RuntimeError('body');real=owner.write_json_fd;calls=0
        def writer(*args):
            nonlocal calls
            calls+=1
            if calls==2:raise OSError('receipt')
            return real(*args)
        with mock.patch.object(owner,'write_json_fd',side_effect=writer):
            with self.assertRaises(owner.CommandFailure) as cm:self.invoke(mock.Mock(side_effect=failure))
        self.assertIs(cm.exception.causes[0],failure);self.assertEqual(len(cm.exception.causes),2)

    def test_pid_reuse_is_not_signalled(self):
        old={424242:(os.getpid(),424242,'100','S')};reused={424242:(999,424242,'200','S')};calls=0
        def table():
            nonlocal calls
            calls+=1
            return old if calls<=2 else reused
        def pidfd(_pid):return os.open('/dev/null',os.O_RDONLY)
        with tempfile.TemporaryDirectory() as t, mock.patch.object(owner.subprocess,'Popen',FakeProcess), mock.patch.object(owner,'process_table',side_effect=table), mock.patch.object(owner.os,'pidfd_open',side_effect=pidfd), mock.patch.object(owner.signal,'pidfd_send_signal') as send:
            row=owner.run_owned('case',['fake'],Path(t),{},Path(t),1)
            self.assertEqual(row['remainingOwned'],{});send.assert_not_called()
    def test_parent_exit_with_adopted_escape_is_failure_even_joined(self):
        alive=True
        def table():return {424243:(os.getpid(),9999,'101','S')} if alive else {}
        def terminate(_fd,_sig):
            nonlocal alive
            alive=False
        with tempfile.TemporaryDirectory() as t, mock.patch.object(owner.subprocess,'Popen',FakeProcess), mock.patch.object(owner,'process_table',side_effect=table), mock.patch.object(owner.os,'pidfd_open',side_effect=lambda _:os.open('/dev/null',os.O_RDONLY)), mock.patch.object(owner.signal,'pidfd_send_signal',side_effect=terminate), mock.patch.object(owner.os,'waitpid',side_effect=ChildProcessError), mock.patch.object(owner.time,'sleep'):
            with self.assertRaises(owner.CommandFailure) as cm:owner.run_owned('case',['fake'],Path(t),{},Path(t),1)
        self.assertEqual(cm.exception.outcome['remainingOwned'],{})
        self.assertIn('OwnedProcessesAfterCommand',cm.exception.outcome['cleanupErrors'])
    def test_receipt_replaced_does_not_write_target(self):
        with tempfile.TemporaryDirectory() as t:
            root=Path(t);outside=root/'target';outside.write_text('unchanged')
            class Replace(FakeProcess):
                def __init__(self,*a,**kw):
                    super().__init__();(root/'COMMAND.json').unlink();(root/'COMMAND.json').symlink_to(outside)
            with mock.patch.object(owner.subprocess,'Popen',Replace),mock.patch.object(owner,'process_table',return_value={}):
                with self.assertRaises(owner.CommandFailure):owner.run_owned('case',['fake'],root,{},root,1)
            self.assertEqual(outside.read_text(),'unchanged')


class RevisedOwnerControls(unittest.TestCase):
    def test_census_and_signal_errors_do_not_skip_known_kill(self):
        alive=True;sent=[];ticks=iter(range(1000))
        class Live(FakeProcess):
            def poll(self):return None if alive else 0
        def send(fd,sig):
            nonlocal alive
            sent.append(sig)
            if sig==signal.SIGTERM:raise OSError('term failed')
            alive=False
        failure=PermissionError('proc unavailable')
        with tempfile.TemporaryDirectory() as t, mock.patch.object(owner.subprocess,'Popen',Live), mock.patch.object(owner,'process_table',side_effect=failure), mock.patch.object(owner.os,'pidfd_open',side_effect=lambda _:os.open('/dev/null',os.O_RDONLY)), mock.patch.object(owner,'pidfd_exited',side_effect=lambda _:not alive), mock.patch.object(owner.signal,'pidfd_send_signal',side_effect=send), mock.patch.object(owner.time,'monotonic',side_effect=lambda:next(ticks)), mock.patch.object(owner.time,'sleep'):
            with self.assertRaises(owner.CommandFailure) as cm:owner.run_owned('case',['fake'],Path(t),{},Path(t),1)
        self.assertIs(cm.exception.causes[0],failure);self.assertIn(signal.SIGKILL,sent)
        self.assertIsNone(cm.exception.outcome['remainingOwned']);self.assertIn('OSError',cm.exception.outcome['cleanupErrors'])
    def test_reap_error_still_attempts_kill(self):
        alive=True;sent=[];ticks=iter(range(1000))
        def table():return {424243:(os.getpid(),9999,'101','S')} if alive else {}
        def send(fd,sig):
            nonlocal alive
            sent.append(sig)
            if sig==signal.SIGKILL:alive=False
        with tempfile.TemporaryDirectory() as t, mock.patch.object(owner.subprocess,'Popen',FakeProcess), mock.patch.object(owner,'process_table',side_effect=table), mock.patch.object(owner.os,'pidfd_open',side_effect=lambda _:os.open('/dev/null',os.O_RDONLY)), mock.patch.object(owner,'pidfd_exited',side_effect=lambda _:not alive), mock.patch.object(owner.signal,'pidfd_send_signal',side_effect=send), mock.patch.object(owner.os,'waitpid',side_effect=OSError('reap')), mock.patch.object(owner.time,'monotonic',side_effect=lambda:next(ticks)), mock.patch.object(owner.time,'sleep'):
            with self.assertRaises(owner.CommandFailure) as cm:owner.run_owned('case',['fake'],Path(t),{},Path(t),1)
        self.assertIn(signal.SIGKILL,sent);self.assertIn('OSError',cm.exception.outcome['cleanupErrors'])


class RevisionControls(unittest.TestCase):
    def test_complete_last_newline_required(self):
        ids=[10,11,12,20,21,51,60,61,70,71,72,73,79,90,91,92,93,94,95,100,103,104]
        raw=''.join(str(x)+' 1\n' for x in ids).encode()
        r.stage_records(raw,True,'normal')
        with self.assertRaises(ValueError):r.stage_records(raw[:-1],True,'normal')
    def test_single_publication_inventory(self):
        rows={'.':('dir',),'session.html':('file',),'session.tunit-report.json':('file',),'junit.xml':('file',)}
        r.report_inventory(rows)
        for extra in ('other.tunit-report.json','other.xml','sub/session.tunit-report.json'):
            with self.assertRaises(ValueError):r.report_inventory(dict(rows,**{extra:('file',)}))
    def test_deleted_empty_root_rejected(self):
        with tempfile.TemporaryDirectory() as t:
            root=Path(t)/'root';root.mkdir();run.snapshot(root);root.rmdir()
            with self.assertRaises(FileNotFoundError):run.snapshot(root)
    def test_replaced_report_cache_pipe_roots_rejected_before_read(self):
        for name in ('work','source','cache','report','logs','pipe'):
            with self.subTest(name=name), tempfile.TemporaryDirectory() as t:
                base=Path(t);root=base/name;root.mkdir();identity=run.directory_identity(root)
                root.rename(base/'old');outside=base/'outside';outside.mkdir();root.symlink_to(outside,target_is_directory=True)
                with mock.patch.object(run,'read_regular') as read:
                    with self.assertRaises(ValueError):run.directory_identity(root,identity)
                    with self.assertRaises(ValueError):run.snapshot(root)
                    read.assert_not_called()
    def test_ordinary_replacement_changes_empty_snapshot(self):
        with tempfile.TemporaryDirectory() as t:
            root=Path(t)/'root';root.mkdir();before=run.snapshot(root);root.rename(Path(t)/'old');root.mkdir()
            self.assertNotEqual(before,run.snapshot(root))
    def test_report_mkdir_failure_never_acquires_stage_fd(self):
        with tempfile.TemporaryDirectory() as t:
            root=Path(t);report=root/'report';report.mkdir()
            with mock.patch.object(run.owner,'open_exclusive') as acquire:
                with self.assertRaises(BaseExceptionGroup):run.execute_body_with_stage(1,root/'stages',report,{},lambda _:None)
                acquire.assert_not_called()
    def test_stage_body_and_close_errors_retained(self):
        with tempfile.TemporaryDirectory() as t:
            root=Path(t);dfd=os.open(root,os.O_RDONLY|os.O_DIRECTORY);body=RuntimeError('body');close=os.close
            def closing(value):close(value);raise OSError('closing')
            try:
                with mock.patch.object(run.os,'close',side_effect=closing):
                    with self.assertRaises(BaseExceptionGroup) as cm:run.execute_body_with_stage(dfd,root/'stages',root/'report',{},mock.Mock(side_effect=body))
                self.assertIs(cm.exception.exceptions[0],body);self.assertEqual(len(cm.exception.exceptions),2)
            finally:close(dfd)
    def test_preflight_failure_closes_final_and_directory(self):
        original=run.r.read_json
        def inputs(path):return {'code':{}} if path.name=='CI.json' else original(path)
        with tempfile.TemporaryDirectory() as t:
            root=Path(t)/'root';root.mkdir();work=Path(t)/'work';work.mkdir();closed=[];close=os.close
            def closing(fd):closed.append(fd);close(fd)
            with mock.patch.object(run.r,'read_json',side_effect=inputs),mock.patch.object(run.owner,'enable_subreaper'),mock.patch.object(run.tempfile,'mkdtemp',return_value=str(work)),mock.patch.object(run.shutil,'which',return_value=None),mock.patch.object(run.os,'close',side_effect=closing),mock.patch('builtins.print'):
                with self.assertRaises(SystemExit) as cm:run.main(root)
            self.assertEqual(cm.exception.code,1);self.assertEqual(len(closed),2)
            data=json.loads((work/'FINAL.json').read_text());self.assertEqual(data['classification'],'SHORT_DIAGNOSTIC_FAILED')
            self.assertTrue(data['errors']['orderedRootOccurrences'])
    def test_final_replacement_never_writes_external_target(self):
        original=run.r.read_json
        def inputs(path):return {'code':{}} if path.name=='CI.json' else original(path)
        with tempfile.TemporaryDirectory() as t:
            root=Path(t)/'root';root.mkdir();work=Path(t)/'work';work.mkdir();outside=Path(t)/'outside';outside.write_text('unchanged')
            def missing(_):
                (work/'FINAL.json').rename(work/'original-final');(work/'FINAL.json').symlink_to(outside)
                return None
            with mock.patch.object(run.r,'read_json',side_effect=inputs),mock.patch.object(run.owner,'enable_subreaper'),mock.patch.object(run.tempfile,'mkdtemp',return_value=str(work)),mock.patch.object(run.shutil,'which',side_effect=missing),mock.patch('builtins.print'):
                with self.assertRaises(SystemExit) as cm:run.main(root)
            self.assertEqual(cm.exception.code,1);self.assertEqual(outside.read_text(),'unchanged')
            self.assertEqual((work/'original-final').read_bytes(),b'')
    def test_git_executable_flag_matches(self):
        with tempfile.TemporaryDirectory() as t:
            root=Path(t);file=root/'x';file.write_bytes(b'x');file.chmod(0o600)
            blob=hashlib.sha1(b'blob 1\0x').hexdigest()
            run.verify_tree(root,('100644 blob '+blob+'\tx\0').encode())
            with self.assertRaises(ValueError):run.verify_tree(root,('100755 blob '+blob+'\tx\0').encode())
    def test_daemons_explicitly_disabled(self):
        with tempfile.TemporaryDirectory() as t:
            env=run.child_environment(Path(t),Path('/dotnet/dotnet'))
            self.assertEqual(env['MSBUILDDISABLENODEREUSE'],'1');self.assertEqual(env['DOTNET_CLI_USE_MSBUILD_SERVER'],'0')


class StageIdentityControls(unittest.TestCase):
    def test_original_stage_identity_retained_and_fd_closed(self):
        with tempfile.TemporaryDirectory() as t:
            root=Path(t);dfd=os.open(root,os.O_RDONLY|os.O_DIRECTORY);sources={};fds=[]
            def operation(fd):fds.append(fd);os.write(fd,b'10 1\n')
            try:
                run.execute_body_with_stage(dfd,root/'stages',root/'report',{},operation,sources)
                self.assertEqual(sources[root/'stages'],run.file_identity(root/'stages'))
                with self.assertRaises(OSError):os.fstat(fds[0])
            finally:os.close(dfd)
    def test_replacement_copy_symlink_hardlink_rejected(self):
        for replacement in ('ordinary-copy','symlink','hardlink'):
            with self.subTest(replacement=replacement),tempfile.TemporaryDirectory() as t:
                root=Path(t);dfd=os.open(root,os.O_RDONLY|os.O_DIRECTORY);sources={};fds=[]
                def operation(fd):
                    fds.append(fd);os.write(fd,b'10 1\n');(root/'stages').rename(root/'original')
                    if replacement=='ordinary-copy':(root/'stages').write_bytes((root/'original').read_bytes())
                    elif replacement=='symlink':(root/'stages').symlink_to(root/'original')
                    else:os.link(root/'original',root/'stages')
                try:
                    with self.assertRaises(BaseExceptionGroup):run.execute_body_with_stage(dfd,root/'stages',root/'report',{},operation,sources)
                    original=(root/'original').stat();self.assertEqual(sources[root/'stages'][:2],(original.st_dev,original.st_ino))
                    with self.assertRaises(OSError):os.fstat(fds[0])
                finally:os.close(dfd)
    def test_identity_failure_does_not_replace_body(self):
        with tempfile.TemporaryDirectory() as t:
            root=Path(t);dfd=os.open(root,os.O_RDONLY|os.O_DIRECTORY);body=RuntimeError('body')
            def operation(fd):(root/'stages').unlink();raise body
            try:
                with self.assertRaises(BaseExceptionGroup) as cm:run.execute_body_with_stage(dfd,root/'stages',root/'report',{},operation,{})
                self.assertIs(cm.exception.exceptions[0],body);self.assertEqual(len(cm.exception.exceptions),2)
            finally:os.close(dfd)
    def test_owner_failure_summary_private_values_not_emitted(self):
        row=dict(stage='consumer-break',argv=['PRIVATE'],exitCode=-9,timedOut=True,cancelled=False,errors=['PRIVATE'],cleanupErrors=['PRIVATE'],reportingErrors=[],remainingOwned=None)
        summary=run.owner_failure_summary(owner.CommandFailure(row,[]),'consumer-break')
        self.assertNotIn('PRIVATE',json.dumps(summary));self.assertIsNone(summary['remainingOwnedEmpty']);self.assertEqual(summary['exit'],-9)
        row['remainingOwned']={7:'PRIVATE'};self.assertFalse(run.owner_failure_summary(owner.CommandFailure(row,[]),'consumer-break')['remainingOwnedEmpty'])
        row['remainingOwned']={};self.assertTrue(run.owner_failure_summary(owner.CommandFailure(row,[]),'consumer-break')['remainingOwnedEmpty'])
    def test_owner_failure_summary_invalid_types_rejected(self):
        base=dict(stage='consumer-break',exitCode=2,timedOut=False,cancelled=False,errors=[],cleanupErrors=[],reportingErrors=[],remainingOwned={})
        for key,value in (('exitCode',True),('timedOut',1),('cancelled',None),('remainingOwned',[]),('errors',{}),('stage','PRIVATE')):
            row=dict(base,**{key:value})
            with self.assertRaises(ValueError):run.owner_failure_summary(owner.CommandFailure(row,[]),'consumer-break')
    def test_safe_summary_failure_follows_original(self):
        row=dict(stage='consumer-break',exitCode=2,timedOut=False,cancelled=False,errors=[],cleanupErrors=[],reportingErrors=[],remainingOwned={})
        original=owner.CommandFailure(row,[]);reporting=OSError('private console')
        with mock.patch('builtins.print',side_effect=reporting):
            with self.assertRaises(BaseExceptionGroup) as cm:run.report_owner_failure(original,'consumer-break')
        self.assertIs(cm.exception.exceptions[0],original);self.assertIs(cm.exception.exceptions[1],reporting)
    def test_safe_summary_success_still_raises_original(self):
        row=dict(stage='consumer-break',exitCode=2,timedOut=False,cancelled=False,errors=[],cleanupErrors=[],reportingErrors=[],remainingOwned={})
        original=owner.CommandFailure(row,[])
        with mock.patch('builtins.print'):
            with self.assertRaises(owner.CommandFailure) as cm:run.report_owner_failure(original,'consumer-break')
        self.assertIs(cm.exception,original)
    def test_probe_readiness_precedes_parent_exit(self):
        source=Path(owner_probe.__file__).read_text()
        self.assertIn("signal.signal(signal.SIGTERM,signal.SIG_IGN);os.write(w,b'R')",source)
        self.assertIn("assert os.read(r,1)==b'R';os.close(r);os._exit(0)",source)


class FailureDiagnosticsControls(unittest.TestCase):
    def context(self,phase='pre-guard',row=None):return dict(stage='discovery',phase=phase,ownerInvoked=row is not None,terminal=row)
    def row(self):return dict(stage='discovery',exitCode=0,timedOut=False,cancelled=False,errors=[],cleanupErrors=[],reportingErrors=[],remainingOwned={})
    def test_fixed_reason_and_unknown_private_message(self):
        private='DO_NOT_LEAK argv=/private/token'
        out=run.failure_diagnostic([ValueError('Readonly checkout/output changed'),ValueError(private)],self.context(),[],set())
        self.assertEqual(out['leaves'][0]['fixedReason'],'Readonly checkout/output changed')
        self.assertNotIn('fixedReason',out['leaves'][1]);self.assertNotIn(private,json.dumps(out))
    def test_pre_guard_does_not_claim_owner_invocation(self):
        out=run.failure_diagnostic([ValueError('Prior evidence changed')],self.context(),[],set())
        self.assertFalse(out['ownerInvoked']);self.assertIsNone(out['terminal']);self.assertEqual(out['phase'],'pre-guard')
    def test_post_guard_keeps_actual_successful_owner(self):
        out=run.failure_diagnostic([ValueError('Readonly cache changed')],self.context('post-guard',self.row()),[],set())
        self.assertTrue(out['ownerInvoked']);self.assertEqual(out['terminal']['exit'],0);self.assertTrue(out['terminal']['remainingOwnedEmpty'])
    def test_unknown_remaining_stays_unknown(self):
        row=self.row();row['remainingOwned']=None
        self.assertIsNone(run.failure_diagnostic([],self.context('owner-invoked',row),[],set())['terminal']['remainingOwnedEmpty'])
    def test_invalid_terminal_never_prints_raw(self):
        for key,value in (('stage','SECRET'),('exitCode',True),('timedOut',1),('errors','SECRET'),('remainingOwned',[])):
            row=self.row();row[key]=value
            with self.assertRaises(ValueError):run.failure_diagnostic([],self.context('post-guard',row),[],set())
    def test_snapshot_known_path_and_unknown_hash_only(self):
        a={'known.cs':('file','a'*64,0o600),'SECRET/path':('file','b'*64,0o600)};b={'known.cs':('file','c'*64,0o600)}
        out=run.safe_snapshot_diff('checkout',a,b,{'known.cs'})
        self.assertEqual((out['added'],out['changed']), (1,1));self.assertNotIn('SECRET',json.dumps(out))
        self.assertEqual(next(x for x in out['entries'] if x['kind']=='changed')['knownRelative'],'known.cs')
        self.assertNotIn('knownRelative',run.safe_snapshot_diff('cache',a,b,{'known.cs'})['entries'][0])
    def test_snapshot_bound_and_no_extra_reads(self):
        a={'private-'+str(i):('file','a'*64,0o600) for i in range(20)}
        with mock.patch.object(Path,'read_bytes',side_effect=AssertionError('No diagnostic reads')):
            out=run.safe_snapshot_diff('cache',a,{},set())
        self.assertEqual(out['added'],20);self.assertEqual(len(out['entries']),8);self.assertTrue(out['truncated'])
    def test_snapshot_wrapper_preserves_original_predicate(self):
        evidence=[];run.require_snapshot({'a':1},{'a':1},'Readonly cache changed','cache',evidence);self.assertEqual(evidence,[])
        with self.assertRaises(ValueError) as cm:run.require_snapshot({'a':1},{'b':2},'Readonly cache changed','cache',evidence)
        self.assertEqual(cm.exception.args,('Readonly cache changed',));self.assertEqual(len(evidence),1)
    def test_summary_console_failure_appended_after_original(self):
        original=ValueError('Readonly cache changed');reporting=OSError('private');errors=[original]
        with mock.patch('builtins.print',side_effect=reporting):run.emit_failure_diagnostic(errors,self.context(),[],set())
        self.assertEqual(errors,[original,reporting])
    def test_nested_leaves_bounded_without_messages(self):
        errors=[ExceptionGroup('SECRET',[ValueError('SECRET') for _ in range(20)])]
        out=run.failure_diagnostic(errors,self.context(),[],set());self.assertEqual(len(out['leaves']),8);self.assertTrue(out['leavesTruncated']);self.assertNotIn('SECRET',json.dumps(out))


class DiscoveryOutputControls(unittest.TestCase):
    def test_exact_discovery_argv_only_new_output_option(self):
        args=run.discovery_args(Path('/dotnet'),Path('/dll'),Path('/private/discovery-results'))
        self.assertEqual(args[:-2],[Path('/dotnet'),Path('/dll'),'--list-tests','json','--treenode-filter',run.METHOD_FILTER])
        self.assertEqual(args[-2:],['--results-directory',Path('/private/discovery-results')]);self.assertEqual(args.count('--results-directory'),1)
    def test_owned_output_outside_checkout_preserves_source_seal(self):
        with tempfile.TemporaryDirectory() as t:
            source=Path(t)/'source';source.mkdir();work=Path(t)/'work';work.mkdir();roots={p:run.directory_identity(p) for p in (work,source)};before=run.snapshot(source)
            output=run.prepare_discovery_results(work,source,roots)
            self.assertFalse(output.is_relative_to(source));self.assertEqual(run.snapshot(source),before)
            self.assertEqual(roots[output],run.directory_identity(output))
    def test_original_bin_testresults_still_rejected(self):
        with tempfile.TemporaryDirectory() as t:
            source=Path(t);(source/'test/SharpLink.IntegrationTests/bin/Release/net10.0').mkdir(parents=True);before=run.snapshot(source)
            (source/'test/SharpLink.IntegrationTests/bin/Release/net10.0/TestResults').mkdir()
            with self.assertRaises(ValueError):run.require_snapshot(run.snapshot(source),before,'Readonly checkout/output changed','checkout',[])
    def test_output_under_checkout_is_rejected(self):
        with tempfile.TemporaryDirectory() as t:
            source=Path(t);work=source/'work';work.mkdir();roots={p:run.directory_identity(p) for p in (source,work)}
            with self.assertRaises(ValueError):run.prepare_discovery_results(work,source,roots)
            self.assertFalse((work/'discovery-results').exists())
    def test_existing_or_symlink_output_is_rejected(self):
        for link in (False,True):
            with self.subTest(link=link),tempfile.TemporaryDirectory() as t:
                source=Path(t)/'source';source.mkdir();work=Path(t)/'work';work.mkdir();roots={p:run.directory_identity(p) for p in (source,work)}
                output=work/'discovery-results'
                if link:output.symlink_to(source,target_is_directory=True)
                else:output.mkdir()
                with self.assertRaises(FileExistsError):run.prepare_discovery_results(work,source,roots)
    def test_completed_output_mutation_rejected(self):
        with tempfile.TemporaryDirectory() as t:
            output=Path(t);sealed=run.snapshot(output);(output/'extra').write_bytes(b'x')
            with self.assertRaises(ValueError):run.require_snapshot(run.snapshot(output),sealed,'Prior evidence changed','prior-evidence',[])
    def test_added_default_directory_hash_is_exact(self):
        self.assertEqual(hashlib.sha256(b'test/SharpLink.IntegrationTests/bin/Release/net10.0/TestResults').hexdigest(),'cf1fbd93720962dd9c8ed79976f9839e65c3ce21cb2268e89302d75d9941912d')


class ProbeControls(unittest.TestCase):
    def outcomes(self):
        def row(timeout):return dict(remainingOwned={},exitCode=-15 if timeout else 0,cancelled=False,reportingErrors=[],cleanupErrors=['OwnedProcessesAfterCommand'],errors=['TimeoutError'] if timeout else [],timedOut=timeout)
        return [None,owner.CommandFailure(row(True),[TimeoutError()]),owner.CommandFailure(row(False),[])]
    def invoke(self,outcomes):
        with tempfile.TemporaryDirectory() as t, mock.patch.object(owner_probe.tempfile,'mkdtemp',return_value=t), mock.patch.object(owner_probe.p,'enable_subreaper'), mock.patch.object(owner_probe.p,'run_owned',side_effect=outcomes), mock.patch('builtins.print'):
            owner_probe.main()
    def test_expected_probe_shapes(self):self.invoke(self.outcomes())
    def test_probe_unexpected_report_error_rejected(self):
        outcomes=self.outcomes();outcomes[1].outcome['reportingErrors']=['OSError']
        with self.assertRaises(ValueError):self.invoke(outcomes)
    def test_probe_does_not_inherit_arbitrary_environment(self):
        with tempfile.TemporaryDirectory() as t, mock.patch.dict(os.environ,{'PRIVATE_SENTINEL':'do-not-copy'}), mock.patch.object(owner_probe.tempfile,'mkdtemp',return_value=t), mock.patch.object(owner_probe.p,'enable_subreaper'), mock.patch.object(owner_probe.p,'run_owned',side_effect=self.outcomes()) as execute, mock.patch('builtins.print'):
            owner_probe.main()
            for call in execute.call_args_list:self.assertNotIn('PRIVATE_SENTINEL',call.args[3])
    def test_probe_unjoined_or_green_rejected(self):
        outcomes=self.outcomes();outcomes[2].outcome['remainingOwned']={123:[]}
        with self.assertRaises(ValueError):self.invoke(outcomes)
        with self.assertRaises(ValueError):self.invoke([None,None,None])


class FilesystemControls(unittest.TestCase):
    def test_only_explicit_checkout_root_git_ignored(self):
        with tempfile.TemporaryDirectory() as t:
            root=Path(t);(root/'.git').mkdir();(root/'.git/x').write_text('x')
            self.assertIn('.git/x',run.snapshot(root))
            self.assertNotIn('.git/x',run.snapshot(root,checkout=True))
            before=run.snapshot(root);(root/'.git/x').write_text('changed')
            self.assertNotEqual(before,run.snapshot(root))
    def test_root_git_symlink_rejected_even_checkout(self):
        with tempfile.TemporaryDirectory() as t:
            root=Path(t);(root/'target').mkdir();(root/'.git').symlink_to(root/'target',target_is_directory=True)
            for checkout in (False,True):
                with self.assertRaises(ValueError):run.snapshot(root,checkout=checkout)

    def test_nested_git_not_ignored(self):
        with tempfile.TemporaryDirectory() as t:
            root=Path(t);(root/'reports/.git').mkdir(parents=True);(root/'reports/.git/x').write_text('x')
            self.assertIn('reports/.git/x',run.snapshot(root))
    def test_fifo_read_is_nonblocking_rejection(self):
        with tempfile.TemporaryDirectory() as t:
            path=Path(t)/'fifo';os.mkfifo(path)
            with self.assertRaises(BaseExceptionGroup):run.read_regular(path)
    def test_symlink_read_rejected(self):
        with tempfile.TemporaryDirectory() as t:
            root=Path(t);(root/'target').write_text('private');(root/'link').symlink_to(root/'target')
            with self.assertRaises(OSError):run.read_regular(root/'link')
    def test_special_snapshot_rejected(self):
        with tempfile.TemporaryDirectory() as t:
            root=Path(t);os.mkfifo(root/'fifo')
            with self.assertRaises(ValueError):run.snapshot(root)
    def test_read_and_close_errors_both_retained(self):
        with tempfile.TemporaryDirectory() as t:
            path=Path(t)/'x';path.write_text('x');real_close=os.close
            def close(fd):real_close(fd);raise OSError('close')
            with mock.patch.object(run.os,'read',side_effect=OSError('read')),mock.patch.object(run.os,'close',side_effect=close):
                with self.assertRaises(BaseExceptionGroup) as cm:run.read_regular(path)
            self.assertEqual(len(cm.exception.exceptions),2)


if __name__=='__main__':unittest.main()
