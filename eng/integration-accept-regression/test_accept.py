"""Synthetic Python schema/lifecycle controls only. No SDK or original case executed."""
import copy
import hashlib
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
from types import SimpleNamespace
import xml.etree.ElementTree as ET
import accept_results as a
import audit
import classify as c
import prepare_accept
import process_owner as owner
import run

SOURCE=Path(__file__).with_name('fixture.Lifecycle.cs')
ROWS={a.TARGET:(283,302),a.CONTROL:(262,281)}
TRACE='\n'.join([
 '   at SharpLink.Server.SharpLinkServer.RunAcceptLoopAsync(TaskCompletionSource`1 acceptStarted, TaskCompletionSource`1 running, CancellationToken acceptToken) in /_/src/SharpLink.Server/SharpLinkServer.RunLoop.cs:line 34',
 '   at SharpLink.Server.SharpLinkServer.RunAcceptLoopAsync(TaskCompletionSource`1 acceptStarted, TaskCompletionSource`1 running, CancellationToken acceptToken) in /_/src/SharpLink.Server/SharpLinkServer.RunLoop.cs:line 91',
 '   at SharpLink.Server.SharpLinkServer.ServerLifecycleCoordinator.WaitForAcceptRuntimeShutdownAsync() in /_/src/SharpLink.Server/ServerLifecycleCoordinator.cs:line 323',
 '   at SharpLink.Server.SharpLinkServer.ServerLifecycleCoordinator.CompleteFaultedStopAsync() in /_/src/SharpLink.Server/ServerLifecycleCoordinator.cs:line 306',
 '   at SharpLink.IntegrationTests.TransportConnectionIntegrationTests.EnsureThrows[TException](Task task, String name) in /_/test/SharpLink.IntegrationTests/TransportConnectionIntegrationTests.cs:line 66'])


def sample(scope='baseline'):
 method=a.CONTROL if scope=='control' else a.TARGET;event=scope=='baseline';uid='synthetic-actual-uid'
 row={'uid':uid,'displayName':method,'type':{'namespace':a.NS,'typeName':a.CLASS,'methodName':method,'methodArity':0,
      'parameterTypeFullNames':[],'returnTypeFullName':'System.Threading.Tasks.Task',
      'assemblyFullName':a.ASSEMBLY+', Version=2.0.4.0, Culture=neutral, PublicKeyToken=null'},
      'location':{'file':a.REPORTED,'lineStart':ROWS[method][0],'lineEnd':ROWS[method][1]}}
 test={'id':uid,'displayName':method,'methodName':method,'className':a.CLASS,'filePath':a.REPORTED,
       'lineNumber':ROWS[method][0],'endLineNumber':ROWS[method][1],'status':'passed','durationMs':1}
 native={'schemaVersion':1,'assemblyName':a.ASSEMBLY,'tunitVersion':'1.78.0.0','runtimeVersion':'.NET 10.0.14',
         'publicationGeneration':'a'*32,'summary':c.summary(2 if event else 1,int(event)),
         'groups':[{'className':a.CLASS,'namespace':a.NS,'summary':c.summary(1,0),'tests':[test]}]}
 attrs={'name':a.ASSEMBLY,'tests':str(2 if event else 1),'errors':str(int(event)),'failures':'0','skipped':'0'}
 root=ET.Element('testsuites',attrs);suite=ET.SubElement(root,'testsuite',attrs)
 ET.SubElement(suite,'testcase',{'name':method,'classname':a.CLASS})
 if event:
  failure={'type':'System.IO.IOException','message':'accept failed','stackTrace':TRACE}
  native['unobservedTaskExceptions']=[failure]
  name='Unobserved task exception - IOException: accept failed'
  node={'id':'12345678-1234-4234-9234-123456789abc','displayName':name,'methodName':name,'className':'UnknownClass','status':'error','durationMs':0,'exception':copy.deepcopy(failure)}
  native['groups'].append({'className':'UnknownClass','namespace':'','summary':c.summary(1,1),'tests':[node]})
  case=ET.SubElement(suite,'testcase',{'name':name,'classname':'UnknownClass'})
  error=ET.SubElement(case,'error',{'type':'System.IO.IOException','message':'accept failed'});error.text=a.exception_text(failure)
  output=ET.SubElement(suite,'system-err');output.text='Unobserved task exception: '+a.exception_text(failure)+'\n'
 return native,root,{uid:row},scope


