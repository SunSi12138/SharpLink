"""Tiny synthetic byte-only recovery fixtures. No real artifact restoration/CLR/network."""
import copy,hashlib,io,json,os,sys,tarfile,tempfile,types,unittest
from pathlib import Path
from unittest.mock import patch
import driver,revalidate
sys.path.insert(0,str(driver.NATIVE))
import test_receipt as receipt_fixtures
PLAN=receipt_fixtures.PLAN
from receipt import digest,tree,validate_receipt

class RecoveryTests(unittest.TestCase):
    def setUp(self):
        self.fixture=receipt_fixtures.ReceiptTests('test_valid_synthetic_fixture');self.fixture.setUp();f=self.fixture
        self.base=f.root/'recovery-fixture';self.base.mkdir();self.root=self.base/'restored';self.repo=self.base/'repo';self.out=self.base/'out';self.out.mkdir()
        self.native=self.repo/'native';self.native.mkdir(parents=True);self.here=self.repo/'hosted';self.here.mkdir()
        (self.here/'driver.py').write_text('new validator source fixture')
        wf=self.repo/'.github/workflows/wakeup-native-hosted.yml';wf.parent.mkdir(parents=True);wf.write_text('reviewed workflow fixture')
        sdk=self.base/'sdk.tar.gz'
        with tarfile.open(sdk,'w:gz') as t:
            m=tarfile.TarInfo('sdk/10.0.102/tool.dll');m.size=3;t.addfile(m,io.BytesIO(b'sdk'))
        f.receipt['archiveVerification'][2].update(path=str(sdk),sha512=digest(sdk,'sha512'));f.lock['artifacts'][2]['hash']=digest(sdk,'sha512')
        (self.native/'plan.json').write_text(json.dumps(PLAN));(self.native/'artifact-lock.json').write_text(json.dumps(f.lock))
        self.members={}
        for p in tree(f.runtime):self.members['dotnet/'+str(p.relative_to(f.runtime))]=p.read_bytes()
        for arm in ('D','C'):
            for p in tree(f.source):self.members['source-'+arm+'/'+str(p.relative_to(f.source))]=p.read_bytes()
            for p in tree(f.output):self.members['build-'+arm+'/probe/bin/Release/net10.0/'+p.name]=p.read_bytes()
            self.members['build-'+arm+'/probe/obj/Generated/Generated.cs']=b'generated fixture'
        for name in ('build-D','build-C','build-decoder','source-clean-D','source-clean-C'):
            self.members['evidence/'+name+'.exit.json']=json.dumps({'exitCode':0,'timedOut':False}).encode()
            self.members['evidence/'+name+'.log']=b'Build succeeded fixture'
        self.members['evidence/host.json']=json.dumps({'workflowCommit':revalidate.BUILD_COMMIT,'runId':str(revalidate.BUILD_RUN),'rid':'osx-arm64'}).encode()
        self.members['evidence/sdk-members.json']=json.dumps({'sdk/10.0.102/tool.dll':hashlib.sha256(b'sdk').hexdigest()}).encode()
        self.members['tools/hosted/driver.py']=b'original driver untouched'
        self.members['decoder/bin/Release/net10.0/TraceDecoder.dll']=b'original decoder untouched'
        self.template=copy.deepcopy(f.receipt)
        # Use Linux synthetic closure/plan in validator adapter; no fake ARM DLL run.
        for row in self.template['archiveVerification']:
            p=Path(row['path']);self.members['archives/'+p.name]=p.read_bytes();row['path']=str(self.root/'archives'/p.name)
        self.template['dotnet']=str(self.root/'dotnet/dotnet')
        self.members['receipts/D.json']=json.dumps(self.template).encode()
        self.archive=self.repo/'artifacts/wakeup-input/failed-stage-files.tar.gz';self.archive.parent.mkdir(parents=True)
        with tarfile.open(self.archive,'w:gz') as t:
            for name,b in self.members.items():
                m=tarfile.TarInfo(name);m.size=len(b);t.addfile(m,io.BytesIO(b))
        def stage(stage):
            for n in ('environment-original-','environment-policy-'):driver.write(self.out/(n+stage+'.json'),{})
            return {}
        self.validated=[]
        def validate(p,h,plan,lock):
            self.validated.append(p)
            return validate_receipt(p,h,plan,lock)
        self.d=types.SimpleNamespace(RID='osx-arm64',REPO=self.repo,HERE=self.here,NATIVE=self.native,OUT=self.out,PLAN_SHA='a'*64,TOOLS_SHA='b'*64,digest=digest,tree=tree,write=driver.write,restore_bundle=driver.restore_bundle,stage_environment=stage,validate_receipt=validate)
    def tearDown(self):self.fixture.tearDown()
    def run_recovery(self):
        # Only platform and fixture's synthetic source/official lock are substituted.
        # Every restore/hash/receipt-closure operation is real, on tiny fixture bytes.
        from receipt import check_archives
        def check(rows,lock,rid):return check_archives(rows,lock,'linux-x64')
        with patch.object(revalidate,'FAILURE_TAR_SHA',digest(self.archive)),patch.object(revalidate,'check_original_tools') as original_check,patch.object(revalidate.platform,'system',return_value='Darwin'),patch.object(revalidate.platform,'machine',return_value='arm64'),patch.dict(os.environ,{'GITHUB_RUN_ID':'456','GITHUB_SHA':'c'*40}),patch('receipt.check_archives',side_effect=check),patch.object(driver.subprocess,'Popen',side_effect=AssertionError('No process launch')):
            revalidate.recover(self.d,self.root,{})
            original_check.assert_called_once_with(self.d,self.root)
    def test_recovery_keeps_all_original_bytes_and_issues_distinct_receipts(self):
        self.run_recovery()
        for name,b in self.members.items():self.assertEqual((self.root/name).read_bytes(),b,name)
        self.assertEqual(len(self.validated),2)
        index=json.loads((self.root/'prepared-index.json').read_text())
        self.assertEqual(index['runId'],revalidate.BUILD_RUN);self.assertEqual(index['revalidationRunId'],456)
        for arm in ('D','C'):
            self.assertEqual(index['receiptFiles'][arm],'receipts/revalidated-'+arm+'.json')
            receipt=json.loads((self.root/index['receiptFiles'][arm]).read_text())
            self.assertIn(str(self.root/'revalidation/provenance.json'),{x['path'] for x in receipt['files']})
        self.assertNotEqual(digest(self.root/'receipts/D.json'),index['receipts']['D'])
        self.assertTrue((self.out/'prepared.tar.gz').is_file())
    def test_wrong_input_digest_never_restores(self):
        with patch.object(revalidate.platform,'system',return_value='Darwin'),patch.object(revalidate.platform,'machine',return_value='arm64'):
            with self.assertRaisesRegex(ValueError,'bundle digest mismatch'):revalidate.recover(self.d,self.root,{})
        self.assertFalse(self.root.exists())
    def rewrite_input(self):
        with tarfile.open(self.archive,'w:gz') as t:
            for name,b in self.members.items():
                m=tarfile.TarInfo(name);m.size=len(b);t.addfile(m,io.BytesIO(b))
    def test_failed_original_build_cannot_be_marked_successful(self):
        self.members['evidence/build-C.exit.json']=json.dumps({'exitCode':1,'timedOut':False}).encode();self.rewrite_input()
        with self.assertRaisesRegex(ValueError,'did not succeed'):self.run_recovery()
        self.assertFalse((self.root/'receipts/revalidated-C.json').exists())
    def test_wrong_original_host_and_sdk_record_rejected(self):
        self.members['evidence/host.json']=json.dumps({'workflowCommit':'x'*40,'runId':str(revalidate.BUILD_RUN),'rid':'osx-arm64'}).encode();self.rewrite_input()
        with self.assertRaisesRegex(ValueError,'host/run binding'):self.run_recovery()
        self.assertFalse((self.root/'revalidation').exists())
    def test_changed_recorded_sdk_identity_rejected(self):
        self.members['evidence/sdk-members.json']=json.dumps({'sdk/10.0.102/tool.dll':'0'*64}).encode();self.rewrite_input()
        with self.assertRaisesRegex(ValueError,'Recorded SDK member differs'):self.run_recovery()
        self.assertFalse((self.root/'revalidation').exists())
    def test_original_tools_cannot_be_substituted(self):
        root=self.base/'bad';(root/'tools/native').mkdir(parents=True);(root/'tools/native/plan.json').write_text('{}')
        with self.assertRaisesRegex(ValueError,'Original prepared tool identity'):revalidate.check_original_tools(self.d,root)
    def test_recovery_has_no_command_network_or_clr_path(self):
        import ast
        parsed=ast.parse(Path(revalidate.__file__).read_text())
        forbidden={'run','Popen','check_output','system','urlopen','download_archives','extract'}
        calls=[n.func.attr for n in ast.walk(parsed) if isinstance(n,ast.Call) and isinstance(n.func,ast.Attribute)]
        self.assertEqual(set(calls)&forbidden,{'system'}) # platform.system only, checked below.
        for n in ast.walk(parsed):
            if isinstance(n,ast.Call) and isinstance(n.func,ast.Attribute) and n.func.attr=='system':self.assertEqual(ast.unparse(n.func.value),'platform')
if __name__=='__main__':unittest.main()
