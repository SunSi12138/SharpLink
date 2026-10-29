"""Offline exact N3 timing contract reuse, with both-arm preflight and explicit scope."""
import unittest
from common import ROOT, CONTRACT, schedule, gate_schedule
from unit_gate.provenance import read_corpus

class CampaignTests(unittest.TestCase):
    def test_six_pair_n3_timing_contract(self):
        self.assertEqual((6,48,64),(CONTRACT['pairsPerCell'],CONTRACT['timedProcesses'],CONTRACT['totalProcesses']))
        self.assertEqual((3,6,1),(CONTRACT['warmupSeconds'],CONTRACT['measureSeconds'],CONTRACT['workers']))
        self.assertEqual([11,10],CONTRACT['qpsTargetRatio'])
        self.assertEqual([4,5],CONTRACT['p99TargetRatio'])
        self.assertEqual(48,len(schedule()))
        for mode in CONTRACT['modes']:
            for n in CONTRACT['items']:
                self.assertEqual(['baseline','candidate','candidate','baseline']*3,
                    [s['arm'] for s in schedule() if (s['mode'],s['items'])==(mode,n)])
        for phase in ('strict-cleanup','normal-validate'):self.assertEqual(8,len(gate_schedule(phase)))

    def test_exact_both_arm_corpora(self):
        for arm,counts in [('baseline',(2316,2311)),('candidate',(2331,2326))]:
            raw,ordinary,workers=read_corpus(ROOT/'unit_gate',arm)
            self.assertEqual(counts,(len(raw),len(ordinary)))
            self.assertEqual(5,len(workers))
            self.assertEqual(counts,(CONTRACT['ordinaryUnitGate']['arms'][arm]['rawUids'],
                                    CONTRACT['ordinaryUnitGate']['arms'][arm]['ordinaryUids']))

    def test_scope_preserves_known_limitations(self):
        for value in ('adverse legacy-fallback','no fallback TCP','GenerationControl','AOT','2.0.3'):
            self.assertIn(value,CONTRACT['releaseBoundary'])
        self.assertEqual(['static-intercepted','dynamic-intercepted'],CONTRACT['modes'])
        self.assertEqual([1,100],CONTRACT['items'])
        self.assertNotIn('supersedesCampaign',CONTRACT)

if __name__=='__main__':unittest.main()
