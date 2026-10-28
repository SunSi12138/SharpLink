import collections
import importlib.util
import json
from pathlib import Path
import sys
import unittest
HERE=Path(__file__).resolve().parent
sys.path.insert(0,str(HERE))
from contract import SOURCES, TREES, schedule, validate_complete_summary
from verify_request import validate_request, BRANCH, MESSAGE, REQUEST

class ContractTests(unittest.TestCase):
    def test_exact_schedule(self):
        rows=schedule();self.assertEqual(len(rows),48)
        counts=collections.Counter((r['contrast'],r['deadline'],r['variant']) for r in rows)
        self.assertEqual(set(counts.values()),{6});self.assertEqual(len(counts),8)
        for i in range(0,len(rows),2):
            left,right=rows[i:i+2]
            self.assertEqual((left['contrast'],left['deadline'],left['round']),(right['contrast'],right['deadline'],right['round']))
            self.assertEqual((left['variant'],right['variant']),('baseline','candidate') if left['round']%2 else ('candidate','baseline'))
        self.assertEqual(len({(r['contrast'],r['deadline'],r['sample']) for r in rows}),48)
    def test_timed_loops_and_interceptor_unchanged(self):
        original=(HERE.parent/'tcp/wakeup-probe/Program.cs').read_text();current=(HERE/'timed/Program.cs').read_text()
        for start,end in [('async ValueTask Operation()', 'var first='), ('async Task Warmup()', 'string Hash('), ('sealed class PassThrough', None)]:
            def piece(s):
                a=s.index(start);return s[a:] if end is None else s[a:s.index(end,a)]
            self.assertEqual(piece(original),piece(current))
        self.assertNotIn('ProofInterceptor',current)
        self.assertNotIn('_frameworkTasks',current)
        self.assertEqual((HERE/'timed/ExactRank.cs').read_bytes(),(HERE.parent/'tcp/wakeup-probe/ExactRank.cs').read_bytes())
    def test_contract_diff_is_only_deadline(self):
        off=(HERE/'module-off/Contract.cs').read_text();on=(HERE/'module-on/Contract.cs').read_text()
        self.assertEqual(on.replace('    [SharpLink.Sdk.Timeout(30)]\n',''),off)
        self.assertEqual(off,(HERE.parent/'tcp/module/Contract.cs').read_text())
    def test_cold_is_untimed_and_natural(self):
        text=(HERE/'cold/Program.cs').read_text()
        for forbidden in ['Stopwatch','SetMinThreads','SetMaxThreads','GC.GetTotalAllocatedBytes','Thread.Sleep','ThreadPool.']:
            self.assertNotIn(forbidden,text)
        self.assertIn('ServerStreamBootstrapCompletion',text);self.assertIn('StartServerStreamingInvokerAsync',text)
        self.assertIn('remainingBootstrapOwners != 0',text)
    def summary(self):
        metrics={name:{'pairedRatios':[1]*6} for name in ('operationsPerSecond','p50Us','p99Us','p999Us','allocatedBytesPerOperation','cpuUsPerOperation')}
        return [dict(mode='static-intercepted',items=1,workers=1,pairs=6,metrics=metrics)],dict(evidenceValidation='Passed',inventoryErrors=[],rawSamples=12,completedRawSamples=12,failedSamples=[],incompletePairs=[],bootstrapReplicates=20000,bootstrapSeed=529)
    def test_complete_summary(self):validate_complete_summary(*self.summary())
    def test_summary_missing_integrity_rejected(self):
        summary,integrity=self.summary();del integrity['failedSamples']
        with self.assertRaises(ValueError):validate_complete_summary(summary,integrity)
    def test_summary_partial_pairs_rejected(self):
        summary,integrity=self.summary();integrity['incompletePairs']=[{'round':6}]
        with self.assertRaises(ValueError):validate_complete_summary(summary,integrity)
    def test_summary_failed_samples_rejected(self):
        summary,integrity=self.summary();integrity['failedSamples']=[{'sample':'failed'}]
        with self.assertRaises(ValueError):validate_complete_summary(summary,integrity)
    def test_summary_missing_ratio_rejected(self):
        summary,integrity=self.summary();summary[0]['metrics']['p99Us']['pairedRatios'].pop()
        with self.assertRaises(ValueError):validate_complete_summary(summary,integrity)
    def request(self):
        return dict(state='authorized',campaign='bootstrap-supervision-48tcp-v1',reviewedToolCommit='a'*40,reviewedToolTree='b'*40,sources=dict(SOURCES),trees=dict(TREES))
    def validate(self,data=None,**overrides):
        args=dict(environment=dict(GITHUB_EVENT_NAME='push',GITHUB_REF=BRANCH,GITHUB_RUN_ATTEMPT='1',GITHUB_REPOSITORY='SunSi12138/SharpLink',GITHUB_SHA='c'*40),head='c'*40,parent='a'*40,parent_tree='b'*40,changed=[REQUEST],message=MESSAGE,clean=True)
        args.update(overrides);validate_request(self.request() if data is None else data,**args)
    def test_valid_request(self): self.validate()
    def test_dormant_request_rejected(self):
        with self.assertRaises(ValueError):self.validate(json.loads((HERE/'REQUEST.json').read_text()))
    def test_extra_file_rejected(self):
        with self.assertRaises(ValueError):self.validate(changed=[REQUEST,'src/changed.cs'])
    def test_wrong_source_rejected(self):
        data=self.request();data['sources']['a']='0'*40
        with self.assertRaises(ValueError):self.validate(data)
    def test_wrong_tree_rejected(self):
        data=self.request();data['trees']['b']='0'*40
        with self.assertRaises(ValueError):self.validate(data)
    def test_rerun_rejected(self):
        env=dict(GITHUB_EVENT_NAME='push',GITHUB_REF=BRANCH,GITHUB_RUN_ATTEMPT='2',GITHUB_REPOSITORY='SunSi12138/SharpLink',GITHUB_SHA='c'*40)
        with self.assertRaises(ValueError):self.validate(environment=env)
    def test_wrong_parent_rejected(self):
        with self.assertRaises(ValueError):self.validate(parent='d'*40)
    def test_dirty_tool_rejected(self):
        with self.assertRaises(ValueError):self.validate(clean=False)

if __name__=='__main__':unittest.main()
