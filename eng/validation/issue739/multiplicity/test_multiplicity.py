import copy
import hashlib
import json
import pathlib
import os
import unittest
from counters import NAMES, counter_source
from project import ROOT, HERE, mutations, harness, replace
from run import ledger, summarize, validate_row
from validate import SCHEMA

def row(variant='B', sequence=0):
    n=128; counts={name:0 for name in NAMES}
    counts.update(logical_decision=n+1, logical_slow=n+1, logical_helper_entry=n+1,
        operation_registration_entry=n+1,operation_registration_accepted=n+1,
        permit_plain_new=n+1,context_cache_hit=n+1,push_null_snapshot=n+1,restore_snapshot_null=n+1)
    return {'operations':n,'bytes':(n+1)*272+8,'bytesPerOperation':273.0,
        'cpuNanosecondsPerOperation':100.0,'qps':1000.0,'p50Nanoseconds':10.0,'p99Nanoseconds':20.0,
        'transport':'tcp','concurrency':1,'sample':f'test-{sequence}-{variant}','_sequence':sequence,'_variant':variant,
        'multiplicity':{'variant':variant,'counterNames':NAMES,'delta':[counts[k] for k in NAMES],
            'operationInflightBefore':0,'operationInflightAfter':0}}

class ProjectionTests(unittest.TestCase):
    def test_all_pinned_inputs_match(self):
        expected=json.loads((HERE/'expected_hashes.json').read_text())
        for filename,digest in expected.items():
            self.assertEqual(hashlib.sha256((ROOT/filename).read_bytes()).hexdigest(),digest,filename)
    def test_every_exact_replacement_succeeds(self):
        for filename in json.loads((HERE/'expected_hashes.json').read_text()):
            if filename.startswith('src/'):
                old=(ROOT/filename).read_text();new=mutations(filename,old)
                self.assertNotEqual(old,new,filename)
                self.assertNotIn('.AsTask()',new[len(old):])
    def test_replacement_fails_closed(self):
        with self.assertRaises(ValueError):replace('abc','missing','new')
        with self.assertRaises(ValueError):replace('abc abc','abc','new')
    def test_harness_projection_contains_fences(self):
        text=harness((ROOT/'eng/validation/issue739/Program.cs').read_text())
        self.assertIn('multiplicity.VerifyConfiguration(client, rpc, server, rpcKind)',text)
        self.assertIn('multiplicity.WaitForRegistrationReturn();\n        multiplicity.CaptureStart()',text)
        self.assertIn('await Drain();\n        multiplicity.WaitForRegistrationReturn();',text)
    def test_no_mutable_operation_read_after_forward(self):
        text=mutations('src/SharpLink.Client/RpcRequestOperation.cs',(ROOT/'src/SharpLink.Client/RpcRequestOperation.cs').read_text())
        end=text.split('_core.OnCompleted(continuation, state, token, flags);',1)[1].split('public Exception?',1)[0]
        self.assertNotIn('_core.',end)
        self.assertNotIn('Id',end)
        self.assertIn('operation_registration_inflight, -1',end)
    def test_counter_primitives_fixed_storage(self):
        text=counter_source()
        self.assertEqual(len(NAMES),len(set(NAMES)))
        self.assertIn('private static readonly long[] Values',text)
        self.assertNotIn('AsyncLocal',text)

