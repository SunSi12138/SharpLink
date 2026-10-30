"""Synthetic offline controls only: no CodeQL, .NET, network, or real scan execution."""
import copy
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
from types import SimpleNamespace
import evidence as e


def sample():
 return {'version':'2.1.0','runs':[{'tool':{'driver':{'name':'CodeQL','semanticVersion':'2.27.3','rules':[{'id':'cs/test','defaultConfiguration':{'level':'warning'},'properties':{'security-severity':'8.1'}}]}},'automationDetails':{'id':'/language:csharp/'},'invocations':[{'executionSuccessful':True}], 'results':[{'ruleId':'cs/test','ruleIndex':0,'level':'error','message':{'text':'synthetic'},'suppressions':[{'kind':'external','status':'accepted'}],'baselineState':'unchanged'}]}]}

class SarifTests(unittest.TestCase):
 def test_finding_error_high_suppressed_unchanged_is_not_security_pass(self):
  r=e.assess_sarif(sample());self.assertEqual(r['executionEvidence'],'COMPLETE');self.assertEqual(r['securityDecision'],'NOT_ASSESSED_REQUIRES_INDEPENDENT_REVIEW')
  f=r['findings'][0];self.assertEqual(f['effectiveLevel'],'error');self.assertEqual(f['securitySeverityBand'],'high');self.assertEqual(f['security-severity'],'8.1');self.assertEqual(f['baselineState'],'unchanged');self.assertEqual(f['suppressions'],sample()['runs'][0]['results'][0]['suppressions']);self.assertEqual(f['originalResult'],sample()['runs'][0]['results'][0]);self.assertEqual(r['historicalGitHubAlertDisposition'],'NOT_OBTAINED')
 def test_zero_results_explicit_not_absent(self):
  d=sample();d['runs'][0]['results']=[];self.assertEqual(e.assess_sarif(d)['findingCount'],0)
  del d['runs'][0]['results']
  with self.assertRaises(ValueError):e.assess_sarif(d)
 def test_identity_mutations(self):
  for field,value in [('name','other'),('semanticVersion','2.28.0')]:
   d=sample();d['runs'][0]['tool']['driver'][field]=value
   with self.assertRaises(ValueError):e.assess_sarif(d)
  for field,value in [('version','2.0.0'),('runs',[]),('runs',sample()['runs']*2)]:
   d=sample();d[field]=value
   with self.assertRaises(ValueError):e.assess_sarif(d)
  d=sample();d['runs'][0]['automationDetails']['id']='wrong'
  with self.assertRaises(ValueError):e.assess_sarif(d)
 def test_missing_invocation_rejected(self):
  for value in [None,[]]:
   d=sample();d['runs'][0]['invocations']=value
   with self.assertRaises(ValueError):e.assess_sarif(d)
 def test_failed_invocation_and_error_notifications_incomplete_not_silent(self):
  for mutation in ['failed','missing','execution','configuration']:
   d=sample();i=d['runs'][0]['invocations'][0]
   if mutation=='failed':i['executionSuccessful']=False
   elif mutation=='missing':del i['executionSuccessful']
   else:i['toolExecutionNotifications' if mutation=='execution' else 'toolConfigurationNotifications']=[{'level':'error','message':{'text':'failure'}}]
   r=e.assess_sarif(d);self.assertEqual(r['executionEvidence'],'INCOMPLETE');self.assertEqual(r['findingCount'],1);self.assertTrue(r['executionErrors'])
 def test_warning_diagnostic_retained(self):
  d=sample();d['runs'][0]['invocations'][0]['toolExecutionNotifications']=[{'level':'warning','message':{'text':'coverage limitation'}}]
  r=e.assess_sarif(d);self.assertEqual(len(r['notifications']),1)
 def test_rule_mismatch_unresolved_and_duplicate_rejected(self):
  for mutation in ['id','index','conflict','index-conflict','duplicate','none']:
   d=sample();r=d['runs'][0]['results'][0]
   if mutation=='id':r['ruleId']='unknown'
   elif mutation=='index':r['ruleIndex']=1
   elif mutation=='conflict':r['rule']={'id':'different'}
   elif mutation=='index-conflict':r['rule']={'id':'cs/test','index':1}
   elif mutation=='none':del r['ruleId'];del r['ruleIndex']
   else:del r['ruleIndex'];d['runs'][0]['tool']['driver']['rules']*=2
   with self.assertRaises(ValueError):e.assess_sarif(d)
 def test_extension_rule_resolves_and_all_metadata_retained(self):
  d=sample();d['runs'][0]['tool']['extensions']=[{'name':'pack','rules':[{'id':'ext/rule','properties':{'security-severity':'9.9'}}]}];d['runs'][0]['results'].append({'rule':{'id':'ext/rule','index':0,'toolComponent':{'index':0}},'message':{'text':'extended'}})
  r=e.assess_sarif(d);self.assertEqual(r['findingCount'],2);self.assertEqual(r['findings'][1]['securitySeverityBand'],'critical')
 def test_component_reference_metadata_must_match(self):
  for component in [{'name':'wrong'},{'id':'unresolved'},{'guid':'wrong'},{'index':True},{'index':-1}]:
   d=sample();d['runs'][0]['results'][0]['rule']={'toolComponent':component}
   with self.assertRaises(ValueError):e.assess_sarif(d)
  d=sample();d['runs'][0]['results'][0]['rule']={'toolComponent':{'name':'CodeQL'}}
  self.assertEqual(e.assess_sarif(d)['findingCount'],1)
 def test_security_severity_missing_preserved_not_imputed(self):
  d=sample();d['runs'][0]['tool']['driver']['rules'][0]['properties']={};r=e.assess_sarif(d);self.assertIsNone(r['findings'][0]['security-severity']);self.assertEqual(r['securitySeverityCounts'],{'not-provided':1})
 def test_bad_severities_and_level_rejected(self):
  for score in ['NaN','Infinity','-1','11','nonsense']:
   d=sample();d['runs'][0]['tool']['driver']['rules'][0]['properties']['security-severity']=score
   with self.assertRaises(ValueError):e.assess_sarif(d)
  d=sample();d['runs'][0]['results'][0]['level']='critical'
  with self.assertRaises(ValueError):e.assess_sarif(d)

