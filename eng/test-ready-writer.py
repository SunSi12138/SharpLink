#!/usr/bin/env python3
"""Fail-closed evidence/transform tests. No build or source modification."""
import copy, hashlib, importlib.util, json, pathlib, unittest
ROOT=pathlib.Path(__file__).resolve().parents[1]
def load(name):
    spec=importlib.util.spec_from_file_location(name,ROOT/'eng'/f'{name}.py');m=importlib.util.module_from_spec(spec);spec.loader.exec_module(m);return m
verify=load('verify-ready-writer');prepare=load('prepare-ready-writer');runner=load('run-ready-writer')
class Guards(unittest.TestCase):
    def fixture(self):
        case=verify.expected_plan()[0];t,g,c,n,b,w,f,r=case;total=c*n
        samples=[]
        for mode in verify.MODES:
            for round_ in range(4):
                owner=mode.startswith('B3');q=16 if mode.endswith('q16') else 1
                samples.append(dict(Mode=mode,Round=round_,Source='a'*40,Transport=t,Streams=c,ItemsPerStream=n,ItemBytes=b,StreamWindow=8192,ConnectionWindow=w,ItemsReceived=total,BytesReturned=total*b,ElapsedMs=100.0,ItemsPerSecond=total*10,CpuMs=200.0,AllocatedBytesPerItem=12.0,ProducerDurationMs=[10.0]*c,UpdateFrames=100,ReadyWriterMetrics=dict(SchedulingQuantum=q,RingSlotsPerStream=16,MaximumRingDepth=16,FramesReleased=total,CreditBytesApplied=total*b,NormalQueueRejections=0,WireUpdateNotifications=100,ReadyNotifications=c,EventChannelReadCalls=c+100,ExistingPumpBudgetRmwLowerBound=2*total,PumpOwnedCreditDebits=total if owner else 0,ProducerSideCreditGateOperations=0 if owner else total,CreditOwnerQueueCompletions=0)))
        metadata=dict(Source='a'*40,transport=t,Pgo=str(g),streams=c,items=n,bytes=b,connection=w,flush=f,slots=16,rounds=4,orderOffset=r,ProcessorCount=4)
        return dict(status='completed',error=None,metadata=metadata,samples=samples),case
    def reject(self,mutate):
        doc,case=self.fixture();mutate(doc)
        with self.assertRaises((ValueError,KeyError)):verify.validate_report(doc,'a'*40,case)
    def test_valid_complete_report(self):
        d,c=self.fixture();self.assertEqual(len(verify.validate_report(d,'a'*40,c)),16)
    def test_missing_and_duplicate_rows(self):
        self.reject(lambda d:d['samples'].pop());self.reject(lambda d:d['samples'].append(d['samples'][0]))
    def test_failed_report(self):self.reject(lambda d:d.update(status='failed'))
    def test_source_and_configuration(self):
        self.reject(lambda d:d['metadata'].update(Source='b'*40));self.reject(lambda d:d['metadata'].update(flush=1))
    def test_fabricated_throughput(self):self.reject(lambda d:d['samples'][0].update(ItemsPerSecond=1))
    def test_nan_and_credit_loss(self):
        self.reject(lambda d:d['samples'][0].update(CpuMs=float('nan')));self.reject(lambda d:d['samples'][0].update(BytesReturned=0))
    def test_missing_settlement(self):self.reject(lambda d:d['samples'][0]['ReadyWriterMetrics'].update(FramesReleased=0))
    def test_hidden_pump_rmw(self):self.reject(lambda d:d['samples'][0]['ReadyWriterMetrics'].update(ExistingPumpBudgetRmwLowerBound=0))
    def test_empty_channel_read(self):self.reject(lambda d:d['samples'][0]['ReadyWriterMetrics'].update(EventChannelReadCalls=999))
    def test_wrong_authority_and_ring(self):
        self.reject(lambda d:d['samples'][0]['ReadyWriterMetrics'].update(PumpOwnedCreditDebits=1));self.reject(lambda d:d['samples'][0]['ReadyWriterMetrics'].update(MaximumRingDepth=17))
    def test_population_cannot_be_shrunk(self):
        p=dict(plan=verify.expected_plan(),rounds=4,slots=16,quanta=[1,16],source_tree='a'*40,host_sha256='b'*64,cpu_affinity=[0,1,2,3]);verify.validate_provenance(p)
        p['plan'].pop()
        with self.assertRaises(ValueError):verify.validate_provenance(p)
    def test_runner_matches_independent_matrix(self):self.assertEqual(json.loads(json.dumps(runner.plan())),verify.expected_plan())
    def test_layout_slot_and_flush_deadlock_guard(self):
        dest=ROOT/'src/SharpLink.Runtime';backup=ROOT/'artifacts/ready-writer-hook'
        originals={n:((backup/n) if (dest/'RpcSession.ReadyWriter.cs').exists() else dest/n).read_text() for n in prepare.HASHES}
        owned=prepare.transform('OwnedFrame.cs',originals['OwnedFrame.cs']);pump=prepare.transform('RpcSession.SendPump.cs',originals['RpcSession.SendPump.cs'])
        self.assertEqual(owned.count('private readonly object? _completionState;'),1)
        self.assertIn('_completionState = ready.Completion',owned)
        self.assertNotIn('ReadySource { get;',owned)
        self.assertIn('Volatile.Read(ref _readyWriterExperiment) is null &&',pump)
        self.assertIn('frame.ReadyCompletion?.Complete(exception);',pump)
    def test_wire_permission_counters_validate_balanced_measurements(self):
        document,case=self.fixture()
        for sample in document['samples']:
            sample['ReadyWriterMetrics'].update(WireCreditBytesObserved=sample['BytesReturned'],ExcessWireCreditBytes=0,IgnoredWireUpdates=0)
        self.assertEqual(len(verify.validate_report(document,'a'*40,case)),16)
        for field in ('WireCreditBytesObserved','ExcessWireCreditBytes','IgnoredWireUpdates'):
            bad=copy.deepcopy(document);bad['samples'][0]['ReadyWriterMetrics'][field]+=1
            with self.assertRaises(ValueError):verify.validate_report(bad,'a'*40,case)
    def test_partial_wire_accounting_cannot_be_hidden(self):
        document,case=self.fixture()
        document['samples'][0]['ReadyWriterMetrics']['ExcessWireCreditBytes']=0
        with self.assertRaises(ValueError):verify.validate_report(document,'a'*40,case)
    def test_drift_and_duplicate_anchors_fail(self):
        with self.assertRaises(ValueError):prepare.transform('OwnedFrame.cs','altered source')
        with self.assertRaises(ValueError):prepare.once('xx','x','y')
if __name__=='__main__':unittest.main()
