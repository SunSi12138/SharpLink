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
runner=load('test_execution_runner',ROOT/'eng/run-pure-negotiation-full-regression.py')
census=load('test_execution_census',ROOT/'eng/run-unit-census.py')
owner=load('test_execution_owner',ROOT/'eng/package-smoke-process.py')
TimedProjectionControls=load('test_portable_timed',ROOT/'eng/tests/test_timed_stream_full_results.py').Controls
RebaseControls=load('test_current_rebase',ROOT/'eng/tests/test_three_fixture_census.py').Controls

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


    def test_workflow_has_no_dotnet_cli_before_owned_runner(self):
        lines=(ROOT/'.github/workflows/pure-negotiation-full-regression.yml').read_text().splitlines()
        scripts=[];index=0
        while index<len(lines):
            line=lines[index];index+=1
            if line.lstrip().startswith('run:'):
                indent=len(line)-len(line.lstrip());value=line.split('run:',1)[1].strip()
                if value=='|':
                    block=[]
                    while index<len(lines) and (not lines[index].strip() or len(lines[index])-len(lines[index].lstrip())>indent):
                        block.append(lines[index].strip());index+=1
                    scripts.append('\n'.join(block))
                else:scripts.append(value)
        self.assertEqual(scripts,[
            'test "$(git rev-parse HEAD:src)" = "c3a94c896418a5d5ec62646461f17c67dc89bc96"\n'
            'test "$(git rev-parse HEAD:test)" = "fb8f193f979ba936fcd3e51eff98562f27f06a50"',
            'python3 eng/run-pure-negotiation-full-regression.py --configuration Release'])
        self.assertFalse(any('dotnet' in script.lower() for script in scripts))
        self.assertIn('        uses: actions/setup-dotnet@a98b56852c35b8e3190ac28c8c2271da59106c68',lines)

    def test_every_current_helper_source_and_catalog_binding_matches(self):
        runner.verify_helpers();runner.verify_source(r.census,ROOT)
        self.assertEqual(r.CATALOG_SHA256,'3d05292b01e9ec133f10fad420593895e122469eb82528d6190ac2e9a8dcde12')
        g=load('verify_full_rebase',ROOT/'eng/rebase-three-fixture-census.py')
        self.assertEqual({name:g.tree_id(ROOT/name) for name in ('src','test')},runner.SOURCE_TREES)
        catalog=r.census.read_json(ROOT/'eng/unit-census.json')
        self.assertEqual((len(catalog['methods']),len(catalog['sourcePins'])),(2463,1599))
        self.assertEqual({name:catalog['sourceDerivation'][key] for name,key in (('src','candidateProductTree'),('test','candidateTestTree'))},runner.SOURCE_TREES)

    def test_changed_helper_or_source_pin_rejects_before_execution(self):
        with patch.object(r.census,'sha',return_value='0'*64):self.reject(lambda:runner.verify_source(r.census,ROOT))
        original=Path.read_bytes;target=ROOT/'eng/unit-census.json'
        def changed(path):return original(path)+(b'\n' if path==target else b'')
        with patch.object(Path,'read_bytes',changed):self.reject(runner.verify_helpers)

    def test_original_failed_and_twenty_three_success_evidence_are_distinct_and_exact(self):
        historical=(('original-ordinary-result.json','dae32c0724ad78dca30234d7d109e45e56566efe900aa854b6461edb4a50f214'),
                    ('twenty-three-case-result.json','363a314514e08f19abee140e4ea9c2e692e9836aaeebf02d317036b012fc07af'))
        for name,digest in historical:self.assertEqual(r.census.sha(ROOT/'eng/full-regression-history'/name),digest)
        old=r.census.read_json(ROOT/'eng/full-regression-history/original-ordinary-result.json')
        self.assertEqual((old['runId'],old['counts']['total'],old['counts']['passed'],old['counts']['failed']),(41940224644,2879,2589,290))
        self.assertFalse(old['executionQualified']);self.assertEqual(len(old['failures']),253)
        self.assertEqual(sum(row['count'] for row in old['failures']),290)
        current=r.census.read_json(ROOT/'eng/full-regression-history/twenty-three-case-result.json')
        self.assertEqual((current['runId'],current['head']),(41959478271,'8dba79eacbb18744cc6ccc01ad107bb460d574a4'))
        summary=current['summary'];self.assertTrue(summary['candidateValidationQualified']);self.assertFalse(summary['fullSuiteQualified'])
        self.assertEqual(len(summary['cases']),23);self.assertTrue(all(row['selectedPassed'] and row['bodyExitCode']==0 and row['ownerClosed'] for row in summary['cases']))

    def test_expired_preparation_or_overall_cannot_extend_or_claim_handoff(self):
        for mode in ('preparation','overall'):
            with tempfile.TemporaryDirectory() as tmp,patch.object(runner.time,'monotonic',return_value=1000.0):
                lane=census.Lane(Path(tmp),r.census,owner);lane.source=ROOT;lane.preparation_complete=False
                lane.stop=900.0 if mode=='preparation' else 2000.0;before=lane.stop
                with patch.object(lane,'run') as run:
                    self.reject(lambda:runner.run_body(lane,r,{},set(),900.0 if mode=='overall' else 6600.0));run.assert_not_called()
                self.assertEqual(lane.stop,before);self.assertFalse(lane.preparation_complete)
                self.assertFalse(json.loads(runner.terminal_diagnostic(lane,census,r))['preparationQualifiedAtHandoff'])

    def test_handoff_crossing_deadline_or_seal_failure_cannot_revive_preparation(self):
        for mode in ('clock','seal'):
            with tempfile.TemporaryDirectory() as tmp:
                clock=[1000.0]
                with patch.object(runner.time,'monotonic',side_effect=lambda:clock[0]):
                    lane=census.Lane(Path(tmp),r.census,owner);lane.source=ROOT;lane.preparation_complete=False;lane.stop=2000.0
                    def source(*args):
                        if mode=='clock':clock[0]=2100.0
                        else:raise r.census.Rejected('UC_INPUT_CHANGED')
                    with patch.object(runner,'verify_source',side_effect=source),patch.object(lane,'run') as run:
                        self.reject(lambda:runner.run_body(lane,r,{},set(),6600.0));run.assert_not_called()
                    self.assertFalse(lane.preparation_complete);self.assertEqual(lane.stop,2000.0)

    def test_isolated_sdk_directory_and_environment_are_exact(self):
        for mode in ('valid','wrong-root','wrong-env','multilevel','symlink'):
            with tempfile.TemporaryDirectory() as tmp:
                lane=census.Lane(Path(tmp),r.census,owner);sdk=Path(tmp)/'pure-negotiation-full-dotnet';sdk.mkdir();dotnet=sdk/'dotnet';dotnet.write_bytes(b'never execute')
                lane.env.update(DOTNET_ROOT=str(sdk),DOTNET_MULTILEVEL_LOOKUP='0')
                if mode=='wrong-env':lane.env['DOTNET_ROOT_X64']='/other'
                if mode=='multilevel':lane.env['DOTNET_MULTILEVEL_LOOKUP']='1'
                if mode=='symlink':actual=Path(tmp)/'actual';sdk.rename(actual);sdk.symlink_to(actual,target_is_directory=True)
                with patch.dict(os.environ,RUNNER_TEMP=tmp if mode!='wrong-root' else '/other'),patch.object(census.shutil,'which',return_value=str(dotnet)):
                    if mode=='valid':runner.verify_isolated_sdk(lane,census)
                    else:self.reject(lambda:runner.verify_isolated_sdk(lane,census))

    def test_exact_sdk_and_two_runtime_hook_rejects_other_versions(self):
        Lane=runner.lane_type(census)
        for label,value in (('sdk-version','10.0.115'),('sdk-runtimes','Microsoft.NETCore.App 10.0.14 [/sdk/shared/Microsoft.NETCore.App]')):
            with tempfile.TemporaryDirectory() as tmp:
                lane=Lane(Path(tmp),r.census,owner);out=Path(tmp)/'output';out.write_text(value)
                with patch.object(census.Lane,'run',return_value=out):self.reject(lambda:lane.run(label,['/sdk/dotnet']))

    def test_exact_source_tree_hook_adds_only_two_checks(self):
        with tempfile.TemporaryDirectory() as tmp:
            lane=runner.lane_type(census)(Path(tmp),r.census,owner);lane.source=ROOT
            with patch.object(census.Lane,'run',return_value=Path(tmp)),patch.object(lane,'git',side_effect=lambda label,*args,**kwargs:runner.SOURCE_TREES[args[1].split(':')[1]]) as git,patch.object(runner,'verify_source') as source:
                lane.run('clone-head',['git','rev-parse','HEAD'])
                self.assertEqual(git.call_count,2);source.assert_called_once_with(r.census,ROOT)
            with patch.object(census.Lane,'run',return_value=Path(tmp)),patch.object(lane,'git',return_value='0'*40):
                self.reject(lambda:lane.run('clone-head',['git','rev-parse','HEAD']))

    def test_full_2879_result_and_exception_categories_are_bounded_source_identities(self):
        ordinary,explicit,native,xml=fixture(('passed',)*2879);explicit={f'explicit-{i}' for i in range(5)}
        cases=native['groups'][0]['tests'];canary='PRIVATE_CANARY_FULL_EXCEPTION'
        types=['SharpLink.Runtime.SharpLinkProtocolViolationException','System.ArgumentException','System.InvalidOperationException','TUnit.Assertions.Exceptions.AssertionException','Unknown.'+canary]
        for test,typ in zip(cases,types):
            test['status']='error';test['exception']={'type':typ,'message':canary,'stackTrace':canary,'innerException':{'type':canary,'message':canary}}
        refresh(native);facts={};self.assertFalse(r.classify(native,junit(native),ordinary,explicit,facts,RUNTIMES))
        before=copy.deepcopy(facts['methodStatusCounts']);runner.project_exception_categories(r,native,ordinary,facts)
        self.assertEqual(facts['observed'],2879);self.assertEqual(facts['methodStatusCounts'],before)
        self.assertEqual(facts['exceptionCategoryCounts'],[{'category':cat,'count':1} for cat in ('argument','assertion','invalid-operation','protocol-violation','unknown')])
        self.assertEqual(len(facts['methodExceptionCategoryCounts']),5);self.assertNotIn(canary,json.dumps(facts))
        self.assertFalse(facts['allOrdinaryPassed']);self.assertFalse(facts['retriesObserved'])

    def test_category_projection_requires_reconciliation_and_never_infers_inner_cause(self):
        ordinary,explicit,native,xml=fixture(('error','error'));facts={}
        self.reject(lambda:runner.project_exception_categories(r,native,ordinary,facts))
        cases=native['groups'][0]['tests'];cases[0]['exception']={'type':'System.AggregateException','message':'PRIVATE','innerException':{'type':'System.ArgumentException','message':'PRIVATE'}}
        cases[1]['exception']={'type':['PRIVATE'],'message':'PRIVATE'}
        r.classify(native,junit(native),ordinary,explicit,facts,RUNTIMES);runner.project_exception_categories(r,native,ordinary,facts)
        self.assertEqual(facts['exceptionCategoryCounts'],[{'category':'aggregate','count':1},{'category':'unknown','count':1}]);self.assertNotIn('PRIVATE',json.dumps(facts))
        cases[1]['id']=cases[0]['id'];self.reject(lambda:runner.project_exception_categories(r,native,ordinary,facts))

    def test_closed_full_body_is_once_unfiltered_and_retains_failure_and_budgets(self):
        for passing,exit_code in ((True,0),(False,2),(False,0),(True,2)):
            with tempfile.TemporaryDirectory() as tmp,patch.object(runner.time,'monotonic',return_value=1000.0):
                lane=census.Lane(Path(tmp),r.census,owner);lane.source=Path(tmp)/'src';lane.preparation_complete=False;lane.sequence=32;lane.stop=2000.0
                dll=lane.source/'test/SharpLink.UnitTests/bin/Release/net10.0/SharpLink.UnitTests.dll';dll.parent.mkdir(parents=True);dll.write_bytes(b'fixture')
                sdk=Path(tmp)/'sdk';sdk.mkdir();dotnet=sdk/'dotnet';dotnet.write_bytes(b'never execute')
                lane.inputs[dotnet]=r.census.file_identity(dotnet);lane.trees[sdk]=r.census.snapshot(sdk)
                lane.identity={'binarySHA256':r.census.sha(dll),'installedRuntimes':runner.RUNTIMES}
                ordinary,explicit,native,xml=fixture(('passed','passed' if passing else 'error'));labels=[];originals=[]
                def run(label,argv,*args):
                    labels.append(label);lane.sequence+=1
                    if label=='pure-negotiation-full-python-controls':
                        self.assertTrue(lane.preparation_complete);self.assertEqual(argv,[sys.executable,'-B',ROOT/'eng/tests/test_pure_negotiation_full_regression.py']);self.assertEqual(args,(ROOT,60));return
                    self.assertEqual(args,(lane.source,1800));self.assertEqual(argv,r.body_argv(dotnet,dll,lane.report))
                    self.assertNotIn('--filter-uid',argv);self.assertEqual(argv[argv.index('--maximum-parallel-tests')+1],'8');self.assertEqual(argv[argv.index('--timeout')+1],'1500s')
                    (lane.report/'session.html').write_text('PRIVATE');(lane.report/'session.tunit-report.json').write_text(json.dumps(native));(lane.report/'junit.xml').write_text(xml)
                    receipt={'stage':label,'argv':argv,'exitCode':exit_code,'timedOut':False,'timeoutSeconds':1800,'remainingOwned':{},'errors':[],'cleanupErrors':[],'reportingErrors':[]}
                    lane.commands[label]={'receipt':receipt,'argv':argv,'seconds':1800,'ownerProvenance':'validated-original-success' if exit_code==0 else 'original-owner-failure'}
                    if exit_code!=0:
                        original=owner.CommandFailure(receipt,[]);originals.append(original);raise original
                with patch.object(runner,'verify_source'),patch.object(runner,'project_timed_stream',return_value={'syntheticControlOnly':True}),patch.object(lane,'run',side_effect=run):runner.run_body(lane,r,ordinary,explicit,6600.0)
                self.assertEqual(labels,['pure-negotiation-full-python-controls','ordinary-execution']);self.assertEqual(lane.sequence,34);self.assertEqual(lane.stop,4000.0)
                self.assertEqual(lane.body_passed,passing);self.assertTrue(lane.execution_facts['ownerClosed']);self.assertTrue(lane.execution_facts['executionClosureObserved'])
                self.assertEqual(lane.execution_facts['bodyExitCode'],exit_code)
                if exit_code!=0:self.assertIn(originals[0],lane.errors)
                qualified=passing and exit_code==0
                with contextlib.redirect_stdout(io.StringIO()) as output:self.assertEqual(runner.finish(lane,census,r),0 if qualified else 1)
                summary=json.loads(output.getvalue());self.assertEqual(summary['fullOrdinaryRegressionQualified'],qualified)
                self.assertEqual(summary['historicalFullOrdinaryRun']['errors'],290);self.assertFalse(summary['historicalFullOrdinaryRun']['qualified'])
                self.assertEqual(summary['historicalTwentyThreeCaseRun']['passed'],23);self.assertNotIn('PRIVATE',output.getvalue())

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
            lane.stable=True;lane.unfiltered=True;lane.sequence=32;lane.identity={'sdk':'10.0.114','installedRuntimes':runner.RUNTIMES}
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
            sdk=Path(tmp)/'sdk';sdk.mkdir();dotnet=sdk/'dotnet';dotnet.write_bytes(b'not executable')
            with patch.object(runner,'verify_candidate_success'),patch.object(runner,'verify_isolated_sdk'),patch.object(census.shutil,'which',return_value=str(dotnet)),patch.object(census,'execute',side_effect=fake_execute),patch.object(r.census,'write_json',side_effect=late),patch.object(runner.time,'monotonic',side_effect=lambda:clock[0]):
                self.reject(lambda:runner.prepare(lane,census,r))
            self.assertEqual(lane.stop,2.0);self.assertTrue((lane.evidence/'preparation.json').exists())
    def test_exact_sdk_metadata_directory_is_prepared_and_sealed(self):
        with tempfile.TemporaryDirectory() as tmp:
            lane=census.Lane(Path(tmp),r.census,owner);sdk=Path(tmp)/'sdk';sdk.mkdir();(sdk/'sdk-file').write_bytes(b'unchanged')
            directory=runner.prepare_sdk_metadata(lane,sdk)
            self.assertEqual(directory,sdk/'metadata');self.assertEqual(list(directory.iterdir()),[])
            self.assertEqual(lane.trees[directory],r.census.snapshot(directory));lane.check()
            self.assertEqual((sdk/'sdk-file').read_bytes(),b'unchanged')
            (directory/'unexpected').write_text('must reject')
            self.reject(lane.check)
    def test_existing_sdk_metadata_never_blessed(self):
        for kind in ('empty','populated','file','symlink','dangling-symlink'):
            with self.subTest(kind=kind),tempfile.TemporaryDirectory() as tmp:
                lane=census.Lane(Path(tmp),r.census,owner);sdk=Path(tmp)/'sdk';sdk.mkdir();metadata=sdk/'metadata'
                if kind in ('empty','populated'):
                    metadata.mkdir()
                    if kind=='populated':(metadata/'marker').write_text('must not bless')
                elif kind=='file':metadata.write_text('must not replace')
                else:metadata.symlink_to(sdk if kind=='symlink' else sdk/'absent',target_is_directory=True)
                self.reject(lambda:runner.prepare_sdk_metadata(lane,sdk));self.assertNotIn(metadata,lane.trees)
    def test_sdk_metadata_parent_symlink_is_rejected(self):
        with tempfile.TemporaryDirectory() as tmp:
            lane=census.Lane(Path(tmp),r.census,owner);actual=Path(tmp)/'actual';actual.mkdir();link=Path(tmp)/'sdk';link.symlink_to(actual,target_is_directory=True)
            self.reject(lambda:runner.prepare_sdk_metadata(lane,link));self.assertFalse((actual/'metadata').exists())
    def test_prepared_sdk_metadata_digest_matches_actual_failure(self):
        self.assertEqual(r.census.digest(['metadata']),'6e7016a4b9238039ebab5b4acfc24c0d7f2c9025ab5a928cb3e1bc586b65a640')
    def test_unknown_body_owner_cannot_seal_or_parse(self):
        with tempfile.TemporaryDirectory() as tmp:
            lane=census.Lane(Path(tmp),r.census,owner);lane.source=Path(tmp)/'src'
            dll=lane.source/'test/SharpLink.UnitTests/bin/Release/net10.0/SharpLink.UnitTests.dll';dll.parent.mkdir(parents=True);dll.write_bytes(b'fixture')
            sdk=Path(tmp)/'sdk';sdk.mkdir();dotnet=sdk/'dotnet';dotnet.write_bytes(b'not executable')
            lane.inputs[dotnet]=r.census.file_identity(dotnet);lane.trees[sdk]=r.census.snapshot(sdk);lane.identity={'binarySHA256':r.census.sha(dll)}
            original=RuntimeError('owner unknown')
            def run(label,argv,*args):
                if label=='pure-negotiation-full-python-controls':
                    self.assertEqual(argv,[sys.executable,'-B',ROOT/'eng/tests/test_pure_negotiation_full_regression.py']);self.assertEqual(args,(ROOT,60))
                    lane.sequence=33;return None
                lane.commands[label]={'ownerProvenance':None,'receipt':None};raise original
            with patch.object(runner,'verify_source'),patch.object(lane,'run',side_effect=run),patch.object(r,'report_snapshot') as snapshot,patch.object(r,'classify') as classify:
                runner.run_body(lane,r,{},set(),runner.time.monotonic()+6600)
                snapshot.assert_not_called();classify.assert_not_called()
            self.assertFalse(lane.execution_facts['ownerClosed']);self.assertNotIn('bodyExitCode',lane.execution_facts);self.assertIs(lane.errors[0],original);self.assertNotIn(lane.report,lane.seals)
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
            self.assertEqual(writes[0],b'FULL_REGRESSION_FAILED_SUMMARY\n');self.assertFalse(json.loads(writes[1])['executionQualified'])
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
    def test_preparation_seal_diagnostic_is_forwarded_without_private_values(self):
        with tempfile.TemporaryDirectory() as tmp:
            lane=census.Lane(Path(tmp),r.census,owner);lane.stage='build';lane.validation_phase='metadata-complete'
            lane.seal_diagnostics[Path('/PRIVATE_ROOT')]={'domain':'sdk','projectIndex':None,'addedCount':1,'private':'PRIVATE_DETAIL','addedPathsSHA256':'a'*64}
            original=r.census.Rejected('UC_SEAL');lane.record(original);writes=[]
            with patch.object(lane,'check',side_effect=original),patch.object(runner.os,'write',side_effect=lambda fd,data:writes.append(data) or len(data)):
                self.assertEqual(runner.finish(lane,census,r),1)
            self.assertNotIn(b'PRIVATE',b''.join(writes));facts=json.loads(writes[-1])
            self.assertEqual(facts['stage'],'build');self.assertFalse(facts['censusQualified']);self.assertFalse(facts['stableQualified'])
            self.assertEqual(facts['sealDifferences'][0]['domain'],'sdk');self.assertEqual(facts['sealDifferences'][0]['addedCount'],1)
            self.assertEqual(facts['sealDifferences'][0]['addedPathsSHA256'],'a'*64);self.assertIs(lane.errors[0],original)
    def test_census_diagnostic_survives_execution_diagnostic_failure(self):
        with tempfile.TemporaryDirectory() as tmp:
            lane=census.Lane(Path(tmp),r.census,owner);original=r.census.Rejected('UC_SEAL');lane.record(original);writes=[];secondary=OSError('PRIVATE_DIAGNOSTIC')
            with patch.object(lane,'check',side_effect=original),patch.object(runner,'terminal_diagnostic',side_effect=secondary),patch.object(runner.os,'write',side_effect=lambda fd,data:writes.append(data) or len(data)):
                self.assertEqual(runner.finish(lane,census,r),1)
            self.assertIn(secondary,lane.errors);self.assertFalse(json.loads(writes[-1])['censusQualified']);self.assertNotIn(b'PRIVATE',b''.join(writes))
    def test_census_diagnostic_failure_retains_original_and_secondary(self):
        with tempfile.TemporaryDirectory() as tmp:
            lane=census.Lane(Path(tmp),r.census,owner);original=r.census.Rejected('UC_SEAL');lane.record(original);secondary=OSError('PRIVATE_CENSUS')
            with patch.object(lane,'check',side_effect=original),patch.object(census,'terminal_diagnostic',side_effect=secondary),patch.object(runner.os,'write',return_value=1):
                self.assertEqual(runner.finish(lane,census,r),1)
            self.assertIs(lane.errors[0],original);self.assertIs(lane.errors[-1],secondary)
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
        script='import tempfile,runpy; tempfile.mkdtemp=lambda *a,**k: (_ for _ in ()).throw(OSError("PRIVATE")); runpy.run_path('+repr(str(ROOT/'eng/run-pure-negotiation-full-regression.py'))+',run_name="__main__")'
        child=subprocess.run([sys.executable,'-B','-c',script],capture_output=True,timeout=10)
        self.assertEqual(child.returncode,1);self.assertEqual(child.stdout,b'');self.assertEqual(child.stderr,b'FULL_REGRESSION_FAILED_BOOTSTRAP\n')