class CampaignTests(unittest.TestCase):
 def setUp(self):
  self.tmp=tempfile.TemporaryDirectory();self.addCleanup(self.tmp.cleanup);self.root=Path(self.tmp.name);self.out=self.root/'out';self.out.mkdir();self.sarif=self.root/'sarif';self.sarif.mkdir();self.db=self.root/'db';self.results=self.db/'csharp/results/codeql/csharp-queries'
  (self.sarif/'csharp.sarif').write_text(json.dumps(sample()))
  e.write(self.out/'source-identity.json',{'sourceFiles':{'A.cs':'0'*64}});e.write(self.out/'toolchain.json',{'actualVersion':{'version':'2.27.3'},'bundleSha256':e.BUNDLE_SHA,'queryPackVersion':'1.10.2'})
  for name in e.EXPECTED['queries']:
   p=self.results/(name+'.bqrs');p.parent.mkdir(parents=True,exist_ok=True);p.write_bytes(b'synthetic query result')
 def collect(self,init='success',analyze='success'):
  with patch.object(e,'source_identity',return_value={'A.cs':'0'*64}),patch.object(e,'config_receipt'),patch.object(e,'validate_staged_tools'),patch.object(e,'workspace_inventory',return_value={'synthetic':'scope'}),patch.object(e,'sarif_scope',return_value={'synthetic':'scope'}),patch.dict(e.os.environ,{'GITHUB_WORKSPACE':str(self.root)}):e.collect(self.root,self.db,self.sarif,self.out,init,analyze)
 def test_complete_campaign_has_all59_raw_sarif_and_no_security_pass(self):
  self.collect()
  for name in ['request.json','effective-scope-before.json','effective-scope-after.json','staged-tools.json','workspace-scope-before.json']:e.write(self.out/name,{'synthetic':'receipt'})
  e.finalize(self.out);r=json.loads((self.out/'completion.json').read_text());self.assertEqual(r['executionEvidence'],'COMPLETE');self.assertEqual(r['findingCount'],1);self.assertIn('NOT_ASSESSED',r['securityDecision']);self.assertEqual((self.out/'csharp.sarif').read_bytes(),(self.sarif/'csharp.sarif').read_bytes());self.assertEqual(json.loads((self.out/'query-result-hashes.json').read_text())['queryCount'],59)
 def test_completion_without_required_receipts_is_incomplete(self):
  self.collect()
  with self.assertRaises(ValueError):e.finalize(self.out)
  self.assertEqual(json.loads((self.out/'artifact-hashes.json').read_text())['executionEvidence'],'INCOMPLETE')
 def test_missing_query_rejected_and_raw_preserved(self):
  (self.results/(e.EXPECTED['queries'][0]+'.bqrs')).unlink()
  with self.assertRaises(ValueError):self.collect()
  self.assertTrue((self.out/'csharp.sarif').is_file());self.assertFalse((self.out/'completion.json').exists())
 def test_extra_query_rejected(self):
  (self.results/'extra.bqrs').write_bytes(b'x')
  with self.assertRaises(ValueError):self.collect()
 def test_empty_query_rejected(self):
  (self.results/(e.EXPECTED['queries'][0]+'.bqrs')).write_bytes(b'')
  with self.assertRaises(ValueError):self.collect()
 def test_wrong_toolchain_rejected(self):
  (self.out/'toolchain.json').write_text(json.dumps({'actualVersion':{'version':'2.28'},'bundleSha256':e.BUNDLE_SHA,'queryPackVersion':'1.10.2'}))
  with self.assertRaises(ValueError):self.collect()
 def test_source_mutation_rejected(self):
  with patch.object(e,'source_identity',return_value={'A.cs':'1'*64}),patch.object(e,'config_receipt'):
   with self.assertRaises(ValueError):e.collect(self.root,self.db,self.sarif,self.out,'success','success')
 def test_failed_action_preserves_findings_not_complete(self):
  with self.assertRaises(ValueError):self.collect(analyze='failure')
  self.assertTrue((self.out/'findings.json').is_file());self.assertTrue((self.out/'csharp.sarif').is_file());self.assertFalse((self.out/'completion.json').exists())
 def test_error_diagnostic_retains_query_and_scope_receipts_but_fails(self):
  doc=sample();doc['runs'][0]['invocations'][0]['toolExecutionNotifications']=[{'level':'error','message':{'text':'synthetic extraction failure'}}];(self.sarif/'csharp.sarif').write_text(json.dumps(doc))
  with self.assertRaises(ValueError):self.collect()
  self.assertTrue((self.out/'query-result-hashes.json').is_file());self.assertTrue((self.out/'workspace-scope-after.json').is_file());self.assertTrue((self.out/'check-outcomes.json').is_file());self.assertFalse((self.out/'completion.json').exists())
 def test_missing_sarif_never_zero(self):
  (self.sarif/'csharp.sarif').unlink()
  with self.assertRaises(ValueError):self.collect()
 def test_extra_sarif_rejected(self):
  (self.sarif/'extra.sarif').write_text('{}')
  with self.assertRaises(ValueError):self.collect()
  self.assertTrue((self.out/'csharp.sarif').is_file())
 def test_symlink_sarif_rejected(self):
  p=self.sarif/'csharp.sarif';p.unlink();p.symlink_to(self.out/'toolchain.json')
  with self.assertRaises(ValueError):self.collect()
 def test_incomplete_finalize_marks_manifest_and_fails(self):
  with self.assertRaises(ValueError):e.finalize(self.out)
  self.assertEqual(json.loads((self.out/'artifact-hashes.json').read_text())['executionEvidence'],'INCOMPLETE')
 def test_artifact_extra_file_and_directory_rejected(self):
  for directory in [False,True]:
   p=self.out/'unrelated'
   if directory:p.mkdir()
   else:p.write_text('never export')
   with self.assertRaises(ValueError):e.finalize(self.out)
   if directory:p.rmdir()
   else:p.unlink()
 def test_actual_cli_and_pack_closure_with_mocked_cli(self):
  bundle=self.root/'bundle';bundle.write_bytes(b'fake frozen bundle')
  cli=self.root/'codeql';cli.write_bytes(b'fake cli')
  pack=self.root/'qlpacks/codeql/csharp-queries/1.10.2';pack.mkdir(parents=True)
  (pack/'qlpack.yml').write_text('name: codeql/csharp-queries\nversion: 1.10.2\n')
  for name in e.EXPECTED['queries']:
   p=pack/(name+'.qlx');p.parent.mkdir(parents=True,exist_ok=True);p.write_bytes(b'fake precompiled query')
  (self.out/'toolchain.json').unlink()
  with patch.object(e,'BUNDLE_SHA',e.sha(bundle)),patch.object(e.subprocess,'run',return_value=SimpleNamespace(stdout='{"version":"2.27.3"}')) as run:
   e.capture_toolchain(cli,bundle,self.out)
   self.assertEqual(run.call_args.args[0],[str(cli),'version','--format=json'])
  r=json.loads((self.out/'toolchain.json').read_text());self.assertEqual(len(r['queryFileHashes']),59);self.assertEqual(r['queryPackVersion'],'1.10.2')
 def test_actual_cli_version_mismatch_rejected(self):
  bundle=self.root/'bundle';bundle.write_bytes(b'fake frozen bundle')
  with patch.object(e,'BUNDLE_SHA',e.sha(bundle)),patch.object(e.subprocess,'run',return_value=SimpleNamespace(stdout='{"version":"2.28.0"}')):
   with self.assertRaises(ValueError):e.capture_toolchain(self.root/'codeql',bundle,self.out)
 def test_bundle_mismatch_blocks_cli_execution(self):
  bundle=self.root/'bundle';bundle.write_bytes(b'bad')
  with patch.object(e.subprocess,'run') as process:
   with self.assertRaises(ValueError):e.capture_toolchain(self.root/'codeql',bundle,self.out)
   process.assert_not_called()


