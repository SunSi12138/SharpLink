"""Offline WU1 scope guards: preserve the successful CF2 harness, not its candidate."""
import json
from pathlib import Path
import unittest
from common import ROOT, CONTRACT, schedule, gate_schedule

class CampaignReuseTests(unittest.TestCase):
    def test_frozen_old_campaign_is_not_superseded_or_reused(self):
        previous=json.loads((ROOT.parent/'connection-fanout-comparison/protocol.json').read_text())
        self.assertEqual(previous['campaign'],'connectionfanout-base3d76-48tcp-CF2')
        self.assertEqual(previous['candidateSource'],'279fdb2a93736eb25bec948c3acd65f2ed4830d9')
        self.assertEqual(CONTRACT['campaign'],'windowupdate-base3d76-48tcp-WU1')
        self.assertEqual(CONTRACT['candidateSource'],'0e1fe885d24044fae759c1c57b27ac44e3a0db4e')
        self.assertNotIn('supersedesCampaign',CONTRACT)
        for key in ('runtime','sdk','modes','items','pairsPerCell','timedProcesses','warmupSeconds',
                    'measureSeconds','workers','connectionPoolMin','connectionPoolMax','heartbeatSeconds',
                    'requestTimeout','clientInterceptors','serverInterceptors','qpsTargetRatio','p99TargetRatio',
                    'strictProcesses','normalProcesses','processTimeoutSeconds','cleanupGuardSeconds',
                    'dependencyPolicy','baselineSource','baselineTree','baselineApi','candidateApi'):
            self.assertEqual(CONTRACT[key],previous[key],key)

    def test_no_measurement_or_cold_fixture_change(self):
        old=ROOT.parent/'connection-fanout-comparison/fixture';new=ROOT/'fixture'
        self.assertEqual({str(p.relative_to(old)) for p in old.rglob('*') if p.is_file()},
                         {str(p.relative_to(new)) for p in new.rglob('*') if p.is_file()})
        for p in old.rglob('*'):
            if not p.is_file():continue
            expected=p.read_text()
            if p.name=='ArmAdapter.cs':
                expected=expected.replace('279fdb2a93736eb25bec948c3acd65f2ed4830d9','0e1fe885d24044fae759c1c57b27ac44e3a0db4e').replace('279fdb2a','0e1fe885')
            self.assertEqual((new/p.relative_to(old)).read_text(),expected,str(p))

    def test_cpu_allocation_summary_and_acceptance_code_reused(self):
        old=(ROOT.parent/'connection-fanout-comparison/validate.py').read_text()
        expected=old.replace('connection-fanout-comparison','window-update-comparison').replace('candidate-279fdb2a','candidate-0e1fe885').replace('CONNECTION_FANOUT_COMPARISON','WINDOW_UPDATE_COMPARISON')
        self.assertEqual((ROOT/'validate.py').read_text(),expected)

    def test_full_finite_schedule_kept(self):
        self.assertEqual(len(gate_schedule('strict-cleanup')),8)
        self.assertEqual(len(gate_schedule('normal-validate')),8)
        timed=schedule();self.assertEqual(len(timed),32)
        for mode in CONTRACT['modes']:
            for n in CONTRACT['items']:
                arms=[x['arm'] for x in timed if x['mode']==mode and x['items']==n]
                self.assertEqual(arms,['baseline','candidate','candidate','baseline']*2)

    def test_actual_uid_counts_bound_to_protocol(self):
        from unit_gate.provenance import read_corpus
        raw,ordinary,_=read_corpus(ROOT/'unit_gate')
        self.assertEqual((len(raw),len(ordinary)),(CONTRACT['ordinaryUnitGate']['rawUids'],CONTRACT['ordinaryUnitGate']['ordinaryUids']))
        self.assertEqual((len(raw),len(ordinary)),(2349,2344))

if __name__=='__main__':unittest.main()
