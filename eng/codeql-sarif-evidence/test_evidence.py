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
  e.write(self.out/'manual-build.json',{'workspaceCSharp':{}});e.write(self.out/'source-identity.json',{'sourceFiles':{'A.cs':'0'*64}});e.write(self.out/'toolchain.json',{'actualVersion':{'version':'2.27.3'},'bundleSha256':e.BUNDLE_SHA,'queryPackVersion':'1.10.2'})
  for name in e.EXPECTED['queries']:
   p=self.results/(name+'.bqrs');p.parent.mkdir(parents=True,exist_ok=True);p.write_bytes(b'synthetic query result')
 def collect(self,init='success',analyze='success',observation_error=None,semantic_error=None):
  def observe(*args):
   e.write(self.out/'compilation-file-hashes.json',{'synthetic':'safe partial observations','status':'INCOMPLETE' if observation_error else 'SYNTHETIC'})
   if observation_error:raise ValueError(observation_error)
  def semantic(*args):
   self.assertTrue((self.out/'compilation-file-hashes.json').is_file())
   e.write(self.out/'compilation-evidence.json',{'synthetic':'semantic result','status':'INCOMPLETE' if semantic_error else 'SYNTHETIC'})
   if semantic_error:raise ValueError(semantic_error)
  with patch.object(e,'source_identity',return_value={'A.cs':'0'*64}),patch.object(e,'config_receipt'),patch.object(e,'validate_staged_tools'),patch.object(e,'validate_process_receipts'),patch.object(e,'workspace_inventory',return_value={'synthetic':'scope'}),patch.object(e,'manual_sarif_scope',return_value={'synthetic':'scope'}),patch.object(e.manual,'validate_ledger'),patch.object(e.manual,'scan_workspace',return_value={}),patch.object(e.manual,'archive',side_effect=lambda *args:[e.write(self.out/name,{'synthetic':'native receipt'}) for name in ['manual-archive.json','manual-coverage.json']]),patch.object(e.manual,'native_log_receipt',side_effect=lambda *args,**kwargs:e.write(self.out/'native-compiler-log.json',{'synthetic':'native log receipt'})),patch.object(e.compilation_evidence,'observe_files',side_effect=observe),patch.object(e.compilation_evidence,'validate',side_effect=semantic),patch.dict(e.os.environ,{'GITHUB_WORKSPACE':str(self.root)}):e.collect(self.root,self.db,self.sarif,self.out,init,analyze)
 def test_complete_campaign_has_all59_raw_sarif_and_no_security_pass(self):
  self.collect()
  for name in ['request.json','effective-scope-before.json','effective-scope-after.json','staged-tools.json','workspace-scope-before.json','process-sdk.json','process-restore.json','process-build.json','process-lifecycle.json']:e.write(self.out/name,{'synthetic':'receipt'})
  for name in e.EXTRA_EVIDENCE:
   path=self.out/name;path.parent.mkdir(parents=True,exist_ok=True)
   if not path.exists():e.write(path,{'synthetic':'separate supplemental receipt, not actual binary'})
  e.finalize(self.out);r=json.loads((self.out/'completion.json').read_text());self.assertEqual(r['executionEvidence'],'COMPLETE');self.assertEqual(r['findingCount'],1);self.assertIn('NOT_ASSESSED',r['securityDecision']);self.assertEqual((self.out/'csharp.sarif').read_bytes(),(self.sarif/'csharp.sarif').read_bytes());self.assertEqual(json.loads((self.out/'query-result-hashes.json').read_text())['queryCount'],59)
 def test_observation_failure_preserves_partial_and_other_checks_run(self):
  with self.assertRaises(ValueError):self.collect(observation_error='synthetic unknown reference path')
  checks=json.loads((self.out/'check-outcomes.json').read_text())['checks']
  self.assertEqual(checks['compilationFileHashObservations']['status'],'INCOMPLETE')
  self.assertEqual(checks['structuredCompilationEvidence']['status'],'PASS')
  self.assertTrue((self.out/'compilation-file-hashes.json').is_file());self.assertTrue((self.out/'query-result-hashes.json').is_file())
  self.assertFalse((self.out/'completion.json').exists())
 def test_semantic_failure_does_not_discard_prior_observed_hash_receipt(self):
  with self.assertRaises(ValueError):self.collect(semantic_error='synthetic error diagnostic')
  self.assertTrue((self.out/'compilation-file-hashes.json').is_file());self.assertTrue((self.out/'compilation-evidence.json').is_file())
  self.assertTrue((self.out/'csharp.sarif').is_file());self.assertFalse((self.out/'completion.json').exists())
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
  self.assertEqual(e.validate_environment({'CODEQL_EXTRACTOR_CSHARP_OPTION_LOGGING_VERBOSITY':'info'})['RUNNER_DEBUG'],'')
  for key,values in {'CODEQL_EXTRACTOR_CSHARP_OPTION_LOGGING_VERBOSITY':['','warnings','debug'],'RUNNER_DEBUG':['1','true'],'CODEQL_ACTION_DEBUG_COMBINED_SARIF':['true','TRUE'],'CODEQL_OVERLAY_DATABASE_MODE':['overlay','overlay-base'],'CODE_SCANNING_IS_ANALYZING_DEFAULT_BRANCH':['true'],'CODEQL_ACTION_TEMP':['/override']}.items():
   for value in values:
    with self.assertRaises(ValueError):e.validate_environment({'CODEQL_EXTRACTOR_CSHARP_OPTION_LOGGING_VERBOSITY':'info',key:value})
 def test_effective_config_and_mutations(self):
  with tempfile.TemporaryDirectory() as temp:
   root=Path(temp);source=root/'source';db=root/'db'
   config={'languages':['csharp'],'analysisKinds':['code-scanning'],'buildMode':'manual','debugMode':False,'overlayDatabaseMode':'none','useOverlayDatabaseCaching':False,'trapCaches':{},'dependencyCachingEnabled':'none','dependencyCachingRestoredKeys':[],'extraQueryExclusions':[],'repositoryRoot':str(source),'dbLocation':str(db),'originalUserInput':{},'computedConfig':{}}
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

