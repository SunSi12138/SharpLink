import copy
import importlib.util
import json
import pathlib
import tempfile
import sys
import unittest
from scope_project import HERE, ROOT, MULTIPLICITY, SITES, project, replace, sha
spec=importlib.util.spec_from_file_location('scope_runner',HERE/'run.py')
runner=importlib.util.module_from_spec(spec);spec.loader.exec_module(runner)
sys.modules['run']=runner.base
spec=importlib.util.spec_from_file_location('multiplicity_tests',MULTIPLICITY/'test_multiplicity.py')
oldtests=importlib.util.module_from_spec(spec);spec.loader.exec_module(oldtests)

METHODS=['SharpLink.Runtime.SharedMemoryPipeReader.ReadAsync/0','SharpLink.Runtime.SharedMemoryControlChannel.RunReaderAsync/0',
    'SharpLink.Runtime.SharedMemoryControlChannel.WaitWithCancellationAsync/0','SharpLink.Client.SharpLinkClient.ProcessRequestLoop/0','SharpLink.Server.SharpLinkServer.ProcessRequestLoop/0']
def row():
    r=oldtests.schema_row();r['transport']='shm'
    before=[0]*37;after=[0]*37
    before[33]=after[33]=133
    for o in [0,16]:
        before[o+13]=after[o+13]=1;before[o+14]=1;after[o+14]=22
        after[o:o+13]=[2,17,100,1,1,0,0,0,9,8,0,0,0]
        after[o+15]=2
    r['scopes']={'schemaVersion':1,'variant':'B','processStartUtc':'2026-10-10T11:00:00Z','corelibSha256':'a'*64,
        'before':before,'after':after,'delta':[b-a for a,b in zip(before,after)],
        'beforeSnapshotBegin':1,'beforeSnapshotEnd':2,'afterSnapshotBegin':27,'afterSnapshotEnd':28,
        'stateMachineFields':[{'method':m,'fields':[]} for m in METHODS], 'sites':['pipe','control'],
        'fieldOrder':runner.SCOPE_FIELDS,'trailingFields':['overlap','coldThreads','threadMismatch','invalidObservation','pricedOwnerOverlap'],
        'meaning':'fixture','boundary':'fixture'}
    return r

def update(r,index,value):
    s=r['scopes'];s['after'][index]=s['before'][index]+value;s['delta'][index]=value

