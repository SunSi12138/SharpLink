"""Synthetic full-campaign and process-boundary controls; no CLR/process execution."""
import copy,json,subprocess,tempfile,unittest
from pathlib import Path
from unittest.mock import patch,MagicMock
import run
from run import BASE,BASE_TREE,HEAD,TREE,OVERLAY,launch_plan,sha
from validate import validate
import test_measurement
class CampaignControls(unittest.TestCase):
 def setUp(self):
  self.temp=tempfile.TemporaryDirectory();self.root=Path(self.temp.name);root=Path(__file__).resolve().parents[2]
  overlays={rel:sha(root/'eng/release-flush-limited/RpcSessionFlushPolicyEvidenceRunner.cs' if rel.endswith('RpcSessionFlushPolicyEvidenceRunner.cs') else root/rel) for rel in OVERLAY}
  self.record=dict(completed=True,baseline=BASE,candidate=HEAD,candidateTree=TREE,sourceTrees=dict(baseline=BASE_TREE,candidate=TREE),overlays=overlays,sourceRoots=dict(baseline='/frozen/baseline',candidate='/frozen/candidate'),outputRoot=str(self.root),plannedLaunches=launch_plan(),launches=[dict(x,status='completed') for x in launch_plan()],binaries={})
  for arm in ('baseline','candidate'):
   folder=self.root/'binaries'/arm;folder.mkdir(parents=True);self.record['binaries'][arm]={}
   for name in ('SharpLink.Benchmarks.dll','SharpLink.Runtime.dll','SharpLink.Client.dll','SharpLink.Server.dll','SharpLink.Abstractions.dll'):
    f=folder/name;f.write_bytes(b'synthetic-not-a-real-assembly');self.record['binaries'][arm][name]=sha(f)
  for row in launch_plan():
   doc=test_measurement.DocumentControls().fixture();state=row['state'];doc['state']=state;doc['commit']=BASE if row['arm']=='baseline' else HEAD
   doc['runtimeFiles']={f'/synthetic/{n}':'A'*64 for n in ('dotnet','System.Private.CoreLib.dll','libcoreclr.so','libclrjit.so','libhostpolicy.so')}
   for cell in doc['measurements']:cell['state']=state
   f=self.root/f"{row['index']:02d}-{state}.json";f.write_text(json.dumps(doc))
   source=Path(self.record['sourceRoots'][row['arm']]);args=['dotnet','exec','--fx-version','10.0.14',str(source/'test/SharpLink.Benchmarks/bin/Release/net10.0/SharpLink.Benchmarks.dll'),'--rpc-session-flush-policy-evidence',state,str(f)]
   f.with_suffix('.receipt.json').write_text(json.dumps(dict(argv=args,cwd=str(source),timeoutSeconds=180,exitCode=0,processGroupQuiescent=True)))
  self.save()
 def tearDown(self):self.temp.cleanup()
 def save(self):(self.root/'execution.json').write_text(json.dumps(self.record))
 def test_complete_synthetic_campaign(self):self.assertEqual(validate(self.root)['rawRoundCount'],180)
 def test_identity_and_completeness_mutations(self):
  original=copy.deepcopy(self.record)
  for change in [lambda d:d.update(binaries={}),lambda d:d['binaries'].pop('baseline'),lambda d:d['binaries'].update(candidate={}),lambda d:d['sourceTrees'].update(baseline=TREE),lambda d:d['overlays'].pop(OVERLAY[0]),lambda d:d['overlays'].update({OVERLAY[0]:'0'*64}),lambda d:d['launches'].pop(),lambda d:d['launches'][0].update(state='post-update'),lambda d:d.update(completed=False)]:
   self.record=copy.deepcopy(original);change(self.record);self.save()
   with self.assertRaises(ValueError):validate(self.root)
 def test_binary_mutation(self):
  (self.root/'binaries/baseline/SharpLink.Benchmarks.dll').write_bytes(b'changed')
  with self.assertRaises(ValueError):validate(self.root)
 def test_receipt_mutations(self):
  f=self.root/'00-static-baseline.receipt.json';original=json.loads(f.read_text())
  for change in [lambda d:d.update(argv=d['argv'][:-1]+['wrong-output']),lambda d:d['argv'].__setitem__(3,'10.0.13'),lambda d:d.update(cwd='/wrong'),lambda d:d.update(timeoutSeconds=181),lambda d:d.update(exitCode=1),lambda d:d.update(processGroupQuiescent=False),lambda d:d.update(timedOut=True),lambda d:d.update(descendantLeak=True)]:
   data=copy.deepcopy(original);change(data);f.write_text(json.dumps(data))
   with self.assertRaises(ValueError):validate(self.root)
 def test_runtime_filenames_and_missing_round_file(self):
  f=self.root/'00-static-baseline.json';d=json.loads(f.read_text());d['runtimeFiles']['wrong']=d['runtimeFiles'].pop('/synthetic/dotnet');f.write_text(json.dumps(d))
  with self.assertRaises(ValueError):validate(self.root)
  f.unlink()
  with self.assertRaises(ValueError):validate(self.root)
class ProcessControls(unittest.TestCase):
 def run_fake(self,wait_effect,alive_effect):
  with tempfile.TemporaryDirectory() as tmp:
   p=MagicMock(pid=123);p.wait.side_effect=wait_effect
   with patch.object(run.subprocess,'Popen',return_value=p),patch.object(run,'group_alive',side_effect=alive_effect),patch.object(run.os,'killpg'):
    try:run.command(['fake'],Path(tmp)/'fake.log',Path(tmp),{},1);failed=False
    except RuntimeError:failed=True
   return failed,json.loads((Path(tmp)/'fake.receipt.json').read_text()),p
 def test_clean_process(self):
  fail,data,p=self.run_fake([0],[False,False]);self.assertFalse(fail);self.assertTrue(data['processGroupQuiescent'])
 def test_timeout_retained_and_bounded(self):
  fail,data,p=self.run_fake([subprocess.TimeoutExpired('fake',1),-9],[False,False]);self.assertTrue(fail);self.assertTrue(data['timedOut']);self.assertEqual(p.wait.call_args_list[-1].kwargs,{'timeout':5})
 def test_cleanup_timeout_not_success(self):
  fail,data,p=self.run_fake([subprocess.TimeoutExpired('fake',1),subprocess.TimeoutExpired('fake',5)],[False,False]);self.assertTrue(fail);self.assertTrue(data['cleanupTimedOut'])
 def test_descendant_leak_is_failure_even_if_cleaned(self):
  fail,data,p=self.run_fake([0],[True,False,False]);self.assertTrue(fail);self.assertTrue(data['descendantLeak'])
 def test_unknown_group_is_failure(self):
  fail,data,p=self.run_fake([0],[subprocess.TimeoutExpired('ps',5)]);self.assertTrue(fail);self.assertFalse(data['processGroupQuiescent'])