class ManualSarifScopeTests(unittest.TestCase):
 def setUp(self):
  self.tmp=tempfile.TemporaryDirectory();self.addCleanup(self.tmp.cleanup);self.root=Path(self.tmp.name);self.source=self.root/'source';self.source.mkdir();self.out=self.root/'out';self.out.mkdir()
  self.tracked={'src/Extracted.cs':'1'*64,'test/Enumerated.cs':'2'*64};self.generated='src/obj/Generated.cs';self.external=str(self.root/'nuget/tunit.core/1.73.19'/e.manual.PACKAGE_MEMBER)
  self.receipt={'csharpFiles':{'src/Extracted.cs':{'kind':'tracked'},self.generated:{'kind':'generated-build-output'},e.manual.PACKAGE_KEY:{'kind':'external-package-source','packageId':'TUnit.Core','packageVersion':'1.73.19','member':e.manual.PACKAGE_MEMBER}},'externalSourcePaths':{self.external:e.manual.PACKAGE_KEY}}
  e.write(self.out/'manual-archive.json',self.receipt);e.write(self.out/'source-identity.json',{'sourceFiles':self.tracked});e.write(self.out/'workspace-scope-after.json',{'evidenceStatus':'WORKSPACE_BYTES_OBSERVED_NOT_EXTRACTION_PROOF','problems':[],'csharpFiles':{p:{'kind':'tracked','sha256':h}for p,h in self.tracked.items()}})
 def doc(self):
  d=sample();d['runs'][0]['artifacts']=[{'location':{'uri':p,'uriBaseId':'%SRCROOT%'}}for p in self.tracked];return d
 def test_enumerated_not_archived_does_not_add_native_coverage(self):
  r=e.manual_sarif_scope(self.doc(),self.source,self.out);self.assertEqual(r['enumeratedTrackedNotArchived'],['test/Enumerated.cs']);self.assertEqual(r['nativeArchiveCSharpCount'],3);self.assertEqual(r['successMarkerPaths'],[])
 def test_generated_and_exact_external_need_native_binding(self):
  d=self.doc();d['runs'][0]['artifacts'] += [{'location':{'uri':self.generated,'uriBaseId':'%SRCROOT%'}},{'location':{'uri':'file://'+self.external}}]
  r=e.manual_sarif_scope(d,self.source,self.out);self.assertIn(e.manual.PACKAGE_KEY,r['sarifCSharpPaths'])
  for uri in ['src/Unknown.cs','src/obj/Other.cs','file://'+self.external.replace('1.73.19','1.73.20'),'../outside.cs','%2e%2e/outside.cs','src/Extracted.cs?query','src/Extracted.cs#fragment']:
   with self.subTest(uri=uri):
    bad=self.doc();bad['runs'][0]['artifacts'].append({'location':{'uri':uri,'uriBaseId':'%SRCROOT%'}})
    with self.assertRaises(ValueError):e.manual_sarif_scope(bad,self.source,self.out)
 def test_diagnostic_error_on_enumerated_file_is_not_filtered(self):
  d=self.doc();d['runs'][0]['invocations'][0]['toolExecutionNotifications']=[{'level':'error','message':{'text':f'Compiler error in {self.source}/test/Enumerated.cs: retained'},'locations':[{'physicalLocation':{'artifactLocation':{'uri':'test/Enumerated.cs','uriBaseId':'%SRCROOT%','index':1}}}]}]
  r=e.manual_sarif_scope(d,self.source,self.out);self.assertEqual(r['diagnosticTrackedNotArchived'],['test/Enumerated.cs']);self.assertEqual(e.assess_sarif(d)['executionEvidence'],'INCOMPLETE')
 def test_success_marker_without_archive_is_visible_and_incomplete(self):
  d=self.doc();d['runs'][0]['invocations'][0]['toolExecutionNotifications']=[{'descriptor':{'id':'cs/diagnostics/successfully-extracted-files'},'level':'none','message':{'text':''},'locations':[{'physicalLocation':{'artifactLocation':{'uri':'test/Enumerated.cs','uriBaseId':'%SRCROOT%','index':1}}}]}]
  with self.assertRaises(ValueError):e.manual_sarif_scope(d,self.source,self.out)
  r=json.loads((self.out/'sarif-scope.json').read_text());self.assertEqual(r['successMarkersAbsentFromNativeArchive'],['test/Enumerated.cs']);self.assertEqual(r['evidenceStatus'],'INCOMPLETE')
 def test_unknown_diagnostics_case_paths_and_staged_tools_rejected(self):
  for value in ['/opt/Unknown.cs','/opt/Unknown.CS',str(e.ROOT.parents[1])+'/Injected.cs',self.external.replace('GeneratedNamespace','Unknown')]:
   d=self.doc();d['runs'][0]['invocations'][0]['toolExecutionNotifications']=[{'level':'none','message':{'text':'Compiler message in '+value+': bad'}}]
   with self.assertRaises(ValueError):e.manual_sarif_scope(d,self.source,self.out)
 def test_duplicate_wrong_base_and_conflicting_index_rejected(self):
  d=self.doc();d['runs'][0]['artifacts'].append(d['runs'][0]['artifacts'][0])
  with self.assertRaises(ValueError):e.manual_sarif_scope(d,self.source,self.out)
  d=self.doc();d['runs'][0]['artifacts'][0]['location']['uriBaseId']='%UNKNOWN%'
  with self.assertRaises(ValueError):e.manual_sarif_scope(d,self.source,self.out)
  d=self.doc();d['runs'][0]['invocations'][0]['toolExecutionNotifications']=[{'message':{'text':''},'locations':[{'physicalLocation':{'artifactLocation':{'uri':'test/Enumerated.cs','uriBaseId':'%SRCROOT%','index':0}}}]}]
  with self.assertRaises(ValueError):e.manual_sarif_scope(d,self.source,self.out)
 def test_tracked_post_hash_required_for_enumeration(self):
  p=self.out/'workspace-scope-after.json';d=json.loads(p.read_text());d['csharpFiles']['test/Enumerated.cs']['sha256']='0'*64;p.write_text(json.dumps(d))
  with self.assertRaises(ValueError):e.manual_sarif_scope(self.doc(),self.source,self.out)
 def test_after_scope_failure_retains_partial_inventory(self):
  (self.out/'manual-build.json').write_text(json.dumps({'workspaceCSharp':{}}))
  def partial(source,tracked,projects,problems):problems.append('unknown bin C#');return {'unknown.cs':{'kind':'unknown-or-invalid'}}
  (self.out/'workspace-scope-after.json').unlink()
  with patch.object(e,'source_identity',return_value=self.tracked),patch.object(e.manual,'validate_ledger',return_value=[]),patch.object(e.manual,'scan_workspace',side_effect=partial):
   with self.assertRaises(ValueError):e.source_after(self.source,self.out)
  r=json.loads((self.out/'workspace-scope-after.json').read_text());self.assertEqual(r['evidenceStatus'],'INCOMPLETE');self.assertIn('unknown.cs',r['csharpFiles'])

