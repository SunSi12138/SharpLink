"""Pure offline controls for bounded diagnostic names and unmatched endpoint counts."""
import copy
import unittest
import summary
import test_driver as driver


class MethodSummaryTests(unittest.TestCase):
    def sample(self):
        value=driver.SummaryTests().sample()
        value['managedThreadTimeSamples']['runtime-other']=3
        value['methodSamples'].update(leafMethods=[{'method':'System.Threading.Monitor.Enter','samples':3}],
                                      frameworkLeafWithSharpLinkAncestorSamples=2)
        return value

    def test_valid_method_counts_not_cpu_and_all_sample_buckets_preserved(self):
        value=self.sample()
        value['managedThreadTimeSamples']['other-managed']=6
        value['methodSamples'].update(nonAllowlistedLeafSamples=2,rejectedNameSamples=1,omittedAllowedSamples=3)
        value['externalThreadSamples']=17
        text='\n'.join(summary.render(value))
        self.assertIn('method=System.Threading.Monitor.Enter samples=3',text)
        self.assertIn('frameworkLeafWithSharpLinkAncestorSamples=2',text)
        self.assertIn('sampled-thread-time-not-CPU',text)
        self.assertIn('omittedAllowedSamples=3',text)
        self.assertIn('externalThreadSamples=17',text)
        self.assertNotIn('cpuPercent',text)

    def test_max_twelve_names_bounded_and_no_raw_extras(self):
        value=self.sample()
        names=[('System.'+str(chr(65+i))*145+'.M') for i in range(12)]
        value['methodSamples'].update(leafMethods=[{'method':name,'samples':1} for name in names],
                                      frameworkLeafWithSharpLinkAncestorSamples=0)
        value['managedThreadTimeSamples']['runtime-other']=12
        value.update(rawStack='SECRET_STACK',path='SECRET_PATH',address='0x1234')
        text='\n'.join(summary.render(value))
        self.assertEqual(text.count('METHOD_SAMPLE method='),12)
        self.assertLess(len(text),8192)
        self.assertNotIn('SECRET',text)
        self.assertNotIn('0x1234',text)
        value['methodSamples']['leafMethods'].append({'method':'System.Z.M','samples':1})
        with self.assertRaises(ValueError):summary.render(value)

    def test_invalid_names_types_counts_and_order_fail_closed(self):
        for name in ('Private.Secret.Method','Systematic.Foo.Run','System.Foo.Run(secret)',
                     'System.Foo.Run\nLEAK','System.Foo.Run /tmp/secret','System.Foo<Secret>.Run',
                     'System.Foo[Secret].Run','System._0x1234_.Run','System.'+'a'*160+'.Run',
                     'System.中文.Run','System.Foo::Run',None,42,{},[]):
            value=self.sample();value['methodSamples']['leafMethods'][0]['method']=name
            with self.subTest(name=name),self.assertRaises(ValueError):summary.render(value)
        for count in (0,-1,True,'3',float('nan'),2**64,10**400):
            value=self.sample();value['methodSamples']['leafMethods'][0]['samples']=count
            with self.subTest(count=count),self.assertRaises(ValueError):summary.render(value)
        for rows in ([{'method':'System.B.M','samples':1},{'method':'System.A.M','samples':2}],
                     [{'method':'System.A.M','samples':1},{'method':'System.A.M','samples':2}]):
            value=self.sample();value['methodSamples']['leafMethods']=rows
            with self.assertRaises(ValueError):summary.render(value)
        for key in summary.METHOD_KEYS:
            value=self.sample();del value['methodSamples'][key]
            with self.assertRaises(ValueError):summary.render(value)

    def test_method_accounting_and_framework_ancestor_bound(self):
        for change in ({'omittedAllowedSamples':1},{'frameworkLeafWithSharpLinkAncestorSamples':4},
                       {'windowValid':False},{'nonAllowlistedLeafSamples':True}):
            value=self.sample();value['methodSamples'].update(change)
            with self.assertRaises(ValueError):summary.render(value)

    def test_invalid_window_withholds_methods_and_endpoint_regions_unavailable(self):
        value=driver.SummaryTests().sample()
        value.update(status='Incomplete',failures=['marker-cardinality','unmatched-events'],unmatchedEvents=2)
        value['methodSamples']['windowValid']=False
        value['unmatchedBreakdown']['wait:stop:unavailable']=2
        text='\n'.join(summary.render(value))
        self.assertIn('DIAGNOSTIC_WINDOW unavailable',text)
        self.assertIn('missing-stop-observed-unavailable=2',text)
        self.assertNotIn('METHOD_SAMPLE',text)
        self.assertNotIn('MANAGED_THREAD_TIME_SAMPLES',text)
        value['methodSamples']['leafMethods']=[{'method':'System.A.M','samples':1}]
        with self.assertRaises(ValueError):summary.render(value)

    def test_partial_window_preserves_known_unknown_and_outside_unmatched_failure(self):
        value=self.sample()
        value.update(status='Incomplete',coverage='Partial',failures=['unknown-coverage','unmatched-events','lost-or-truncated'],
                     unknownStacks=1,lostEvents=2,unmatchedEvents=1)
        value['managedThreadTimeSamples']['unknown']=1
        value['methodSamples']['unavailableLeafSamples']=1
        value['unmatchedBreakdown']['wait:stop:after']=1
        text='\n'.join(summary.render(value))
        self.assertIn('status=Incomplete',text)
        self.assertIn('unknownStacks=1',text)
        self.assertIn('lostEvents=2',text)
        self.assertIn('method=System.Threading.Monitor.Enter samples=3',text)
        self.assertIn('missing-stop-observed-after=1',text)
        self.assertIn('unmatched-events',text)

    def test_unmatched_schema_total_and_region_validation(self):
        base=self.sample();base.update(status='Incomplete',failures=['unmatched-events'],unmatchedEvents=1)
        base['unmatchedBreakdown']['wait:stop:inside']=1
        for mutation in ('extra','missing','wrong-total','unavailable','bool'):
            value=copy.deepcopy(base)
            if mutation=='extra':value['unmatchedBreakdown']['SECRET']=1
            elif mutation=='missing':del value['unmatchedBreakdown']['gc:start:before']
            elif mutation=='wrong-total':value['unmatchedEvents']=2
            elif mutation=='unavailable':
                value['unmatchedBreakdown']['wait:stop:inside']=0
                value['unmatchedBreakdown']['wait:stop:unavailable']=1
            else:value['unmatchedBreakdown']['wait:stop:inside']=True
            with self.subTest(mutation=mutation),self.assertRaises(ValueError):summary.render(value)

    def test_parse_failure_never_reads_or_prints_new_zero_metrics(self):
        value=self.sample();value.update(status='Incomplete',failures=['parse-failure'],methodSamples='SECRET',unmatchedBreakdown='SECRET')
        self.assertEqual(summary.render(value),['DIAGNOSTIC_ANALYSIS status=Incomplete reasons=parse-failure'])
        value['status']='Complete'
        with self.assertRaises(ValueError):summary.render(value)


if __name__=='__main__':unittest.main()
