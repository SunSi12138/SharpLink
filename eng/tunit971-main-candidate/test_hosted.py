"""Pure Python schema/configuration/guard controls. No SDK/child workload."""
import copy
import hashlib
import json
import os
from pathlib import Path, PureWindowsPath
import tempfile
import unittest
from unittest.mock import patch
from types import SimpleNamespace
import xml.etree.ElementTree as ET
import accept_results as a
import audit
import classify as c
import hosted_process as h
import run_hosted as run
import report_checks as reports
from schema_samples import sample,classify,SOURCE,ROWS,TRACE
class Schema(unittest.TestCase):
 def test_exact_three_scopes_and_green_inconclusive(self):
  for scope,expected in [('baseline','ORIGINAL_ACCEPT_EVENT_REPRODUCED'),('fixed','ORIGINAL_ACCEPT_FIXED_PASS'),('control','NEIGHBOR_CONTROL_PASS'),('baseline-green','INCONCLUSIVE_BASELINE_NO_EVENT')]:
   self.assertEqual(classify(sample(scope))['classification'],expected)
 def test_independently_read_source_spans_and_no_counter_inference(self):
  self.assertEqual(a.source_spans(SOURCE),ROWS)
  for method,(start,end) in ROWS.items():
   lines=SOURCE.read_text().splitlines();self.assertEqual(lines[start-1].strip(),'[Test]');self.assertEqual(lines[end-1].strip(),'}')
   self.assertIn(method,'\n'.join(lines[start-1:end]))
  self.assertIn('no fixture weakref/counter inference',classify(sample())['collectionEvidence'])
 def test_actual_discovery_identity_and_literal_rows(self):
  for scope in ('baseline','control'):
   n,x,ledger,_=sample(scope);doc={'schemaVersion':1,'tests':list(ledger.values())};method=a.CONTROL if scope=='control' else a.TARGET
   self.assertEqual(a.discovery(doc,method,SOURCE),ledger)
   for field,bad in [('file','/_/other'),('lineStart',True),('lineStart',284),('lineEnd',301)]:
    wrong=copy.deepcopy(doc);wrong['tests'][0]['location'][field]=bad
    with self.assertRaises(ValueError):a.discovery(wrong,method,SOURCE)
   for update in ({'schemaVersion':True},{'tests':[]},{'tests':doc['tests']*2}):
    wrong=copy.deepcopy(doc);wrong.update(update)
    with self.assertRaises(ValueError):a.discovery(wrong,method,SOURCE)
 def test_coherent_wrong_native_ledger_span_rejected(self):
  n,x,ledger,scope=sample();row=next(iter(ledger.values()));row['location']['lineStart']=284;n['groups'][0]['tests'][0]['lineNumber']=284
  with self.assertRaises(ValueError):classify((n,x,ledger,scope))
 def test_baseline_cannot_accept_focused_counter_or_unknown_graph(self):
  changes={
   'missing-session':lambda n,x,l,s:n.pop('unobservedTaskExceptions'),
   'extra-event':lambda n,x,l,s:n['unobservedTaskExceptions'].append(copy.deepcopy(n['unobservedTaskExceptions'][0])),
   'null-event':lambda n,x,l,s:n.update(unobservedTaskExceptions=None),
   'other-message':lambda n,x,l,s:n['unobservedTaskExceptions'][0].update(message='terminal-owned-'+'b'*32),
   'inner':lambda n,x,l,s:n['unobservedTaskExceptions'][0].update(innerException={}),
   'nostack':lambda n,x,l,s:n['unobservedTaskExceptions'][0].pop('stackTrace'),
   'wrong-origin':lambda n,x,l,s:n['unobservedTaskExceptions'][0].update(stackTrace='at other'),
   'counterbody':lambda n,x,l,s:n['groups'][0]['tests'][0].update(status='error',exception={'type':'System.Exception','message':c.COUNTER}),
   'bodyerror-null':lambda n,x,l,s:n['groups'][0]['tests'][0].update(exception=None),
   'bodyskip-null':lambda n,x,l,s:n['groups'][0]['tests'][0].update(skipReason=None),
   'retry':lambda n,x,l,s:n['groups'][0]['tests'][0].update(attempts=[]),
   'bodyfailed':lambda n,x,l,s:n['groups'][0]['tests'][0].update(status='failed'),
   'wronguid':lambda n,x,l,s:n['groups'][0]['tests'][0].update(id='notselected'),
   'nan':lambda n,x,l,s:n['groups'][0]['tests'][0].update(durationMs=float('nan')),
   'wrongpackage':lambda n,x,l,s:n.update(tunitVersion='1.73.19.0'),
   'wrongassembly':lambda n,x,l,s:n.update(assemblyName='SharpLink.UnitTests'),
   'wrongruntime':lambda n,x,l,s:n.update(runtimeVersion='.NET 10.0.2'),
   'extra-group':lambda n,x,l,s:n['groups'].append(copy.deepcopy(n['groups'][-1])),
   'unbound-engine-meta':lambda n,x,l,s:n['groups'][-1]['tests'][0].update(filePath=a.REPORTED),
   'wrong-engine-class':lambda n,x,l,s:n['groups'][-1]['tests'][0].update(className='other'),
   'wrong-engine-display':lambda n,x,l,s:n['groups'][-1]['tests'][0].update(displayName='other'),
   'engine-time':lambda n,x,l,s:n['groups'][-1]['tests'][0].update(durationMs=True),
   'engine-id':lambda n,x,l,s:n['groups'][-1]['tests'][0].update(id='arbitrary'),
   'different-engine-graph':lambda n,x,l,s:n['groups'][-1]['tests'][0]['exception'].update(message='other'),
   'bool-summary':lambda n,x,l,s:n['summary'].update(failed=True),
   'skipped-summary':lambda n,x,l,s:n['summary'].update(skipped=1),
   'wrong-junit-map':lambda n,x,l,s:setattr(x[0].findall('testcase')[-1][0],'tag','failure'),
   'wrong-junit-text':lambda n,x,l,s:setattr(x[0].findall('testcase')[-1][0],'text','other'),
   'extra-junit-case':lambda n,x,l,s:ET.SubElement(x[0],'testcase'),
   'missing-systemerr':lambda n,x,l,s:x[0].remove(x[0].find('system-err')),
   'extra-systemerr':lambda n,x,l,s:ET.SubElement(x[0],'system-err'),
   'bare-systemerr':lambda n,x,l,s:setattr(x[0].find('system-err'),'text',TRACE),
   'missing-newline':lambda n,x,l,s:setattr(x[0].find('system-err'),'text',x[0].find('system-err').text.rstrip('\n')),
  }
  for name,change in changes.items():
   parts=sample();change(*parts)
   with self.subTest(name=name):
    with self.assertRaises((ValueError,TypeError,KeyError)):classify(parts)
 def test_fixed_and_green_require_no_event_even_null(self):
  for scope in ('fixed','control','baseline-green'):
   for value in (None,[],{}):
    parts=sample(scope);parts[0]['unobservedTaskExceptions']=value
    with self.assertRaises(ValueError):classify(parts)
 def test_unique_publication_engine_and_discovered_uid(self):
  uid=sample()[0]['groups'][-1]['tests'][0]['id']
  for kwargs in ({'seen':{'a'*32}},{'engine_uids':{uid}},{'all_uids':{uid}},{'paired_runtime':'.NET 10.0.2'}):
   with self.assertRaises(ValueError):classify(sample(),**kwargs)
 def test_missing_null_bool_native_end_and_malformed_json(self):
  for value in (None,True,301):
   parts=sample();parts[0]['groups'][0]['tests'][0]['endLineNumber']=value
   with self.assertRaises(ValueError):classify(parts)
  with tempfile.TemporaryDirectory() as tmp:
   path=Path(tmp)/'report.json'
   for text in ('{"x":1,"x":2}','{"x":NaN}','{"x":Infinity}'):
    path.write_text(text)
    with self.assertRaises(ValueError):c.read_json(path)