class LedgerTests(unittest.TestCase):
    def test_source_budget_and_signed_residual(self):
        result=ledger(row(),False)
        self.assertEqual(result['predictedBytes'],129*272)
        self.assertEqual(result['signedResidualBytes'],8)
        negative=row();negative['bytes']=0
        self.assertEqual(ledger(negative,False)['signedResidualBytes'],-129*272)
    def test_sentinel_is_counted_not_subtracted(self):
        self.assertEqual(ledger(row(),False)['counts']['permit_plain_new'],129)
    def test_correctness_runtime_never_gets_budget(self):
        self.assertIsNone(ledger(row(),True)['predictedBytes'])
    def test_nonzero_inflight_invalidates_prediction(self):
        item=row();item['multiplicity']['operationInflightAfter']=1
        result=ledger(item,False)
        self.assertIsNone(result['predictedBytes']);self.assertTrue(result['assumptionErrors'])
    def test_context_mismatch_invalidates_prediction(self):
        item=row();item['multiplicity']['delta'][NAMES.index('restore_snapshot_null')]-=1
        self.assertIsNone(ledger(item,False)['predictedBytes'])
    def test_vanilla_is_not_attributed(self):
        self.assertIsNone(ledger(row('A'),False)['predictedBytes'])
    def test_56_rpc_schedule_structure(self):
        sequence=list('ABBA'*3+'AA')
        self.assertEqual(4*len(sequence),56)
        items=[]
        for i,v in enumerate(sequence):
            item=row(v,i);item['_ledger']=ledger(item,False);items.append(item)
        result=summarize(items,False)['cells'][0]
        self.assertEqual(len(result['ABBA']),3)
        self.assertEqual(len(result['AA']['samples']),2)
        self.assertEqual(len(result['diagnosticLedgers']),6)
    def test_AA_outliers_do_not_change_matched_medians(self):
        items=[]
        for i,v in enumerate('ABBA'*3+'AA'):
            item=row(v,i);item['_ledger']=ledger(item,False)
            if i>=12:item['bytesPerOperation']=999999
            items.append(item)
        result=summarize(items,False)['cells'][0]
        self.assertEqual(result['matchedABBAOnlyVariantMedians']['A']['bytesPerOperation'],273.0)

def schema_row():
    data={}
    for name,rule in SCHEMA['properties'].items():
        kinds=rule['type'] if isinstance(rule['type'],list) else [rule['type']]
        kind='null' if 'null' in kinds else kinds[0]
        data[name]={'null':None,'boolean':False,'integer':0,'number':0.0,'string':''}[kind]
    runtime=os.environ.get('ISSUE739_RUNTIME_VERSION','10.0.12')
    data.update(schemaVersion=2,sourceSha='eb99fe887cf2129d9b88441245ca0a4a6406b6c2',processId=123,
        transport='tcp',rpc='add',concurrency=1,operations=128,warmup=64,sample='schema-test',runtime=runtime,
        ticksStart=10,ticksEnd=20,stopwatchFrequency=10,elapsedSeconds=1.0,qps=128.0,
        bytes=272*128,bytesPerOperation=272.0,cpuMilliseconds=1.0,cpuNanosecondsPerOperation=1e6/128,
        p50Nanoseconds=1.0,p99Nanoseconds=2.0,gen0=0)
    m=copy.deepcopy(row()['multiplicity'])
    m.update(configurationVerified=True,before=[0]*len(NAMES),after=m['delta'],
        beforeSnapshotBegin=1,beforeSnapshotEnd=2,afterSnapshotBegin=25,afterSnapshotEnd=26,
        expectedMeasuredRpcInvocations=129,measuredDrainSentinels=1,payloadOperations=128)
    data['multiplicity']=m
    return data

class SchemaTests(unittest.TestCase):
    def check(self,data,pids=None):
        validate_row(data,'tcp',1,128,64,'schema-test','B',data['runtime'],set() if pids is None else pids)
    def test_valid_schema(self):self.check(schema_row())
    def test_missing_field(self):
        data=schema_row();del data['bytes']
        with self.assertRaises(ValueError):self.check(data)
    def test_unknown_field(self):
        data=schema_row();data['invented']=0
        with self.assertRaises(ValueError):self.check(data)
    def test_wrong_denominator(self):
        data=schema_row();data['bytesPerOperation']=1.0
        with self.assertRaises(ValueError):self.check(data)
    def test_duplicate_pid(self):
        with self.assertRaises(ValueError):self.check(schema_row(),{123})
    def test_nan(self):
        data=schema_row();data['p50Nanoseconds']=float('nan')
        with self.assertRaises(ValueError):self.check(data)
    def test_wrong_cell(self):
        data=schema_row();data['concurrency']=32
        with self.assertRaises(ValueError):self.check(data)
    def test_wrong_variant(self):
        data=schema_row();data['multiplicity']['variant']='A'
        with self.assertRaises(ValueError):self.check(data)
    def test_counter_delta_mismatch(self):
        data=schema_row();data['multiplicity']['before'][0]=1
        with self.assertRaises(ValueError):self.check(data)
    def test_reversed_boundary(self):
        data=schema_row();data['multiplicity']['beforeSnapshotEnd']=11
        with self.assertRaises(ValueError):self.check(data)

if __name__=='__main__':unittest.main()
