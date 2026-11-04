"""Python-only ordinary execution protocol controls. No SDK invocation."""
import contextlib
import copy
import importlib.util
import io
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch
import xml.etree.ElementTree as ET
sys.dont_write_bytecode=True
ROOT=Path(__file__).resolve().parents[2]
RUNTIMES=[{'framework':'Microsoft.NETCore.App','version':'10.0.14'}]
BODY_ENV={'TUNIT_ENABLE_JUNIT_REPORTER':'1','TUNIT_DISABLE_ARTIFACT_UPLOAD':'1','TUNIT_DISABLE_GITHUB_REPORTER':'1','TUNIT_AGGREGATE_REPORTS':'off'}
def load(name,path):
    spec=importlib.util.spec_from_file_location(name,path);obj=importlib.util.module_from_spec(spec);spec.loader.exec_module(obj);return obj
r=load('test_execution_results',ROOT/'eng/unit-execution-results.py')
runner=load('test_execution_runner',ROOT/'eng/run-unit-execution.py')
census=load('test_execution_census',ROOT/'eng/run-unit-census.py')
owner=load('test_execution_owner',ROOT/'eng/package-smoke-process.py')

def fixture(statuses=('passed','passed')):
    ordinary={};tests=[]
    for i,status in enumerate(statuses):
        uid='opaque-'+str(i);name='M'+str(i);row={'uid':uid,'displayName':name,'type':{'namespace':'Known','typeName':'Known.K','methodName':name},'location':{'file':'/_/test/SharpLink.UnitTests/K.cs','lineStart':3+i*5,'lineEnd':6+i*5}}
        ordinary[uid]=row;tests.append({'id':uid,'displayName':name,'methodName':name,'className':'Known.K','filePath':row['location']['file'],'lineNumber':row['location']['lineStart'],'endLineNumber':row['location']['lineEnd'],'status':status,'durationMs':1.0})
    native={'schemaVersion':1,'assemblyName':'SharpLink.UnitTests','tunitVersion':'1.73.19.0','runtimeVersion':'.NET 10.0.14','publicationGeneration':'a'*32,'groups':[{'namespace':'Known','className':'Known.K','tests':tests}]}
    refresh(native)
    return ordinary,{'explicit-probe'},native,junit(native)

def refresh(native):
    tests=[]
    for group in native['groups']:group['summary']=r.summary(group['tests']);tests+=group['tests']
    native['summary']=r.summary(tests)

def junit(native):
    tests=[t for g in native['groups'] for t in g['tests']];mapped={'passed':None,'failed':'failure','error':'error','timedOut':'error','cancelled':'error','skipped':'skipped'}
    children=[mapped[t['status']] for t in tests]
    attrs={'name':'SharpLink.UnitTests','tests':str(len(tests)),'failures':str(children.count('failure')),'errors':str(children.count('error')),'skipped':str(children.count('skipped'))}
    root=ET.Element('testsuites',attrs);suite=ET.SubElement(root,'testsuite',attrs)
    for t,kind in zip(tests,children):
        case=ET.SubElement(suite,'testcase',{'name':t['displayName'],'classname':t['className']})
        if kind:ET.SubElement(case,kind)
    return ET.tostring(root,encoding='unicode')