class RestoredAudit(unittest.TestCase):
 def synthetic_assets(self,root,packages):
  import base64
  canonical=base64.b64encode(bytes(64)).decode();cache={};documents={}
  names=['TUnit/1.78.0','TUnit.Engine/1.78.0','Microsoft.Testing.Platform/2.5.2']
  for identity in names:cache[identity.lower()]={'canonicalContentHash':canonical,'archiveSHA256':'synthetic'}
  for name,item in a.CATALOG['projects'].items():
   project=root/item['path'];project.parent.mkdir(parents=True,exist_ok=True)
   refs={str(root/x['path']):{'projectPath':str(root/x['path'])} for x in audit.restore_reference_projection(item['references'])}
   libraries={key:{'type':'package','path':key.lower(),'sha512':canonical} for key in names} if name==a.ASSEMBLY else {}
   value={'project':{'restore':{'projectPath':str(project),'projectName':item['restoreName'],'sources':{'https://api.nuget.org/v3/index.json':{}},
                                'frameworks':{item['framework']:{'projectReferences':refs}}}},
          'packageFolders':{str(packages):{}},'libraries':libraries,'targets':{'net10.0':{}}}
   path=project.parent/'obj/project.assets.json';path.parent.mkdir();path.write_text(json.dumps(value));documents[name]=(path,value)
  return cache,documents

 def test_all19_actual_assets_versions_edges_and_source_rejection(self):
  with tempfile.TemporaryDirectory() as temporary:
   root=Path(temporary)/'source';root.mkdir();packages=Path(temporary)/'packages';packages.mkdir()
   cache,documents=self.synthetic_assets(root,packages)
   edges={name:value['references'] for name,value in a.CATALOG['projects'].items()}
   with patch.object(audit,'static_graph',return_value=edges),patch.object(audit,'cache_audit',return_value=cache):
    result=audit.restored(root,packages);self.assertEqual(len(result['projectEdges']),19)
    path,value=documents['SharpLink.Sdk'];saved=copy.deepcopy(value)
    refs=value['project']['restore']['frameworks']['net10.0']['projectReferences']
    generator=str(root/'src/SharpLink.Generator/SharpLink.Generator.csproj')
    self.assertNotIn(generator,refs)
    refs[generator]={'projectPath':generator};path.write_text(json.dumps(value))
    with self.assertRaises(ValueError):audit.restored(root,packages) # false assembly reference is forbidden, not optional.
    value=copy.deepcopy(saved);value['project']['restore']['frameworks']['net10.0']['projectReferences']={};path.write_text(json.dumps(value))
    with self.assertRaises(ValueError):audit.restored(root,packages)
    path.write_text(json.dumps(saved));path,value=documents[a.ASSEMBLY];saved=copy.deepcopy(value)
    value['project']['restore']['sources']={'https://untrusted.invalid':{}};path.write_text(json.dumps(value))
    with self.assertRaises(ValueError):audit.restored(root,packages)
    path.write_text(json.dumps(saved));extra=root/'unexpected/project.assets.json';extra.parent.mkdir();extra.write_text('{}')
    with self.assertRaises(ValueError):audit.restored(root,packages)
    extra.unlink()
    value=copy.deepcopy(saved);value['libraries']['TUnit/1.73.19']=value['libraries'].pop('TUnit/1.78.0');path.write_text(json.dumps(value))
    with self.assertRaises(ValueError):audit.restored(root,packages)

 def test_project_restore_identity_uses_both_paths_not_global_name(self):
  import os
  for prefix in ('Dynamic','Replacement'):
   for kind in ('Contracts','Services'):
    self.assertEqual(a.CATALOG['projects']['SharpLink.'+prefix+kind]['restoreName'],'SharpLink.DynamicPlugin.'+kind)
  with tempfile.TemporaryDirectory() as temporary:
   root=Path(temporary)/'source';root.mkdir();packages=Path(temporary)/'packages';packages.mkdir()
   cache,documents=self.synthetic_assets(root,packages)
   edges={name:value['references'] for name,value in a.CATALOG['projects'].items()}
   with patch.object(audit,'static_graph',return_value=edges),patch.object(audit,'cache_audit',return_value=cache):
    for prefix in ('Dynamic','Replacement'):
     name='SharpLink.'+prefix+'Services';target='SharpLink.'+prefix+'Contracts'
     path,value=documents[name];relative=os.path.relpath(root/audit.PROJECTS[target],root/Path(audit.PROJECTS[name]).parent)
     value['libraries']={'SharpLink.DynamicPlugin.Contracts/2.0.4':{'type':'project','path':relative,'msbuildProject':relative}}
     path.write_text(json.dumps(value))
    audit.restored(root,packages) # Both same-name Contracts resolve by their distinct actual paths.
    path,value=documents['SharpLink.DynamicServices'];good=copy.deepcopy(value)
    cases=[]
    bad=copy.deepcopy(good);bad['project']['restore']['projectName']='SharpLink.DynamicServices';cases.append(bad)
    for field in ('path','msbuildProject'):
     bad=copy.deepcopy(good);bad['libraries']['SharpLink.DynamicPlugin.Contracts/2.0.4'][field]='../SharpLink.ReplacementContracts/SharpLink.ReplacementContracts.csproj';cases.append(bad)
    bad=copy.deepcopy(good);bad['libraries']['SharpLink.DynamicPlugin.Contracts/2.0.4'].update(path='../SharpLink.ReplacementContracts/SharpLink.ReplacementContracts.csproj',msbuildProject='../SharpLink.ReplacementContracts/SharpLink.ReplacementContracts.csproj');cases.append(bad)
    bad=copy.deepcopy(good);bad['libraries']['SharpLink.DynamicContracts/2.0.4']=bad['libraries'].pop('SharpLink.DynamicPlugin.Contracts/2.0.4');cases.append(bad)
    for field in ('path','msbuildProject'):
     bad=copy.deepcopy(good);del bad['libraries']['SharpLink.DynamicPlugin.Contracts/2.0.4'][field];cases.append(bad)
    bad=copy.deepcopy(good);bad['project']['restore']['projectPath']=str(root/audit.PROJECTS['SharpLink.ReplacementServices']);cases.append(bad)
    for bad in cases:
     path.write_text(json.dumps(bad))
     with self.assertRaises(ValueError):audit.restored(root,packages)
    path.write_text(json.dumps(good));audit.restored(root,packages)

 def test_restore_projection_is_exact_false_exclusion_not_optional_edges(self):
  full=[edge for project in a.CATALOG['projects'].values() for edge in project['references']]
  projected=audit.restore_reference_projection(full)
  self.assertEqual(len(full),60);self.assertEqual(len(projected),44)
  self.assertTrue(all(edge['attributes'].get('ReferenceOutputAssembly','true').lower()=='true' for edge in projected))
  sdk=audit.restore_reference_projection(a.CATALOG['projects']['SharpLink.Sdk']['references'])
  self.assertEqual([x['path'] for x in sdk],['src/SharpLink.Abstractions/SharpLink.Abstractions.csproj'])
  source={'path':'example.csproj','attributes':{'ReferenceOutputAssembly':'TRUE'}}
  self.assertEqual(audit.restore_reference_projection([source]),[source])
  for flag in ('unknown','1',''):
   with self.assertRaises(ValueError):audit.restore_reference_projection([{'path':'example','attributes':{'ReferenceOutputAssembly':flag}}])

 def test_builder_versions_are_canonical_four_uint16_not_ignored(self):
  path='runtimes/linux-x64/lib/net10.0/Example.dll';base={'rid':'linux-x64','assetType':'runtime'}
  for invalid in (None,True,1,{},[], '', '1.2.3','1.2.3.4.5','01.2.3.4','1.2.3.04','+1.2.3.4','-1.2.3.4','1.2.3.4 ',' 1.2.3.4','1.2.3.4\n','1.2.3.65536','2147483647.0.0.0','１.2.3.4'):
   for key in ('fileVersion','assemblyVersion'):
    actual=dict(base,fileVersion='1.2.3.4');actual[key]=invalid
    with self.assertRaises(ValueError):audit.require_rid_metadata('Example/1',path,actual,{path:base})
  for suffix in ('.dll','.exe','.winmd','.DLL'):
   managed=path+suffix;audit.require_rid_metadata('Example/1',managed,dict(base,fileVersion='1.2.3.4',assemblyVersion='1.2.3.4'),{managed:base})
  with self.assertRaises(ValueError):audit.require_rid_metadata('Example/1',path,dict(base,assemblyVersion='1.2.3.4'),{path:base})
  with self.assertRaises(ValueError):audit.require_rid_metadata('Example/1',path,base,{path:dict(base,fileVersion='1.2.3.4')})

 def test_observed_config_descriptor_hash_and_unchanged_copy_binding(self):
  actual={'rid':'linux-musl-x64','assetType':'native','fileVersion':'0.0.0.0'}
  self.assertEqual(hashlib.sha256(json.dumps(actual,sort_keys=True,separators=(',',':'),allow_nan=False).encode()).hexdigest(),'c2176658d7b1b9806d9ee8d18531aa0a409e4a41da7268c66c9f5862e75d14f1')
  path='runtimes/linux-musl-x64/native/Cov_x64.config'
  audit.require_rid_metadata('Microsoft.Testing.Extensions.CodeCoverage/18.12.0',path,actual,{path:{'rid':'linux-musl-x64','assetType':'native'}})
  with self.assertRaises(ValueError):audit.require_rid_metadata('Microsoft.Testing.Extensions.CodeCoverage/18.12.0',path,dict(actual,assemblyVersion='0.0.0.0'),{path:{'rid':'linux-musl-x64','assetType':'native'}})

 def test_rid_diagnostic_unknown_escape_and_keys_are_hash_only_without_reads(self):
  for relative,mapping in [('SECRET/path',{}),('../SECRET/path',{'../SECRET/path':{}}),('/SECRET/path',{'/SECRET/path':{}})]:
   with patch.object(Path,'read_bytes',side_effect=AssertionError('must not read FS')):
    with self.assertRaises(ValueError) as caught:audit.require_rid_metadata('SECRET/TOKEN/path',relative,{'SECRET-KEY':'SECRET-VALUE'},mapping)
    detail=caught.exception.rid_asset.public()
   output=json.dumps(detail);self.assertNotIn('SECRET',output);self.assertNotIn('knownPackageRelative',detail);self.assertNotIn('package',detail)
  path='runtimes/linux/native/test.so';many={f'SECRET-{n}':n for n in range(20)}
  with self.assertRaises(ValueError) as caught:audit.require_rid_metadata('Example/1.0.0',path,many,{})
  detail=caught.exception.rid_asset.public();self.assertEqual(detail['depsKeyCount'],20);self.assertEqual(len(detail['depsKeys']),16);self.assertTrue(detail['keysTruncated'])



