#!/usr/bin/env python3
"""Pure control tests; never execute .NET, collectors, sockets or runtime tests."""
import copy
import contextlib
import io
import os
import signal
import sys
import json
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch, MagicMock
import run as gate


def fixture(uid, phase):
    negative = phase == 'baseline-tcp'
    badflags = phase == 'baseline-flags' and uid == gate.FLAGS_UIDS[0]
    failed = negative or badflags
    counters = dict(total=1, passed=0 if failed else 1, failed=1 if failed else 0, skipped=0, cancelled=0, timedOut=0, flaky=0)
    test = dict(id=uid, status='error' if failed else 'passed', output='')
    if failed:
        test['exception'] = dict(type='System.Exception' if badflags else 'System.InvalidOperationException',
            message=gate.FLAGS_ERROR if badflags else gate.NEGATIVE,
            stackTrace='   at ' + (gate.FLAGS_STACK if badflags else gate.TCP_STACK) + ') in fixture.cs:line 100')
    if phase != 'baseline-flags':
        record = dict(schema=1, earlyBreak=uid == gate.TCP_UIDS[1], runtime='10.0.14', producerAwaitRegistered=True,
            consumerCompleted=True, inputIsConnectionToken=negative, inputCanBeCanceled=True, enumeratorCanBeCanceled=True, methodCalls=1, enumeratorCalls=1, inputCancelledBeforeRelease=not negative, enumeratorCancelledBeforeRelease=not negative, observationMilliseconds=10000,
            observationCompleted=not negative, elapsedMilliseconds=10001 if negative else 3,
            sameConnection=True, releaseWasWithheld=True, teardownComplete=True,
            before=dict(cancellation=0 if negative else 1, moveCompleted=0 if negative else 1, disposed=0 if negative else 1, overlap=0),
            after=dict(cancellation=0 if negative else 1, moveCompleted=1, disposed=1, overlap=0),
            verdict='connection-token-baseline' if negative else 'cancelled-candidate')
        test['output'] = gate.RECORD + json.dumps(record)
    return dict(schemaVersion=1, assemblyName='SharpLink.UnitTests' if phase == 'baseline-flags' else 'SharpLink.IntegrationTests',
        tunitVersion='1.73.19.0', runtimeVersion='.NET 10.0.14', publicationGeneration='a' * 32,
        summary=counters.copy(), groups=[dict(summary=counters.copy(), tests=[test])]), 2 if failed else 0