class Controls(unittest.TestCase):
    def reject(self,action):
        with self.assertRaises(Exception):action()
    def test_complete_native_and_junit_pass(self):
        ordinary,explicit,native,xml=fixture();facts={}
        self.assertTrue(r.classify(native,xml,ordinary,explicit,facts,RUNTIMES));self.assertEqual(facts['observed'],2);self.assertEqual(facts['statusCounts']['passed'],2)
    def test_all_nonpassing_terminal_states_never_qualify(self):
        for status in ('failed','error','skipped','timedOut','cancelled'):
            ordinary,explicit,native,xml=fixture(('passed',status));facts={}
            self.assertFalse(r.classify(native,xml,ordinary,explicit,facts,RUNTIMES));self.assertEqual(facts['observed'],2)
    def test_duplicate_missing_foreign_and_explicit_uid(self):
        for mode in ('duplicate','missing','foreign','explicit'):
            ordinary,explicit,native,xml=fixture();tests=native['groups'][0]['tests']
            if mode=='duplicate':tests[1]['id']=tests[0]['id']
            elif mode=='missing':tests.pop()
            else:tests[1]['id']='explicit-probe' if mode=='explicit' else 'foreign'
            refresh(native);self.reject(lambda:r.classify(native,junit(native),ordinary,explicit,{},RUNTIMES))
    def test_identity_fields_and_source_span(self):
        for key,value in [('id','foreign'),('displayName','other'),('methodName','other'),('className','other'),('filePath','/_/other'),('lineNumber',True),('endLineNumber',99)]:
            ordinary,explicit,native,xml=fixture();native['groups'][0]['tests'][0][key]=value
            self.reject(lambda:r.classify(native,xml,ordinary,explicit,{},RUNTIMES))
        for key,value in [('assemblyName','Other'),('tunitVersion','9.0'),('runtimeVersion','.NET 9.0.1'),('publicationGeneration','bad'),('schemaVersion',True)]:
            ordinary,explicit,native,xml=fixture();native[key]=value;self.reject(lambda:r.classify(native,xml,ordinary,explicit,{},RUNTIMES))
    def test_groups_summaries_and_junit_independent(self):
        for mode in ('group','top','duplicate-group','junit-count','junit-name','junit-child','entity'):
            ordinary,explicit,native,xml=fixture()
            if mode=='group':native['groups'][0]['summary']['passed']=1
            elif mode=='top':native['summary']['total']=1
            elif mode=='duplicate-group':native['groups'].append(copy.deepcopy(native['groups'][0]))
            elif mode=='junit-count':xml=xml.replace('tests="2"','tests="3"')
            elif mode=='junit-name':xml=xml.replace('name="M0"','name="other"')
            elif mode=='junit-child':xml=xml.replace('<testcase name="M0" classname="Known.K" />','<testcase name="M0" classname="Known.K"><system-out>private</system-out></testcase>')
            else:xml='<!DOCTYPE testsuites>'+xml
            self.reject(lambda:r.classify(native,xml,ordinary,explicit,{},RUNTIMES))
    def test_retry_even_zero_and_flaky_never_pass(self):
        for field,value in [('retryAttempt',0),('retryAttempt',1),('attempts',[])]:
            ordinary,explicit,native,xml=fixture();native['groups'][0]['tests'][0][field]=value;refresh(native)
            self.assertFalse(r.classify(native,xml,ordinary,explicit,{},RUNTIMES))
    def test_pass_exception_or_skip_fields_rejected(self):
        for field in ('exception','skipReason'):
            ordinary,explicit,native,xml=fixture();native['groups'][0]['tests'][0][field]=None
            self.reject(lambda:r.classify(native,xml,ordinary,explicit,{},RUNTIMES))
    def test_exact_junit_utf16_sanitizer_and_runtime_selection(self):
        self.assertEqual(r.xml_name('A\0\U0001f600\uffff'),'A[0x0][0xD83D][0xDE00][0xFFFF]')
        self.assertEqual(r.xml_name('\t\n\rnormal'),'\t\n\rnormal')
        ordinary,explicit,native,xml=fixture();native['runtimeVersion']='.NET 10.0.999'
        self.reject(lambda:r.classify(native,xml,ordinary,explicit,{},RUNTIMES))
    def test_duration_nonfinite_and_unknown_status(self):
        for key,value in [('durationMs',float('nan')),('durationMs',True),('durationMs',-1),('status','inProgress')]:
            ordinary,explicit,native,xml=fixture();native['groups'][0]['tests'][0][key]=value
            self.reject(lambda:r.classify(native,xml,ordinary,explicit,{},RUNTIMES))
    def test_matching_display_names_use_multiset_not_collapse(self):
        ordinary,explicit,native,xml=fixture()
        for row in ordinary.values():row['displayName']='same'
        for test in native['groups'][0]['tests']:test['displayName']='same'
        self.assertTrue(r.classify(native,junit(native),ordinary,explicit,{},RUNTIMES))
    def test_argv_exact_unfiltered_and_neutral_reports(self):
        dotnet=Path('/sdk/dotnet');dll=Path('/output/tests.dll');report=Path('/work/results');argv=r.body_argv(dotnet,dll,report)
        self.assertIn('--report-html',argv);self.assertNotIn('--filter-uid',argv);r.verify_body_inputs(argv,dotnet,dll,report,BODY_ENV)
        for bad in (argv+['--filter-uid','x'],argv+['@file'],['private',*argv[1:]]):self.reject(lambda:r.verify_body_inputs(bad,dotnet,dll,report,BODY_ENV))
        for env in ({'TUNIT_DISABLE_HTML_REPORTER':'1'},{'TUNIT_DISABLE_JSON_REPORT':'false'},{'commandLineOptions__treenode-filter__0':'x'}):self.reject(lambda:r.verify_body_inputs(argv,dotnet,dll,report,{**BODY_ENV,**env}))
    def test_report_domain_fixed_parent_and_bound_children(self):
        with tempfile.TemporaryDirectory() as tmp:
            root=Path(tmp);identity=r.census.legacy.directory_identity_strict(root)
            for name in r.PARENT_REPORTS:(root/name).write_text('x')
            child=root/'dispatch-probes'/('context-before-'+'a'*32);child.mkdir(parents=True);(child/'child.html').write_text('private')
            state=r.report_snapshot(root,identity);self.assertEqual(len(state),7)
            (root/'unknown').write_text('x');self.reject(lambda:r.report_snapshot(root,identity))
    def test_report_alias_symlink_special_or_unknown_scenario_rejected(self):
        for mode in ('symlink','alias','fifo','scenario'):
            with tempfile.TemporaryDirectory() as tmp:
                root=Path(tmp);identity=r.census.legacy.directory_identity_strict(root);(root/'session.html').write_text('x')
                if mode=='symlink':(root/'junit.xml').symlink_to(root/'session.html')
                elif mode=='alias':os.link(root/'session.html',root/'junit.xml')
                elif mode=='fifo':os.mkfifo(root/'junit.xml')
                else:(root/'dispatch-probes'/('foreign-'+'a'*32)).mkdir(parents=True)
                self.reject(lambda:r.report_snapshot(root,identity))
    def test_report_oversize_rejected_before_snapshot_read(self):
        with tempfile.TemporaryDirectory() as tmp:
            root=Path(tmp);identity=r.census.legacy.directory_identity_strict(root)
            with (root/'session.tunit-report.json').open('wb') as f:f.truncate(16*1024*1024+1)
            with patch.object(r.census,'snapshot') as scan:
                self.reject(lambda:r.report_snapshot(root,identity));scan.assert_not_called()
    def test_report_mutation_after_seal_rejected(self):
        with tempfile.TemporaryDirectory() as tmp:
            root=Path(tmp);identity=r.census.legacy.directory_identity_strict(root);(root/'session.html').write_text('one')
            state=r.report_snapshot(root,identity);(root/'session.html').write_text('two');self.reject(lambda:r.census.verify_snapshot(root,state))
    def test_real_owned_success_nonzero_timeout_and_child(self):
        owner.enable_subreaper()
        for mode,script in [('success','pass'),('failure','raise SystemExit(3)'),('timeout','import time;time.sleep(10)'),('child','import subprocess,sys;subprocess.Popen([sys.executable,"-c","import time;time.sleep(10)"])')]:
            with tempfile.TemporaryDirectory() as tmp:
                root=Path(tmp);argv=[sys.executable,'-c',script];seconds=.2 if mode=='timeout' else 2
                try:owner.run_owned(mode,argv,root,os.environ.copy(),root,seconds);provenance='validated-original-success'
                except owner.CommandFailure as error:provenance='original-owner-failure';self.assertIsNotNone(error.outcome)
                receipt=json.loads((root/(mode+'.json')).read_text());state={'ownerProvenance':provenance,'receipt':receipt,'seconds':seconds}
                self.assertEqual(r.owner_closed(state,mode,argv),mode in ('success','failure'))
                self.assertEqual(receipt['remainingOwned'],{})
    def test_unknown_owner_provenance_and_cleanup_never_closed(self):
        argv=['python'];receipt={'stage':'ordinary-execution','argv':argv,'exitCode':0,'timedOut':False,'timeoutSeconds':2,'remainingOwned':{},'errors':[],'cleanupErrors':[],'reportingErrors':[]}
        for key,value in [('exitCode',None),('remainingOwned',{'1':[]}),('cleanupErrors',['x']),('errors',['x']),('reportingErrors',['x']),('timedOut',True)]:
            bad=dict(receipt);bad[key]=value;self.assertFalse(r.owner_closed({'ownerProvenance':'original-owner-failure','receipt':bad,'seconds':2},'ordinary-execution',argv))
        self.assertFalse(r.owner_closed({'ownerProvenance':'unvalidated-success-receipt','receipt':receipt,'seconds':2},'ordinary-execution',argv))
    def test_ledger_mismatch_and_explicit_partition(self):
        rows=[{'native':{'uid':str(i)},'initialEligibility':'ordinary' if i<2879 else 'explicit'} for i in range(2884)]
        first={'identity':{},'actual':rows,'rawJSONSHA256':'a'};second=dict(first,rawJSONSHA256='b')
        ordinary,explicit=r.expected_ordinary(first,second);self.assertEqual((len(ordinary),len(explicit)),(2879,5))
        bad=copy.deepcopy(second);bad['actual'][0]['initialEligibility']='unknown';self.reject(lambda:r.expected_ordinary(first,bad))
    def test_original_prepare_deadline_cannot_be_extended(self):
        seen=[];clock=[1.0]
        def preparation(lane,*_):seen.append(lane);lane.stop=0.0;raise r.census.Rejected('UE_PREPARATION')
        real=runner.load
        def modules(name,path):return census if name=='execution_census_runner' else r if name=='execution_results' else owner
        with patch.object(runner,'load',side_effect=modules),patch.object(runner,'prepare',side_effect=preparation),patch.object(runner,'run_body') as body,contextlib.redirect_stdout(io.StringIO()):
            self.assertEqual(runner.main(),1);body.assert_not_called()
        self.assertFalse(seen[0].preparation_complete);self.assertEqual(seen[0].stop,0.0)
    def test_prepare_certificate_deadline_crossing_real_guard(self):
        rows=[{'native':{'uid':str(i)},'initialEligibility':'ordinary' if i<2879 else 'explicit'} for i in range(2884)]
        def fake_execute(lane):
            lane.stable=True;lane.unfiltered=True
            for number in (1,2):
                directory=lane.evidence/('ledger'+str(number));directory.mkdir()
                r.census.write_json(directory,'ledger.json',{'identity':{},'actual':rows})
                lane.seals[directory]=r.census.snapshot(directory)
                lane.extend_evidence(['ledger'+str(number),'ledger'+str(number)+'/ledger.json'])
        with tempfile.TemporaryDirectory() as tmp:
            lane=census.Lane(Path(tmp),r.census,owner);lane.stop=2.0;clock=[1.0];real=r.census.write_json
            def late(directory,name,value):
                real(directory,name,value)
                if name=='preparation.json':clock[0]=3.0
            with patch.object(census,'execute',side_effect=fake_execute),patch.object(r.census,'write_json',side_effect=late),patch.object(runner.time,'monotonic',side_effect=lambda:clock[0]):
                self.reject(lambda:runner.prepare(lane,census,r))
            self.assertEqual(lane.stop,2.0);self.assertTrue((lane.evidence/'preparation.json').exists())
    def test_unknown_body_owner_cannot_seal_or_parse(self):
        with tempfile.TemporaryDirectory() as tmp:
            lane=census.Lane(Path(tmp),r.census,owner);lane.source=Path(tmp)/'src'
            dll=lane.source/'test/SharpLink.UnitTests/bin/Release/net10.0/SharpLink.UnitTests.dll';dll.parent.mkdir(parents=True);dll.write_bytes(b'fixture')
            sdk=Path(tmp)/'sdk';sdk.mkdir();dotnet=sdk/'dotnet';dotnet.write_bytes(b'not executable')
            lane.inputs[dotnet]=r.census.file_identity(dotnet);lane.trees[sdk]=r.census.snapshot(sdk);lane.identity={'binarySHA256':r.census.sha(dll)}
            original=RuntimeError('owner unknown')
            def run(label,argv,*args):
                if label=='execution-python-controls':lane.sequence=31;return None
                lane.commands[label]={'ownerProvenance':None,'receipt':None};raise original
            with patch.object(lane,'run',side_effect=run),patch.object(r,'report_snapshot') as snapshot,patch.object(r,'classify') as classify:
                runner.run_body(lane,r,{},set(),runner.time.monotonic()+6600)
                snapshot.assert_not_called();classify.assert_not_called()
            self.assertFalse(lane.execution_facts['ownerClosed']);self.assertIs(lane.errors[0],original);self.assertNotIn(lane.report,lane.seals)
    def test_malformed_xml_and_duplicate_json_rejected(self):
        ordinary,explicit,native,xml=fixture();self.reject(lambda:r.classify(native,'<broken',ordinary,explicit,{},RUNTIMES))
        with tempfile.TemporaryDirectory() as tmp:
            path=Path(tmp)/'native.json';path.write_text('{"groups":[],"groups":[]}');self.reject(lambda:r.census.read_json(path))
    def test_deadline_crossed_final_summary_does_not_pass(self):
        with tempfile.TemporaryDirectory() as tmp:
            lane=census.Lane(Path(tmp),r.census,owner);lane.body_passed=True;lane.stop=2.0;clock=[1.0];real=r.census.write_json
            def late(directory,name,value):real(directory,name,value);clock[0]=3.0
            with patch.object(r.census,'write_json',side_effect=late),patch.object(runner.time,'monotonic',side_effect=lambda:clock[0]),patch.object(runner.os,'write',return_value=1),contextlib.redirect_stdout(io.StringIO()) as output:
                self.assertEqual(runner.finish(lane,census,r),1)
            self.assertNotIn('"executionQualified": true',output.getvalue());self.assertTrue(lane.errors)
    def test_summary_writer_failure_retains_original(self):
        with tempfile.TemporaryDirectory() as tmp:
            lane=census.Lane(Path(tmp),r.census,owner);original=ValueError('PRIVATE_ORIGINAL');lane.record(original)
            with patch.object(r.census,'write_json',side_effect=OSError('PRIVATE_WRITER')),patch.object(runner.os,'write',return_value=1):self.assertEqual(runner.finish(lane,census,r),1)
            self.assertIs(lane.errors[0],original);self.assertTrue(any(isinstance(e,OSError) for e in lane.errors))
    def test_post_emit_deadline_revokes_with_fixed_terminal_record(self):
        with tempfile.TemporaryDirectory() as tmp:
            lane=census.Lane(Path(tmp),r.census,owner);lane.body_passed=True;lane.stop=2.0;clock=[1.0];writes=[]
            def emit(*args,**kwargs):clock[0]=3.0
            with patch.object(runner.time,'monotonic',side_effect=lambda:clock[0]),patch('builtins.print',side_effect=emit),patch.object(runner.os,'write',side_effect=lambda fd,data:writes.append(data) or len(data)):
                self.assertEqual(runner.finish(lane,census,r),1)
            self.assertEqual(writes[0],b'UNIT_EXECUTION_FAILED_SUMMARY\n');self.assertFalse(json.loads(writes[1])['executionQualified'])
    def test_summary_and_terminal_failure_are_independent(self):
        with tempfile.TemporaryDirectory() as tmp:
            lane=census.Lane(Path(tmp),r.census,owner);original=RuntimeError('original');lane.record(original);diagnostic=OSError('terminal')
            with patch.object(r.census,'write_json',side_effect=ValueError('summary')),patch.object(runner,'terminal_diagnostic',side_effect=diagnostic),patch.object(runner.os,'write',return_value=1):
                self.assertEqual(runner.finish(lane,census,r),1)
            self.assertIs(lane.errors[0],original);self.assertIn(diagnostic,lane.errors);self.assertTrue(any(isinstance(e,ValueError) for e in lane.errors))
    def test_bootstrap_graph_preserves_original_and_occurrences(self):
        with tempfile.TemporaryDirectory() as tmp:
            original=RuntimeError('original');group=ExceptionGroup('group',[original]);group.__cause__=original
            errors=[original,group,original];self.assertTrue(runner.bootstrap_persist(Path(tmp),errors))
            graph=json.loads((Path(tmp)/'bootstrap-0.json').read_text());self.assertEqual(graph['orderedRootOccurrences'],[0,1,0]);self.assertEqual(graph['nodes'][1]['members'],[0]);self.assertEqual(graph['nodes'][1]['cause'],0)
    def test_bootstrap_graph_write_failure_retains_original(self):
        original=RuntimeError('original');errors=[original]
        with tempfile.TemporaryDirectory() as tmp,patch.object(runner.os,'open',side_effect=OSError('write')):
            self.assertFalse(runner.bootstrap_persist(Path(tmp),errors))
        self.assertIs(errors[0],original);self.assertEqual(len(errors),3)
    def test_terminal_diagnostic_survives_json_formatter_and_keeps_private_values(self):
        with tempfile.TemporaryDirectory() as tmp:
            lane=census.Lane(Path(tmp),r.census,owner);lane.errors=[r.census.Rejected('UE_UID'),RuntimeError('PRIVATE_SENTINEL')]
            with patch.object(runner.json,'dumps',side_effect=RuntimeError('json')),patch('builtins.print',side_effect=RuntimeError('print')):
                raw=runner.terminal_diagnostic(lane,census,r)
            self.assertNotIn(b'PRIVATE_SENTINEL',raw);value=json.loads(raw);self.assertFalse(value['executionQualified']);self.assertEqual(value['codes'],['UE_UID'])
    def test_main_retains_lane_receipts_when_actual_persistence_fails(self):
        original=RuntimeError('PRIVATE_ORIGINAL');receipt={'private':'original receipt'}
        real_persist=census.persist
        def modules(name,path):return census if name=='execution_census_runner' else r if name=='execution_results' else owner
        def preparation(lane,*_):
            lane.commands['ordinary-execution']={'receipt':receipt};raise original
        def failed_persist(*args):
            with patch.object(census.os,'open',side_effect=OSError('PRIVATE_PERSIST')):return real_persist(*args)
        with patch.object(runner,'load',side_effect=modules),patch.object(runner,'prepare',side_effect=preparation),patch.object(census,'persist',side_effect=failed_persist),contextlib.redirect_stdout(io.StringIO()):
            self.assertEqual(runner.main(),1)
        active=runner.ACTIVE_RECORD;lane=active['lane']
        self.assertIs(active['errors'],lane.errors);self.assertIs(runner.main.last_errors,lane.errors)
        self.assertIs(lane.errors[0],original);self.assertFalse(lane.durable)
        self.assertIs(lane.commands['ordinary-execution']['receipt'],receipt)
        self.assertGreaterEqual(sum(isinstance(e,OSError) for e in lane.errors),2)
    def test_real_main_outermost_retains_original_and_secondary_occurrences(self):
        original=RuntimeError('PRIVATE_ORIGINAL');persistence=OSError('PRIVATE_PERSIST');emission=OSError('PRIVATE_EMIT');receipt={}
        def modules(name,path):return census if name=='execution_census_runner' else r if name=='execution_results' else owner
        def preparation(lane,*_):
            lane.commands['ordinary-execution']={'receipt':receipt};raise original
        with patch.object(runner,'load',side_effect=modules),patch.object(runner,'prepare',side_effect=preparation),patch.object(census,'persist',side_effect=persistence),patch.object(runner.os,'write',side_effect=emission):
            self.assertEqual(runner.guarded_main(),1)
        active=runner.ACTIVE_RECORD;lane=active['lane']
        self.assertIs(active['errors'],lane.errors);self.assertIs(runner.main.last_errors,lane.errors)
        self.assertIs(lane.errors[0],original);self.assertEqual(lane.errors.count(persistence),2)
        self.assertIs(lane.errors[-1],emission);self.assertIs(lane.commands['ordinary-execution']['receipt'],receipt)
    def test_bootstrap_failure_real_child_is_literal(self):
        script='import tempfile,runpy; tempfile.mkdtemp=lambda *a,**k: (_ for _ in ()).throw(OSError("PRIVATE")); runpy.run_path('+repr(str(ROOT/'eng/run-unit-execution.py'))+',run_name="__main__")'
        child=subprocess.run([sys.executable,'-B','-c',script],capture_output=True,timeout=10)
        self.assertEqual(child.returncode,1);self.assertEqual(child.stdout,b'');self.assertEqual(child.stderr,b'UNIT_EXECUTION_FAILED_BOOTSTRAP\n')

if __name__=='__main__':unittest.main()