def outcome(**kw):
    row={'expectedExit':2,'exitCode':0,'timedOut':False,'cancelled':False,'directProcessTerminal':True,
         'cleanupErrors':[],'reportingErrors':[],'descendantJoinProven':False}
    row.update(kw);return h.HostedCommandFailure(row,[ValueError('Unexpected hosted command exit')])


class HostedWiring(unittest.TestCase):
    def test_green_exact_predicate_not_timeout_cancel_cleanup(self):
        self.assertTrue(run.clean_baseline_green(outcome()))
        for change in ({'exitCode':False},{'expectedExit':0},{'timedOut':True},{'cancelled':True},
                       {'directProcessTerminal':False},{'cleanupErrors':['bad']},{'reportingErrors':['bad']}):
            self.assertFalse(run.clean_baseline_green(outcome(**change)))
        error=outcome();error.causes.append(OSError('guard'));self.assertFalse(run.clean_baseline_green(error))
        self.assertFalse(run.clean_baseline_green(BaseExceptionGroup('guard',[outcome(),ValueError()])))
    def lane(self,root):
        lane=object.__new__(run.Lane);lane.work=root/'work';lane.work.mkdir()
        source=root/'source'/a.FIXTURE;source.parent.mkdir(parents=True);source.write_bytes(SOURCE.read_bytes())
        lane.roots={'baseline':root/'source','fixed':root/'source'};lane.dotnet=Path('/official/dotnet')
        lane.results=[];lane.runtimes={'10.0.14'};lane.paired_runtime=None;lane.seen_publications=set();lane.seen_engine_uids=set();lane.all_discovery_uids=set();lane.baseline_reproduced=False
        lane.inputs_guard=lambda:None;lane.current_guard=lambda:None
        return lane
    def test_green_never_runs_next_body_and_retains_original(self):
        with tempfile.TemporaryDirectory() as t:
            lane=self.lane(Path(t));label='baseline-original-accept';directory=lane.work/label;(directory/'reports').mkdir(parents=True)
            n,x,ledger,_=sample('baseline-green');(directory/'reports/session.tunit-report.json').write_text(json.dumps(n));(directory/'reports/junit.xml').write_text(ET.tostring(x,encoding='unicode'));(directory/(label+'.stderr')).write_text('')
            original=BaseExceptionGroup('original',[outcome()])
            with patch.object(lane,'command',side_effect=original) as command:
                with self.assertRaises(run.InconclusiveBaseline) as caught:lane.filtered(label,'baseline','target',scope='baseline',ledger=ledger)
            self.assertIs(caught.exception.__cause__,original);self.assertEqual(command.call_count,1);self.assertEqual(lane.results,[])
    def test_green_report_error_cannot_replace_original(self):
        with tempfile.TemporaryDirectory() as t:
            lane=self.lane(Path(t));original=outcome()
            with patch.object(lane,'command',side_effect=original):
                with self.assertRaises(BaseExceptionGroup) as caught:lane.filtered('baseline','baseline','target',scope='baseline',ledger=sample()[2])
            self.assertIs(caught.exception.exceptions[0],original)
    def test_fixed_must_wait_for_real_baseline_event(self):
        with tempfile.TemporaryDirectory() as t:
            lane=self.lane(Path(t))
            with patch.object(lane,'command',side_effect=AssertionError('no call')):
                with self.assertRaises(ValueError):lane.filtered('fixed','fixed','target',scope='fixed',ledger=sample('fixed')[2])
    def test_only_body_has_detailed_strict_reports_listing_exact_json(self):
        for key in ('target','control'):
            listing=run.filtered_argv(Path('/dotnet'),Path('/unit.dll'),Path('/out'),key,True)
            body=run.filtered_argv(Path('/dotnet'),Path('/unit.dll'),Path('/out'),key,False)
            self.assertEqual(listing[-2:],['--list-tests','json']);self.assertNotIn('--output',listing);self.assertNotIn('--fail-on-unobserved-task-exceptions',listing)
            self.assertEqual(body[body.index('--output')+1],'Detailed');self.assertEqual(body.count('--fail-on-unobserved-task-exceptions'),1)
            self.assertEqual(body[body.index('--maximum-parallel-tests')+1],'1')
            self.assertEqual(body[body.index('--treenode-filter')+1],run.FILTERS[key]);self.assertNotIn('--filter-uid',body)
        with self.assertRaises(ValueError):run.filtered_argv(Path('/d'),Path('/x'),Path('/o'),'all',False)
    def test_pathmap_both_platforms_single_root_and_no_suffix_acceptance(self):
        self.assertEqual(run.path_map(Path('/private/work/main')),'-p:PathMap=/private/work/main/=/_/')
        self.assertEqual(run.path_map(PureWindowsPath('D:/work/main')),'-p:PathMap=D:/work/main/=/_/')
        for root in (Path('relative'),Path('/work/../other'),Path('/work/bad,value')):
            with self.assertRaises(ValueError):run.path_map(root)
        for actual in ('D:/work/main/'+a.FIXTURE,'/_/other/'+a.FIXTURE,'/other/'+a.FIXTURE):
            parts=sample();next(iter(parts[2].values()))['location']['file']=actual
            with self.assertRaises(ValueError):classify(parts)
    def test_raw_oracle_unchanged_baseline_one_fixed_zero(self):
        reports.validate_stderr('baseline','Unobserved task exception: System.AggregateException: private')
        for text in ('','Unobserved task exception: x\nUnobserved task exception: y','Unhandled exception in AppDomain'):
            with self.assertRaises(ValueError):reports.validate_stderr('baseline',text)
        for scope in ('fixed','control','baseline-green'):
            reports.validate_stderr(scope,'')
            with self.assertRaises(ValueError):reports.validate_stderr(scope,'UNOBSERVED private')
    def test_safe_diagnostics_never_echo_arbitrary_path_or_raw(self):
        n,x,ledger,_=sample();row=next(iter(ledger.values()));row['location']['file']='SECRET';row['uid']='SECRET-UID'
        detail=reports.DiscoveryPathDiagnostics({'tests':[row]},SOURCE).public()
        self.assertNotIn('SECRET',json.dumps(detail));self.assertEqual(detail['mismatchCount'],1)
        with self.assertRaises(ValueError) as caught:reports.validate_stderr('baseline','SECRET',lambda:'SECRET-STDOUT')
        self.assertNotIn('SECRET',json.dumps(caught.exception.raw_events.public()))
    def test_full_source_inventory_rejects_special_and_symlink(self):
        with tempfile.TemporaryDirectory() as t:
            root=Path(t);(root/'ok').write_text('x');self.assertEqual(set(run.inventory(root)),{'ok'})
            os.mkfifo(root/'fifo')
            with self.assertRaises(ValueError):run.inventory(root)
            (root/'fifo').unlink();(root/'link').symlink_to(root/'ok')
            with self.assertRaises(ValueError):run.inventory(root)
    def test_trusted_final_sink_refuses_directory_replacement_no_outside_write(self):
        with tempfile.TemporaryDirectory() as t:
            parent=Path(t);root=parent/'work';root.mkdir();outside=parent/'outside';outside.mkdir();sink=run.PrivateSink(root)
            root.rename(parent/'moved');root.symlink_to(outside,target_is_directory=True)
            with self.assertRaises(ValueError):sink.write({'private':'secret'})
            sink.close();self.assertFalse((outside/'FINAL.json').exists());self.assertEqual((parent/'moved/FINAL.json').read_text(),'')
    def test_trusted_final_sink_refuses_receipt_substitution(self):
        with tempfile.TemporaryDirectory() as t:
            root=Path(t);sink=run.PrivateSink(root);outside=root/'untouched';outside.write_text('original')
            (root/'FINAL.json').rename(root/'original');(root/'FINAL.json').symlink_to(outside)
            with self.assertRaises(ValueError):sink.write({'private':'secret'})
            sink.close();self.assertEqual(outside.read_text(),'original')
    def test_public_failure_summary_is_structured_only(self):
        raw=run.safe_leaves([ValueError('SECRET'),outcome(),BaseExceptionGroup('SECRET',[RuntimeError('SECRET')]*12)])
        self.assertNotIn('SECRET',json.dumps(raw));self.assertTrue(raw['truncated']);self.assertEqual(len(raw['leaves']),8);self.assertFalse(raw['leaves'][1]['descendantJoinProven'])
    def test_top_failure_sink_keeps_body_before_guard_and_reporting(self):
        class BadSink:
            def write(self,value):raise OSError('report error')
            def close(self):raise OSError('close error')
        body=ValueError('body');guard=RuntimeError('guard');lane=SimpleNamespace(final_guards=lambda:[guard],stage='body',results=[],commands_started=1,sink=BadSink())
        with patch('builtins.print'):
            with self.assertRaises(BaseExceptionGroup) as caught:run.finish(lane,[body])
        self.assertIs(caught.exception.exceptions[0],body);self.assertIs(caught.exception.exceptions[1],guard);self.assertEqual(len(caught.exception.exceptions),4)
    def test_final_console_error_never_returns_success(self):
        lane=SimpleNamespace(final_guards=lambda:[],stage='body',results=[],commands_started=16,sink=SimpleNamespace(write=lambda v:None,close=lambda:None))
        with patch('builtins.print',side_effect=OSError('console')):
            with self.assertRaises(OSError):run.finish(lane,[])
    def test_declared_bounds_and_no_platform_ownership_promotion(self):
        self.assertEqual(run.ACTIVE_SECONDS,2*60+4*30+4*600+6*120)
        self.assertEqual(run.MAX_COMMANDS,16);self.assertLess(run.ACTIVE_SECONDS+run.CLEANUP_SECONDS,run.TOTAL_SECONDS)
        self.assertIn('no independent descendant join',run.QUALIFICATION)
        self.assertNotIn('remainingOwned',Path(run.__file__).read_text())



