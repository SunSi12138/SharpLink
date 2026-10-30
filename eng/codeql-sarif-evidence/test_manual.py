"""Offline synthetic manual-scope controls: never execute .NET/CodeQL or product code."""
import base64, copy, hashlib, json, tempfile, unittest, zipfile, sys, time, os, signal
import process_host as host
from pathlib import Path
from unittest.mock import patch
import manual as m
import evidence as e

class ManualControls(unittest.TestCase):
 def setUp(self):
  self.temp=tempfile.TemporaryDirectory();self.addCleanup(self.temp.cleanup);self.root=Path(self.temp.name);self.source=self.root/'workspace';self.source.mkdir();self.out=self.root/'out';self.out.mkdir();self.tracked={}
  self.projects=[{'path':f'src/{name}/{name}.csproj','scope':'direct','production':True,'purpose':'synthetic','assemblyName':{'value':'SameName'}} for name in ['One','Two']]
  for name,data in [('global.json',b'{}'),('src/One/A.cs',b'class A {}'),('src/Two/B.cs',b'class B {}')]:self.file(name,data);self.tracked[name]=m.sha(self.source/name)
 def file(self,name,data=b'synthetic'):
  p=self.source/name;p.parent.mkdir(parents=True,exist_ok=True);p.write_bytes(data);return p
 def generated(self):return self.file('src/One/obj/Release/net10.0/generated/G.cs',b'// generated')
 def archive(self,names=None):
  path=self.root/'src.zip'
  with zipfile.ZipFile(path,'w') as archive:
   for name in names or [str(self.source/'src/One/A.cs').lstrip('/')]:archive.writestr(name,b'class A {}')
  return path
 def test_native_archive_valid_subset_not_all1383(self):
  r=m.archive_manifest(self.archive(),self.source,m.scan_workspace(self.source,self.tracked,self.projects));self.assertEqual(r['csharpCount'],1);self.assertFalse(r['archiveBytesExported'])
 def test_archive_extra_external_traversal_duplicate_and_changed_bytes(self):
  for names in [['opt/Outside.cs'],['../Outside.cs'],[str(self.source/'src/One/A.cs').lstrip('/')]*2,[str(self.source/'src/Two/B.cs').lstrip('/')]]:
   with self.assertRaises(ValueError):m.archive_manifest(self.archive(names),self.source,m.scan_workspace(self.source,self.tracked,self.projects))
 def test_archive_utf8_bom_only_recorded_not_arbitrary_rewrite(self):
  p=self.file('src/One/A.cs',b'\xef\xbb\xbfclass A {}');self.tracked['src/One/A.cs']=m.sha(p)
  r=m.archive_manifest(self.archive(),self.source,m.scan_workspace(self.source,self.tracked,self.projects));self.assertEqual(r['csharpFiles']['src/One/A.cs']['contentRelation'],'utf8-bom-removed')
  self.file('src/One/A.cs',b'class A {}\n');self.tracked['src/One/A.cs']=m.sha(p)
  with self.assertRaises(ValueError):m.archive_manifest(self.archive(),self.source,m.scan_workspace(self.source,self.tracked,self.projects))
 def test_generated_boundary_and_specific_producer_unknown(self):
  self.generated();r=m.scan_workspace(self.source,self.tracked,self.projects);g=r['src/One/obj/Release/net10.0/generated/G.cs'];self.assertEqual(g['owningProjectByOutputDirectory'],self.projects[0]['path']);self.assertTrue(g['specificGeneratorProducer'].startswith('UNKNOWN'))
  for path in ['src/One/bin/Release/net10.0/G.cs','tools/G.cs','src/Unknown/obj/G.cs','src/One/Other.cs']:
   p=self.file(path)
   with self.assertRaises(ValueError):m.scan_workspace(self.source,self.tracked,self.projects)
   p.unlink()
 def test_unreached_project_generated_source_not_auto_allowed(self):
  self.generated();projects=copy.deepcopy(self.projects);projects[0]['scope']='unreached'
  with self.assertRaises(ValueError):m.scan_workspace(self.source,self.tracked,projects)
 def test_symlink_extra_global_and_tracked_tamper(self):
  p=self.source/'link';p.symlink_to(self.root,target_is_directory=True)
  with self.assertRaises(ValueError):m.scan_workspace(self.source,self.tracked,self.projects)
  p.unlink();p=self.file('nested/global.json',b'{}')
  with self.assertRaises(ValueError):m.scan_workspace(self.source,self.tracked,self.projects)
  p.unlink();self.file('src/One/A.cs',b'changed')
  with self.assertRaises(ValueError):m.scan_workspace(self.source,self.tracked,self.projects)
 def assets(self,project):
  directory=(self.source/project['path']).parent;doc={'project':{'restore':{'projectPath':str(self.source/project['path']),'outputPath':str(directory/'obj')+'/','originalTargetFrameworks':['net10.0']},'frameworks':{'net10.0':{}}}}
  p=directory/'obj/project.assets.json';p.parent.mkdir(parents=True,exist_ok=True);p.write_text(json.dumps(doc));return p,doc
 def test_project_assets_exact_boundary_not_dll_basename(self):
  a,doc=self.assets(self.projects[0]);b,_=self.assets(self.projects[1]);self.assertNotEqual(m.project_assets(self.source,self.projects[0])['path'],m.project_assets(self.source,self.projects[1])['path']);b.write_text(json.dumps(doc))
  with self.assertRaises(ValueError):m.project_assets(self.source,self.projects[1])
 def test_target_framework_and_output_path_mutations(self):
  p,doc=self.assets(self.projects[0])
  for field,value in [('projectPath','/other/A.csproj'),('outputPath','/outside'),('originalTargetFrameworks',['net9.0'])]:
   d=copy.deepcopy(doc);d['project']['restore'][field]=value;p.write_text(json.dumps(d))
   with self.assertRaises(ValueError):m.project_assets(self.source,self.projects[0])
  d=copy.deepcopy(doc);d['project']['frameworks']={'net9.0':{}};p.write_text(json.dumps(d))
  with self.assertRaises(ValueError):m.project_assets(self.source,self.projects[0])
 def test_build_failure_and_missing_output_retained_not_complete(self):
  m.write(self.out/'process-sdk.json',{'capturedOutput':'10.0.114\n'});m.write(self.out/'process-lifecycle.json',{'safeToAnalyze':True})
  m.write(self.out/'source-identity.json',{'sourceFiles':self.tracked})
  with patch.object(m,'validate_ledger',return_value=self.projects),patch.object(m.subprocess,'check_output',return_value='10.0.114\n'):
   with self.assertRaises(ValueError):m.build(self.source,self.out,'success','failure')
  r=json.loads((self.out/'manual-build.json').read_text());self.assertEqual(r['evidenceStatus'],'INCOMPLETE');self.assertEqual(len(r['projects']),2);self.assertTrue(r['problems'])
 def test_same_basename_outputs_kept_as_two_project_identities(self):
  m.write(self.out/'process-sdk.json',{'capturedOutput':'10.0.114\n'});m.write(self.out/'process-lifecycle.json',{'safeToAnalyze':True})
  m.write(self.out/'source-identity.json',{'sourceFiles':self.tracked})
  for project in self.projects:
   self.assets(project);directory=Path(project['path']).parent/'bin/Release/net10.0'
   self.file(str(directory/'SameName.dll'),b'compiled-'+project['path'].encode());self.file(str(directory/'SameName.pdb'),b'pdb')
  with patch.object(m,'validate_ledger',return_value=self.projects),patch.object(m.subprocess,'check_output',return_value='10.0.114\n'):m.build(self.source,self.out,'success','success')
  r=json.loads((self.out/'manual-build.json').read_text());self.assertEqual(len(r['projects']),2);self.assertNotEqual(r['projects'][0]['dll']['path'],r['projects'][1]['dll']['path']);self.assertEqual(r['projects'][0]['pdbCodeViewBinding'],'NOT_CHECKED')
 def test_manual_sarif_inventory_checks_archive_not_static1339(self):
  m.write(self.out/'source-identity.json',{'sourceFiles':self.tracked});m.write(self.out/'workspace-scope-after.json',{'evidenceStatus':'WORKSPACE_BYTES_OBSERVED_NOT_EXTRACTION_PROOF','problems':[],'csharpFiles':m.scan_workspace(self.source,self.tracked,self.projects)})
  m.write(self.out/'manual-archive.json',{'csharpFiles':{'src/One/A.cs':{}}});doc={'runs':[{'artifacts':[{'location':{'uri':'src/One/A.cs','uriBaseId':'%SRCROOT%'}}],'invocations':[{}]}]}
  self.assertEqual(e.manual_sarif_scope(doc,self.source,self.out)['csharpArtifactCount'],1)
  for value in ['/opt/External.cs','/home/external/External.CS',str(self.source/'src/One/Unobserved.cs')]:
   d=copy.deepcopy(doc);d['runs'][0]['invocations'][0]['toolExecutionNotifications']=[{'level':'none','message':{'text':'Compiler message in '+value+': outside'}}]
   with self.assertRaises(ValueError):e.manual_sarif_scope(d,self.source,self.out)
  doc['runs'][0]['artifacts'][0]['location']['uri']='../tools/Other.cs'
  with self.assertRaises(ValueError):e.manual_sarif_scope(doc,self.source,self.out)

 def native_log(self,status='SUCCEEDED',cwd=None):
  db=self.root/'db';directory=db/'csharp/log';directory.mkdir(parents=True,exist_ok=True)
  (directory/'csharp.log').write_text('[123] [001] [INFO] EXTRACTION STARTING at synthetic\n[123] [001] [INFO]   Current working directory: '+str(cwd or self.source)+'\n[123] [001] [INFO] EXTRACTION '+status+' in 00:00:01\n')
  (directory/'csharp.abc.txt').write_text('# Arguments to Roslyn: synthetic not exported')
  return db
 def test_native_log_safe_receipt_not_per_project_proof(self):
  r=m.native_log_receipt(self.source,self.native_log(),self.out);self.assertEqual(r['contexts'][0]['terminalRecords'][0]['status'],'SUCCEEDED');self.assertFalse(r['rawLogsOrArgumentsExported']);self.assertIn('NOT_PROVEN',r['perProjectCompilationContextMapping']);self.assertNotIn('synthetic not exported',json.dumps(r))
 def test_failed_native_extraction_retained_and_incomplete(self):
  with self.assertRaises(ValueError):m.native_log_receipt(self.source,self.native_log('FAILED'),self.out)
  self.assertTrue((self.out/'native-compiler-log.json').is_file())
 def test_native_log_missing_ambiguous_status_or_outside_cwd_rejected(self):
  db=self.native_log();p=db/'csharp/log/csharp.log';p.write_text('EXTRACTION SUCCEEDED unprefixed\n')
  with self.assertRaises(ValueError):m.native_log_receipt(self.source,db,self.out)
  (self.out/'native-compiler-log.json').unlink();db=self.native_log(cwd=Path('/opt/external'))
  with self.assertRaises(ValueError):m.native_log_receipt(self.source,db,self.out)
  p.unlink()
  with self.assertRaises(ValueError):m.native_log_receipt(self.source,db,self.out)

 def test_native_partial_second_pid_error_and_duplicate_terminals_rejected(self):
  for suffix in ['[456] [001] [INFO] EXTRACTION STARTING at later\n[456] [001] [INFO]   Current working directory: '+str(self.source)+'\n[456] [001] [ERROR] Unhandled exception secret-not-exported\n','[123] [001] [INFO] EXTRACTION SUCCEEDED in duplicate\n']:
   db=self.native_log();log=db/'csharp/log/csharp.log'
   with log.open('a') as f:f.write(suffix)
   with self.assertRaises(ValueError):m.native_log_receipt(self.source,db,self.out)
   raw=(self.out/'native-compiler-log.json').read_text();self.assertNotIn('secret-not-exported',raw);(self.out/'native-compiler-log.json').unlink()
 def test_owned_python_timeout_quiescence_and_no_safe_followup(self):
  receipt=host.execute([sys.executable,'-c','import time;time.sleep(30)'],self.source,.1,self.out/'timeout.json')
  self.assertTrue(receipt['timedOut']);self.assertTrue(receipt['processGroupQuiescent']);self.assertFalse(host.safe(receipt))
 def test_parent_exit_descendant_is_killed_not_reclassified_success(self):
  script='import subprocess,sys;subprocess.Popen([sys.executable,"-c","import signal,time;signal.signal(signal.SIGTERM,signal.SIG_IGN);time.sleep(30)"])'
  receipt=host.execute([sys.executable,'-c',script],self.source,2,self.out/'descendant.json')
  self.assertTrue(receipt['descendantLeak']);self.assertTrue(receipt['processGroupQuiescent']);self.assertFalse(host.safe(receipt))
 def test_owned_success_and_normal_nonzero_are_quiescent(self):
  for code in [0,3]:
   receipt=host.execute([sys.executable,'-c','raise SystemExit('+str(code)+')'],self.source,2,self.out/('exit'+str(code)+'.json'))
   self.assertTrue(host.safe(receipt));self.assertEqual(receipt['exitCode'],code)
 def test_process_unknown_timeout_or_mismatched_command_blocks_gate(self):
  for name in ['sdk','restore','build']:
   argv,timeout=host.plan(name);m.write(self.out/('process-'+name+'.json'),{'argv':argv,'cwd':str(self.source),'timeoutSeconds':timeout,'exitCode':0,'completed':True,'processGroupQuiescent':True,'capturedOutput':'10.0.114\n'})
  self.assertTrue(host.lifecycle(self.source,self.out,{'sdk':'success','restore':'success','build':'success'})['safeToAnalyze'])
  p=self.out/'process-build.json';d=json.loads(p.read_text());d['timedOut']=True;p.write_text(json.dumps(d))
  with self.assertRaises(ValueError):host.lifecycle(self.source,self.out,{'sdk':'success','restore':'success','build':'success'})
  self.assertFalse(json.loads((self.out/'process-lifecycle.json').read_text())['safeToAnalyze'])

 def test_cancellation_during_final_cleanup_cannot_be_safe(self):
  real_save=host.save;sent=False
  def save_and_cancel(path,receipt):
   nonlocal sent
   real_save(path,receipt)
   if receipt.get('completed') and not sent:sent=True;os.kill(os.getpid(),signal.SIGTERM)
  with patch.object(host,'save',side_effect=save_and_cancel):receipt=host.execute([sys.executable,'-c','pass'],self.source,2,self.out/'cancel-final.json')
  self.assertFalse(host.safe(receipt));self.assertIn('cancelled',json.loads((self.out/'cancel-final.json').read_text()))
 def test_cancelled_original_step_overrides_success_receipt(self):
  for name in ['sdk','restore','build']:
   argv,timeout=host.plan(name);m.write(self.out/('process-'+name+'.json'),{'argv':argv,'cwd':str(self.source),'timeoutSeconds':timeout,'exitCode':0,'completed':True,'processGroupQuiescent':True,'capturedOutput':'10.0.114\n'})
  with self.assertRaises(ValueError):host.lifecycle(self.source,self.out,{'sdk':'success','restore':'success','build':'cancelled'})

 def copy_fixture(self,duplicate=False):
  owner={'path':m.COPY_PROJECT,'scope':'direct','production':False,'purpose':'synthetic declared copy','assemblyName':{'value':'SharpLink.Generator.Tests'}}
  self.projects.append(owner)
  declaration='<None Include="'+m.COPY_INCLUDE+'" Link="'+m.COPY_NAME+'" CopyToOutputDirectory="PreserveNewest" />'
  csproj=self.file(m.COPY_PROJECT,('<Project><ItemGroup>'+declaration*(2 if duplicate else 1)+'</ItemGroup></Project>').encode())
  original=self.file(m.COPY_SOURCE,b'// exact tracked content')
  for path in (csproj,original):self.tracked[str(path.relative_to(self.source))]=m.sha(path)
  target=str(Path(m.COPY_PROJECT).parent/'bin/Release/net10.0'/m.COPY_NAME)
  self.file(target,original.read_bytes());return target
 def test_exact_none_copy_is_not_generated_or_compile_input(self):
  target=self.copy_fixture();observed=m.scan_workspace(self.source,self.tracked,self.projects);row=observed[target]
  self.assertEqual(row['kind'],'copied-tracked-content');self.assertEqual(row['originalTrackedSha256'],self.tracked[m.COPY_SOURCE]);self.assertFalse(row['compilerInputProven'])
  archive=self.root/'copy.zip'
  with zipfile.ZipFile(archive,'w') as z:z.writestr(str(self.source/target).lstrip('/'),(self.source/target).read_bytes())
  receipt=m.archive_manifest(archive,self.source,observed)
  self.assertEqual(receipt['csharpFiles'][target]['declarationItemType'],'None');self.assertFalse(receipt['csharpFiles'][target]['compilerInputProven'])
 def test_copy_tamper_moved_target_extra_bin_and_duplicate_declaration_fail(self):
  target=self.copy_fixture();p=self.source/target;p.write_bytes(b'changed')
  with self.assertRaises(ValueError):m.scan_workspace(self.source,self.tracked,self.projects)
  p.write_bytes((self.source/m.COPY_SOURCE).read_bytes());moved=p.with_name('Other.cs');p.rename(moved)
  with self.assertRaises(ValueError):m.scan_workspace(self.source,self.tracked,self.projects)
  moved.rename(p);extra=self.file(str(Path(target).parent/'Extra.cs'))
  with self.assertRaises(ValueError):m.scan_workspace(self.source,self.tracked,self.projects)
  extra.unlink();project=self.source/m.COPY_PROJECT;data=project.read_text();project.write_text(data.replace('</ItemGroup>',data[data.index('<None'):data.index('</ItemGroup>')]+'</ItemGroup>'));self.tracked[m.COPY_PROJECT]=m.sha(project)
  with self.assertRaises(ValueError):m.scan_workspace(self.source,self.tracked,self.projects)
 def test_partial_workspace_inventory_preserves_known_and_unknown_entries(self):
  self.generated();self.file('unknown/Bad.cs');self.file('src/One/A.cs',b'tampered');errors=[]
  observed=m.scan_workspace(self.source,self.tracked,self.projects,errors)
  self.assertEqual(observed['src/Two/B.cs']['kind'],'tracked');self.assertEqual(observed['unknown/Bad.cs']['kind'],'unknown-or-invalid');self.assertEqual(observed['src/One/A.cs']['kind'],'unknown-or-invalid');self.assertGreaterEqual(len(errors),2)
  m.write(self.out/'source-identity.json',{'sourceFiles':self.tracked});m.write(self.out/'process-sdk.json',{'capturedOutput':m.SDK});m.write(self.out/'process-lifecycle.json',{'safeToAnalyze':True})
  with patch.object(m,'validate_ledger',return_value=self.projects):
   with self.assertRaises(ValueError):m.build(self.source,self.out,'success','success')
  partial=json.loads((self.out/'manual-build.json').read_text());self.assertIn('src/Two/B.cs',partial['workspaceCSharp']);self.assertEqual(partial['evidenceStatus'],'INCOMPLETE')
 def output_fixture(self,project,configuration):
  self.assets(project);directory=Path(project['path']).parent/'bin'/configuration/'net10.0'
  self.file(str(directory/(project['assemblyName']['value']+'.dll')),project['path'].encode());self.file(str(directory/(project['assemblyName']['value']+'.pdb')),b'pdb')
 def build_fixture(self,projects):
  m.write(self.out/'source-identity.json',{'sourceFiles':self.tracked});m.write(self.out/'process-sdk.json',{'capturedOutput':m.SDK});m.write(self.out/'process-lifecycle.json',{'safeToAnalyze':True})
  with patch.object(m,'validate_ledger',return_value=projects):m.build(self.source,self.out,'success','success')
  return json.loads((self.out/'manual-build.json').read_text())
 def test_three_named_transitive_debug_outputs_do_not_change_release_invocation(self):
  projects=self.projects+[{'path':p,'scope':'transitive','production':False,'purpose':'synthetic known transitive','assemblyName':{'value':Path(p).stem}} for p in sorted(m.TRANSITIVE_DEBUG)]
  for p in projects:self.output_fixture(p,'Debug' if p['scope']=='transitive' else 'Release')
  receipt=self.build_fixture(projects)
  self.assertEqual(receipt['configuration'],'Release');self.assertEqual([x['observedProjectConfiguration'] for x in receipt['projects']],['Release','Release','Debug','Debug','Debug'])
  self.assertTrue(all(x['invocationConfiguration']=='Release' for x in receipt['projects']))
 def test_no_configuration_fallback_and_unknown_transitive_rejected(self):
  p={'path':sorted(m.TRANSITIVE_DEBUG)[0],'scope':'transitive','production':False,'purpose':'known','assemblyName':{'value':'SameName'}}
  self.output_fixture(p,'Release')
  with self.assertRaises(ValueError):self.build_fixture([p])
  row=json.loads((self.out/'manual-build.json').read_text())['projects'][0];self.assertEqual(row['status'],'INCOMPLETE');self.assertIn('/bin/Debug/',row['error'])
  with self.assertRaises(ValueError):m.project_configuration({'path':'test/Unexpected/Unexpected.csproj','scope':'transitive'})
  direct=self.projects[0];self.output_fixture(direct,'Debug')
  with self.assertRaises(ValueError):m.regular((self.source/direct['path']).parent/'bin/Release/net10.0/SameName.dll')
 def test_project_output_ancestor_symlink_cannot_mix_same_basename(self):
  one,two=self.projects;self.output_fixture(two,'Release');self.assets(one)
  directory=(self.source/one['path']).parent/'bin/Release';directory.parent.mkdir(parents=True);directory.symlink_to((self.source/two['path']).parent/'bin/Release',target_is_directory=True)
  with self.assertRaises(ValueError):m.regular(directory/'net10.0/SameName.dll')

 def package_fixture(self,project=None,source_bytes=b'namespace TUnit.Generated { }',extra_members=None,props_bytes=None):
  project=project or self.projects[0];assets,doc=self.assets(project);folder=self.root/'nuget';package=folder/m.PACKAGE_PATH;package.mkdir(parents=True,exist_ok=True)
  props_bytes=props_bytes or b'<Project><ItemGroup Condition="&apos;$(Language)&apos; == &apos;C#&apos;"><Compile Include="$(MSBuildThisFileDirectory)TUnit.Core.GeneratedNamespace.cs" Visible="false" /></ItemGroup></Project>'
  nupkg=package/'tunit.core.1.73.19.nupkg'
  with zipfile.ZipFile(nupkg,'w') as archive:
   for name,data in [(m.PACKAGE_MEMBER,source_bytes),(m.PACKAGE_PROPS,props_bytes)]+(extra_members or []):archive.writestr(zipfile.ZipInfo(name,(2000,1,1,0,0,0)),data)
  for name,data in [(m.PACKAGE_MEMBER,source_bytes),(m.PACKAGE_PROPS,props_bytes)]:p=package/name;p.parent.mkdir(parents=True,exist_ok=True);p.write_bytes(data)
  raw_digest=base64.b64encode(hashlib.sha512(nupkg.read_bytes()).digest()).decode()
  digest=base64.b64encode(b'c'*64).decode() # Explicit synthetic official-API response; not actual NuGet execution.
  doc.update(libraries={m.PACKAGE_ID:{'type':'package','path':m.PACKAGE_PATH,'sha512':digest,'files':[m.PACKAGE_MEMBER,m.PACKAGE_PROPS]}},targets={'net10.0':{m.PACKAGE_ID:{'type':'package','build':{m.PACKAGE_PROPS:{}}}}},packageFolders={str(folder)+'/':{}})
  assets.write_text(json.dumps(doc))
  reference={'project':project['path'],'assetsPath':str(assets.relative_to(self.source)),'assetsSha256':m.sha(assets),'assetsContentHash':digest,'selectedTargets':['net10.0']}
  self.package_receipt={'status':'PACKAGE_BYTES_AND_OFFICIAL_CONTENT_HASH_OBSERVED_NOT_SIGNATURE_TRUST','problems':[],'signatureTrust':'NOT_VERIFIED','externalPackageSources':{str(package/m.PACKAGE_MEMBER):{'canonicalSourceId':m.PACKAGE_KEY,'kind':'external-package-source','packageId':'TUnit.Core','packageVersion':'1.73.19','packagePath':m.PACKAGE_PATH,'member':m.PACKAGE_MEMBER,'memberSha256':m.sha(package/m.PACKAGE_MEMBER),'materializedSha256':m.sha(package/m.PACKAGE_MEMBER),'nupkgPath':str(nupkg),'nupkgSha256':m.sha(nupkg),'nupkgRawSha512':raw_digest,'nupkgContentHash':digest,'propsMember':m.PACKAGE_PROPS,'propsPath':str(package/m.PACKAGE_PROPS),'propsSha256':m.sha(package/m.PACKAGE_PROPS),'referencingProjects':[reference]}}}
  return assets,doc,nupkg,package/m.PACKAGE_MEMBER,package/m.PACKAGE_PROPS
 def package_archive(self,materialized,data=None):
  archive=self.root/'package-src.zip'
  with zipfile.ZipFile(archive,'w') as z:
   z.writestr(str(self.source/'src/One/A.cs').lstrip('/'),(self.source/'src/One/A.cs').read_bytes());z.writestr(str(materialized).lstrip('/'),materialized.read_bytes() if data is None else data)
  return archive
 def test_actual_package_hash_member_materialized_and_archive_binding(self):
  assets,doc,nupkg,materialized,props=self.package_fixture();bindings=m.package_sources(self.source,self.projects[0],self.package_receipt);binding=bindings[str(materialized)]
  self.assertEqual(binding['nupkgContentHash'],doc['libraries'][m.PACKAGE_ID]['sha512']);self.assertEqual(binding['memberSha256'],m.sha(materialized));self.assertFalse(binding['perProjectCompilerInputProven'])
  workspace=m.scan_workspace(self.source,self.tracked,self.projects);receipt=m.archive_manifest(self.package_archive(materialized),self.source,workspace,bindings)
  self.assertEqual(receipt['externalSourcePaths'],{str(materialized):m.PACKAGE_KEY});self.assertEqual(m.archived_source_key(str(materialized),self.source,receipt),m.PACKAGE_KEY);self.assertEqual(receipt['csharpFiles'][m.PACKAGE_KEY]['kind'],'external-package-source')
  for value in [str(materialized.with_name('Other.cs')),'/opt/Other.cs',str(materialized.parent/'..'/'net10.0'/materialized.name)]:
   with self.assertRaises(ValueError):m.archived_source_key(value,self.source,receipt)
  forged=copy.deepcopy(receipt);forged['externalSourcePaths']={'/opt/Other.cs':m.PACKAGE_KEY}
  with self.assertRaises(ValueError):m.archived_source_key('/opt/Other.cs',self.source,forged)
 def test_raw_zip_hash_never_substitutes_for_missing_official_api_receipt(self):
  assets,doc,nupkg,materialized,props=self.package_fixture()
  with self.assertRaises(ValueError):m.package_sources(self.source,self.projects[0])
  bad=copy.deepcopy(self.package_receipt);bad['status']='INCOMPLETE'
  with self.assertRaises(ValueError):m.package_sources(self.source,self.projects[0],bad)
  binding=m.package_sources(self.source,self.projects[0],self.package_receipt)[str(materialized)]
  self.assertNotEqual(binding['nupkgContentHash'],binding['nupkgRawSha512'])
 def test_build_does_not_invoke_post_codeql_package_collector(self):
  for project in self.projects:self.output_fixture(project,'Release')
  with patch.object(m,'package_sources',side_effect=AssertionError('must be deferred until after CodeQL')):
   self.build_fixture(self.projects)
  result=json.loads((self.out/'manual-build.json').read_text());self.assertEqual(result['packageEvidence'],'DEFERRED_UNTIL_POST_CODEQL_COLLECTION')
 def test_same_exact_package_source_multiple_projects_deduplicated(self):
  self.package_fixture(self.projects[0]);one=m.package_sources(self.source,self.projects[0],self.package_receipt);self.package_fixture(self.projects[1]);two=m.package_sources(self.source,self.projects[1],self.package_receipt);m.merge_package_sources(one,two)
  self.assertEqual(len(one),1);self.assertEqual(len(next(iter(one.values()))['referencingProjects']),2)
  bad=copy.deepcopy(two);next(iter(bad.values()))['memberSha256']='0'*64
  with self.assertRaises(ValueError):m.merge_package_sources(one,bad)
 def test_package_assets_version_hash_members_and_build_selection_fail_closed(self):
  for mutation in ['version','path','sha512','contentHash','missingMember','noBuild','missingNupkg','unresolved']:
   with self.subTest(mutation=mutation):
    assets,doc,nupkg,materialized,props=self.package_fixture();lib=doc['libraries'][m.PACKAGE_ID]
    if mutation=='version':doc['libraries']['TUnit.Core/1.73.20']=doc['libraries'].pop(m.PACKAGE_ID)
    elif mutation=='path':lib['path']='../../outside'
    elif mutation=='sha512':lib['sha512']=base64.b64encode(b'x'*64).decode()
    elif mutation=='contentHash':lib['contentHash']=base64.b64encode(b'x'*64).decode()
    elif mutation=='missingMember':lib['files'].remove(m.PACKAGE_MEMBER)
    elif mutation=='noBuild':doc['targets']['net10.0'][m.PACKAGE_ID]['build']={}
    elif mutation=='missingNupkg':nupkg.unlink()
    elif mutation=='unresolved':doc['libraries']={}
    assets.write_text(json.dumps(doc))
    if mutation=='unresolved':
     self.assertEqual(m.package_sources(self.source,self.projects[0],self.package_receipt),{})
     with self.assertRaises(ValueError):m.archive_manifest(self.package_archive(materialized),self.source,m.scan_workspace(self.source,self.tracked,self.projects))
    else:
     with self.assertRaises((ValueError,FileNotFoundError)):m.package_sources(self.source,self.projects[0],self.package_receipt)
 def test_nupkg_sha512_is_computed_not_trusted_sidecar_and_bytes_must_match(self):
  for mutation in ['nupkg','source','props']:
   with self.subTest(mutation=mutation):
    assets,doc,nupkg,materialized,props=self.package_fixture()
    if mutation=='nupkg':
     with nupkg.open('ab') as f:f.write(b'tamper')
     nupkg.with_suffix('.nupkg.sha512').write_text(doc['libraries'][m.PACKAGE_ID]['sha512'])
    else:(materialized if mutation=='source' else props).write_bytes(b'tamper')
    with self.assertRaises(ValueError):m.package_sources(self.source,self.projects[0],self.package_receipt)
 def test_package_zip_traversal_duplicate_and_wrong_compile_include_rejected(self):
  for extra in [[('../Outside.cs',b'bad')],[(m.PACKAGE_MEMBER,b'duplicate')],[('/absolute.cs',b'bad')]]:
   self.package_fixture(extra_members=extra)
   with self.assertRaises(ValueError):m.package_sources(self.source,self.projects[0],self.package_receipt)
  self.package_fixture(props_bytes=b'<Project><ItemGroup><Compile Include="../../unrelated.cs" /></ItemGroup></Project>')
  with self.assertRaises(ValueError):m.package_sources(self.source,self.projects[0],self.package_receipt)
 def test_package_mutation_after_build_snapshot_rejected(self):
  for mutation in ['nupkg','source','props','assets']:
   with self.subTest(mutation=mutation):
    assets,doc,nupkg,materialized,props=self.package_fixture();bindings=m.package_sources(self.source,self.projects[0],self.package_receipt);archive=self.package_archive(materialized)
    target={'nupkg':nupkg,'source':materialized,'props':props,'assets':assets}[mutation]
    with target.open('ab') as f:f.write(b' ')
    with self.assertRaises(ValueError):m.archive_manifest(archive,self.source,m.scan_workspace(self.source,self.tracked,self.projects),bindings)
 def test_external_archive_only_bom_transcoding_and_partial_inventory(self):
  assets,doc,nupkg,materialized,props=self.package_fixture(source_bytes=b'\xef\xbb\xbfnamespace TUnit.Generated {}');bindings=m.package_sources(self.source,self.projects[0],self.package_receipt);workspace=m.scan_workspace(self.source,self.tracked,self.projects)
  receipt=m.archive_manifest(self.package_archive(materialized,materialized.read_bytes()[3:]),self.source,workspace,bindings);self.assertEqual(receipt['csharpFiles'][m.PACKAGE_KEY]['contentRelation'],'utf8-bom-removed')
  with self.assertRaises(ValueError):m.archive_manifest(self.package_archive(materialized,b'different'),self.source,workspace,bindings)
  archive=self.package_archive(materialized)
  with zipfile.ZipFile(archive,'a') as z:z.writestr('opt/unresolved/Extra.cs',b'bad')
  errors=[];partial=m.archive_manifest(archive,self.source,workspace,bindings,errors);self.assertTrue(errors);self.assertIn(m.PACKAGE_KEY,partial['csharpFiles']);self.assertIn('src/One/A.cs',partial['csharpFiles']);self.assertEqual(partial['inventoryStatus'],'INCOMPLETE')

 def test_direct_project_debug_output_is_not_a_release_fallback(self):
  self.output_fixture(self.projects[0],'Debug')
  with self.assertRaises(ValueError):self.build_fixture([self.projects[0]])
  row=json.loads((self.out/'manual-build.json').read_text())['projects'][0]
  self.assertEqual(row['observedProjectConfiguration'],'Release');self.assertIn('/bin/Release/',row['error']);self.assertEqual(row['status'],'INCOMPLETE')
 def test_copy_duplicate_owner_rejected(self):
  self.copy_fixture();self.projects.append(copy.deepcopy(self.projects[-1]))
  with self.assertRaises(ValueError):m.scan_workspace(self.source,self.tracked,self.projects)
 def test_package_missing_actual_member_and_ambiguous_cache_roots_rejected(self):
  assets,doc,nupkg,materialized,props=self.package_fixture()
  with zipfile.ZipFile(nupkg,'w') as z:z.writestr(m.PACKAGE_PROPS,props.read_bytes())
  doc['libraries'][m.PACKAGE_ID]['sha512']=m.package_digest(nupkg);assets.write_text(json.dumps(doc))
  with self.assertRaises(ValueError):m.package_sources(self.source,self.projects[0],self.package_receipt)
  assets,doc,nupkg,materialized,props=self.package_fixture();other=self.root/'other-cache';second=other/m.PACKAGE_PATH/nupkg.name;second.parent.mkdir(parents=True);second.write_bytes(nupkg.read_bytes());doc['packageFolders'][str(other)+'/']={};assets.write_text(json.dumps(doc))
  with self.assertRaises(ValueError):m.package_sources(self.source,self.projects[0],self.package_receipt)

if __name__=='__main__':unittest.main()