class ScopeTests(unittest.TestCase):
 def test_safe_environment_only(self):
  self.assertEqual(e.validate_environment({})['RUNNER_DEBUG'],'')
  for key,values in {'RUNNER_DEBUG':['1','true'],'CODEQL_ACTION_DEBUG_COMBINED_SARIF':['true','TRUE'],'CODEQL_OVERLAY_DATABASE_MODE':['overlay','overlay-base'],'CODE_SCANNING_IS_ANALYZING_DEFAULT_BRANCH':['true'],'CODEQL_ACTION_TEMP':['/override']}.items():
   for value in values:
    with self.assertRaises(ValueError):e.validate_environment({key:value})
 def test_effective_config_and_mutations(self):
  with tempfile.TemporaryDirectory() as temp:
   root=Path(temp);source=root/'source';db=root/'db'
   config={'languages':['csharp'],'analysisKinds':['code-scanning'],'buildMode':'none','debugMode':False,'overlayDatabaseMode':'none','useOverlayDatabaseCaching':False,'trapCaches':{},'dependencyCachingEnabled':'none','dependencyCachingRestoredKeys':[],'extraQueryExclusions':[],'repositoryRoot':str(source),'dbLocation':str(db),'originalUserInput':{},'computedConfig':{}}
   e.effective_config(config,root,source,db)
   for key in config:
    modified=copy.deepcopy(config);modified[key]='wrong'
    with self.assertRaises(ValueError):e.effective_config(modified,root,source,db)
    modified=copy.deepcopy(config);del modified[key]
    with self.assertRaises(ValueError):e.effective_config(modified,root,source,db)
   for key,value in [('disable-default-queries',True),('paths',['src']),('paths-ignore',['tests']),('query-filters',[{'exclude':{'id':'cs/test'}}]),('packs',['extra']),('queries',['custom']),('threat-models',['local']),('default-setup',{'org':{'model-packs':['extra']}}),('unknown',{'future':'scope'})]:
    modified=copy.deepcopy(config);modified['computedConfig'][key]=value
    with self.assertRaises(ValueError):e.effective_config(modified,root,source,db)
   (root/'pr-diff-range.json').write_text('[]')
   with self.assertRaises(ValueError):e.effective_config(config,root,source,db)