class CommandModel(unittest.TestCase):
    def test_exact_sixteen_commands_dry_interpreter_no_process(self):
        with tempfile.TemporaryDirectory() as t:
            work=Path(t);roots={v:work/v for v in ('baseline','fixed')}
            for root in roots.values():root.mkdir()
            calls=[]
            lane=SimpleNamespace(roots=roots,current={v:{run.candidate.COORDINATOR:'different-'+v,'unchanged':'same'} for v in roots},
                                 dotnet=Path('/official/dotnet'),work=work,pinned={},results=[],commands_started=0,host_sha='synthetic',
                                 env={v:{'NUGET_PACKAGES':str(work/('packages-'+v))} for v in roots})
            def command(label,variant,argv,seconds,**kw):
                calls.append((label,variant,argv,seconds,kw));lane.commands_started+=1
                out=work/(label+'.stdout');err=work/(label+'.stderr');err.write_text('')
                text='10.0.114' if label.startswith('sdk-version') else 'Host:\n  Version: 10.0.14\n  Architecture: arm64\n\n' if label=='sdk-info' else 'Microsoft.NETCore.App 10.0.14 [/official/shared]\n' if label=='runtime-inventory' else ''
                out.write_text(text);return work,(out,err)
            lane.command=command
            def filtered(label,variant,key,listing=False,scope=None,ledger=None):
                command(label,variant,run.filtered_argv(lane.dotnet,roots[variant]/'test.dll',work/label,key,listing),120,expected_exit=2 if scope=='baseline' else 0,filtered=True)
                if listing:return sample('control' if key=='control' else 'baseline')[2]
                lane.results.append({'classification':{'baseline':'ORIGINAL_ACCEPT_EVENT_REPRODUCED','fixed':'ORIGINAL_ACCEPT_FIXED_PASS','control':'NEIGHBOR_CONTROL_PASS'}[scope]})
            lane.filtered=filtered
            restored={'cache':{},'packages':['TUnit/1.78.0']}
            with patch.object(run.sys,'platform','darwin'),patch.object(audit,'static_graph'),patch.object(audit,'restored',return_value=restored),patch.object(audit,'built',return_value={}),patch('builtins.print'):
                run.execute(lane)
            self.assertEqual(tuple(x[0] for x in calls),run.EXPECTED_STAGES)
            self.assertEqual(sum(x[3] for x in calls),3360)
            self.assertEqual([x[4].get('expected_exit',0) for x in calls],[0]*13+[2,0,0])
            self.assertEqual([x[4].get('phase') for x in calls[:2]],['prepare','prepare'])
            for row in calls[8:10]:
                self.assertIn('--no-restore',row[2]);self.assertIn('-p:UseSharedCompilation=false',row[2]);self.assertIn(run.path_map(roots[row[1]]),row[2])
            self.assertEqual(len(lane.results),3)
    def test_windows_assets_comparison_uses_git_style_relative_names(self):
        self.assertEqual((PureWindowsPath('test/Example.csproj').parent/'obj/project.assets.json').as_posix(),'test/obj/project.assets.json')
        self.assertIn("expected={(Path(path).parent/'obj/project.assets.json').as_posix()",Path(audit.__file__).read_text())