def classify(parts,**kw):
 n,x,ledger,scope=parts
 return a.classify(n,ET.tostring(x,encoding='unicode'),ledger,scope,SOURCE,{'10.0.14'},**kw)


def outcome(**updates):
 row={'stage':'baseline-original-accept','exitCode':0,'expectedExit':2,'timedOut':False,'errors':[],
      'cleanupErrors':[],'reportingErrors':[],'remainingOwned':{},'elapsedSeconds':0,'argv':['not-public']}
 row.update(updates);return row


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


class Lifecycle(unittest.TestCase):
 def test_owner_green_qualification_is_exact_not_generic_exit0(self):
  good=BaseExceptionGroup('original',[owner.CommandFailure(outcome(),[])])
  self.assertTrue(run.clean_baseline_green_owner(good))
  for updates in ({'exitCode':False},{'exitCode':1},{'timedOut':True},{'expectedExit':0},{'remainingOwned':None},
                  {'remainingOwned':[]},{'remainingOwned':{1:'live'}},{'errors':['body']},{'cleanupErrors':['cleanup']},{'reportingErrors':['report']}):
   wrong=BaseExceptionGroup('original',[owner.CommandFailure(outcome(**updates),[])])
   self.assertFalse(run.clean_baseline_green_owner(wrong))
  self.assertFalse(run.clean_baseline_green_owner(BaseExceptionGroup('both',[good,ValueError('guard')])))
 def make_lane(self,tmp):
  lane=object.__new__(run.Lane);lane.work=Path(tmp)/'work';lane.work.mkdir();root=Path(tmp)/'source';source=root/a.FIXTURE;source.parent.mkdir(parents=True);source.write_bytes(SOURCE.read_bytes())
  lane.roots={'baseline':root,'fixed':root};lane.dotnet=Path('/official/dotnet');lane.baseline_reproduced=False
  lane.results=[];lane.seen_publications=set();lane.seen_engine_uids=set();lane.all_discovery_uids=set();lane.runtimes={'10.0.14'};lane.paired_runtime=None
  lane.inputs_guard=lambda:None;lane.current_guard=lambda:None;lane.root_guard=lambda:None
  return lane
 def test_real_exit0_stays_failure_retains_owner_and_stops(self):
  with tempfile.TemporaryDirectory() as tmp:
   lane=self.make_lane(tmp);label='baseline-original-accept';directory=lane.work/label;(directory/'reports').mkdir(parents=True)
   n,x,ledger,scope=sample('baseline-green');(directory/'reports/session.tunit-report.json').write_text(json.dumps(n));(directory/'reports/junit.xml').write_text(ET.tostring(x,encoding='unicode'));(directory/(label+'.stderr')).write_text('')
   original=BaseExceptionGroup('original',[owner.CommandFailure(outcome(),[])])
   with patch.object(lane,'command',side_effect=original) as command:
    with self.assertRaises(run.InconclusiveBaseline) as result:lane.filtered(label,'baseline','target',scope='baseline',ledger=ledger)
   self.assertIs(result.exception.__cause__,original);self.assertEqual(command.call_count,1);self.assertEqual(lane.results,[]);self.assertFalse(lane.baseline_reproduced)
 def test_green_report_failure_follows_original(self):
  with tempfile.TemporaryDirectory() as tmp:
   lane=self.make_lane(tmp);original=BaseExceptionGroup('original',[owner.CommandFailure(outcome(),[])])
   with patch.object(lane,'command',side_effect=original):
    with self.assertRaises(BaseExceptionGroup) as result:lane.filtered('baseline-original-accept','baseline','target',scope='baseline',ledger=sample()[2])
   self.assertIs(result.exception.exceptions[0],original);self.assertIsInstance(result.exception.exceptions[1],FileNotFoundError)
 def test_fixed_body_cannot_run_before_baseline_event(self):
  with tempfile.TemporaryDirectory() as tmp:
   lane=self.make_lane(tmp)
   with patch.object(lane,'command',side_effect=AssertionError('must not run')):
    with self.assertRaises(ValueError):lane.filtered('fixed','fixed','target',scope='fixed',ledger=sample()[2])
 def test_three_body_argv_have_strict_and_frozen_expected_exits(self):
  class Halt(Exception):pass
  with tempfile.TemporaryDirectory() as tmp:
   lane=self.make_lane(tmp);lane.baseline_reproduced=True;seen=[]
   def capture(label,variant,argv,seconds,**kwargs):seen.append((argv,seconds,kwargs));raise Halt()
   lane.command=capture
   for variant,key,scope in [('baseline','target','baseline'),('fixed','target','fixed'),('fixed','control','control')]:
    with self.assertRaises(Halt):lane.filtered('case',variant,key,scope=scope,ledger=sample(scope)[2])
   self.assertEqual([x[2]['expected_exit'] for x in seen],[2,0,0]);self.assertTrue(all(x[1]==120 and '--fail-on-unobserved-task-exceptions' in x[0] for x in seen))
 def test_body_and_formatter_failure_remain_nonzero_private_graph(self):
  with tempfile.TemporaryDirectory() as tmp:
   lane=SimpleNamespace(work=Path(tmp),manifest_sha='synthetic',stage='baseline',results=[],active_scope='baseline',active_uid='actual-uid')
   body=ValueError('BASELINE_NO_EVENT_INCONCLUSIVE');errors=[body]
   def broken(*args,**kwargs):raise OSError('secret-console-content')
   self.assertEqual(run.finish(lane,errors,writer=broken,fallback=lambda *args:None),1)
   self.assertIs(errors[0],body);self.assertTrue(any(isinstance(x,OSError) for x in errors))
   receipt=c.read_json(Path(tmp)/'RESULT.json');self.assertEqual(receipt['status'],'FAILED');self.assertEqual(receipt['errors'][0]['type'],'builtins.ValueError')


