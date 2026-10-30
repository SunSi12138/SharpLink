"""Offline synthetic manual-scope controls: never execute .NET/CodeQL or product code."""
import copy, hashlib, json, tempfile, unittest, zipfile, sys, time, os, signal
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
  m.write(self.out/'manual-archive.json',{'csharpFiles':{'src/One/A.cs':{}}});doc={'runs':[{'artifacts':[{'location':{'uri':'src/One/A.cs','uriBaseId':'%SRCROOT%'}}],'invocations':[{}]}]}
  self.assertEqual(e.manual_sarif_scope(doc,self.source,self.out)['csharpArtifactCount'],1)
  for value in ['/opt/External.cs','/home/external/External.CS',str(self.source/'src/Two/B.cs')]:
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

if __name__=='__main__':unittest.main()
