#!/usr/bin/env python3
"""Offline synthetic closure/archive fixtures. No real binaries, CLR or child processes."""
import copy,hashlib,io,json,os,subprocess,sys,tarfile,tempfile,unittest
from pathlib import Path
from unittest.mock import patch
import collect
from receipt import PRODUCTS,SOURCE_ANCHORS,validate_receipt,digest

PLAN=json.loads(Path(__file__).with_name('plan.json').read_text())
class ReceiptTests(unittest.TestCase):
    def setUp(self):
        self.temp=tempfile.TemporaryDirectory();self.root=Path(self.temp.name);self.runtime=self.root/'dotnet-root';self.output=self.root/'output';self.source=self.root/'source';self.generated=self.root/'generated';self.core=self.runtime/'shared/Microsoft.NETCore.App/10.0.14';self.asp=self.runtime/'shared/Microsoft.AspNetCore.App/10.0.14';self.fxr=self.runtime/'host/fxr/10.0.14'
        paths=[self.runtime/'dotnet',self.core/'libcoreclr.so',self.core/'libclrjit.so',self.core/'libhostpolicy.so',self.core/'System.Private.CoreLib.dll',self.fxr/'libhostfxr.so',self.asp/'Microsoft.AspNetCore.dll',self.output/'WakeupNativeProbe.dll',self.generated/'Generated.cs',self.root/'build.log']+[self.output/name for name in PRODUCTS]+[self.source/name for name in SOURCE_ANCHORS]
        for path in paths:path.parent.mkdir(parents=True,exist_ok=True);path.write_text('OFFLINE FIXTURE '+path.name)
        config={'runtimeOptions':{'tfm':'net10.0','rollForward':'Disable','framework':{'name':'Microsoft.NETCore.App','version':'10.0.14'}}}
        (self.output/'WakeupNativeProbe.runtimeconfig.json').write_text(json.dumps(config))
        deps={'targets':{'.NETCoreApp,Version=v10.0':{p[:-4]+'/3.0.0':{'runtime':{p:{}}} for p in PRODUCTS}},'libraries':{p[:-4]+'/3.0.0':{} for p in PRODUCTS}}
        (self.output/'WakeupNativeProbe.deps.json').write_text(json.dumps(deps))
        rows=[];artifacts=[]
        for component,members in [('runtime',[p for p in self.runtime.rglob('*') if p.is_file() and self.asp not in p.parents]),('aspnetcore-runtime',list(self.asp.glob('*'))),('sdk',[])]:
            archive=self.root/(component+'.tar.gz')
            with tarfile.open(archive,'w:gz') as tar:
                for path in members:tar.add(path,arcname=str(path.relative_to(self.runtime)))
            sha=digest(archive,'sha512');url='https://fixture.invalid/'+component
            rows.append({'component':component,'path':str(archive),'sha512':sha,'officialUrl':url});artifacts.append({'component':component,'rid':'linux-x64','hash':sha,'url':url})
        self.lock={'artifacts':artifacts}
        self.receipt={'schema':2,'arm':'D','rid':'linux-x64','sourceCommit':PLAN['baseline'],'sdkVersion':PLAN['sdk'],'runtimeVersion':PLAN['runtime'],'dotnet':str(self.runtime/'dotnet'),'probeDll':str(self.output/'WakeupNativeProbe.dll'),'sourceRoot':str(self.source),'generatedRoot':str(self.generated),'buildLog':str(self.root/'build.log'),'buildSucceeded':True,'productDiffReviewed':True,'archiveVerification':rows,'files':[{'path':str(p),'sha256':digest(p)} for p in sorted(self.root.rglob('*')) if p.is_file() and not p.name.endswith('.tar.gz')]}
        self.path=self.root/'receipt.json'
    def tearDown(self):self.temp.cleanup()
    def write(self,receipt=None):
        self.path.write_text(json.dumps(self.receipt if receipt is None else receipt));return digest(self.path)
    def validate(self,receipt=None):return validate_receipt(self.path,self.write(receipt),PLAN,self.lock)
    def test_source_anchors_exist_in_both_frozen_real_git_trees(self):
        # Read actual committed trees, not synthetic fixture files. Git only; no CLR.
        repository=Path(__file__).resolve().parents[2]
        for revision in (PLAN['baseline'],PLAN['candidate']):
            with self.subTest(revision=revision):
                paths=set(subprocess.check_output(['git','ls-tree','-r','--name-only',revision,'--',*SOURCE_ANCHORS],cwd=repository,text=True).splitlines())
                self.assertEqual(paths,set(SOURCE_ANCHORS))
        self.assertIn('src/SharpLink.Runtime/RpcSession.SendPump.cs',SOURCE_ANCHORS)
        self.assertNotIn('src/SharpLink.Runtime/SendPump.cs',SOURCE_ANCHORS)
    def test_remote_candidate_has_equal_complete_tree_and_explicit_new_identity(self):
        tools=Path(__file__).resolve().parent;mapping=json.loads((tools/'source-binding.json').read_text());repository=tools.parents[1]
        self.assertEqual(PLAN['candidate'],mapping['remoteCandidate'])
        self.assertNotEqual(mapping['remoteCandidate'],mapping['localReviewedCandidate'])
        for commit in (mapping['remoteCandidate'],mapping['localReviewedCandidate']):
            actual=subprocess.check_output(['git','rev-parse',commit+'^{tree}'],cwd=repository,text=True).strip()
            self.assertEqual(actual,mapping['identicalCompleteTree'])
        parent=subprocess.check_output(['git','rev-parse',mapping['remoteCandidate']+'^'],cwd=repository,text=True).strip()
        self.assertEqual(parent,PLAN['baseline'])
    def test_valid_synthetic_fixture(self):self.assertEqual(self.validate()['schema'],2)
    def test_strict_flags_schema_digest(self):
        for field,value in [('buildSucceeded','false'),('productDiffReviewed','false'),('buildSucceeded',1),('schema',True),('schema','2')]:
            with self.subTest(field=field,value=value):
                bad=copy.deepcopy(self.receipt);bad[field]=value
                with self.assertRaises(ValueError):self.validate(bad)
        self.write()
        with self.assertRaises(ValueError):validate_receipt(self.path,'0'*64,PLAN,self.lock)
        bad=copy.deepcopy(self.receipt);bad['extra']=True
        with self.assertRaises(ValueError):self.validate(bad)
    def test_each_required_closure_member_cannot_be_omitted(self):
        for item in self.receipt['files']:
            with self.subTest(path=item['path']):
                bad=copy.deepcopy(self.receipt);bad['files']=[x for x in bad['files'] if x['path']!=item['path']]
                with self.assertRaises(ValueError):self.validate(bad)
    def test_archive_nonempty_claim_duplicates_and_bytes_rejected(self):
        for rows in [{'unverified':True},[],self.receipt['archiveVerification'][:2],self.receipt['archiveVerification'][:2]*2]:
            bad=copy.deepcopy(self.receipt);bad['archiveVerification']=rows
            with self.assertRaises(ValueError):self.validate(bad)
        bad=copy.deepcopy(self.receipt);bad['archiveVerification'][0]['sha512']='0'*128
        with self.assertRaises(ValueError):self.validate(bad)
        archive=Path(self.receipt['archiveVerification'][0]['path']);archive.write_bytes(b'corrupt')
        with self.assertRaises(ValueError):self.validate()
    def test_runtime_rehash_does_not_override_official_member(self):
        p=self.core/'libclrjit.so';p.write_text('tampered JIT')
        for row in self.receipt['files']:
            if row['path']==str(p):row['sha256']=digest(p)
        with self.assertRaisesRegex(ValueError,'Installed official member differs'):self.validate()
    def test_unreviewed_newer_hostfxr_is_rejected(self):
        (self.runtime/'host/fxr/10.0.99').mkdir()
        with self.assertRaisesRegex(ValueError,'different hostfxr'):self.validate()
    def test_changed_file_and_extra_output_are_rejected(self):
        p=self.output/'SharpLink.Runtime.dll';old=p.read_text();p.write_text('tampered')
        with self.assertRaises(ValueError):self.validate()
        p.write_text(old);(self.output/'unreviewed.dll').write_text('extra')
        with self.assertRaises(ValueError):self.validate()
    def test_runtimeconfig_and_deps_requirements(self):
        p=self.output/'WakeupNativeProbe.runtimeconfig.json';original=p.read_text()
        for change in ['wrong-version','tier-override','roll-forward']:
            config=json.loads(original);opts=config['runtimeOptions']
            if change=='wrong-version':opts['framework']['version']='10.0.2'
            elif change=='tier-override':opts['configProperties']={'System.Runtime.TieredCompilation':False}
            else:opts['rollForward']='LatestPatch'
            p.write_text(json.dumps(config))
            for row in self.receipt['files']:
                if row['path']==str(p):row['sha256']=digest(p)
            with self.subTest(change=change),self.assertRaises(ValueError):self.validate()
    def test_original_review_bypass_never_reaches_launch(self):
        bad=copy.deepcopy(self.receipt);bad['buildSucceeded']='false';bad['productDiffReviewed']='false';bad['archiveVerification']={'unverified':True};bad['files']=bad['files'][:2]
        receipt_hash=self.write(bad);root=Path(collect.__file__).parent
        args=['collect.py','--reviewed-plan-sha256',digest(root/'plan.json'),'--reviewed-tools-sha256',digest(root/'tool-lock.json'),'--reviewed-receipt-sha256',receipt_hash,'--receipt',str(self.path),'--cell','default-c1','--output',str(self.root/'out')]
        with patch.object(sys,'argv',args),patch.dict(os.environ,{},clear=True),patch.object(collect.subprocess,'Popen') as launch:
            with self.assertRaisesRegex(ValueError,'Approval flags must be boolean true'):collect.main()
            launch.assert_not_called()
    def test_profiler_and_native_loader_override_channels(self):
        for key in ['CORECLR_ENABLE_PROFILING','COR_ENABLE_PROFILING','CORECLR_PROFILER_PATH','LD_PRELOAD','LD_AUDIT','DYLD_INSERT_LIBRARIES','MONO_ENV_OPTIONS','DOTNET_STARTUP_HOOKS']:
            with self.subTest(key=key),self.assertRaises(ValueError):collect.validate_environment({key:'0'})
    def test_provider_and_decoder_contract_static_fixture(self):
        mask=int(PLAN['providers'].split(':')[1],16)
        for required in (0x1000,0x1000000000,0x20000):self.assertEqual(mask&required,required)
        decoder=(Path(__file__).parent/'decoder/Program.cs').read_text()
        for text in ['map.ILOffset(i)','map.NativeOffset(i)','map.ReJITID','map.MethodExtent','map.ClrInstanceID','sequence = count','eventVersion = e.Version']:
            self.assertIn(text,decoder)
if __name__=='__main__':unittest.main()