class Inputs(unittest.TestCase):
 def test_exact_central_transform_and_owner_unchanged(self):
  before=Path(__file__).with_name('package-preimage.props').read_bytes();after=prepare_accept.upgrade_bytes(before,a.CATALOG)
  self.assertEqual(after.replace(b'Version="1.78.0"',b'Version="1.73.19"'),before)
  for bad in (after,before+b' ',before+before):
   with self.assertRaises(ValueError):prepare_accept.upgrade_bytes(bad,a.CATALOG)
  self.assertEqual(hashlib.sha256(Path(__file__).with_name('process_owner.py').read_bytes()).hexdigest(),'44d66d88933b21d525241ac76e0dbe421a10682dba461fe784e4706cc578a9b5')
 def test_catalog_original19_closure_and_runtime7(self):
  projects=a.CATALOG['projects'];self.assertEqual(len(projects),19);self.assertEqual(len(a.CATALOG['runtimeProjects']),7)
  found=set();todo=['SharpLink.IntegrationTests'];paths={v['path']:n for n,v in projects.items()}
  while todo:
   name=todo.pop()
   if name in found:continue
   found.add(name);todo.extend(paths[x['path']] for x in projects[name]['references'])
  self.assertEqual(found,set(projects))
  self.assertEqual(projects['SharpLink.Generator']['framework'],'netstandard2.0')
  self.assertTrue(all(v['framework']=='net10.0' for n,v in projects.items() if n!='SharpLink.Generator'))
 def test_metadata_extensions_remain_typed_and_strict(self):
  self.assertEqual(audit.metadata_json('{/*x*/"a":1,}','workload-set'),{'a':1})
  with self.assertRaises(ValueError):audit.metadata_json('{/*x*/"a":1}','install-state')
  for raw in ('{"a":1,,}','{"a":NaN}','{"a":1,"a":2}'):
   with self.assertRaises(ValueError):audit.metadata_json(raw,'workload-set')
 def test_literal_error_whitelist_not_untrusted_exception_prose(self):
  rows=run.failure_leaves([ValueError('SECRET_FULL_REPORT')],'baseline')
  self.assertNotIn('SECRET_FULL_REPORT',json.dumps(rows))
  rows=run.failure_leaves([run.InconclusiveBaseline('BASELINE_NO_EVENT_INCONCLUSIVE')],'baseline')
  self.assertIn('BASELINE_NO_EVENT_INCONCLUSIVE',json.dumps(rows))


