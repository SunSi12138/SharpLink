#!/usr/bin/env python3
"""The paired native companion cannot substitute another source, workload or tier."""
import copy
import unittest

from summarize import ARMS, require_native_pair, verify_registration_companion


class RegistrationCompanionTests(unittest.TestCase):
    def setUp(self):
        settings = {'DOTNET_TieredPGO': '1', 'DOTNET_TC_CallCountThreshold': '30'}
        runtime = {'RuntimeVersion': '10.0.2', 'Architecture': 'X64'}
        self.primary = {'Modes': {'pgo': settings}, 'Iterations': 5000,
                        'ProbeFiles': {'probe.cs': 'same authored input'}, 'Arms': {}}
        for arm in ARMS:
            self.primary['Arms'][arm] = {
                'Binary': f'/build/{arm}/probe.dll', 'Sources': {'src/owner.cs': arm},
                'Binaries': {'runtime.dll': arm}, 'AuxiliaryBinaries': {'service.dll': 'same'},
                'RuntimeBinding': runtime, 'RuntimeFiles': {'host': 'same installed runtime'},
                'Exits': {'metadata': 0, 'fullopts': 0, 'tier0': 0, 'tier1': 0, 'pgo': 0}}
        self.proof = copy.deepcopy(self.primary)
        self.proof.update(CaptureKind='registration-pgo-companion',
                          Filter='SharpLink.Client.PendingRequestTable:OnRegistered')
        for binding in self.proof['Arms'].values():
            binding['Exits'] = {'metadata': 0, 'pgo': 0}
        cohort = {'ClientReady': 2, 'ServerPhysical': 2, 'AuthenticationCalls': 2,
                  'SameConnectionObjects': True, 'SameSessionObjects': True,
                  'AllReady': True, 'AuthenticatedTls': True, 'TlsProtocols': ['Tls13', 'Tls13']}
        network = {phase: cohort for phase in ['Before', 'PreUpdate', 'Resumed', 'After']}
        network.update(RpcCalls=30000, HeldProxyAcrossPhases=True, MaintenanceEnabled=True,
                       UpdateStage='Succeeded', GenerationAdvanced=True, Result=31,
                       OneWayCallsObserved=600,
                       ShapeMeasurements=[{'Name': shape, 'ProcessManagedAllocatedBytes': block}
                                          for shape in ['client-stream', 'server-stream', 'duplex-stream',
                                                        'one-way-client-stream', 'health']
                                          for block in range(6)])
        self.samples = {arm: {'RuntimeBinding': runtime, 'Network': copy.deepcopy(network)} for arm in ARMS}

    def verify(self):
        return verify_registration_companion(self.proof, self.primary, self.samples, '10.0.2')

    def test_same_context_pair_retains_every_supplemental_sample(self):
        result = self.verify()
        self.assertEqual(result['Launches'], 2)
        self.assertEqual(result['Samples'], self.samples)
        self.assertEqual(len(result['Samples']['baseline']['Network']['ShapeMeasurements']), 30)

    def test_changed_source_binary_runtime_or_settings_is_rejected(self):
        original = copy.deepcopy(self.proof)
        for key in ['Sources', 'Binaries', 'AuxiliaryBinaries', 'RuntimeFiles', 'RuntimeBinding']:
            with self.subTest(key=key):
                self.proof = copy.deepcopy(original)
                self.proof['Arms']['baseline'][key] = {'changed': True}
                with self.assertRaises(AssertionError):
                    self.verify()
        self.proof = copy.deepcopy(original)
        self.proof['Modes']['pgo']['DOTNET_TC_CallCountThreshold'] = '1'
        with self.assertRaises(AssertionError):
            self.verify()

    def test_failed_launch_or_changed_workload_is_rejected(self):
        self.proof['Arms']['baseline']['Exits']['pgo'] = 1
        with self.assertRaises(AssertionError):
            self.verify()
        self.proof['Arms']['baseline']['Exits']['pgo'] = 0
        self.samples['baseline']['Network']['RpcCalls'] = 29999
        with self.assertRaises(AssertionError):
            self.verify()

    def test_both_complete_tier1_bodies_are_required_in_the_same_context(self):
        def row(arm, **changes):
            return dict({'Kind': 'shapes', 'Arm': arm, 'Target': 'OnRegistered',
                         'Mode': 'pgo', 'Tier': 'Tier1', 'CompleteNativeBody': True}, **changes)
        pair = [row(arm) for arm in ARMS]
        self.assertEqual(set(require_native_pair(pair, 'shapes', 'OnRegistered', 'pgo', 'Tier1')), set(ARMS))
        for replacement in [None, row('baseline', Tier='Instrumented Tier0'),
                            row('baseline', Kind='retry'), row('baseline', CompleteNativeBody=False)]:
            with self.subTest(replacement=replacement):
                rows = [pair[1]] + ([] if replacement is None else [replacement])
                with self.assertRaisesRegex(AssertionError, 'Missing complete native coverage'):
                    require_native_pair(rows, 'shapes', 'OnRegistered', 'pgo', 'Tier1')


if __name__ == '__main__':
    unittest.main()