class ReportTests(unittest.TestCase):
    def validate(self, report, uid=gate.TCP_UIDS[0], phase='baseline-tcp', code=2, timeout=False):
        gate.validate_report(report, uid, phase, code, timeout)


    def test_frozen_workflow_trigger_and_always_exact_report_upload(self):
        workflow = (Path(__file__).resolve().parents[2] / '.github/workflows/static-cancel-causal-validation.yml').read_text()
        self.assertIn("branches: ['validation/static-cancel-causal-diag-20261030']", workflow)
        self.assertIn("paths: ['eng/static-cancel-causal-validation/request.json']", workflow)
        self.assertIn("ref: ${{ github.sha }}", workflow)
        self.assertIn('--release-root release --baseline-root baseline --candidate-root candidate', workflow)
        self.assertNotIn('workflow_dispatch:', workflow)
        for forbidden in ['actions/cache', 'upload-sarif', 'GITHUB_STEP_SUMMARY']:
            self.assertNotIn(forbidden, workflow)
        self.assertIn('if: ${{ always() }}', workflow)
        self.assertIn('actions/upload-artifact@cf430e030ddbb5b0abf93d22962f4752f3646cd9', workflow)
        self.assertIn('retention-days: 3', workflow)
        self.assertIn('if-no-files-found: error', workflow)
        self.assertIn('overwrite: false', workflow)
        self.assertEqual(workflow.count('persist-credentials: false'), 3)
        self.assertEqual(workflow.count('sha512sum --check'), 1)
        self.assertNotIn(gate.PINS['request'], gate.PINS['tool_files'])
        for path,blob in gate.PINS['arms']['candidate']['fixtures'].items():
            if path.endswith('CausalValidationTests.cs'):
                self.assertNotEqual(gate.PINS['arms']['baseline']['fixtures'][path],blob)
            else:
                self.assertEqual(gate.PINS['arms']['baseline']['fixtures'][path],blob)

    def test_every_execution_preserves_exit_timeout_and_launch_failure(self):
        with tempfile.TemporaryDirectory() as directory:
            root=Path(directory);log=root/'worker.log'
            with patch.object(gate.hosts,'execute',return_value=(-9,True)):
                self.assertEqual(gate.execute_recorded(['fake'],root,{},180,log),(-9,True))
            result=json.loads((root/'worker.log.outcome.json').read_text())
            self.assertEqual(result,dict(argv=['fake'],cwd=str(root),timeout_seconds=180,exit_code=-9,timed_out=True))
            with patch.object(gate.hosts,'execute',side_effect=OSError('controlled launch failure')):
                with self.assertRaises(OSError):gate.execute_recorded(['fake'],root,{},180,root/'launch.log')
            self.assertIn('controlled launch failure',(root/'launch.log.outcome.json').read_text())

    def test_exact_ten_expected_outcomes(self):
        for phase, uids in [('baseline-flags', gate.FLAGS_UIDS), ('baseline-tcp', gate.TCP_UIDS), ('candidate-tcp', gate.TCP_UIDS)]:
            for uid in uids:
                report, code = fixture(uid, phase)
                self.validate(report, uid, phase, code)

    def test_process_timeout_crash_pass_or_arbitrary_failure_rejected(self):
        r, _ = fixture(gate.TCP_UIDS[0], 'baseline-tcp')
        for code in [0, -9, 124, 139]:
            with self.assertRaises(ValueError): self.validate(r, code=code)
        with self.assertRaises(ValueError): self.validate(r, timeout=True)
        for key, value in [('message', 'timeout'), ('message', gate.NEGATIVE+' '), ('type', 'TimeoutException'), ('type', 'InvalidOperationException'), ('stackTrace', 'other'), ('innerException', {})]:
            q=copy.deepcopy(r);q['groups'][0]['tests'][0]['exception'][key]=value
            with self.assertRaises(ValueError): self.validate(q)

    def test_report_identity_counters_uid_retries_and_publication_fail_closed(self):
        r, _ = fixture(gate.TCP_UIDS[0], 'baseline-tcp')
        for key,value in [('schemaVersion', True), ('assemblyName','Other'), ('tunitVersion','old'), ('runtimeVersion','.NET 10.0.2'), ('publicationGeneration','')]:
            q=copy.deepcopy(r);q[key]=value
            with self.assertRaises(ValueError):self.validate(q)
        for location in ['summary','group']:
            for key,value in [('total',2),('passed',1),('failed',0),('skipped',1),('cancelled',1),('timedOut',1),('flaky',1),('failed',True)]:
                q=copy.deepcopy(r);summary=q['summary'] if location=='summary' else q['groups'][0]['summary'];summary[key]=value
                with self.assertRaises(ValueError):self.validate(q)
        for key,value in [('id','wrong'),('status','passed'),('status','failed'),('retryAttempt',1),('attempts',[{}])]:
            q=copy.deepcopy(r);q['groups'][0]['tests'][0][key]=value
            with self.assertRaises(ValueError):self.validate(q)
        q=copy.deepcopy(r);q['groups'][0]['tests'].append(q['groups'][0]['tests'][0])
        with self.assertRaises(ValueError):self.validate(q)

    def test_causal_prerequisites_and_physical_before_after_fail_closed(self):
        r,_=fixture(gate.TCP_UIDS[0],'baseline-tcp');t=r['groups'][0]['tests'][0];record=json.loads(t['output'][len(gate.RECORD):])
        mutations=[('schema',True),('earlyBreak',True),('runtime','10.0.2'),('producerAwaitRegistered',False),('consumerCompleted',False),
            ('inputIsConnectionToken',False),('inputCancelledBeforeRelease',True),('enumeratorCancelledBeforeRelease',1),('inputCanBeCanceled',None),('enumeratorCanBeCanceled','true'),('methodCalls',2),('enumeratorCalls',True),('observationMilliseconds',9999),('observationCompleted',True),('elapsedMilliseconds',9999),
            ('sameConnection',False),('releaseWasWithheld',False),('teardownComplete',False),('verdict','cancelled-candidate')]
        for key,value in mutations:
            q=copy.deepcopy(r);data=copy.deepcopy(record);data[key]=value;q['groups'][0]['tests'][0]['output']=gate.RECORD+json.dumps(data)
            with self.assertRaises(ValueError):self.validate(q)
        for part in ['before','after']:
            for key in record[part]:
                q=copy.deepcopy(r);data=copy.deepcopy(record);data[part][key]+=1;q['groups'][0]['tests'][0]['output']=gate.RECORD+json.dumps(data)
                with self.assertRaises(ValueError):self.validate(q)
        for output in ['',t['output']+'\n'+t['output'],gate.RECORD+'{"schema":1,"schema":1}']:
            q=copy.deepcopy(r);q['groups'][0]['tests'][0]['output']=output
            with self.assertRaises(ValueError):self.validate(q)

    def test_candidate_cannot_accept_baseline_record(self):
        r,_=fixture(gate.TCP_UIDS[0],'candidate-tcp');baseline,_=fixture(gate.TCP_UIDS[0],'baseline-tcp')
        r['groups'][0]['tests'][0]['output']=baseline['groups'][0]['tests'][0]['output']
        with self.assertRaises(ValueError):self.validate(r,phase='candidate-tcp',code=0)

    def test_flags_exact_exception_and_stack_required(self):
        r,c=fixture(gate.FLAGS_UIDS[0],'baseline-flags')
        for key,value in [('message','generic fail'),('message',gate.FLAGS_ERROR+' '),('type','TimeoutException'),('type','Exception'),('stackTrace','EnsureRequestFlags'),('stackTrace','RequestShouldAdvertiseCancellationForOwnedStreams'),('stackTrace',gate.TCP_STACK)]:
            q=copy.deepcopy(r);q['groups'][0]['tests'][0]['exception'][key]=value
            with self.assertRaises(ValueError):self.validate(q,gate.FLAGS_UIDS[0],'baseline-flags',c)