class FullGraphAudit(unittest.TestCase):
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
 def test_edge_diagnostics_are_bounded_safe_and_do_not_accept_unknown(self):
  name='SharpLink.Sdk';known='src/SharpLink.Abstractions/SharpLink.Abstractions.csproj'
  with self.assertRaises(ValueError) as caught:audit.require_project_edges(name,{known},set(),{'SECRET-UNKNOWN-PATH'})
  original=caught.exception;detail=original.project_edges.public()
  self.assertEqual((detail['missingCount'],detail['extraCount']),(1,1))
  self.assertEqual(detail['missing'][0]['relative'],known)
  self.assertEqual(detail['extra'][0]['category'],'unknown-project-relative')
  self.assertNotIn('SECRET-UNKNOWN-PATH',json.dumps(run.failure_leaves([original],'restore-baseline')))
  self.assertEqual(detail['sourceSHA256'],a.CATALOG['projects'][name]['sha256'])
  unknown={f'SECRET-{i}' for i in range(20)}
  with self.assertRaises(ValueError) as caught:audit.require_project_edges(name,set(),set(),unknown)
  detail=caught.exception.project_edges.public();self.assertEqual(len(detail['extra']),16);self.assertEqual(detail['extraCount'],20);self.assertTrue(detail['truncated'])
  audit.require_project_edges(name,{known},set(),{known})
 def test_edge_diagnostic_failure_preserves_original_private_body(self):
  name='SharpLink.Sdk'
  with patch.object(audit,'ProjectEdgeDiagnostics',side_effect=RuntimeError('diagnostic failed')):
   with self.assertRaises(BaseExceptionGroup) as caught:audit.require_project_edges(name,{'missing'},set(),set())
  self.assertIsInstance(caught.exception.exceptions[0],ValueError)
  self.assertIsInstance(caught.exception.exceptions[1],RuntimeError)
  with self.assertRaises(ValueError) as caught:audit.require_project_edges(name,{'missing'},set(),set())
  original=caught.exception
  with tempfile.TemporaryDirectory() as tmp:
   lane=SimpleNamespace(work=Path(tmp),manifest_sha='synthetic',stage='restore-baseline',results=[],active_scope=None,active_uid=None)
   errors=[original]
   with patch.object(audit.ProjectEdgeDiagnostics,'public',side_effect=RuntimeError('format failed')):
    self.assertEqual(run.finish(lane,errors,writer=lambda *args,**kwargs:None,fallback=lambda *args:None),1)
   self.assertIs(errors[0],original);self.assertIsInstance(errors[1],RuntimeError)
 def synthetic_build(self,root,packages):
  output=root/Path(audit.PROJECTS[a.ASSEMBLY]).parent/'bin/Release/net10.0';output.mkdir(parents=True)
  assets={'libraries':{},'targets':{'net10.0':{}}};libs={};targets={}
  identities=['TUnit/1.78.0','TUnit.Engine/1.78.0','Microsoft.Testing.Platform/2.5.2']
  for identity in identities:
   name=identity.split('/')[0];relative='lib/net10.0/'+name+'.dll';directory=packages/identity.lower();file=directory/relative;file.parent.mkdir(parents=True);file.write_bytes(name.encode())
   (output/(name+'.dll')).write_bytes(name.encode())
   assets['libraries'][identity]={'type':'package','path':identity.lower()};assets['targets']['net10.0'][identity]={'runtime':{relative:{}}}
   libs[identity]={'type':'package','path':identity.lower()};targets[identity]={'runtime':{relative:{}}}
  for name in a.CATALOG['runtimeProjects']:
   p=root/Path(audit.PROJECTS[name]).parent/'bin/Release/net10.0'/(name+'.dll');p.parent.mkdir(parents=True,exist_ok=True);p.write_bytes(name.encode())
   (output/(name+'.dll')).write_bytes(name.encode());libs[name+'/2.0.4']={'type':'project'};targets[name+'/2.0.4']={}
  obj=root/Path(audit.PROJECTS[a.ASSEMBLY]).parent/'obj';obj.mkdir()
  (obj/'project.assets.json').write_text(json.dumps(assets))
  deps={'libraries':libs,'runtimeTarget':{'name':'net10.0'},'targets':{'net10.0':targets}}
  (output/(a.ASSEMBLY+'.deps.json')).write_text(json.dumps(deps))
  (output/(a.ASSEMBLY+'.runtimeconfig.json')).write_text(json.dumps({'runtimeOptions':{'tfm':'net10.0','framework':{'name':'Microsoft.NETCore.App','version':'10.0.0'}}}))
  return output,identities
 def test_runtime_assets_and_seven_project_outputs_are_bound(self):
  with tempfile.TemporaryDirectory() as temporary:
   root=Path(temporary)/'source';packages=Path(temporary)/'packages';output,identities=self.synthetic_build(root,packages)
   result=audit.built(root,packages,identities);self.assertEqual(len(result['runtimeProjects']),7)
   extra=output/'unbound.dll';extra.write_bytes(b'other')
   with self.assertRaises(ValueError):audit.built(root,packages,identities)
   extra.unlink();file=output/'TUnit.Engine.dll';original=file.read_bytes();file.write_bytes(b'wrong')
   with self.assertRaises(ValueError):audit.built(root,packages,identities)
   file.write_bytes(original);p=root/'src/SharpLink.Server/bin/Release/net10.0/SharpLink.Server.dll';p.write_bytes(b'other')
   with self.assertRaises(ValueError):audit.built(root,packages,identities)
 def test_rid_copy_bytes_complete_inventory_and_special_nodes(self):
  import os
  with tempfile.TemporaryDirectory() as temporary:
   root=Path(temporary)/'source';packages=Path(temporary)/'packages';output,identities=self.synthetic_build(root,packages)
   assets_path=root/Path(audit.PROJECTS[a.ASSEMBLY]).parent/'obj/project.assets.json'
   deps_path=output/(a.ASSEMBLY+'.deps.json');assets=c.read_json(assets_path);deps=c.read_json(deps_path)
   identity='TUnit/1.78.0';relative='runtimes/linux-x64/native/libexample.so';entry={'rid':'linux-x64','assetType':'native'}
   for obj in (assets['targets']['net10.0'][identity],deps['targets']['net10.0'][identity]):obj['runtimeTargets']={relative:entry}
   assets_path.write_text(json.dumps(assets));deps_path.write_text(json.dumps(deps))
   installed=packages/identity.lower()/relative;installed.parent.mkdir(parents=True);installed.write_bytes(b'official')
   copied=output/relative;copied.parent.mkdir(parents=True);copied.write_bytes(b'official')
   audit.built(root,packages,identities)
   copied.write_bytes(b'wrong')
   with self.assertRaises(ValueError):audit.built(root,packages,identities)
   copied.unlink()
   with self.assertRaises(FileNotFoundError):audit.built(root,packages,identities)
   copied.symlink_to(installed)
   with self.assertRaises(ValueError):audit.built(root,packages,identities)
   copied.unlink();copied.write_bytes(b'official');extra=copied.parent/'extra.so';extra.write_bytes(b'unmapped')
   with self.assertRaises(ValueError):audit.built(root,packages,identities)
   extra.unlink();os.mkfifo(extra)
   with self.assertRaises(ValueError):audit.built(root,packages,identities)
   extra.unlink();deps['targets']['net10.0'][identity]['runtimeTargets'][relative]['rid']='wrong';deps_path.write_text(json.dumps(deps))
   with self.assertRaises(ValueError):audit.built(root,packages,identities)
 def test_unattributed_extra_raw_event_never_becomes_inconclusive(self):
  run.validate_stderr('baseline','Unobserved task exception: original\n')
  for text in ('','Unobserved task exception: one\nUnobserved task exception: two\n','Unhandled exception in AppDomain: bad\n'):
   with self.assertRaises(ValueError):run.validate_stderr('baseline',text)
  for scope in ('fixed','control','baseline-green'):
   run.validate_stderr(scope,'--no-progress is deprecated\n')
   with self.assertRaises(ValueError):run.validate_stderr(scope,'Unobserved task exception: event\n')
 def test_immutable_tree_special_nodes_and_owner_causes(self):
  import os
  with tempfile.TemporaryDirectory() as temporary:
   root=Path(temporary);(root/'ordinary').write_text('data');run.files(root)
   os.mkfifo(root/'fifo')
   with self.assertRaises(ValueError):run.files(root)
  error=BaseExceptionGroup('original',[owner.CommandFailure(outcome(),[ValueError('unexpected')])])
  self.assertFalse(run.clean_baseline_green_owner(error))



if __name__=='__main__':unittest.main(verbosity=2)