class ValidationTests(unittest.TestCase):
    def check(self,r):runner.validate_row(r,'shm',1,128,64,'schema-test','B',r['runtime'],set())
    def test_valid(self):self.check(row())
    def test_scope_and_owner_addition(self):
        r=row();budget=runner.ledger(r,False)
        self.assertEqual(budget['measuredInclusiveScopeBytes'],34)
        self.assertEqual(budget['conditionalReconciledBytes'],129*272+34)
        r['bytes']=0;self.assertEqual(runner.ledger(r,False)['signedResidualBytes'],-(129*272+34))
    def test_correctness_never_prices_owner_budget(self):
        r=row();self.assertIsNone(runner.ledger(r,True)['conditionalReconciledBytes'])
        self.assertEqual(runner.ledger(r,True)['measuredInclusiveScopeBytes'],34)
    def test_overlap_retains_scopes_rejects_sum(self):
        for index in [32,34,35,36]:
            r=row();update(r,index,1)
            with self.assertRaises(ValueError):self.check(r)
            result=runner.ledger(r,False)
            self.assertIsNone(result['conditionalReconciledBytes'])
            self.assertTrue(result['additiveReconciliationRejected'])
            self.assertEqual(result['measuredInclusiveScopeBytes'],34)
    def test_cold_threads_preserve_raw_scope_but_never_additive_price(self):
        r=row();update(r,33,9)
        self.check(r)
        result=runner.ledger(r,False)
        self.assertEqual(result['status'],'scope-only-cold-thread-observation')
        self.assertEqual(result['measuredInclusiveScopeBytes'],34)
        self.assertEqual(len(result['scopeSites']),2)
        self.assertTrue(result['additiveReconciliationRejected'])
        self.assertEqual(result['scopeInvariantViolations'],['coldThreads'])
        self.assertIsNone(result['conditionalReconciledBytes'])
        self.assertIsNone(result['signedResidualBytes'])
        self.assertIsNone(result['signedResidualBytesPerPayloadOperation'])
    def test_cold_plus_true_overlap_still_fails(self):
        r=row();update(r,33,9);update(r,32,1)
        with self.assertRaises(ValueError):self.check(r)
    def test_recovery_filter_preserves_14_process_cohort(self):
        text=(HERE/'run.py').read_text()
        self.assertIn("choices=[32]",text)
        self.assertIn('for concurrency in concurrencies:',text)
        self.assertEqual(len([32])*len('ABBA'*3+'AA'),14)
    def test_prior_overlap_not_hidden(self):
        r=row();r['scopes']['before'][32]=r['scopes']['after'][32]=1
        with self.assertRaises(ValueError):self.check(r)
    def test_counters_reconcile(self):
        for index in [0,1,3,8,15]:
            r=row();update(r,index,r['scopes']['delta'][index]+1)
            with self.assertRaises(ValueError):self.check(r)
    def test_schema_rejects_unknown_missing_negative(self):
        r=row();r['scopes']['invented']=1
        with self.assertRaises(ValueError):self.check(r)
        r=row();del r['scopes']['sites']
        with self.assertRaises(ValueError):self.check(r)
        r=row();update(r,2,-1)
        with self.assertRaises(ValueError):self.check(r)
    def test_timestamp_and_layout(self):
        r=row();r['scopes']['beforeSnapshotEnd']=11
        with self.assertRaises(ValueError):self.check(r)
        r=row();r['scopes']['stateMachineFields'].pop()
        with self.assertRaises(ValueError):self.check(r)
    def test_valid_completion_race_counts(self):
        r=row();m=r['multiplicity']
        for name,value in {'logical_fast':3,'logical_slow':126,'logical_helper_entry':126,'operation_registration_entry':124,'operation_registration_accepted':124}.items():
            i=runner.NAMES.index(name);m['after'][i]=m['before'][i]+value;m['delta'][i]=value
        self.check(r)
        self.assertEqual(runner.ledger(r,False)['conditionalReconciledBytes'],124*136+129*136+34)
    def test_owner_changes_are_outcomes_not_schema_failures(self):
        for name in ['operation_new','send_capacity_wait_uses','context_snapshot_new']:
            r=row();m=r['multiplicity'];i=runner.NAMES.index(name);m['after'][i]=m['before'][i]+1;m['delta'][i]=1
            self.check(r)
            result=runner.ledger(r,False)
            self.assertEqual(result['observedUnpricedOwnerActivity'][name],1)
            if name=='context_snapshot_new':self.assertIsNone(result['conditionalReconciledBytes'])
    def test_invalid_owner_balance_unprices_without_discarding_sample(self):
        r=row();m=r['multiplicity'];i=runner.NAMES.index('operation_registration_accepted');m['after'][i]=130;m['delta'][i]=130
        self.check(r)
        result=runner.ledger(r,False)
        self.assertIsNone(result['conditionalReconciledBytes'])
        self.assertTrue(result['ownerBudget']['assumptionErrors'])
    def test_28_process_schedule(self):self.assertEqual(2*len('ABBA'*3+'AA'),28)

class ProjectionTests(unittest.TestCase):
    def test_all_expected_dependencies(self):
        for name,digest in json.loads((HERE/'expected_hashes.json').read_text()).items():self.assertEqual(sha(ROOT/name),digest,name)
    def test_exact_sites(self):
        for path,(old,new,count) in SITES.items():
            text=(ROOT/path).read_text();modified=replace(text,old,new,count)
            self.assertEqual(modified.replace(new,old),text)
    def test_no_async_wrapper_or_conversion(self):
        text=(HERE/'ScopeCounters.cs.in').read_text()
        self.assertNotIn('async ValueTask',text);self.assertNotIn('.AsTask(',text)
        self.assertNotIn('await ',text)
        self.assertIn('return read;',text);self.assertIn('[ThreadStatic]',text)
        self.assertIn('Owner.ScopeDepth++',text)
    def test_projection_unchanged_async_body_and_precise_patches(self):
        with tempfile.TemporaryDirectory() as tmp:
            output=pathlib.Path(tmp)/'out';output.mkdir()
            variants=project(pathlib.Path(tmp)/'work',output)
            for v,dest in variants.items():
                path='src/SharpLink.Runtime/Transport/SharedMemoryPipelines.cs'
                self.assertEqual(sha(dest/path),sha(ROOT/path))
                text=(dest/'eng/validation/issue739/Program.cs').read_text()
                self.assertIn('scopes.WarmThreads()',text)
            counters=(variants['B']/'src/SharpLink.Abstractions/Issue739MultiplicityCounters.cs').read_text()
            for name in ['logical_helper_entry','operation_registration_accepted','permit_plain_new','push_null_snapshot','restore_snapshot_null']:
                self.assertIn('index == '+name,counters)
            self.assertEqual((output/'A-source.patch').read_text(),'')
            self.assertNotIn('SharedMemoryPipelines.cs',(output/'B-source.patch').read_text())

if __name__=='__main__':unittest.main()