class DiagnosticPreservationTests(unittest.TestCase):
    def test_only_unresolved_baseline_explicit_case_is_scheduled(self):
        self.assertEqual([(p, a, list(u)) for p,a,_,u in gate.PLAN], [
            ('baseline-tcp','baseline',gate.TCP_UIDS[:1])])
        self.assertEqual(sum(len(u) for _,_,_,u in gate.PLAN),1)

    def test_snapshot_never_substitutes_for_completed_causal_marker(self):
        r,_=fixture(gate.TCP_UIDS[0],'baseline-tcp')
        r['groups'][0]['tests'][0]['output']='STATIC_CANCEL_DIAGNOSTIC_SNAPSHOT|'+json.dumps(dict(schema=1,earlyBreak=False))
        with self.assertRaisesRegex(ValueError,'missing or duplicate causal record'):
            gate.validate_report(r,gate.TCP_UIDS[0],'baseline-tcp',2,False)

    def test_rejected_raw_report_and_exact_oracle_reason_are_retained(self):
        with tempfile.TemporaryDirectory() as directory:
            root=Path(directory); out=root/'case'; uid=gate.TCP_UIDS[0]
            report,_=fixture(uid,'baseline-tcp')
            report['groups'][0]['tests'][0]['exception']['message']='different real assertion'
            raw=json.dumps(report)
            def execute(*args):
                (out/'result.tunit-report.json').write_text(raw)
                return 2,False
            with patch.object(gate,'execute_recorded',side_effect=execute):
                with self.assertRaisesRegex(ValueError,'generic failure is not causal evidence'):
                    gate.run_case(root,root/'test.dll',uid,'baseline-tcp',{},'host',out)
            self.assertEqual((out/'result.tunit-report.json').read_text(),raw)
            detail=json.loads((out/'oracle.json').read_text())
            self.assertEqual(detail['stage'],'validate-exact-report-and-causal-witness')
            self.assertFalse(detail['accepted']); self.assertEqual(detail['exit_code'],2)
            self.assertEqual(detail['exception'],'generic failure is not causal evidence')
            self.assertTrue((out/'argv.json').is_file())

    def test_missing_report_has_explicit_stage_and_cannot_pass(self):
        with tempfile.TemporaryDirectory() as directory:
            root=Path(directory);out=root/'case'
            with patch.object(gate,'execute_recorded',return_value=(2,False)):
                with self.assertRaisesRegex(ValueError,'missing/ambiguous'):
                    gate.run_case(root,root/'test.dll',gate.TCP_UIDS[0],'baseline-tcp',{},'host',out)
            detail=json.loads((out/'oracle.json').read_text())
            self.assertEqual(detail['stage'],'locate-final-report');self.assertFalse(detail['accepted'])

    def test_unsafe_group_oracle_write_failure_stays_fatal(self):
        with tempfile.TemporaryDirectory() as directory:
            root=Path(directory);out=root/'case';original=Path.write_text
            def write(path,*args,**kwargs):
                if path.name=='oracle.json':raise OSError('full disk')
                return original(path,*args,**kwargs)
            with patch.object(gate,'execute_recorded',side_effect=gate.hosts.UnsafeProcessGroup('not quiet')),patch.object(Path,'write_text',write):
                with self.assertRaises(gate.hosts.UnsafeProcessGroup):
                    gate.run_case(root,root/'test.dll',gate.TCP_UIDS[0],'baseline-tcp',{},'host',out)


