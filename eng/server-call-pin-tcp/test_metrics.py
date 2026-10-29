"""Unified pin offline recorded CPU/allocation metric checks; no product execution."""
import contextlib
import copy
from fractions import Fraction
import io
import math
import unittest
from unittest.mock import patch
import test_protocol
from common import ARMS, CONTRACT, median, rational, write
from validate import RESOURCE_METRICS, resource_metrics, resource_pair, validate_sample, print_summary, exact_metrics


class ResourceMetricTests(unittest.TestCase):
    def test_decimal_fraction_and_exact_ratio_median(self):
        baseline=resource_metrics(dict(cpuUsPerOperation=0.1,allocatedBytesPerOperation=3),'baseline')
        candidate=resource_metrics(dict(cpuUsPerOperation=0.03,allocatedBytesPerOperation=1),'candidate')
        self.assertEqual(Fraction(1,10),baseline['cpuUsPerOperation'])
        ratios=resource_pair(baseline,candidate)
        self.assertEqual(Fraction(3,10),ratios['cpuUsPerOperation'])
        self.assertEqual(Fraction(1,3),ratios['allocatedBytesPerOperation'])
        self.assertEqual(Fraction(5,12),median([Fraction(1,3),Fraction(1,2),Fraction(7,8),Fraction(1,4)]))
        self.assertEqual((11,10),tuple(CONTRACT['qpsTargetRatio']))
        self.assertEqual((4,5),tuple(CONTRACT['p99TargetRatio']))

    def test_candidate_zero_valid_baseline_zero_forbidden(self):
        baseline=resource_metrics({key:1 for key in RESOURCE_METRICS},'baseline')
        candidate=resource_metrics({key:0 for key in RESOURCE_METRICS},'candidate')
        self.assertTrue(all(value==0 for value in resource_pair(baseline,candidate).values()))
        for key in RESOURCE_METRICS:
            data={field:1 for field in RESOURCE_METRICS};data[key]=0
            with self.assertRaises(ValueError):resource_metrics(data,'baseline')
            data[key]=-0.0
            with self.assertRaises(ValueError):resource_metrics(data,'baseline')

    def test_missing_type_nonfinite_negative_values_fail_closed(self):
        for arm in ARMS:
            for key in RESOURCE_METRICS:
                valid={field:1 for field in RESOURCE_METRICS}
                missing=valid.copy();del missing[key]
                with self.subTest(arm=arm,key=key,missing=True),self.assertRaises(ValueError):resource_metrics(missing,arm)
                for bad in (None,True,False,'1','NaN',[],{},-1,-0.01,float('nan'),float('inf'),-float('inf'),10**400):
                    data=valid.copy();data[key]=bad
                    with self.subTest(arm=arm,key=key,bad=repr(bad)),self.assertRaises(ValueError):resource_metrics(data,arm)
        with self.assertRaises(ValueError):resource_metrics({key:1 for key in RESOURCE_METRICS},'unknown')

    def test_pair_denominator_and_display_overflow_fail_closed(self):
        valid={key:Fraction(1) for key in RESOURCE_METRICS}
        for key in RESOURCE_METRICS:
            for bad in (Fraction(0),Fraction(-1),0.1,True,None):
                base=valid.copy();base[key]=bad
                with self.assertRaises(ValueError):resource_pair(base,valid)
            for bad in (Fraction(-1),0.1,True,None):
                candidate=valid.copy();candidate[key]=bad
                with self.assertRaises(ValueError):resource_pair(valid,candidate)
        tiny=resource_metrics({key:5e-324 for key in RESOURCE_METRICS},'baseline')
        huge=resource_metrics({key:1e308 for key in RESOURCE_METRICS},'candidate')
        with self.assertRaises(ValueError):resource_pair(tiny,huge)
        with self.assertRaises(ValueError):resource_pair(huge,tiny)

    def test_timed_sample_requires_metrics_without_changing_cold_schema(self):
        for arm in ARMS:
            args=list(test_protocol.ProtocolTests().sample(arm))
            validate_sample(*args)
            for key in RESOURCE_METRICS:
                bad=copy.deepcopy(args);del bad[0][key]
                with self.assertRaises(ValueError):validate_sample(*bad)
                bad=copy.deepcopy(args);bad[0][key]=float('nan')
                with self.assertRaises(ValueError):validate_sample(*bad)
                bad=copy.deepcopy(args);bad[0][key]=0
                if arm=='baseline':
                    with self.assertRaises(ValueError):validate_sample(*bad)
                else:validate_sample(*bad)

    def test_full_synthetic_campaign_four_cells_six_pairs_exact_costs(self):
        original=test_protocol.ProtocolTests.sample
        captured=[]
        def sample(case,arm='baseline',mode='static-intercepted'):
            result=original(case,arm,mode)
            result[0].update(cpuUsPerOperation=0.1 if arm=='baseline' else 0.03,
                             allocatedBytesPerOperation=3 if arm=='baseline' else 1)
            return result
        def capture(path,value):
            if path.name=='summary.json':captured.append(copy.deepcopy(value))
            write(path,value)
        with patch.object(test_protocol.ProtocolTests,'sample',sample),patch('validate.write',side_effect=capture):
            test_protocol.ProtocolTests().test_full_synthetic_campaign_and_failure_suppression()
        valid=captured[0]
        self.assertEqual('DescriptiveOnly',valid['status'])
        self.assertEqual(4,len(valid['cells']))
        for row in valid['cells']:
            for key,base,candidate,ratio in [('cpuUsPerOperation',Fraction(1,10),Fraction(3,100),Fraction(3,10)),
                                            ('allocatedBytesPerOperation',Fraction(3),Fraction(1),Fraction(1,3))]:
                series=row['resourceMetrics'][key]
                self.assertEqual([rational(base)]*6,series['baseline'])
                self.assertEqual([rational(candidate)]*6,series['candidate'])
                self.assertEqual([rational(ratio)]*6,series['pairedRatios'])
                self.assertEqual(rational(ratio),series['medianPairedRatio'])
                self.assertEqual(rational(base),series['baselineMedian'])
                self.assertEqual(rational(candidate),series['candidateMedian'])
        stream=io.StringIO()
        with contextlib.redirect_stdout(stream):print_summary(valid)
        output=stream.getvalue()
        self.assertEqual(4,output.count('cpuUsPerOperation baseline=['))
        self.assertEqual(4,output.count('allocatedBytesPerOperation baseline=['))
        self.assertIn('client+server+runtime',output)
        self.assertNotIn('rawExactMetrics',output)
        self.assertNotIn('numerator',output)
        for result in captured[1:]:
            if result['status']=='Invalid':
                self.assertFalse(any('resourceMetrics' in row for row in result['cells']))
                stream=io.StringIO()
                with contextlib.redirect_stdout(stream):print_summary(result)
                self.assertNotIn('cpuUsPerOperation baseline=[',stream.getvalue())
                self.assertNotIn('allocatedBytesPerOperation baseline=[',stream.getvalue())


if __name__=='__main__':unittest.main()
