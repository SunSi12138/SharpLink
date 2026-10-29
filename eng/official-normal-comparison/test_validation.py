"""Offline adversarial inputs only: no probe, build, RPC, or timing execution."""
import contextlib
import copy
import io
from pathlib import Path
import unittest
import tempfile
import json
import test_protocol
from common import CONTRACT
from identity import IdentityError
from validate import CHECKS, NORMAL_CHECKS, DRAIN_COUNTS, exact_metrics, frozen_plan, phase_census, failure_reason, reason_counts, print_summary, CheckError, validate_host, validate_sample


class ValidationTests(unittest.TestCase):
    def sample(self, arm='dev', phase='normal-measure'):
        args = list(test_protocol.ProtocolTests().sample(arm))
        args[0]['phase'] = phase
        args[0]['strictCleanupExecuted'] = phase == 'strict-cleanup'
        args[0]['correctness'] = {key: True for key in CHECKS} if phase == 'strict-cleanup' else {}
        if phase != 'normal-measure':
            args[0].update(warmupSeconds=0, requestedSeconds=0, operations=0, elapsedTicks=0)
        return args

    def test_generated_identity_every_component_and_hash_encoding_are_enforced(self):
        for arm in ('official','dev'):
            for key,value in [('arm','wrong'),('sourceRevision','wrong'),('api',99),('protocol',3),
                              ('generator','0.0.0'),('abi','wrong'),('rpcAssemblyHash',''),
                              ('rpcAssemblyHash','0'*32),('rpcAssemblyHash','F'*32),
                              ('rpcAssemblyHash','1'*31),('rpcAssemblyHash','1'*33),
                              ('rpcAssemblyHash','g'*32),('rpcAssemblyHash',None)]:
                args=self.sample(arm); args[0]['generatedManifest'][key]=value
                with self.subTest(arm=arm,key=key,value=value),self.assertRaises(ValueError):
                    validate_sample(*args)
            for key in self.sample(arm)[0]['generatedManifest']:
                args=self.sample(arm); del args[0]['generatedManifest'][key]
                with self.assertRaises(ValueError): validate_sample(*args)

    def test_dynamic_channel_requires_exact_type_not_substring(self):
        args=list(test_protocol.ProtocolTests().sample('official','dynamic-intercepted'))
        validate_sample(*args)
        for value in ('Fake.SharpLinkModuleRpcChannel','SharpLink.Client.SharpLinkModuleRpcChannelSuffix',
                      'SharpLinkModuleRpcChannel','SharpLink.Client.SharpLinkClient'):
            bad=copy.deepcopy(args); bad[0]['channelType']=value
            with self.assertRaises(ValueError): validate_sample(*bad)

    def test_strict_candidate_complete_only(self):
        args = self.sample(phase='strict-cleanup')
        validate_sample(*args, phase='strict-cleanup')
        for key in CHECKS:
            for mutation in ('missing', False, 1):
                bad = copy.deepcopy(args)
                if mutation == 'missing': del bad[0]['correctness'][key]
                else: bad[0]['correctness'][key] = mutation
                with self.subTest(key=key, mutation=mutation), self.assertRaises(ValueError):
                    validate_sample(*bad, phase='strict-cleanup')

    def test_official_never_strict_eligible(self):
        with self.assertRaises(ValueError):
            validate_sample(*self.sample('official', 'strict-cleanup'), phase='strict-cleanup')

    def test_every_phase_rejects_wrong_phase(self):
        for phase in ('strict-cleanup', 'normal-validate', 'normal-measure'):
            for wrong in ('strict-cleanup', 'normal-validate', 'normal-measure', 'validate', 'measure'):
                if wrong == phase: continue
                args = self.sample(phase=phase); args[0]['phase'] = wrong
                with self.subTest(phase=phase, wrong=wrong), self.assertRaises(ValueError):
                    validate_sample(*args, phase=phase)

    def test_normal_must_not_hide_executed_cleanup(self):
        for phase in ('normal-validate', 'normal-measure'):
            for arm in ('official', 'dev'):
                args = self.sample(arm, phase); validate_sample(*args, phase=phase)
                for field, value in [('strictCleanupExecuted', True), ('strictCleanupExecuted', 0),
                                     ('correctness', {key:True for key in CHECKS})]:
                    bad = copy.deepcopy(args); bad[0][field] = value
                    with self.subTest(phase=phase, arm=arm, field=field), self.assertRaises(ValueError):
                        validate_sample(*bad, phase=phase)

    def test_normal_proof_every_field_required_exact_boolean(self):
        for phase in ('strict-cleanup', 'normal-validate', 'normal-measure'):
            for key in NORMAL_CHECKS:
                for value in (False, 1, None):
                    args = self.sample(phase=phase); args[0]['normalCorrectness'][key] = value
                    with self.subTest(phase=phase,key=key,value=value), self.assertRaises(ValueError):
                        validate_sample(*args, phase=phase)

    def test_both_drains_all_counters_exact_integer(self):
        for field in ('beforeWarmupDrain', 'afterWorkDrain'):
            for key, expected in DRAIN_COUNTS.items():
                for bad_value in (expected + 1, bool(expected), float(expected)):
                    args = self.sample(); args[0][field][key] = bad_value
                    with self.subTest(field=field,key=key,value=bad_value), self.assertRaises(ValueError):
                        validate_sample(*args)
                args = self.sample(); del args[0][field][key]
                with self.assertRaises(ValueError): validate_sample(*args)

    def test_cold_forbids_timing_and_timing_requires_exact_contract(self):
        for phase in ('strict-cleanup', 'normal-validate'):
            for key in ('warmupSeconds', 'requestedSeconds', 'operations', 'elapsedTicks'):
                args = self.sample(phase=phase); args[0][key] = 1
                with self.assertRaises(ValueError): validate_sample(*args, phase=phase)
        for key, value in [('warmupSeconds', 2.9), ('requestedSeconds', 5.9), ('operations', 0),
                           ('operations', True), ('elapsedTicks', 0), ('schemaVersion', 1)]:
            args = self.sample(); args[0][key] = value
            with self.assertRaises(ValueError): validate_sample(*args)

    def host(self):
        slot = dict(id='static-intercepted-n1-r1-official', arm='official', mode='static-intercepted', items=1, affinity=[0,2])
        prepared, runtime, root = Path('/prepared'), Path('/runtime/dotnet'), Path('/run')
        command = ['taskset','-c','0,2',str(runtime),str(prepared/'official/PhysicalTcpProbe.dll'),
                   slot['mode'],'1','1','3','6',str(root/'runs'/(slot['id']+'.json')),'normal-measure',
                   str(root/'ticks'/(slot['id']+'.json'))]
        host = dict(status='success',exitCode=0,processGroupQuiescent=True,slotId=slot['id'],phase='normal-measure',launchIndex=12,pid=42,command=command)
        return [host,slot,prepared,runtime,root,'normal-measure',12]

    def test_host_identity_order_failure_and_command_are_not_optional(self):
        validate_host(*self.host())
        for field, value in [('status','failed'),('exitCode',1),('exitCode',False),('slotId','wrong'),
                             ('phase','strict-cleanup'),('launchIndex',13),('launchIndex',True),
                             ('pid',0),('pid',None),('command',[]),('processGroupQuiescent',False),
                             ('descendantLeak',True),('timedOut',True)]:
            args = self.host(); args[0][field] = value
            with self.subTest(field=field), self.assertRaises(ValueError): validate_host(*args)
        for index in range(len(self.host()[0]['command'])):
            args = self.host(); args[0]['command'][index] += 'tampered'
            with self.subTest(index=index), self.assertRaises(ValueError): validate_host(*args)

    def test_fixed_check_reason_codes_do_not_include_details(self):
        for message, code in [('Wrong source','source_identity'), ('Wrong executing runtime','runtime_identity'),
                              ('Wrong independently generated manifest','generated_manifest'),
                              ('beforeWarmupDrain incomplete','cleanup_gate'),
                              ('SECRET exception /private/path','validation_check')]:
            self.assertEqual(code, failure_reason(CheckError(message)))

    def test_loaded_census_and_framework_representatives_are_exact(self):
        for field, value in [('loadedAssemblyCount', 0), ('loadedAssemblyCount', True),
                             ('frameworkLocations', ['/runtime/shared/../escaped/10.0.14/a.dll']*2)]:
            args=self.sample(); args[0][field]=value
            with self.subTest(field=field), self.assertRaises(ValueError): validate_sample(*args)

    def test_summary_never_prints_private_fields_or_metrics_when_invalid(self):
        secret = 'SECRET_RAW_TICK_COMMAND_ENV_PATH'
        result = dict(status='Invalid',failures=[secret], inventoryErrors=[secret], rawExactMetrics=secret,
                      cells=[dict(mode=CONTRACT['modes'][0], items=1, completePairs=5,
                                  pairedRatios={'qps':[secret], 'p99Seconds':[secret]})])
        stream = io.StringIO()
        with contextlib.redirect_stdout(stream): print_summary(result)
        out = stream.getvalue()
        self.assertNotIn(secret,out)
        self.assertIn('INVALID no-target-conclusion',out)
        self.assertNotIn('medianQps',out)

    def test_census_missing_records_never_imply_failed_timings(self):
        with tempfile.TemporaryDirectory() as td:
            root=Path(td)
            census=phase_census(root)
            timing=census['phases'][2]
            self.assertEqual(0,timing['attempted']); self.assertEqual(0,timing['failedExit'])
            self.assertEqual(48,timing['missingHost']); self.assertEqual(48,timing['missingUnclassified'])
            (root/'not-attempted.json').write_text(json.dumps([s for s in frozen_plan() if s['phase']=='normal-measure']))
            timing=phase_census(root)['phases'][2]
            self.assertEqual(48,timing['notAttemptedDeclared']); self.assertEqual(0,timing['missingUnclassified'])
            result=dict(status='Invalid',failures=[{}]*48,inventoryErrors=[{}],cells=[],census=phase_census(root))
            stream=io.StringIO()
            with contextlib.redirect_stdout(stream): print_summary(result)
            self.assertIn('invalidTimingSlots=48',stream.getvalue())
            self.assertIn('attempted=0 cleanExit=0 failedExit=0',stream.getvalue())
            self.assertIn('notAttemptedDeclared=48',stream.getvalue())

    def test_census_classifies_observed_exits_and_launch_failures(self):
        with tempfile.TemporaryDirectory() as td:
            root=Path(td); (root/'runs').mkdir()
            slots=[s for s in frozen_plan() if s['phase']=='normal-measure']
            for index,slot in enumerate(slots[:4]):
                host=dict(slotId=slot['id'],phase=slot['phase'],launchIndex=slot['launchIndex'],
                          pid=900+index,exitCode=0,status='success',processGroupQuiescent=True)
                if index==1: host.update(exitCode=1,status='failed')
                if index==2: host.update(pid=None,exitCode=None,status='failed')
                if index==3: host.update(slotId='ARBITRARY_SECRET')
                (root/'runs'/(slot['id']+'.host.json')).write_text(json.dumps(host))
            (root/'runs'/'ARBITRARY_SECRET.host.json').write_text('{}')
            census=phase_census(root); timing=census['phases'][2]
            self.assertEqual((2,1,1,1,1,44),tuple(timing[k] for k in
                             ('attempted','cleanExit','failedExit','launchFailed','invalidHost','missingHost')))
            stream=io.StringIO()
            with contextlib.redirect_stdout(stream):
                print_summary(dict(status='Invalid',failures=[],inventoryErrors=[],cells=[],census=census))
            self.assertNotIn('ARBITRARY_SECRET',stream.getvalue())
            self.assertIn(slots[1]['id'],stream.getvalue())

    def test_not_attempted_declaration_requires_exact_unique_frozen_slots(self):
        with tempfile.TemporaryDirectory() as td:
            root=Path(td); slot=frozen_plan()[12]
            for records in [[dict(slot,id='SECRET')],[slot,slot],[dict(slot,launchIndex=99)],{'bad':'SECRET'}]:
                (root/'not-attempted.json').write_text(json.dumps(records))
                census=phase_census(root)
                self.assertEqual('invalid',census['notAttemptedDeclaration'])
                self.assertTrue(all(r['notAttemptedDeclared']==0 for r in census['phases']))
            (root/'not-attempted.json').write_text(json.dumps([slot]))
            (root/'runs').mkdir()
            (root/'runs'/(slot['id']+'.json')).write_text('{}')
            census=phase_census(root)
            self.assertEqual('invalid',census['notAttemptedDeclaration'])
            self.assertEqual(0,census['phases'][2]['notAttemptedDeclared'])

    def test_failure_reason_summary_never_echoes_exception_paths_or_arbitrary_codes(self):
        secret='PRIVATE_PATH_RAW_EXCEPTION_DO_NOT_PRINT'
        error=IdentityError('framework_loaded_identity',secret)
        self.assertEqual('framework_loaded_identity',failure_reason(error))
        self.assertEqual('missing_input',failure_reason(FileNotFoundError(secret)))
        self.assertEqual('validation_check',failure_reason(ValueError(secret)))
        failures=[dict(error=secret,reasonCode=failure_reason(error)),dict(error=secret,reasonCode=secret)]
        counts=reason_counts(failures,[])
        self.assertEqual({'framework_loaded_identity':1,'validation_check':1},counts)
        counts[secret]=42
        stream=io.StringIO()
        with contextlib.redirect_stdout(stream):
            print_summary(dict(status='Invalid',failures=failures,inventoryErrors=[],cells=[],failureReasonCounts=counts))
        self.assertNotIn(secret,stream.getvalue())
        self.assertIn('framework_loaded_identity=1',stream.getvalue())

    def test_summary_only_ordinary_metrics_when_valid(self):
        ratio = dict(numerator=1, denominator=1, decimal=1.0)
        row = dict(mode=CONTRACT['modes'][0],items=1,completePairs=6,
                   pairedRatios={'qps':[ratio]*6,'p99Seconds':[ratio]*6},
                   qpsMedianPairedRatio=ratio,p99MedianPairedRatio=ratio,
                   qpsTargetGapPercentagePoints=ratio,p99TargetGapPercentagePoints=ratio,
                   observedMedianTargetsReached=False)
        stream=io.StringIO()
        with contextlib.redirect_stdout(stream):
            print_summary(dict(status='DescriptiveOnly',failures=[],inventoryErrors=[],cells=[row],rawExactMetrics='SECRET'))
        out=stream.getvalue()
        self.assertNotIn('SECRET',out); self.assertIn('qpsPairs=[',out); self.assertIn('observedTargets=False',out)


if __name__ == '__main__': unittest.main()