class ProcessIsolationTests(unittest.TestCase):
    def test_successful_leader_with_live_child_is_failed_and_group_quiescent(self):
        with tempfile.TemporaryDirectory() as directory:
            root=Path(directory);real=gate.hosts.subprocess.Popen
            command=[sys.executable,'-c',"import subprocess,sys; p=subprocess.Popen([sys.executable,'-c','import time; time.sleep(60)']); print(p.pid,flush=True)"]
            with patch.object(gate.hosts.subprocess,'Popen',wraps=real) as popen:
                code,timed_out=gate.execute_recorded(command,root,dict(os.environ),10,root/'leaked.log')
            self.assertEqual(code,-1);self.assertFalse(timed_out)
            status=json.loads((root/'leaked.log.status.json').read_text())
            self.assertEqual(status['exit'],0);self.assertTrue(status['descendantLeak']);self.assertTrue(status['processGroupQuiescent'])
            # A terminated zombie is not executing and must not create an infinite cleanup wait.
            child=int((root/'leaked.log').read_text().strip())
            listing=subprocess.run(['ps','-o','stat=','-p',str(child)],capture_output=True,text=True).stdout.strip()
            self.assertTrue(not listing or listing.startswith('Z'))

    def test_zombie_group_members_are_not_live(self):
        with patch.object(gate.hosts.subprocess,'check_output',return_value='123 Z\n124 S\n'):
            self.assertFalse(gate.hosts.group_alive(123));self.assertTrue(gate.hosts.group_alive(124))

    def test_monitor_cleanup_wait_errors_abort_before_next_case_and_retain_status(self):
        for kind in ('ps','kill','poll','recheck','wait','timeout-kill'):
            with self.subTest(kind=kind),tempfile.TemporaryDirectory() as directory:
                root=Path(directory);process=MagicMock(pid=1234);process.wait.return_value=0
                error=subprocess.CalledProcessError(1,['ps']) if kind=='ps' else OSError('RAW_MONITOR_FAILURE')
                alive=[False,False]
                if kind=='ps':alive=[error]
                if kind=='kill':alive=[True]
                if kind=='poll':alive=[True,error]
                if kind=='recheck':alive=[False,error]
                if kind=='wait':process.wait.side_effect=error
                if kind=='timeout-kill':process.wait.side_effect=subprocess.TimeoutExpired(['host'],1)
                stages=[];next_cases=[]
                def campaign():
                    gate.attempt_case(lambda:gate.execute_recorded(['host'],root,{},1,root/'worker.log'),'baseline-tcp','unsafe',stages)
                    gate.attempt_case(lambda:next_cases.append('must-not-run'),'baseline-tcp','next',stages)
                with patch.object(gate.hosts.subprocess,'Popen',return_value=process),patch.object(gate.hosts,'group_alive',side_effect=alive), \
                     patch.object(gate.hosts.os,'killpg',side_effect=error if kind in ('kill','timeout-kill') else None), \
                     contextlib.redirect_stdout(io.StringIO()) as stdout:
                    with self.assertRaises(gate.hosts.UnsafeProcessGroup):campaign()
                self.assertEqual(next_cases,[])
                status=json.loads((root/'worker.log.status.json').read_text())
                self.assertFalse(status['processGroupQuiescent']);self.assertEqual(status['monitorError']['type'],type(error).__name__)
                self.assertIn('unsafe_process_group',json.loads((root/'worker.log.outcome.json').read_text()))
                self.assertNotIn('RAW_MONITOR_FAILURE',stdout.getvalue())

    def test_status_or_outer_outcome_write_failure_cannot_downgrade_unsafe_group(self):
        for suffix in ('.status.json','.outcome.json'):
            with self.subTest(suffix=suffix),tempfile.TemporaryDirectory() as directory:
                root=Path(directory);process=MagicMock(pid=1234);process.wait.return_value=0;original=Path.open
                def opened(path,*args,**kwargs):
                    if str(path).endswith(suffix):raise OSError('storage failed')
                    return original(path,*args,**kwargs)
                with patch.object(gate.hosts.subprocess,'Popen',return_value=process),patch.object(gate.hosts,'group_alive',side_effect=OSError('ps failed')),patch.object(Path,'open',opened):
                    with self.assertRaises(gate.hosts.UnsafeProcessGroup):gate.execute_recorded(['host'],root,{},1,root/'worker.log')

    def test_fatal_build_does_not_start_next_phase_and_keeps_campaign_summary(self):
        with tempfile.TemporaryDirectory() as directory:
            root=Path(directory);output=root/'out';output.mkdir()
            argv=['run.py','--release-root',str(root/'release'),'--baseline-root',str(root/'baseline'),'--candidate-root',str(root/'candidate')]
            with patch.object(sys,'argv',argv),patch.object(gate,'verify_release',return_value={}),patch.object(gate,'verify_source'), \
                 patch.object(gate.hosts,'verify_hosts',return_value=('sdk','runtime')),patch.dict(os.environ,{'CAUSAL_OUTPUT':str(output)}), \
                 patch.object(gate,'execute_recorded',side_effect=gate.hosts.UnsafeProcessGroup('not quiescent')) as execute:
                with self.assertRaises(gate.hosts.UnsafeProcessGroup):gate.main()
            output=output/'cases'
            self.assertEqual(execute.call_count,1)
            self.assertFalse((output/'baseline-tcp'/'0').exists());self.assertFalse((output/'candidate-tcp').exists())
            summary=json.loads((output/'summary.json').read_text())
            self.assertTrue(summary['stages'][0]['unsafe_process_group'])