class IsolationTests(unittest.TestCase):
 def setUp(self):
  self.tmp=tempfile.TemporaryDirectory();self.addCleanup(self.tmp.cleanup);self.root=Path(self.tmp.name);self.source=self.root/'workspace';self.source.mkdir();self.tracked={f'src/Part{i}.cs':'unused' for i in range(1383)};self.tracked['global.json']='unused'
  for name in self.tracked:
   p=self.source/name;p.parent.mkdir(parents=True,exist_ok=True);p.write_text('synthetic')
 def test_exact_workspace_and_csharp_set(self):
  r=e.workspace_inventory(self.source,self.tracked,self.source);self.assertEqual(r['csharpCount'],1383);self.assertEqual(r['globalJsonPaths'],['global.json']);self.assertEqual(r['untrackedOrGeneratedCSharpFiles'],[])
 def test_nested_source_layout_rejected(self):
  with self.assertRaises(ValueError):e.workspace_inventory(self.source,self.tracked,self.root)
 def test_second_tree_generated_source_extra_global_and_missing_file_rejected(self):
  for name in ['tools/src/Other.cs','obj/Generated.cs','nested/global.json','src/Unknown.CS']:
   p=self.source/name;p.parent.mkdir(parents=True,exist_ok=True);p.write_text('unexpected')
   with self.assertRaises(ValueError):e.workspace_inventory(self.source,self.tracked,self.source)
   p.unlink()
  p=self.source/'src/Part0.cs';p.unlink()
  with self.assertRaises(ValueError):e.workspace_inventory(self.source,self.tracked,self.source)
 def test_symlink_directory_rejected(self):
  (self.source/'second').symlink_to(self.root,target_is_directory=True)
  with self.assertRaises(ValueError):e.workspace_inventory(self.source,self.tracked,self.source)
 def sarif(self):
  doc=sample();doc['runs'][0]['artifacts']=[{'location':{'uri':name,'uriBaseId':'%SRCROOT%'}}for name in self.tracked if name.endswith('.cs')];return doc
 def test_sarif_exact_uri_set_and_notifications(self):
  doc=self.sarif();doc['runs'][0]['invocations'][0]['toolExecutionNotifications']=[{'level':'none','message':{'text':f'Compiler message in {self.source}/src/Part0.cs: synthetic'}}]
  self.assertEqual(e.sarif_scope(doc,self.source,self.tracked,self.root/'staged')['csharpArtifacts'],1383)
 def test_sarif_outside_temp_tool_diagnostics_and_uri_rejected(self):
  for path in [self.root/'tools/Other.cs',self.root/'staged/Other.cs',Path('/opt/External.cs'),Path('/home/external/External.CS')]:
   doc=self.sarif();doc['runs'][0]['invocations'][0]['toolExecutionNotifications']=[{'level':'none','message':{'text':f'Compiler message in {path}: bad input'}}]
   with self.assertRaises(ValueError):e.sarif_scope(doc,self.source,self.tracked,self.root/'staged')
  for uri in ['../tools/Other.cs','%2e%2e/tools/Other.cs','/tmp/Other.cs','src/Missing.cs']:
   doc=self.sarif();doc['runs'][0]['artifacts'][0]['location']['uri']=uri
   with self.assertRaises(ValueError):e.sarif_scope(doc,self.source,self.tracked,self.root/'staged')
 def test_sarif_missing_duplicate_or_wrong_base_rejected(self):
  for mode in ['missing','duplicate','base']:
   doc=self.sarif()
   if mode=='missing':doc['runs'][0]['artifacts'].pop()
   elif mode=='duplicate':doc['runs'][0]['artifacts'].append(doc['runs'][0]['artifacts'][0])
   else:doc['runs'][0]['artifacts'][0]['location']['uriBaseId']='%OTHER%'
   with self.assertRaises(ValueError):e.sarif_scope(doc,self.source,self.tracked,self.root/'staged')

