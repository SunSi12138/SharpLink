#!/usr/bin/env python3
"""Offline fixtures only. No network, CLR, GitHub writes or real subprocesses."""
import hashlib,inspect,io,json,os,shutil,sys,tarfile,tempfile,unittest
from pathlib import Path
from unittest.mock import patch
import driver
from audit_bundle import audit

class ProtocolTests(unittest.TestCase):
    def setUp(self):
        self.tmp=tempfile.TemporaryDirectory();self.root=Path(self.tmp.name)
        self.out_patch=patch.object(driver,'OUT',self.root/'out');self.out_patch.start()
    def tearDown(self):self.out_patch.stop();self.tmp.cleanup()
    def request(self,r,branch='codex/wakeup-native-prepare-v1',attempt='1'):
        r={'environmentPolicy':driver.ENVIRONMENT_POLICY,**r}
        (self.root/'REQUEST.json').write_text(json.dumps(r))
        with patch.object(driver,'HERE',self.root),patch.object(driver,'RID','linux-x64'),patch.dict(os.environ,{'GITHUB_REF_NAME':branch,'GITHUB_RUN_ATTEMPT':attempt},clear=True):return driver.request()
    def assert_stage_rejects_inherited_overrides(self,stage):
        keys=('COMPlus_TieredCompilation','DOTNET_TieredPGO','CORECLR_ENABLE_PROFILING','COR_ENABLE_PROFILING','LD_PRELOAD','DYLD_INSERT_LIBRARIES')
        for key in keys:
            with self.subTest(stage=stage,key=key),patch.object(driver,'OUT',self.root/key),patch.dict(os.environ,{key:'0'},clear=True),patch.object(driver,'run') as managed_launch,patch.object(driver.subprocess,'Popen') as process_launch,patch.object(driver,'download_archives') as download:
                with self.assertRaisesRegex(ValueError,'Inherited runtime configuration rejected'):
                    if stage=='prepare':driver.prepare(self.root/'not-created')
                    else:driver.collect(self.root/'not-created',{})
                managed_launch.assert_not_called();process_launch.assert_not_called();download.assert_not_called()
                self.assertEqual(os.environ[key],'0') # Reject, never silently scrub or retry.
                self.assertFalse((self.root/'not-created').exists())
    def test_prepare_rejects_overrides_before_any_managed_launch(self):
        self.assert_stage_rejects_inherited_overrides('prepare')
    def test_collect_rejects_overrides_before_any_managed_launch(self):
        self.assert_stage_rejects_inherited_overrides('collect')
    def known_linux(self):
        return {'DOTNET_MULTILEVEL_LOOKUP':'0','LD_LIBRARY_PATH':driver.PYTHON_LINUX+'/lib',
            'pythonLocation':driver.PYTHON_LINUX,'RUNNER_TOOL_CACHE':'/opt/hostedtoolcache'}
    def python_identity(self):
        from contextlib import ExitStack
        stack=ExitStack()
        for name,value in [('version_info',(3,12,15)),('prefix',driver.PYTHON_LINUX),('executable',driver.PYTHON_LINUX+'/bin/python3')]:stack.enter_context(patch.object(driver.sys,name,value))
        stack.enter_context(patch.object(driver,'RID','linux-x64'))
        return stack
    def test_exact_known_values_normalized_and_original_recorded(self):
        original=self.known_linux()
        with self.python_identity(),patch.dict(os.environ,original,clear=True):
            result=driver.stage_environment('prepare')
            self.assertNotIn('LD_LIBRARY_PATH',result);self.assertNotIn('DOTNET_MULTILEVEL_LOOKUP',result)
            self.assertEqual(dict(os.environ),original)
        self.assertEqual(json.loads((driver.OUT/'environment-original-prepare.json').read_text()),original)
        self.assertEqual(set(json.loads((driver.OUT/'environment-policy-prepare.json').read_text())['removed']),{'DOTNET_MULTILEVEL_LOOKUP','LD_LIBRARY_PATH'})
        driver.validate_environment(result) # Original native collector needs no relaxation.
    def test_prepare_checks_normalized_python_before_any_archive_or_clr(self):
        calls=[];root=self.root/'prepare'
        def host(path):(path/'evidence').mkdir()
        def run(cmd,log,env=None,**kwargs):calls.append((cmd,env))
        with self.python_identity(),patch.dict(os.environ,self.known_linux(),clear=True),patch.object(driver,'host',side_effect=host),patch.object(driver,'run',side_effect=run),patch.object(driver,'download_archives',side_effect=RuntimeError('stop at offline download boundary')):
            with self.assertRaisesRegex(RuntimeError,'offline download'):driver.prepare(root)
        self.assertEqual(len(calls),1)
        self.assertEqual(calls[0][0][0],driver.PYTHON_LINUX+'/bin/python3')
        self.assertNotIn('LD_LIBRARY_PATH',calls[0][1]);driver.validate_environment(calls[0][1])
        self.assertTrue((root/'evidence/environment-original-prepare.json').exists())
    def test_unknown_environment_policy_request_rejected(self):
        with self.assertRaisesRegex(ValueError,'Unreviewed environment policy'):
            self.request({'schema':1,'stage':'prepare','environmentPolicy':'anything-else'})
    def test_macos_zero_or_absent_normalized_without_loader_exception(self):
        with patch.object(driver,'RID','osx-arm64'):
            self.assertEqual(driver.normalize_environment({'DOTNET_MULTILEVEL_LOOKUP':'0'}),{})
            self.assertEqual(driver.normalize_environment({}),{})
            with self.assertRaises(ValueError):driver.normalize_environment(self.known_linux())
    def test_near_misses_unknown_overrides_and_wrong_python_fail_before_launch(self):
        cases=[{'DOTNET_MULTILEVEL_LOOKUP':v} for v in ('1','false','','00',' 0')]
        cases += [{'LD_LIBRARY_PATH':v} for v in ('',driver.PYTHON_LINUX+'/lib:',driver.PYTHON_LINUX+'/lib:/tmp',driver.PYTHON_LINUX+'/lib/','/tmp/lib')]
        cases += [{k:'0'} for k in ('LD_PRELOAD','DYLD_LIBRARY_PATH','COMPlus_TieredCompilation','CORECLR_ENABLE_PROFILING','DOTNET_TieredPGO')]
        cases += [{'pythonLocation':'/tmp'},{'RUNNER_TOOL_CACHE':'/tmp'}]
        for stage in ('prepare','collect'):
            for i,delta in enumerate(cases):
                env={**self.known_linux(),**delta}
                with self.subTest(stage=stage,delta=delta),self.python_identity(),patch.object(driver,'OUT',self.root/(stage+str(i))),patch.dict(os.environ,env,clear=True),patch.object(driver,'run') as run,patch.object(driver,'download_archives') as download,patch.object(driver,'restore_bundle') as restore:
                    with self.assertRaises(ValueError):
                        if stage=='prepare':driver.prepare(self.root/'uncreated')
                        else:driver.collect(self.root/'uncreated',{})
                    run.assert_not_called();download.assert_not_called();restore.assert_not_called()
                    self.assertEqual(dict(os.environ),env)
                    self.assertTrue((driver.OUT/('environment-original-'+stage+'.json')).exists())
        for name,value in [('prefix','/tmp'),('executable','/usr/bin/python3'),('version_info',(3,12,14))]:
            with self.python_identity(),patch.object(driver.sys,name,value):
                with self.assertRaises(ValueError):driver.normalize_environment(self.known_linux())
    def test_collect_all_launches_receive_normalized_environment(self):
        root,r=self.setup_collect();env=self.known_linux();calls=[]
        def run(cmd,log,env=None,**kwargs):calls.append((cmd,env))
        with self.python_identity(),patch.dict(os.environ,env,clear=True),patch.object(driver,'stage_environment',return_value=driver.normalize_environment(env)),patch.object(driver,'restore_bundle'),patch.object(driver,'host'),patch.object(driver,'download_archives'),patch.object(driver,'check_runtime_info'),patch.object(driver,'validate_receipt'),patch.object(driver,'run',side_effect=run):
            driver.collect(root,r)
        self.assertEqual(len(calls),17)
        for cmd,actual in calls:
            self.assertIsNotNone(actual);driver.validate_environment(actual)
            self.assertNotIn('LD_LIBRARY_PATH',actual);self.assertNotIn('DOTNET_MULTILEVEL_LOOKUP',actual)
    def test_command_cannot_implicitly_inherit_environment(self):
        with patch.object(driver.subprocess,'Popen') as launch:
            with self.assertRaises(ValueError):driver.run(['dotnet','--info'],self.root/'no.log')
            launch.assert_not_called()
    def test_prepare_request_and_no_automatic_rerun(self):
        self.assertEqual(self.request({'schema':1,'stage':'prepare'})['stage'],'prepare')
        for r,branch,attempt in [({'schema':True,'stage':'prepare'},'codex/wakeup-native-prepare-v1','1'),({'schema':1,'stage':'prepare'},'codex/wakeup-native-collect-v1','1'),({'schema':1,'stage':'prepare'},'codex/wakeup-native-prepare-v1','2')]:
            with self.assertRaises(ValueError):self.request(r,branch,attempt)
    def test_collect_requires_both_architectures_and_three_digests(self):
        a={rid:{'bundleSha256':'a'*64,'DReceiptSha256':'b'*64,'CReceiptSha256':'c'*64} for rid in driver.ROOTS}
        r={'schema':1,'stage':'collect','prepareRunId':123,'prepareCommit':'d'*40,'architectures':a}
        self.assertEqual(self.request(r,'codex/wakeup-native-collect-v1')['prepareRunId'],123)
        del a['osx-arm64']
        with self.assertRaises(ValueError):self.request(r,'codex/wakeup-native-collect-v1')
    def tar(self,members):
        path=self.root/'bundle.tar.gz'
        with tarfile.open(path,'w:gz') as t:
            for name,data in members:
                m=tarfile.TarInfo(name);m.size=len(data);m.mode=0o755;t.addfile(m,io.BytesIO(data))
        return path,hashlib.sha256(path.read_bytes()).hexdigest()
    def test_restore_byte_identity_and_fail_closed_digest_path(self):
        bundle,h=self.tar([('bin/probe.dll',b'unchanged bytes')]);target=self.root/'restore'
        driver.restore_bundle(bundle,target,h);self.assertEqual((target/'bin/probe.dll').read_bytes(),b'unchanged bytes')
        with self.assertRaises(ValueError):driver.restore_bundle(bundle,self.root/'bad-hash','0'*64)
        bundle,h=self.tar([('../escape',b'bad')])
        with self.assertRaises(ValueError):driver.restore_bundle(bundle,self.root/'bad-path',h)
        self.assertFalse((self.root/'escape').exists())
    def setup_collect(self):
        root=self.root/'root';root.mkdir();(root/'tools/hosted').mkdir(parents=True)
        (root/'collect-host/evidence').mkdir(parents=True)
        driver.OUT.mkdir()
        for name in ('environment-original-collect.json','environment-policy-collect.json'):(driver.OUT/name).write_text('{}')
        shutil.copy2(driver.HERE/'driver.py',root/'tools/hosted/driver.py');shutil.copy2(driver.REPO/'.github/workflows/wakeup-native-hosted.yml',root/'tools/workflow.yml')
        (root/'prepared-index.json').write_text(json.dumps({'runId':123,'prepareCommit':'d'*40,'rid':'linux-x64','receipts':{'D':'b'*64,'C':'c'*64}}))
        request={'prepareRunId':123,'prepareCommit':'d'*40,'architectures':{'linux-x64':{'bundleSha256':'a'*64,'DReceiptSha256':'b'*64,'CReceiptSha256':'c'*64}}}
        return root,request
    def test_collect_reuses_exact_binaries_and_frozen_order_without_build(self):
        root,r=self.setup_collect();calls=[]
        with patch.object(driver,'RID','linux-x64'),patch.object(driver,'restore_bundle'),patch.object(driver,'host'),patch.object(driver,'download_archives'),patch.object(driver,'check_runtime_info'),patch.object(driver,'validate_receipt'),patch.object(driver,'stage_environment',return_value={}),patch.object(driver,'run',side_effect=lambda cmd,*a,**k:calls.append(cmd)):
            driver.collect(root,r)
        self.assertEqual(len(calls),17) # info + eight real-cell commands + eight decode commands
        for cmd in calls:self.assertNotIn('build',cmd);self.assertNotIn('restore',cmd)
        probes=[cmd for cmd in calls if '--cell' in cmd]
        self.assertEqual([cmd[cmd.index('--cell')+1] for cmd in probes],['default-c1']*2+['default-c8']*2+['timed-c1']*2+['timed-c8']*2)
        self.assertEqual([Path(cmd[cmd.index('--receipt')+1]).stem for cmd in probes],['D','C']*4)
    def test_first_collection_failure_stops_without_retry(self):
        root,r=self.setup_collect();calls=[]
        def run(cmd,*args,**kwargs):
            calls.append(cmd)
            if '--cell' in cmd:raise RuntimeError('offline injected failure')
        with patch.object(driver,'RID','linux-x64'),patch.object(driver,'restore_bundle'),patch.object(driver,'host'),patch.object(driver,'download_archives'),patch.object(driver,'check_runtime_info'),patch.object(driver,'validate_receipt'),patch.object(driver,'stage_environment',return_value={}),patch.object(driver,'run',side_effect=run):
            with self.assertRaises(RuntimeError):driver.collect(root,r)
        self.assertEqual(len(calls),2)
    def test_request_then_prepare_preserves_separate_artifact_manifests(self):
        out=self.root/'artifacts';root=self.root/'restore';gha=self.root/'gha-output'
        def fake_prepare(path):
            (path/'evidence').mkdir(parents=True);(path/'evidence/fake.json').write_text('{}')
        with patch.object(driver,'OUT',out),patch.object(driver,'ROOTS',{'linux-x64':root}),patch.object(driver,'RID','linux-x64'),patch.object(driver,'request',return_value={'stage':'prepare'}),patch.object(driver,'prepare',side_effect=fake_prepare),patch.dict(os.environ,{'GITHUB_OUTPUT':str(gha)}):
            for mode in ('request','prepare'):
                with patch.object(sys,'argv',['driver.py',mode]):driver.main()
        self.assertTrue((out/'artifact-manifest-request.json').is_file())
        self.assertTrue((out/'artifact-manifest-prepare.json').is_file())
        self.assertTrue((out/'evidence/fake.json').is_file())
    def test_runtime_host_info_requires_actual_version_and_arch(self):
        p=self.root/'info.log'
        with patch.object(driver,'RID','osx-arm64'):
            p.write_text('Host:\n  Version: 10.0.14\n  Architecture: arm64\n');driver.check_runtime_info(p)
            p.write_text('Host:\n  Version: 10.0.14\n  Architecture: x64\n')
            with self.assertRaises(ValueError):driver.check_runtime_info(p)
    def test_portable_audit_checks_every_receipt_member_without_extracting(self):
        data=b'identity';filehash=hashlib.sha256(data).hexdigest();receipts={};members=[('file.bin',data),('evidence/host.json',b'{}')]
        for arm in ('D','C'):
            body=json.dumps({'files':[{'path':'/tmp/sharplink-wakeup-native-v1/file.bin','sha256':filehash}]}).encode();members.append(('receipts/'+arm+'.json',body));receipts[arm]=hashlib.sha256(body).hexdigest()
        members.append(('prepared-index.json',json.dumps({'rid':'linux-x64','receipts':receipts}).encode()));bundle,h=self.tar(members)
        self.assertTrue(audit(bundle,h)['allReceiptFileHashesMatch'])
        members[0]=('file.bin',b'tampered');bundle,h=self.tar(members)
        with self.assertRaises(ValueError):audit(bundle,h)
if __name__=='__main__':unittest.main()
