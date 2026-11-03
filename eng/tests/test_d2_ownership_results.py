"""Actual Python protocol controls only; no C# runtime evidence or SDK invocation."""
import contextlib
import copy
import importlib.util
import io
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest
from unittest import mock

sys.dont_write_bytecode = True
ROOT = Path(__file__).resolve().parents[2]

def module(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    value = importlib.util.module_from_spec(spec); spec.loader.exec_module(value); return value

r = module('d2_test_results', ROOT / 'eng/d2-ownership-results.py')
runner = module('d2_test_runner', ROOT / 'eng/run-d2-ownership-controls.py')
runner.initialize()
accepted = module('d2_pinned_synthetic_fixture', ROOT / 'eng/tests/test_retirement_results.py')
CATALOG = r.read_json(ROOT / 'eng/d2-ownership-controls.json')

def rows(catalog):
    return [dict(uid='actual-synthetic-' + str(i), displayName=c['displayName'],
        type=dict(assemblyFullName=catalog['assemblyFullName'], namespace=c['namespace'],
            typeName=c['className'], methodName=c['methodName'], methodArity=0,
            parameterTypeFullNames=[], returnTypeFullName=c['returnType']),
        location=dict(file=str(r.reported_source_path(c['source'])), lineStart=c['span']['signatureLine'],
                      lineEnd=c['span']['endLine'])) for i,c in enumerate(catalog['cases'])]

class D2ProtocolTests(unittest.TestCase):
    def setUp(self):
        self.base = accepted.ClassifierTests(); self.base.setUp(); self.addCleanup(self.base.doCleanups)

    def discovery(self, value):
        return r.discovery(value, CATALOG['cases'], ROOT, CATALOG['assemblyFullName'])

    def test_exact_35_catalog_complete_spans(self):
        r.verify_catalog(ROOT, CATALOG)
        self.assertEqual(sum(s['fixtureInstances'] for s in CATALOG['sourceSpans']),41)
        self.assertEqual(sum(s['hasExpectedQuarantine'] for s in CATALOG['sourceSpans']),13)

    def test_count_identity_and_prefabricated_uid_rejected(self):
        changes = [lambda c:c['cases'].pop(), lambda c:c['cases'].__setitem__(1,copy.deepcopy(c['cases'][0])),
            lambda c:c['cases'][0].update(actualUID='pretend'), lambda c:c.update(expectedFixtureInstances=35),
            lambda c:c['projects'].append('test/SharpLink.UnitTests/SharpLink.UnitTests.csproj'),
            lambda c:c['sourcePins'].pop('test/Directory.Build.props')]
        for change in changes:
            c=copy.deepcopy(CATALOG);change(c)
            with self.assertRaises(ValueError):r.verify_catalog(ROOT,c)

    def test_every_method_end_truncation_rejected(self):
        for index in range(35):
            c=copy.deepcopy(CATALOG);c['sourceSpans'][index]['span']['endLineInclusive']-=1
            with self.assertRaises(ValueError):r.verify_spans(ROOT,c)

    def test_span_hash_boundaries_and_size_rejected(self):
        for field,value in [('sha256','0'*64),('bytes',0),('byteStart',0),('byteEndExclusive',1),
            ('attributeText','[Fact]'),('signatureText','public Task Wrong()'),('signatureContinuationText','{'),
            ('closingLineText','}')]:
            c=copy.deepcopy(CATALOG);c['sourceSpans'][0]['span'][field]=value
            with self.assertRaises(ValueError):r.verify_spans(ROOT,c)
        c=copy.deepcopy(CATALOG);c['sourceSpans'][0]['span']['nextBoundary']['text']='secret'
        with self.assertRaises(ValueError):r.verify_spans(ROOT,c)
        c=copy.deepcopy(CATALOG);c['sourceSpans'][0]['fileBytes']+=1
        with self.assertRaises(ValueError):r.verify_spans(ROOT,c)

    def test_ancestor_complete_inventory_and_absence(self):
        with tempfile.TemporaryDirectory() as d:
            target=Path(d)/'source';target.mkdir()
            for name in CATALOG['sourcePins']:
                q=target/name;q.parent.mkdir(parents=True,exist_ok=True);shutil.copyfile(ROOT/name,q)
            r.verify_catalog(target,CATALOG)
            q=target/'test/Directory.Build.targets';q.write_text('<Project/>')
            with self.assertRaises(ValueError):r.check_ancestors(target,CATALOG)
            q.unlink();q=target/'src/Directory.Build.props';q.write_text('<Project/>')
            with self.assertRaises(ValueError):r.check_ancestors(target,CATALOG)
        c=copy.deepcopy(CATALOG);c['ancestorInputs']['inputs'].pop('test/Directory.Build.targets')
        with self.assertRaises(ValueError):r.check_ancestors(ROOT,c)

    def test_safe_command_receipt_never_exposes_arbitrary_fields(self):
        secret='PRIVATE_RECEIPT_MARKER'
        value=runner.safe_command_record(secret,dict(phase=secret,provenance=secret,receipt=dict(
            exitCode=secret,timedOut=secret,errors=[secret],cleanupErrors=[secret,secret],
            reportingErrors=[secret],remainingOwned={'PRIVATE_PID':secret},argv=[secret])))
        self.assertNotIn(secret,json.dumps(value));self.assertNotIn('PRIVATE_PID',json.dumps(value))
        self.assertIsNone(value['stage']);self.assertIsNone(value['exitCode'])
        self.assertEqual(value['executeErrorCount'],1);self.assertEqual(value['cleanupErrorCount'],2)
        good=runner.safe_command_record('build',dict(phase='owner-call',provenance='original-owner-failure',
            receipt=dict(exitCode=1,timedOut=False,errors=[],cleanupErrors=[],reportingErrors=[],remainingOwned={})))
        self.assertEqual((good['stage'],good['exitCode'],good['timedOut'],good['remainingOwnedCount']),('build',1,False,0))

    def test_compiler_diagnostic_is_only_code_and_allowlisted_coordinates(self):
        with tempfile.TemporaryDirectory() as d:
            work=Path(d);source=work/'src';logs=work/'logs';logs.mkdir();source.mkdir()
            known=source/'test'/'Known.cs';known.parent.mkdir();known.write_text('public class Known {}')
            secret='PRIVATE_COMPILER_MESSAGE'
            lines=[str(known)+'(12,7): error CS0123: '+secret,
                   '/_/test/Known.cs(12,7): error CS0123: '+secret,
                   '/PRIVATE_EXTERNAL_SOURCE.cs(44,2): warning CS9999: '+secret,
                   'CSC : error CS0006: '+secret,
                   secret+' raw arbitrary output']
            (logs/'build.stdout').write_text('\n'.join(lines));(logs/'build.stderr').write_text(secret)
            value=runner.compiler_diagnostic_summary(logs,source,{known:'hash'})
            encoded=json.dumps(value)
            for forbidden in (secret,'PRIVATE_EXTERNAL_SOURCE',str(work),'raw arbitrary output'):self.assertNotIn(forbidden,encoded)
            self.assertEqual(value['diagnostics'][0],dict(severity='error',code='CS0123',source='test/Known.cs',line=12,column=7,occurrences=2))
            self.assertIsNone(value['diagnostics'][1]['source']);self.assertIsNone(value['diagnostics'][1]['line'])
            self.assertEqual(value['diagnostics'][2]['code'],'CS0006')
            (logs/'build.stdout').write_bytes(b'x'*(1024*1024+2))
            bounded=runner.compiler_diagnostic_summary(logs,source,{known:'hash'})
            self.assertTrue(bounded['files'][0]['truncated']);self.assertEqual(bounded['files'][0]['sampledBytes'],1024*1024)
            (logs/'build.stdout').unlink();(logs/'build.stdout').symlink_to(logs/'build.stderr')
            with self.assertRaises(OSError):runner.compiler_diagnostic_summary(logs,source,{known:'hash'})

    def test_real_main_owner_failure_keeps_private_graph_and_safe_receipt(self):
        with tempfile.TemporaryDirectory() as d:
            private=Path(d)/'lane';private.mkdir();output=io.StringIO()
            marker=RuntimeError('PRIVATE_ORIGINAL_OWNER_FAILURE')
            row=dict(stage='head',exitCode=7,timedOut=False,errors=['PRIVATE_ROW_ERROR'],
                     cleanupErrors=[],reportingErrors=[],remainingOwned={})
            failure=runner.owner.CommandFailure(row,[marker])
            try:
                with mock.patch.object(runner.owner,'enable_subreaper'), \
                     mock.patch.object(runner.tempfile,'mkdtemp',return_value=str(private)), \
                     mock.patch.object(runner.owner,'run_owned',side_effect=failure),contextlib.redirect_stdout(output):
                    self.assertEqual(runner.main(),1)
                console=output.getvalue()
                self.assertIn('command-diagnostic:',console);self.assertIn('"exitCode": 7',console)
                for text in ('PRIVATE_ORIGINAL_OWNER_FAILURE','PRIVATE_ROW_ERROR'):self.assertNotIn(text,console)
                graph=(private/'failure-graph.json').read_text()
                self.assertIn('PRIVATE_ORIGINAL_OWNER_FAILURE',graph);self.assertIn('PRIVATE_ROW_ERROR',graph)
            finally:
                fd=getattr(runner.main,'private_directory_fd',None)
                if fd is not None:os.close(fd);del runner.main.private_directory_fd

    def test_diagnostic_failure_does_not_replace_original_owner_error(self):
        with tempfile.TemporaryDirectory() as d:
            private=Path(d)/'lane';private.mkdir();output=io.StringIO()
            primary=RuntimeError('PRIVATE_ORIGINAL');diagnostic=OSError('PRIVATE_DIAGNOSTIC_READ')
            try:
                with mock.patch.object(runner.owner,'enable_subreaper'), \
                     mock.patch.object(runner.tempfile,'mkdtemp',return_value=str(private)), \
                     mock.patch.object(runner.owner,'run_owned',side_effect=primary), \
                     mock.patch.object(runner,'report_command_diagnostics',side_effect=diagnostic),contextlib.redirect_stdout(output):
                    self.assertEqual(runner.main(),1)
                graph=(private/'failure-graph.json').read_text()
                for text in ('PRIVATE_ORIGINAL','PRIVATE_DIAGNOSTIC_READ'):
                    self.assertIn(text,graph);self.assertNotIn(text,output.getvalue())
            finally:
                fd=getattr(runner.main,'private_directory_fd',None)
                if fd is not None:os.close(fd);del runner.main.private_directory_fd

    def test_explicit_restore_config_is_not_an_ancestor_input(self):
        with tempfile.TemporaryDirectory() as d:
            work=Path(d);source=work/'src';source.mkdir()
            for name in CATALOG['sourcePins']:
                q=source/name;q.parent.mkdir(parents=True,exist_ok=True);shutil.copyfile(ROOT/name,q)
            config=runner.write_restore_config(work)
            self.assertEqual(config,work/'nuget-config'/'NuGet.Config')
            self.assertNotIn(config.parent,source.parents)
            self.assertEqual(config.read_text(),'<configuration><packageSources><clear/><add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3"/></packageSources></configuration>\n')
            r.verify_catalog(source,CATALOG)
            # The first CI's layout is still rejected, with identical config bytes.
            for name in ('NuGet.Config','nuget.config'):
                ancestor=work/name;shutil.copyfile(config,ancestor)
                with self.assertRaisesRegex(ValueError,'External ancestor input exists'):
                    r.verify_catalog(source,CATALOG)
                ancestor.unlink()
                r.verify_catalog(source,CATALOG)

    def test_compile_extra_even_rehashed_rejected(self):
        with tempfile.TemporaryDirectory() as d:
            target=Path(d)/'source';target.mkdir();c=copy.deepcopy(CATALOG)
            for name in c['sourcePins']:
                q=target/name;q.parent.mkdir(parents=True,exist_ok=True);shutil.copyfile(ROOT/name,q)
            q=target/c['project'];q.write_text(q.read_text().replace('</Project>','<ItemGroup><Compile Include="extra.cs"/></ItemGroup></Project>'))
            c['sourcePins'][c['project']]=runner.sha(q)
            with self.assertRaises(ValueError):r.verify_catalog(target,c)

    def test_actual_discovery_bijection_35(self):
        matched=self.discovery(dict(schemaVersion=1,tests=rows(CATALOG)))
        self.assertEqual(len(r.freeze_ledger(list(reversed(matched)),CATALOG['cases'])),35)
        self.assertEqual(r.freeze_ledger(matched,CATALOG['cases'])[0]['discovery']['uid'],'actual-synthetic-0')

    def test_discovery_count_uid_source_signature_rejections(self):
        for change in [lambda a:a.pop(),lambda a:a.append(copy.deepcopy(a[0])),
            lambda a:a[1].update(uid=a[0]['uid']),lambda a:a[0].update(uid='--unfiltered'),
            lambda a:a[0]['type'].update(assemblyFullName='wrong'),lambda a:a[0]['type'].update(parameterTypeFullNames=['System.Int32']),
            lambda a:a[0]['location'].update(file='/other/source.cs'),lambda a:a[0]['location'].update(lineEnd=22),
            lambda a:a[0]['type'].update(methodName='Other')]:
            a=rows(CATALOG);change(a)
            with self.assertRaises(ValueError):self.discovery(dict(schemaVersion=1,tests=a))
        matched=self.discovery(dict(schemaVersion=1,tests=rows(CATALOG)))
        with self.assertRaises(ValueError):r.freeze_ledger(matched[:-1],CATALOG['cases'])

    def test_singleton_exact_argv(self):
        argv=r.case_argv('/sdk/dotnet','/build/a.dll','real-uid',Path('/r0'),'150s')
        self.assertEqual(argv,['/sdk/dotnet','/build/a.dll','--filter-uid','real-uid','--maximum-parallel-tests','1',
            '--timeout','150s','--results-directory','/r0','--report-html','--report-html-filename','/r0/session.html','--junit-output-path','/r0/junit.xml'])
        self.assertNotIn('--treenode-filter',argv)

    def verdict(self,native=None,xml=None,generations=()):
        return r.passed(self.base.native if native is None else native,self.base.xml if xml is None else xml,
            self.base.row,self.base.source,'1.73.19.0',generations=generations)

    def test_native_junit_positive_filter_diagnostic_only(self):
        self.assertEqual(self.verdict()['status'],'PASS')
        n=copy.deepcopy(self.base.native);n['filter']={'arbitrary':'DO_NOT_EMIT'}
        verdict=self.verdict(n);self.assertTrue(verdict['filterDiagnostic']['present'])
        self.assertNotIn('DO_NOT_EMIT',json.dumps(verdict))
        with self.assertRaises(ValueError):self.verdict(generations=('a'*32,))

    def test_native_and_junit_failures_never_green(self):
        for status in ('failed','skipped','cancelled','timedOut','error'):
            n=copy.deepcopy(self.base.native);n['groups'][0]['tests'][0]['status']=status
            with self.assertRaises(ValueError):self.verdict(n)
        for key,value in [('id','wrong'),('filePath','/wrong'),('endLineNumber',99),('retryAttempt',0)]:
            n=copy.deepcopy(self.base.native);n['groups'][0]['tests'][0][key]=value
            with self.assertRaises(ValueError):self.verdict(n)
        for key,value in [('runtimeVersion','.NET 9.0.1'),('tunitVersion','2.0'),('publicationGeneration','')]:
            n=copy.deepcopy(self.base.native);n[key]=value
            with self.assertRaises(ValueError):self.verdict(n)
        n=copy.deepcopy(self.base.native);n['groups'][0]['tests']*=2
        with self.assertRaises(ValueError):self.verdict(n)
        for xml in (self.base.xml.replace('errors="0"','errors="1"'),self.base.xml.replace('name="Chosen"','name="Other"'),
                    self.base.xml.replace('classname="Example" />','classname="Example"><skipped/></testcase>'),'<bad'):
            with self.assertRaises((ValueError,r.base.common.ET.ParseError)):self.verdict(xml=xml)

    def test_junit_properties_cannot_hide_failures_or_extra_cases(self):
        start=self.base.xml.index('<testcase')
        for inner in ('<testcase name="Other" classname="Example"><failure>failed</failure></testcase>',
                      '<property name="x"><testcase name="Other"/></property>',
                      '<property name="x"><failure>failed</failure></property>',
                      '<skipped/>','<error/>','<system-out>private</system-out>',
                      '<properties/>','<property name="x"><unknown/></property>'):
            xml=self.base.xml[:start]+'<properties>'+inner+'</properties>'+self.base.xml[start:]
            with self.assertRaises(ValueError):self.verdict(xml=xml)
        for inner in ('<property name="runtime" value=".NET 10"/>','<property name="runtime">.NET 10</property>',''):
            xml=self.base.xml[:start]+'<properties>'+inner+'</properties>'+self.base.xml[start:]
            self.assertEqual(self.verdict(xml=xml)['status'],'PASS')

    def test_owner_all_independent_failure_channels_reject(self):
        row=dict(stage='case-0',argv=['dotnet'],timeoutSeconds=180,exitCode=0,timedOut=False,errors=[],cleanupErrors=[],reportingErrors=[],remainingOwned={},elapsedSeconds=1)
        r.verify_receipt(row,'case-0',['dotnet'],180)
        for key,value in [('exitCode',1),('timedOut',True),('errors',['x']),('cleanupErrors',['x']),('reportingErrors',['x']),('remainingOwned',{'12':'unknown'})]:
            bad=copy.deepcopy(row);bad[key]=value
            with self.assertRaises(ValueError):r.verify_receipt(bad,'case-0',['dotnet'],180)

    def test_report_symlink_hardlink_and_mutation_reject(self):
        with tempfile.TemporaryDirectory() as d:
            p=Path(d);q=p/'report';q.mkdir();identity=r.directory_identity_strict(q)
            outside=p/'x';outside.write_text('{}');target=q/'session.tunit-report.json';target.symlink_to(outside)
            with self.assertRaises(ValueError):r.regular_report(q,identity,target.name)
            target.unlink();os.link(outside,target)
            with self.assertRaises(ValueError):r.regular_report(q,identity,target.name)
            target.unlink();target.write_text('{}');seal=r.seal_directory(q);target.write_text('{"changed":true}')
            with self.assertRaises(ValueError):r.verify_seal(q,seal)

    def test_budget_exact_min_and_no_launch_exhaustion(self):
        r.validate_budget(CATALOG);self.assertEqual(r.launch_budget(180,100),84)
        self.assertEqual(r.launch_budget(180,1000),180)
        for left in (16,0,-1,float('nan')):
            with self.assertRaises(ValueError):r.launch_budget(180,left)
        c=copy.deepcopy(CATALOG);c['limits']['commandBudgetSeconds']=1800
        with self.assertRaises(ValueError):r.validate_budget(c)

    def test_safe_console_does_not_expose_message_type_or_raw_logs(self):
        secret='SENSITIVE_MESSAGE\n/private/token/path <raw stdout>'
        evil=type(secret,(RuntimeError,),{})
        for error in (RuntimeError(secret),evil(secret),ExceptionGroup(secret,[ValueError(secret),OSError(secret)])):
            output=io.StringIO()
            with contextlib.redirect_stdout(output):runner.summarize_failure(error)
            self.assertNotIn(secret,output.getvalue());self.assertNotIn('private',output.getvalue())
            self.assertNotIn('raw stdout',output.getvalue());json.loads(output.getvalue().split(': ',1)[1])
        with mock.patch('builtins.print',side_effect=OSError(secret)):
            with self.assertRaises(OSError):runner.summarize_failure(RuntimeError(secret))

    def test_private_graph_preserves_occurrences_without_console(self):
        original=ValueError('PRIVATE_ORIGINAL');other=OSError('PRIVATE_REPORTER')
        graph=r.base.exception_graph([original,other,original])
        self.assertEqual(len(graph['orderedRootOccurrences']),3)
        self.assertEqual(graph['orderedRootOccurrences'][0],graph['orderedRootOccurrences'][2])
        self.assertIn('PRIVATE_ORIGINAL',json.dumps(graph))
        with tempfile.TemporaryDirectory() as d:
            fd=os.open(d,os.O_RDONLY|os.O_DIRECTORY)
            try:
                errors=[original,other];self.assertEqual(r.persist_failures(fd,errors),'failure-graph.json')
                self.assertTrue((Path(d)/'failure-graph.json').is_file())
            finally:os.close(fd)

    def test_actual_firstfailure_stops_and_retains_all_unrun(self):
        marker = RuntimeError('PRIVATE_FIRST_COMMAND_FAILURE')
        with tempfile.TemporaryDirectory() as d:
            private = Path(d) / 'lane'; private.mkdir()
            output = io.StringIO()
            try:
                with mock.patch.object(runner.owner, 'enable_subreaper'), \
                     mock.patch.object(runner.tempfile, 'mkdtemp', return_value=str(private)), \
                     mock.patch.object(runner.owner, 'run_owned', side_effect=marker) as execute, \
                     contextlib.redirect_stdout(output):
                    self.assertEqual(runner.main(), 1)
                self.assertEqual(execute.call_count, 1)
                summary = json.loads((private/'summary.json').read_text())
                self.assertEqual(summary['groups'],dict(PASS=0,FAILED_OR_INCOMPLETE=0,INCOMPLETE=0,UNRUN=35))
                graph = (private/'failure-graph.json').read_text()
                self.assertIn('PRIVATE_FIRST_COMMAND_FAILURE',graph)
                self.assertNotIn('PRIVATE_FIRST_COMMAND_FAILURE',output.getvalue())
            finally:
                fd=getattr(runner.main,'private_directory_fd',None)
                if fd is not None:os.close(fd);del runner.main.private_directory_fd

    def test_primary_finalguard_and_reporter_failures_remain_private(self):
        primary=RuntimeError('PRIVATE_EXECUTE');guard=ValueError('PRIVATE_GUARD');reporter=OSError('PRIVATE_CONSOLE')
        with tempfile.TemporaryDirectory() as d:
            private=Path(d)/'lane';private.mkdir()
            try:
                with mock.patch.object(runner.owner,'enable_subreaper'), \
                     mock.patch.object(runner.tempfile,'mkdtemp',return_value=str(private)), \
                     mock.patch.object(runner.owner,'run_owned',side_effect=primary), \
                     mock.patch.object(runner.results,'verify_inputs',side_effect=[None,guard,guard]), \
                     mock.patch('builtins.print',side_effect=reporter):
                    self.assertEqual(runner.main(),1)
                graph=(private/'failure-graph.json').read_text()
                for text in ('PRIVATE_EXECUTE','PRIVATE_GUARD','PRIVATE_CONSOLE'):self.assertIn(text,graph)
            finally:
                fd=getattr(runner.main,'private_directory_fd',None)
                if fd is not None:os.close(fd);del runner.main.private_directory_fd

    def test_root_nuget_inputs_trigger_workflow(self):
        workflow=(ROOT/'.github/workflows/d2-ownership-controls.yml').read_text()
        for name in ('NuGet.Config','nuget.config','Directory.Build.props','Directory.Build.targets','Directory.Packages.props','global.json'):
            self.assertIn("      - '"+name+"'",workflow)
        self.assertIn("branches: ['wip/775-code-recovery-20261102']",workflow)
        self.assertIn('contents: read',workflow)
        self.assertNotIn('workflow_dispatch:',workflow)

    def test_true_script_entry_helper_and_import_failures_are_private(self):
        for kind in ('helper','import'):
            with self.subTest(kind=kind),tempfile.TemporaryDirectory() as d:
                source=Path(d)/'source';temp=Path(d)/'tmp';temp.mkdir();(source/'eng').mkdir(parents=True)
                names=['eng/run-d2-ownership-controls.py','eng/d2-ownership-results.py',*r.HELPER_PINS]
                for name in names:
                    dest=source/name;dest.parent.mkdir(parents=True,exist_ok=True);shutil.copyfile(ROOT/name,dest)
                secret='PRIVATE_STARTUP_RAW_MESSAGE'
                if kind=='helper':
                    path=source/'eng/package-smoke-process.py';path.write_text(path.read_text()+'\n# changed input\n')
                else:
                    path=source/'eng/d2-ownership-results.py';path.write_text('raise RuntimeError('+repr(secret)+')\n')
                env=dict(os.environ,TMPDIR=str(temp),PYTHONDONTWRITEBYTECODE='1')
                outcome=subprocess.run([sys.executable,'-B',str(source/'eng/run-d2-ownership-controls.py')],capture_output=True,env=env,timeout=10)
                self.assertEqual(outcome.returncode,1)
                console=(outcome.stdout+outcome.stderr).decode()
                for forbidden in ('Traceback',str(source),secret,'Pinned helper input changed','raise RuntimeError'):
                    self.assertNotIn(forbidden,console)
                self.assertEqual(outcome.stderr,b'')
                receipt=list(temp.glob('sld235-startup-*/failure-graph.json'))
                self.assertEqual(len(receipt),1)
                graph=receipt[0].read_text()
                self.assertIn(secret if kind=='import' else 'Pinned helper input changed',graph)

    def test_preflight_failure_never_calls_owner(self):
        with tempfile.TemporaryDirectory() as d:
            private=Path(d)/'lane';private.mkdir()
            try:
                with mock.patch.object(runner.owner,'enable_subreaper'), \
                     mock.patch.object(runner.tempfile,'mkdtemp',return_value=str(private)), \
                     mock.patch.object(runner.results,'verify_catalog',side_effect=ValueError('PRIVATE_INPUT_CHANGED')), \
                     mock.patch.object(runner.owner,'run_owned') as execute,contextlib.redirect_stdout(io.StringIO()):
                    self.assertEqual(runner.main(),1)
                execute.assert_not_called()
                self.assertIn('PRIVATE_INPUT_CHANGED',(private/'failure-graph.json').read_text())
            finally:
                fd=getattr(runner.main,'private_directory_fd',None)
                if fd is not None:os.close(fd);del runner.main.private_directory_fd

    def test_post_owner_input_mutation_blocks_next_command(self):
        with tempfile.TemporaryDirectory() as d:
            private=Path(d)/'lane';private.mkdir();source=Path(d)/'source';source.mkdir();calls=[]
            for name in [*CATALOG['sourcePins'],'eng/d2-ownership-controls.json']:
                dest=source/name;dest.parent.mkdir(parents=True,exist_ok=True);shutil.copyfile(ROOT/name,dest)
            def execute(label,argv,cwd,env,logs,seconds):
                calls.append(label)
                (source/'src/Directory.Build.props').write_text('<Project><!-- changed after command --></Project>')
                out=logs/(label+'.stdout');err=logs/(label+'.stderr');out.write_text('a'*40+'\n');err.write_text('')
                (logs/(label+'.json')).write_text(json.dumps(dict(stage=label,argv=argv,timeoutSeconds=seconds,exitCode=0,
                    timedOut=False,errors=[],cleanupErrors=[],reportingErrors=[],remainingOwned={},elapsedSeconds=.1)))
                return out,err
            try:
                with mock.patch.object(runner,'ROOT',source),mock.patch.object(runner.owner,'enable_subreaper'), \
                     mock.patch.object(runner.tempfile,'mkdtemp',return_value=str(private)), \
                     mock.patch.object(runner.owner,'run_owned',side_effect=execute),contextlib.redirect_stdout(io.StringIO()):
                    self.assertEqual(runner.main(),1)
                self.assertEqual(calls,['head'])
                self.assertIn('Source input changed',(private/'failure-graph.json').read_text())
            finally:
                fd=getattr(runner.main,'private_directory_fd',None)
                if fd is not None:os.close(fd);del runner.main.private_directory_fd

    def test_launch_recomputes_budget_after_slow_preflight(self):
        for elapsed,expected in ((9500,60),(9550,34),(9590,None)):
            with self.subTest(elapsed=elapsed),tempfile.TemporaryDirectory() as d:
                private=Path(d)/'lane';private.mkdir();clock=[0.0];actual=[]
                def preflight(*args):clock[0]=elapsed
                def execute(*args):actual.append(args[-1]);raise RuntimeError('PRIVATE_STOP_AFTER_FIRST_LAUNCH')
                try:
                    with mock.patch.object(runner.owner,'enable_subreaper'), \
                         mock.patch.object(runner.tempfile,'mkdtemp',return_value=str(private)), \
                         mock.patch.object(runner.time,'monotonic',side_effect=lambda:clock[0]), \
                         mock.patch.object(runner.results,'verify_catalog',side_effect=preflight), \
                         mock.patch.object(runner.owner,'run_owned',side_effect=execute),contextlib.redirect_stdout(io.StringIO()):
                        self.assertEqual(runner.main(),1)
                    self.assertEqual(actual,[] if expected is None else [expected])
                finally:
                    fd=getattr(runner.main,'private_directory_fd',None)
                    if fd is not None:os.close(fd);del runner.main.private_directory_fd

    def test_safe_record_rejects_arbitrary_field_content_and_types(self):
        secret='PRIVATE_CATALOG_FIELD_MARKER'
        record=dict(designId='D2-01',method='Chosen',caseIndex=0,status='UNRUN',uid=None)
        for key in ('method','designId','caseIndex','uid','status'):
            for value in (secret,{'nested':secret},[secret],True,17):
                bad=dict(record);bad[key]=value
                public=json.dumps(runner.safe_record(bad))
                self.assertNotIn(secret,public)
        self.assertEqual(runner.safe_record(record)['designId'],'D2-01')
        self.assertEqual(runner.safe_record(record)['caseIndex'],0)
        self.assertEqual(runner.safe_record(record)['status'],'UNRUN')
        self.assertNotIn('Chosen',json.dumps(runner.safe_record(record)))

    def test_true_script_entry_malformed_catalog_does_not_leak(self):
        secret='PRIVATE_CATALOG_ENTRY_MARKER'
        for field,value in (('methodName',secret),('designId',secret),('caseIndex',{'secret':secret}),
                            ('actualUID',[secret])):
            with self.subTest(field=field),tempfile.TemporaryDirectory() as d:
                source=Path(d)/'source';temp=Path(d)/'tmp';temp.mkdir();(source/'eng').mkdir(parents=True)
                names=['eng/run-d2-ownership-controls.py','eng/d2-ownership-results.py',*CATALOG['sourcePins']]
                for name in names:
                    dest=source/name;dest.parent.mkdir(parents=True,exist_ok=True);shutil.copyfile(ROOT/name,dest)
                catalog=copy.deepcopy(CATALOG);catalog['cases'][0][field]=value
                (source/'eng/d2-ownership-controls.json').write_text(json.dumps(catalog))
                outcome=subprocess.run([sys.executable,'-B',str(source/'eng/run-d2-ownership-controls.py')],
                    capture_output=True,env=dict(os.environ,TMPDIR=str(temp),PYTHONDONTWRITEBYTECODE='1'),timeout=10)
                self.assertEqual(outcome.returncode,1)
                console=(outcome.stdout+outcome.stderr).decode()
                for forbidden in (secret,'Traceback',str(source)):
                    self.assertNotIn(forbidden,console)
                self.assertEqual(outcome.stderr,b'')
                self.assertNotIn('stage: ',console)
                self.assertIn('"UNRUN": 35',console)

    def test_restored_source_inventory_and_metadata(self):
        with tempfile.TemporaryDirectory() as d:
            p=Path(d);package=p/'pkg'/'1.0';package.mkdir(parents=True)
            (package/'x.nupkg').write_bytes(b'archive')
            m=package/'.nupkg.metadata';m.write_text(json.dumps({'source':'https://api.nuget.org/v3/index.json'}))
            runner.verify_package_sources(p)
            m.write_text(json.dumps({'source':'https://other.invalid'}))
            with self.assertRaises(ValueError):runner.verify_package_sources(p)
            m.unlink()
            with self.assertRaises(ValueError):runner.verify_package_sources(p)

if __name__=='__main__':unittest.main()