class IntegrationControls(unittest.TestCase):
    def run_full(self,tmp,passing=True,projection_error=None,mode=None):
        portable=load('integration_timed_fixture',ROOT/'eng/tests/test_timed_stream_full_results.py')
        statuses=('passed',)*2879 if passing else ('passed',)*2607+('error',)*272
        ordinary,native,xml,_=portable.fixture(statuses)
        lane=census.Lane(Path(tmp),r.census,owner);lane.source=Path(tmp)/'source'
        lane.preparation_complete=False;lane.sequence=32;lane.stop=runner.time.monotonic()+3600
        dll=lane.source/'test/SharpLink.UnitTests/bin/Release/net10.0/SharpLink.UnitTests.dll';dll.parent.mkdir(parents=True);dll.write_bytes(b'not executable')
        sdk=Path(tmp)/'sdk';sdk.mkdir();dotnet=sdk/'dotnet';dotnet.write_bytes(b'not executable')
        lane.inputs[dotnet]=r.census.file_identity(dotnet);lane.trees[sdk]=r.census.snapshot(sdk)
        lane.identity={'binarySHA256':r.census.sha(dll),'installedRuntimes':runner.RUNTIMES}
        lane.candidate_success_verified=True
        labels=[];originals=[];projection_calls=[];original_projection=runner.project_timed_stream
        def run(label,argv,*args):
            labels.append(label);lane.sequence+=1
            if label=='pure-negotiation-full-python-controls':
                self.assertEqual(argv,[sys.executable,'-B',ROOT/'eng/tests/test_pure_negotiation_full_regression.py'])
                self.assertEqual(args,(ROOT,60));return
            self.assertEqual(label,'ordinary-execution');self.assertEqual(args,(lane.source,1800))
            self.assertEqual(argv,r.body_argv(dotnet,dll,lane.report));self.assertNotIn('--filter-uid',argv)
            (lane.report/'session.html').write_text(portable.CANARY)
            (lane.report/'session.tunit-report.json').write_text(json.dumps(native))
            (lane.report/'junit.xml').write_text(xml)
            receipt={'stage':label,'argv':argv,'exitCode':0 if passing else 2,'timedOut':False,'timeoutSeconds':1800,'remainingOwned':{},'errors':[],'cleanupErrors':[],'reportingErrors':[]}
            lane.commands[label]={'receipt':receipt,'argv':argv,'seconds':1800,'ownerProvenance':'validated-original-success' if passing else 'original-owner-failure'}
            if mode=='owner':lane.commands[label]['ownerProvenance']=None
            if mode=='report-shape':(lane.report/'session.tunit-report.json').write_text('{"groups":[]}')
            if not passing:
                original=owner.CommandFailure(receipt,[]);originals.append(original);raise original
        def project(results,full_native,full_xml,rows,explicit,facts,source_root):
            self.assertEqual(source_root,lane.source)
            self.assertEqual(len(rows),2879);self.assertEqual(facts['observed'],2879)
            self.assertTrue(facts['executionClosureObserved']);self.assertTrue(facts['ownerClosed'])
            self.assertIn('methodStatusCounts',facts);self.assertIn('exceptionCategoryCounts',facts)
            self.assertEqual(full_native,native);self.assertEqual(full_xml,xml)
            self.assertEqual(facts['statusCounts']['failed'],0 if passing else 272)
            projection_calls.append(True)
            if projection_error is not None:raise projection_error
            result=original_projection(results,full_native,full_xml,rows,explicit,facts,ROOT)
            if mode=='post-projection-source':lane.inputs[dotnet][-1]='0'*64
            if mode=='post-projection-deadline':lane.stop=0.0
            return result
        with patch.object(runner,'verify_source'),patch.object(lane,'run',side_effect=run),patch.object(runner,'project_timed_stream',side_effect=project):
            if mode=='category':
                with patch.object(runner,'project_exception_categories',side_effect=r.census.Rejected('FR_PROJECTION')):
                    runner.run_body(lane,r,ordinary,set(),runner.time.monotonic()+6600)
            else:runner.run_body(lane,r,ordinary,set(),runner.time.monotonic()+6600)
        self.assertEqual(labels,['pure-negotiation-full-python-controls','ordinary-execution']);self.assertEqual(lane.sequence,34)
        return lane,portable,projection_calls,originals

    def test_full_and_projection_success_failure_matrix_preserves_global_facts(self):
        for passing in (True,False):
            for projection_fails in (True,False):
                with self.subTest(fullPass=passing,projectionFails=projection_fails),tempfile.TemporaryDirectory() as tmp:
                    failure=r.census.Rejected('TP_JUNIT') if projection_fails else None
                    lane,portable,calls,originals=self.run_full(tmp,passing,failure)
                    self.assertEqual(len(calls),1);self.assertEqual(lane.body_passed,passing)
                    facts=lane.execution_facts;self.assertEqual(facts['observed'],2879);self.assertEqual(facts['expected'],2879)
                    self.assertEqual(facts['statusCounts']['passed'],2879 if passing else 2607)
                    self.assertEqual(facts['statusCounts']['failed'],0 if passing else 272)
                    self.assertEqual('timedStreamDiagnostic' in facts,not projection_fails)
                    if not projection_fails:
                        projection=facts['timedStreamDiagnostic'];self.assertTrue(projection['allSelectedPassed']);self.assertEqual(len(projection['cases']),4)
                        self.assertEqual(projection['nativeFullReportCanonicalSHA256'],facts['nativeReportSHA256'])
                    if projection_fails:self.assertIn(failure,lane.errors)
                    codes=runner.rejection_codes(census,r,lane.errors)['codes'];self.assertEqual('UE_RESULTS' in codes,not passing)
                    if not passing:self.assertIn(originals[0],lane.errors)
                    with contextlib.redirect_stdout(io.StringIO()) as output:self.assertEqual(runner.finish(lane,census,r),0 if passing and not projection_fails else 1)
                    summary=json.loads(output.getvalue());self.assertNotIn(portable.CANARY,output.getvalue())
                    self.assertEqual(summary['execution']['statusCounts'],facts['statusCounts'])
                    self.assertEqual(summary['fullOrdinaryRegressionQualified'],passing and not projection_fails)
                    self.assertEqual(summary['previousCurrentFullRun']['errors'],272);self.assertEqual(summary['historicalFullOrdinaryRun']['errors'],290)

    def test_global_category_failure_does_not_hide_full_failure_or_start_timed_projection(self):
        with tempfile.TemporaryDirectory() as tmp:
            lane,_,calls,_=self.run_full(tmp,False,mode='category')
            self.assertEqual(calls,[]);self.assertNotIn('timedStreamDiagnostic',lane.execution_facts)
            self.assertEqual(lane.execution_facts['statusCounts']['failed'],272)
            self.assertTrue({'FR_PROJECTION','UE_RESULTS'}<=set(runner.rejection_codes(census,r,lane.errors)['codes']))

    def test_unknown_owner_or_malformed_full_report_prevents_projection(self):
        for mode in ('owner','report-shape'):
            with self.subTest(mode=mode),tempfile.TemporaryDirectory() as tmp:
                lane,_,calls,_=self.run_full(tmp,mode=mode)
                self.assertEqual(calls,[]);self.assertNotIn('timedStreamDiagnostic',lane.execution_facts)
                self.assertFalse(lane.body_passed);self.assertTrue(lane.errors)

    def test_source_seal_and_deadline_after_projection_revoke_qualification(self):
        for mode in ('post-projection-source','post-projection-deadline'):
            with self.subTest(mode=mode),tempfile.TemporaryDirectory() as tmp:
                lane,_,calls,_=self.run_full(tmp,mode=mode)
                self.assertEqual(calls,[True]);self.assertEqual(lane.execution_facts['observed'],2879)
                with contextlib.redirect_stdout(io.StringIO()),patch.object(runner.os,'write',return_value=1):self.assertEqual(runner.finish(lane,census,r),1)
                self.assertTrue(lane.errors)

    def test_projection_failure_and_failed_durable_writes_preserve_full_failure(self):
        with tempfile.TemporaryDirectory() as tmp:
            original=r.census.Rejected('TP_EXCEPTION')
            lane,_,_,_=self.run_full(tmp,False,original)
            with patch.object(r.census,'write_json',side_effect=OSError('PRIVATE_WRITE')),patch.object(runner.os,'write',return_value=1):
                self.assertEqual(runner.finish(lane,census,r),1)
            self.assertIn(original,lane.errors);self.assertEqual(lane.execution_facts['statusCounts']['failed'],272)
            self.assertIn('UE_RESULTS',runner.rejection_codes(census,r,lane.errors)['codes'])
            self.assertTrue(any(isinstance(error,OSError) for error in lane.errors))

    def test_projection_failure_persistence_loss_keeps_original_suite_rejection(self):
        with tempfile.TemporaryDirectory() as tmp:
            original=r.census.Rejected('TP_EXCEPTION');real_persist=census.persist
            def failed_persist(directory,errors,serial):
                if original not in errors:return real_persist(directory,errors,serial)
                with patch.object(census.os,'open',side_effect=OSError('PRIVATE_PERSIST')):
                    return real_persist(directory,errors,serial)
            with patch.object(census,'persist',side_effect=failed_persist):
                lane,_,_,_=self.run_full(tmp,False,original)
            self.assertIn(original,lane.errors);self.assertFalse(lane.durable)
            self.assertEqual(lane.execution_facts['statusCounts']['failed'],272)
            self.assertIn('UE_RESULTS',runner.rejection_codes(census,r,lane.errors)['codes'])
            suite_index=next(i for i,error in enumerate(lane.errors) if type(error) is r.census.Rejected and error.code=='UE_RESULTS')
            self.assertLess(suite_index,lane.errors.index(original))
            self.assertTrue(any(isinstance(error,OSError) for error in lane.errors))
            with contextlib.redirect_stdout(io.StringIO()) as output:self.assertEqual(runner.finish(lane,census,r),1)
            self.assertNotIn('PRIVATE_PERSIST',output.getvalue())

    def test_portable_bytes_and_public_rejection_vocabulary_are_exact(self):
        self.assertEqual(r.census.sha(ROOT/'eng/timed-stream-full-results.py'),'1b1c0c9d56d240dc981d0e24ceedbebc1a13c1d138e6203bd9d67fdd8ad674ef')
        self.assertEqual(r.census.sha(ROOT/'eng/tests/test_timed_stream_full_results.py'),'eee2e402c75a89ba1157c1d13284064fb3d2394cc344dfdf567a57b4dacf8ca1')
        helper=load('proof_timed_codes',ROOT/'eng/timed-stream-full-results.py')
        errors=[r.census.Rejected(code) for code in helper.CODES]+[RuntimeError('PRIVATE_CODE')]
        projected=runner.rejection_codes(census,r,errors)
        self.assertEqual(projected['codes'],sorted(helper.CODES));self.assertEqual(projected['unclassifiedExceptionNodes'],1)
        self.assertNotIn('PRIVATE_CODE',json.dumps(projected))

    def test_bound_candidate_success_evidence_is_exact_and_not_full_qualification(self):
        result=runner.verify_candidate_success(r.census)
        self.assertEqual(runner.CANDIDATE_SUCCESS_SHA256,'b6ba59c1cdf4cccc61db90a31226f342286ba4cdb8f8bfc323e7f5f6127fd90d')
        self.assertEqual(runner.HELPERS[runner.CANDIDATE_SUCCESS_PATH],runner.CANDIDATE_SUCCESS_SHA256)
        self.assertEqual(result['head'],runner.CANDIDATE_HEAD);self.assertEqual(len(result['successfulStageRows']),49)
        summary=result['summary'];self.assertTrue(summary['candidateValidationQualified']);self.assertFalse(summary['fullSuiteQualified'])
        self.assertEqual({row['argumentIdentity']['value'] for row in summary['cases'][:2]},{True,False})
        self.assertEqual(len({row['argumentIdentity']['caseID'] for row in summary['cases']}),16)
        self.assertEqual(summary['historicalFullRun']['errors'],272);self.assertEqual(summary['originalFullRun']['errors'],290)
        self.assertTrue(result['historicalTimedFailureUnresolved'])

    def test_pending_missing_failed_or_wrong_candidate_evidence_cannot_open_gate(self):
        for digest in (None,'','0'*63):
            with patch.object(runner,'CANDIDATE_SUCCESS_SHA256',digest):
                with self.assertRaises(r.census.Rejected):runner.verify_candidate_success(r.census)
        result=r.census.read_json(ROOT/runner.CANDIDATE_SUCCESS_PATH)
        for mode in ('valid','wrong-head','failed','incomplete','owner','source','catalog','errors'):
            value=copy.deepcopy(result)
            if mode=='wrong-head':value['head']='0'*40
            if mode=='failed':value['summary']['candidateValidationQualified']=False
            if mode=='incomplete':value['summary']['cases'].pop()
            if mode=='owner':value['summary']['cases'][0]['ownerClosed']=False
            if mode=='source':value['summary']['sourceTrees']['test']='0'*40
            if mode=='catalog':value['summary']['candidateCatalogSHA256']='0'*64
            if mode=='errors':value['summary']['errorOccurrences']=1
            with patch.object(runner,'CANDIDATE_SUCCESS_SHA256','a'*64),patch.object(r.census,'sha',return_value='a'*64),patch.object(r.census,'read_json',return_value=value):
                if mode=='valid':self.assertEqual(runner.verify_candidate_success(r.census),value)
                else:
                    with self.assertRaises(r.census.Rejected):runner.verify_candidate_success(r.census)


if __name__=='__main__':unittest.main()