class RequestLayoutTests(unittest.TestCase):
    def setUp(self):
        self.temp=tempfile.TemporaryDirectory();self.root=Path(self.temp.name)
        self.git('init','-q');self.git('config','user.email','test@example.invalid');self.git('config','user.name','control')
        self.put('base.txt','base');parent=self.commit()
        self.put('fixture.cs','unchanged fixture');source=self.commit()
        self.pins=copy.deepcopy(gate.PINS)
        self.pins['arms']['candidate'].update(sha=source, tree=self.git('rev-parse','HEAD^{tree}'), parent=parent,
            parent_tree=self.git('rev-parse',parent+'^{tree}'), added_paths=['fixture.cs'],fixtures={'fixture.cs':self.git('rev-parse','HEAD:fixture.cs')})
        for p in self.pins['tool_files']:self.put(p,'tool\n')
        self.tools=self.commit();self.tree=self.git('rev-parse','HEAD^{tree}')
        self.request=dict(schema=1,tools_sha=self.tools,tools_tree=self.tree,
            baseline_sha=self.pins['arms']['baseline']['sha'],candidate_sha=source)
        self.put(self.pins['request'],json.dumps(self.request));self.head=self.commit()

    def tearDown(self):self.temp.cleanup()
    def git(self,*args):return subprocess.check_output(['git','-C',str(self.root),*args],text=True,stderr=subprocess.DEVNULL).strip()
    def put(self,p,s):q=self.root/p;q.parent.mkdir(parents=True,exist_ok=True);q.write_text(s)
    def commit(self):self.git('add','.');self.git('commit','-qm','control');return self.git('rev-parse','HEAD')
    def verify(self):return gate.verify_release(self.root,self.head,self.pins['branch'],self.pins)

    def test_real_request_child_accepted_and_tools_manifest_uses_parent(self):
        self.assertEqual(self.verify(),self.request)
        # The exact previous failure mode: manifest-at-HEAD would incorrectly include request.
        actual=self.git('diff','--name-only',self.pins['arms']['candidate']['sha'],self.head).splitlines()
        self.assertIn(self.pins['request'],actual)
        self.assertNotIn(self.pins['request'],self.pins['tool_files'])

    def test_extra_file_in_tools_commit_rejected_even_with_valid_request_child(self):
        self.git('checkout','--detach',self.tools)
        self.put('extra-tool.txt','not in reviewed tool manifest')
        self.git('add','.');self.git('commit','--amend','--no-edit','-q')
        self.tools=self.git('rev-parse','HEAD');self.request['tools_sha']=self.tools
        self.request['tools_tree']=self.git('rev-parse','HEAD^{tree}')
        self.put(self.pins['request'],json.dumps(self.request));self.head=self.commit()
        with self.assertRaises(ValueError):self.verify()

    def test_request_plus_extra_file_rejected(self):
        self.put('extra.txt','not authorized');self.head=self.commit()
        with self.assertRaises(ValueError):self.verify()

    def test_request_wrong_pin_schema_and_parent_rejected(self):
        for key,value in [('schema',True),('tools_tree','0'*40),('baseline_sha','0'*40),('candidate_sha','0'*40),('tools_sha','0'*40)]:
            self.git('reset','--hard',self.tools);q=self.request.copy();q[key]=value
            self.put(self.pins['request'],json.dumps(q));self.head=self.commit()
            with self.assertRaises((ValueError,subprocess.CalledProcessError)):self.verify()

    def test_fixture_only_source_and_mutations(self):
        source=self.pins['arms']['candidate']['sha'];self.git('checkout','--detach',source)
        gate.verify_source(self.root,'candidate',self.pins)
        self.put('fixture.cs','changed')
        with self.assertRaises(ValueError):gate.verify_source(self.root,'candidate',self.pins)

    def test_wrong_branch_or_untracked_file_rejected(self):
        with self.assertRaises(ValueError):gate.verify_release(self.root,self.head,'refs/heads/dev',self.pins)
        self.put('raw.txt','untracked')
        with self.assertRaises(ValueError):self.verify()


if __name__=='__main__':unittest.main()