class StageTests(unittest.TestCase):
 def setUp(self):
  self.tmp=tempfile.TemporaryDirectory();self.addCleanup(self.tmp.cleanup);self.root=Path(self.tmp.name);self.repo=self.root/'workspace';self.repo.mkdir();self.temp=self.root/'runner-temp';self.temp.mkdir();self.out=self.temp/'evidence';self.out.mkdir();self.stage=self.temp/'tools'
  for relative in e.TOOL_PATHS:
   p=self.repo/relative;p.parent.mkdir(parents=True,exist_ok=True);p.write_text('synthetic '+relative)
  # The request checkout can contain product files; none may enter the staged closure.
  (self.repo/'Product.cs').write_text('not a staged tool')
  for args in [('init','-q'),('config','user.name','Synthetic'),('config','user.email','synthetic@example.invalid'),('add','.'),('commit','-qm','synthetic')]:e.git(self.repo,*args)
  e.write(self.out/'request.json',{'requestCommit':e.git(self.repo,'rev-parse','HEAD'),'reviewedToolCommit':'a'*40,'reviewedToolTree':'b'*40,'status':'REQUEST_IDENTITY_PASSED'})
 def stage_tools(self):
  with patch.object(e,'ROOT',self.repo/'eng/codeql-sarif-evidence'),patch.dict(e.os.environ,{'GITHUB_WORKSPACE':str(self.repo),'RUNNER_TEMP':str(self.temp)}):e.stage_tools(self.stage,self.out)
 def test_exact8_staged_no_git_or_product_and_mutation_rejected(self):
  self.stage_tools();receipt=json.loads((self.out/'staged-tools.json').read_text());self.assertEqual(set(receipt['files']),e.TOOL_PATHS);self.assertFalse((self.stage/'Product.cs').exists());self.assertFalse((self.stage/'.git').exists())
  with patch.object(e,'ROOT',self.stage/'eng/codeql-sarif-evidence'),patch.dict(e.os.environ,{'GITHUB_WORKSPACE':str(self.repo)}):
   e.validate_staged_tools(self.out)
   (self.stage/'eng/codeql-sarif-evidence/evidence.py').write_text('changed')
   with self.assertRaises(ValueError):e.validate_staged_tools(self.out)
 def test_workspace_stage_and_dirty_request_rejected(self):
  self.stage=self.repo/'tools'
  with self.assertRaises(ValueError):self.stage_tools()
  self.stage=self.temp/'tools';(self.repo/'Product.cs').write_text('dirty')
  with self.assertRaises(ValueError):self.stage_tools()
 def test_extra_staged_source_rejected(self):
  self.stage_tools();(self.stage/'Other.cs').write_text('must not execute here')
  with patch.object(e,'ROOT',self.stage/'eng/codeql-sarif-evidence'),patch.dict(e.os.environ,{'GITHUB_WORKSPACE':str(self.repo)}):
   with self.assertRaises(ValueError):e.validate_staged_tools(self.out)

if __name__=='__main__':unittest.main()