class GitMetadataGuard(unittest.TestCase):
    def test_only_selected_checkout_root_metadata_is_excluded(self):
        with tempfile.TemporaryDirectory() as temporary:
            root=Path(temporary);(root/'.git').mkdir();(root/'.git/HEAD').write_text('metadata');(root/'source').write_text('source')
            self.assertEqual(set(run.inventory(root,checkout_metadata=True)),{'source'})
            with self.assertRaises(ValueError):run.inventory(root)
            for value in (None,1,'yes'):
                with self.assertRaises(ValueError):run.inventory(root,checkout_metadata=value)
    def test_prior_report_cache_output_nested_git_add_and_mutate_rejected(self):
        with tempfile.TemporaryDirectory() as temporary:
            for name in ('reports','packages/id/1','bin/Release','payload'):
                root=Path(temporary)/name;root.mkdir(parents=True);(root/'original').write_text('original')
                before=run.inventory(root);hidden=root/'.git';hidden.mkdir();file=hidden/'hidden';file.write_text('added')
                with self.assertRaises(ValueError):run.inventory(root)
                file.write_text('mutated')
                with self.assertRaises(ValueError):run.inventory(root)
                file.unlink();hidden.rmdir();self.assertEqual(run.inventory(root),before)
    def test_checkout_nested_git_rejected_even_empty(self):
        with tempfile.TemporaryDirectory() as temporary:
            root=Path(temporary);(root/'.git').mkdir();nested=root/'bin/.git';nested.mkdir(parents=True)
            with self.assertRaises(ValueError):run.inventory(root,checkout_metadata=True)
    def test_git_symlink_fifo_and_file_cannot_be_excluded(self):
        with tempfile.TemporaryDirectory() as temporary:
            root=Path(temporary);outside=root/'outside';outside.mkdir();meta=root/'.git'
            meta.symlink_to(outside,target_is_directory=True)
            for selected in (False,True):
                with self.assertRaises(ValueError):run.inventory(root,checkout_metadata=selected)
            meta.unlink();os.mkfifo(meta)
            for selected in (False,True):
                with self.assertRaises(ValueError):run.inventory(root,checkout_metadata=selected)
            meta.unlink();meta.write_text('not a selected checkout metadata directory')
            for selected in (False,True):
                with self.assertRaises(ValueError):run.inventory(root,checkout_metadata=selected)
    def test_all_source_call_sites_explicit_other_trees_default(self):
        import ast
        tree=ast.parse(Path(run.__file__).read_text());calls=[n for n in ast.walk(tree) if isinstance(n,ast.Call) and isinstance(n.func,ast.Name) and n.func.id=='inventory']
        selected=[n for n in calls if any(k.arg=='checkout_metadata' and isinstance(k.value,ast.Constant) and k.value.value is True for k in n.keywords)]
        self.assertEqual(len(selected),5) # initialization/current guard/phase before+after/postcommand refresh
        others=[n for n in calls if n not in selected]
        self.assertEqual(len(others),8) # payload x2, cache x4, previous-evidence + fresh evidence
        self.assertTrue(all(not n.keywords for n in others))

if __name__=='__main__':unittest.main(verbosity=2)