class SupplementalProtocolTests(unittest.TestCase):
 def test_native_missing_pid_remains_incomplete_observation_not_success(self):
  with tempfile.TemporaryDirectory() as temp:
   root=Path(temp);source=root/'source';source.mkdir();db=root/'db';log=db/'csharp/log';log.mkdir(parents=True);out=root/'out';out.mkdir()
   (log/'csharp.log').write_text('[123] [INFO] EXTRACTION SUCCEEDED in 1ms\n');(log/'csharp.fake.txt').write_text('synthetic argument log, only hash retained')
   receipt=e.manual.native_log_receipt(source,db,out,observational=True)
   self.assertEqual(receipt['nativePidClosure'],'INCOMPLETE');self.assertFalse(receipt['contexts'][0]['complete']);self.assertEqual(receipt['structuralCompilationEvidence'],'REQUIRED_SEPARATELY')
   strict=root/'strict';strict.mkdir()
   with self.assertRaises(ValueError):e.manual.native_log_receipt(source,db,strict)
 def test_native_errors_and_unsupported_are_never_observationally_waived(self):
  for line in ['[123] [ERROR] retained failure','[123] [INFO] EXTRACTION FAILED with 1 error(s) in 1ms','unknown EXTRACTION missing syntax']:
   with tempfile.TemporaryDirectory() as temp:
    root=Path(temp);source=root/'source';source.mkdir();db=root/'db';log=db/'csharp/log';log.mkdir(parents=True);out=root/'out';out.mkdir()
    (log/'csharp.log').write_text(line+'\n');(log/'csharp.fake.txt').write_text('synthetic')
    with self.assertRaises(ValueError):e.manual.native_log_receipt(source,db,out,observational=True)
    receipt=json.loads((out/'native-compiler-log.json').read_text());self.assertEqual(receipt['nativePidClosure'],'INCOMPLETE')
    if 'EXTRACTION FAILED' in line:self.assertEqual(receipt['failedTerminals'],[{'pid':123,'line':1,'status':'FAILED'}])
 def test_nested_artifact_paths_stay_exact_and_symlink_free(self):
  for name in ['package-evidence/other.nupkg','package-evidence/secret.config','unexpected/known.json']:
   with tempfile.TemporaryDirectory() as temp:
    root=Path(temp);p=root/name;p.parent.mkdir();p.write_text('not allowed')
    with self.assertRaises(ValueError):e.finalize(root)
  with tempfile.TemporaryDirectory() as temp:
   root=Path(temp);(root/'package-evidence').symlink_to(root,target_is_directory=True)
   with self.assertRaises(ValueError):e.finalize(root)

if __name__=='__main__':unittest.main()
