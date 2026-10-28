#!/usr/bin/env python3
"""Source-only regression for archive patching beneath an enclosing Git checkout.
No product builds or repository test fixtures are copied or invoked.
"""
import hashlib,json,os,shutil,subprocess,tempfile,unittest
from pathlib import Path

HERE=Path(__file__).resolve().parent
PREPARE=HERE/'prepare_public_arms.py' if (HERE/'prepare_public_arms.py').exists() else HERE/'maintenance-hot-cost/prepare_public_arms.py'

class ReconstructionTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.scratch=Path(tempfile.mkdtemp(prefix='maintenance-reconstruction-'))
        cls.repo=cls.scratch/'outer-checkout';cls.repo.mkdir()
        cls.git('init','--quiet')
        cls.git('config','user.name','Reconstruction regression')
        cls.git('config','user.email','reconstruction@example.invalid')
        cls.paths=['src/SharpLink.Runtime/RpcSession.cs',
                   'src/SharpLink.Runtime/RpcSession.SendPump.cs',
                   'src/SharpLink.Runtime/RpcSession.SendPump.BackpressureOwnership.cs']
        cls.base={p:''.join(f'{Path(p).name} original line {i}\n' for i in range(40)) for p in cls.paths}
        cls.base['src/SharpLink.Server/SharpLinkServer.InvocationDispatch.cs']='dispatch unchanged\n'
        for p in ['Directory.Build.props','Directory.Packages.props','global.json']:cls.base[p]='fixture configuration\n'
        cls.write(cls.base);cls.parent=cls.commit('parent')
        physical=dict(cls.base);physical[cls.paths[0]]=physical[cls.paths[0]].replace('original line 1\n','physical owner line 1\n')
        cls.write(physical);cls.physical=cls.commit('physical')
        cls.git('checkout','--quiet','--detach',cls.parent)
        pump=dict(cls.base)
        for p in cls.paths:pump[p]=pump[p].replace('original line 35\n','pump owner line 35\n')
        cls.write(pump);cls.pump=cls.commit('pump')
        cls.expected=dict(physical)
        for p in cls.paths:cls.expected[p]=cls.expected[p].replace('original line 35\n','pump owner line 35\n')
        cls.write(cls.expected);cls.candidate=cls.commit('feature')
        cls.outer_before=cls.hashes(cls.repo/'src')

    @classmethod
    def git(cls,*args):
        return subprocess.check_output(['git','-C',str(cls.repo),*args],stderr=subprocess.STDOUT).decode().strip()
    @classmethod
    def write(cls,files):
        for name,body in files.items():
            p=cls.repo/name;p.parent.mkdir(parents=True,exist_ok=True);p.write_text(body)
    @classmethod
    def commit(cls,message):
        cls.git('add','src','Directory.Build.props','Directory.Packages.props','global.json')
        cls.git('commit','--quiet','-m',message);return cls.git('rev-parse','HEAD')
    @staticmethod
    def hashes(root):
        return {str(p.relative_to(root)):hashlib.sha256(p.read_bytes()).hexdigest() for p in root.rglob('*') if p.is_file()}
    def run_case(self,label,source,poison=False,corrupt_expected=False):
        kit=self.repo/'eng'/label;kit.mkdir(parents=True)
        (kit/'prepare_public_arms.py').write_text(source)
        expected={p:hashlib.sha256(b.encode()).hexdigest() for p,b in self.expected.items()}
        if corrupt_expected:expected[self.paths[0]]='0'*64
        (kit/'expected-prerequisite-sources.json').write_text(json.dumps(expected))
        env={k:v for k,v in os.environ.items() if not k.startswith('GIT_')}
        if poison:env.update(GIT_DIR=str(self.repo/'.git'),GIT_WORK_TREE=str(self.repo),GIT_PREFIX='eng/not-the-arm/')
        command=['python3',str(kit/'prepare_public_arms.py'),'--repo',str(self.repo),'--parent',self.parent,'--physical',self.physical,'--pump',self.pump,'--candidate',self.candidate]
        result=subprocess.run(command,cwd=self.repo,env=env,text=True,stdout=subprocess.PIPE,stderr=subprocess.STDOUT)
        (kit/'run.log').write_text(result.stdout)
        self.assertEqual(self.outer_before,self.hashes(self.repo/'src'),'enclosing product source was modified')
        return kit,result,expected
    def test_nested_checkout_applies_exact_patch(self):
        kit,result,expected=self.run_case('positive', PREPARE.read_text())
        self.assertEqual(0,result.returncode,result.stdout)
        self.assertEqual(expected,self.hashes(kit/'arms/published-prerequisites'))
        self.assertTrue((kit/'source-provenance.json').is_file())
    def test_inherited_git_routing_does_not_redirect_apply(self):
        kit,result,expected=self.run_case('inherited-routing', PREPARE.read_text(),poison=True)
        self.assertEqual(0,result.returncode,result.stdout)
        self.assertEqual(expected,self.hashes(kit/'arms/published-prerequisites'))
    def test_hash_guard_still_rejects_wrong_expected_source(self):
        kit,result,_=self.run_case('bad-identity', PREPARE.read_text(),corrupt_expected=True)
        self.assertNotEqual(0,result.returncode)
        self.assertIn('Prerequisite source identity mismatch',result.stdout)
        diagnostic=json.loads((kit/'source-provenance.json').read_text())
        self.assertEqual('PrerequisiteSourceIdentityMismatch',diagnostic['Status'])
        self.assertEqual([self.paths[0]],[x['Path'] for x in diagnostic['Differences']])
        self.assertEqual('0'*64,diagnostic['Differences'][0]['ExpectedSha256'])
    def test_original_unisolated_apply_reproduces_skipped_patch(self):
        source=PREPARE.read_text()
        start=source.index('# An archive has no .git.')
        end=source.index('\nexpected=json.loads',start)
        source=source[:start]+"subprocess.run(['git','apply','-'],input=patch,cwd=baseline,check=True)"+source[end:]
        kit,result,_=self.run_case('original-negative',source)
        self.assertNotEqual(0,result.returncode)
        self.assertIn('Prerequisite source identity mismatch',result.stdout)
        for p in self.paths:self.assertIn(p,result.stdout)
    def test_public_kit_preparation_copies_match(self):
        other=HERE/'maintenance-stream-cost/prepare_public_arms.py'
        if other.exists():self.assertEqual(PREPARE.read_bytes(),other.read_bytes())

    def run_integrated_case(self,label,wrong_candidate=False):
        kit=self.repo/'eng'/label;kit.mkdir(parents=True)
        (kit/'prepare_public_arms.py').write_text(PREPARE.read_text())
        expected=self.git('rev-parse',self.candidate+':src')
        config={'candidate_source_tree':'0'*40 if wrong_candidate else expected,
                'arms':{'prerequisites':{'commit':self.physical,'source_tree':self.git('rev-parse',self.physical+':src')}},
                'comparison':'fixture integrated comparison'}
        (kit.parent/'maintenance-integration-sources.json').write_text(json.dumps(config))
        result=subprocess.run(['python3',str(kit/'prepare_public_arms.py'),'--repo',str(self.repo),
                               '--candidate',self.candidate,'--profile','integrated-prerequisites'],
                              text=True,stdout=subprocess.PIPE,stderr=subprocess.STDOUT)
        return kit,result
    def test_integrated_profile_archives_exact_source_arms(self):
        kit,result=self.run_integrated_case('integrated-positive')
        self.assertEqual(0,result.returncode,result.stdout)
        proof=json.loads((kit/'source-provenance.json').read_text())
        self.assertEqual('SourceSnapshotsVerified',proof['Status'])
        self.assertEqual('integrated-prerequisites',proof['Profile'])
        self.assertEqual(self.physical,proof['published-prerequisites']['Base'])
        self.assertEqual(self.candidate,proof['candidate']['Base'])
        self.assertEqual(self.expected[self.paths[0]],(kit/'arms/candidate'/self.paths[0]).read_text())
        self.assertEqual(self.outer_before,self.hashes(self.repo/'src'))
    def test_integrated_profile_rejects_wrong_candidate_source(self):
        kit,result=self.run_integrated_case('integrated-negative',wrong_candidate=True)
        self.assertNotEqual(0,result.returncode)
        proof=json.loads((kit/'source-provenance.json').read_text())
        self.assertEqual('Failed',proof['Status'])
        self.assertIn('Integrated source tree mismatch: candidate',proof['Error'])
        self.assertFalse((kit/'arms/candidate').exists())

    @classmethod
    def tearDownClass(cls):
        print('Retained deterministic source fixtures:',cls.scratch)

if __name__=='__main__':unittest.main(verbosity=2)
