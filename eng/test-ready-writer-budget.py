#!/usr/bin/env python3
import copy, importlib.util, json, pathlib, unittest
ROOT=pathlib.Path(__file__).resolve().parents[1]
def load(name):
    spec=importlib.util.spec_from_file_location(name,ROOT/'eng'/f'{name}.py');m=importlib.util.module_from_spec(spec);spec.loader.exec_module(m);return m
verify=load('verify-ready-writer-budget');runner=load('run-ready-writer-budget');existing=load('test-ready-writer')
class Guards(unittest.TestCase):
    def fixture(self):
        doc,case=existing.Guards().fixture()
        doc['metadata'].update(preparedByteBudget=8192,allocationDiagnostic=False)
        for row in doc['samples']:
            row['ReadyWriterMetrics'].update(PreparedByteBudgetPerStream=8192,AllocationDiagnostic=0,
                MaximumObservedPacketBytes=33,MaximumQueuedBytesPerStream=528,SumOfStreamQueuedBytePeaks=128*528,RemainingQueuedBytes=0)
        return doc,case+[8192]
    def reject(self,mutate):
        d,c=self.fixture();mutate(d)
        with self.assertRaises((ValueError,KeyError)):verify.validate_document(d,'a'*40,c)
    def test_diagnostic_timings_rejected(self):
        doc, case = self.fixture()
        doc["metadata"]["DiagnosticCapture"] = True
        with self.assertRaises(ValueError): verify.validate_document(doc, "a"*40, case)

    def test_valid_budget_report(self):
        d,c=self.fixture();self.assertEqual(len(verify.validate_document(d,'a'*40,c)),16)
    def test_independently_defined_full_plan(self):
        self.assertEqual(runner.plan(),verify.expected_plan());self.assertEqual(len(runner.plan()),48)
    def test_byte_cap_and_unreleased_bytes(self):
        self.reject(lambda d:d['samples'][0]['ReadyWriterMetrics'].update(MaximumQueuedBytesPerStream=8193))
        self.reject(lambda d:d['samples'][0]['ReadyWriterMetrics'].update(RemainingQueuedBytes=1))
    def test_disabled_diagnostic_is_not_zero_measurement(self):
        self.reject(lambda d:d['samples'][0]['ReadyWriterMetrics'].update(ProducerPreparationAllocatedBytes=0))
    def test_instrumented_timing_rejected(self):
        self.reject(lambda d:d['metadata'].update(allocationDiagnostic=True))
    def test_budget_mismatch_rejected(self):
        self.reject(lambda d:d['metadata'].update(preparedByteBudget=0))
        self.reject(lambda d:d['samples'][0]['ReadyWriterMetrics'].update(PreparedByteBudgetPerStream=0))
    def test_oversized_bound_is_one_packet(self):
        d,c=self.fixture()
        for row in d['samples']:
            row['ReadyWriterMetrics'].update(MaximumObservedPacketBytes=16384,MaximumQueuedBytesPerStream=16384,SumOfStreamQueuedBytePeaks=128*16384)
        self.assertEqual(len(verify.validate_document(d,'a'*40,c)),16)
        d['samples'][0]['ReadyWriterMetrics']['MaximumQueuedBytesPerStream']=16385
        with self.assertRaises(ValueError):verify.validate_document(d,'a'*40,c)
    def test_frame_bound_not_relaxed(self):
        self.reject(lambda d:d['samples'][0]['ReadyWriterMetrics'].update(MaximumRingDepth=17))
    def test_exits_and_population_not_ignored(self):
        # The complete verifier must load all 48 .exit and .json pairs, not glob a subset.
        code=(ROOT/'eng/verify-ready-writer-budget.py').read_text()
        self.assertIn("exit['code']!=0",code);self.assertIn('enumerate(expected_plan())',code)
    def test_no_window_or_pool_knob_added(self):
        source=(ROOT/'test/SharpLink.Benchmarks/ReadyWriterCoordinator.cs').read_text()
        self.assertNotIn('MaxPooledWriters',source)
        self.assertIn('packetBytes <= PreparedByteBudget - QueuedBytes',source)
if __name__=='__main__':unittest.main()
