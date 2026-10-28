"""Offline R1 contract/counterexample fixtures; no benchmark processes execute."""
import copy
import hashlib
import json
from pathlib import Path
import subprocess
import tempfile
import sys
from unittest.mock import patch
from tcp import run_matrix as matrix
import unittest

import paired_931
import paired_r1
import r1_full_validation as validation

ROOT = Path(__file__).resolve().parent
ORIGINAL = '9b11cf3bbd0d7f9ceb126f32f38f3ffec6ff9a41'


def identity(prepared, hashes, arm, mode='dynamic-intercepted', count=100):
    revision = paired_931.BASELINE if arm == 'baseline' else paired_r1.CANDIDATE
    dynamic = mode == 'dynamic-intercepted'
    return dict(runtimeVersion='10.0.13', frameworkLocations=[
        f'/private/shared/{n}/10.0.13/framework.dll' for n in ('Microsoft.NETCore.App', 'Microsoft.AspNetCore.App')],
        processorCount=2, tieredCompilation='default', tieredPgo='default', readyToRun='default',
        mode=mode, itemCount=count, workers=1, warmupSeconds=3, requestedSeconds=6,
        readyConnections=1, connectionPoolMin=1, connectionPoolMax=1, validationFailures=0, operations=2,
        transport='tcp-loopback', requestTimeout='disabled', heartbeatIntervalSeconds=3600,
        serverInterceptors=0, clientInterceptors=1, binaryHashes=hashes[arm], sourceRevision=revision,
        **{m: 1.0 for m in validation.METRICS}, interceptorObserved=True,
        wrapperTypes=['InterceptedAsyncEnumerable[int]'],
        channelType='ModuleRpcChannel' if dynamic else 'StaticChannel',
        streamType='InvokeServerStreamingDeferred' if dynamic else 'StaticStream',
        loadedAssemblies=[dict(name=Path(n).stem, location=str(prepared/arm/n),
            productVersion='3.0.0+'+revision, mvid='12345678-1234-1234-1234-123456789abc') for n in hashes[arm]])


class R1FullTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.prepared = self.root/'prepared'
        self.runtime = Path('/private/dotnet')
        names = ['SharpLink.'+n+'.dll' for n in ('Abstractions', 'Client', 'Runtime', 'Server')]+['PhysicalTcpProbe.dll','ScalarStreamModule.dll']
        self.hashes = {a:{n:hashlib.sha256((a+n).encode()).hexdigest() for n in names} for a in ('baseline','candidate')}

    def test_exact_sources_and_legacy_default_unchanged(self):
        self.assertEqual('ac17c8d8ea0203edbe7b8cf19ddb95eaa24d4d2c', paired_931.CANDIDATE)
        self.assertEqual('94adeae886c4ddfd0c02ec2dcb700cd97e9cad80', paired_r1.CANDIDATE)
        self.assertEqual(paired_r1.CANDIDATE, validation.CANDIDATE)
        self.assertEqual(('10.0.102','10.0.13'), (paired_r1.SDK,paired_r1.RUNTIME))

    def test_original_measurement_and_scalar_source_unchanged(self):
        prefix='eng/physical-ownership-probes/'
        old=subprocess.check_output(['git','show',ORIGINAL+':'+prefix+'tcp/probe/Program.cs'],cwd=ROOT,text=True)
        current=(ROOT/'tcp/r1-full-probe/Program.cs').read_text()
        # Includes setup, Operation, warmup, allocations, every timestamp and all metrics' inputs.
        boundary='string Hash(string path)'
        self.assertEqual(old.split(boundary)[0],current.split(boundary)[0])
        for relative in ('tcp/probe/Program.cs','scalar/Program.cs','scalar/run.py'):
            historical=subprocess.check_output(['git','show',ORIGINAL+':'+prefix+relative],cwd=ROOT)
            self.assertEqual(historical,(ROOT/relative).read_bytes())
        self.assertEqual((ROOT/'tcp/probe/probe.csproj').read_bytes(),(ROOT/'tcp/r1-full-probe/probe.csproj').read_bytes())

    def test_full_product_identity_passes_both_arms_and_zero_allocation(self):
        for arm in self.hashes:
            for mode,count,_ in validation.SCENES:
                row=identity(self.prepared,self.hashes,arm,mode,count)
                row['allocatedBytesPerOperation']=0
                validation.validate_process(row,arm,self.prepared,self.hashes,self.runtime)

    def test_identity_rejects_runtime_source_affinity_path_hash_and_contract_changes(self):
        original=identity(self.prepared,self.hashes,'candidate')
        changes=[('runtimeVersion','10.0.2'),('processorCount',3),('sourceRevision',paired_931.CANDIDATE),
                 ('tieredPgo','0'),('tieredCompilation','0'),('readyToRun','0'),('warmupSeconds',4),
                 ('requestedSeconds',7),('connectionPoolMax',2),('operations',0),('validationFailures',1),
                 ('requestTimeout','enabled'),('clientInterceptors',2),('p99Us',float('nan')),
                 ('binaryHashes',{}),('loadedAssemblies',[]),('interceptorObserved',False),('channelType','StaticChannel')]
        for key,value in changes:
            with self.subTest(key=key):
                row=copy.deepcopy(original);row[key]=value
                with self.assertRaises(ValueError): validation.validate_process(row,'candidate',self.prepared,self.hashes,self.runtime)
        for key,value in [('location','/elsewhere/SharpLink.Client.dll'),('mvid',''),('productVersion','3.0.0+wrong')]:
            row=copy.deepcopy(original);row['loadedAssemblies'][0][key]=value
            with self.assertRaises(ValueError): validation.validate_process(row,'candidate',self.prepared,self.hashes,self.runtime)

    def test_original_48_schedule_and_stop_on_failure_without_replacement(self):
        (self.prepared/'baseline').mkdir(parents=True)
        (self.prepared/'baseline'/'PhysicalTcpProbe.runtimeconfig.json').write_text('{}')
        commands=[]
        def sample(command, log, host, arm, round_number, environment, timeout):
            commands.append((command[5], int(command[6]), arm, round_number))
            self.assertEqual(['taskset','-c','0,1'],command[:3])
            self.assertEqual(['1','3.0','6.0'],command[7:10])
            self.assertEqual('measure',command[-2]);self.assertEqual(99,timeout)
            self.assertNotIn('DOTNET_TieredCompilation',environment)
            self.assertNotIn('DOTNET_JitDisasm',environment)
            Path(command[10]).write_text(json.dumps(dict(operationsPerSecond=1,p50Us=1,p99Us=1,allocatedBytesPerOperation=1)))
            return 0
        args=['run_matrix.py','--prepared',str(self.prepared),'--output',str(self.root/'matrix'),
              '--baseline-revision',paired_931.BASELINE,'--candidate-revision',paired_r1.CANDIDATE,
              '--affinity','0,1','--export-ticks','--rounds','6','--warmup','3','--seconds','6',
              '--workers','1','--items','1,100','--modes','static-intercepted,dynamic-intercepted']
        with patch.object(sys,'argv',args),patch.object(matrix,'execute_sample',side_effect=sample),patch('builtins.print'),patch.dict(matrix.os.environ,{'DOTNET_TieredCompilation':'0','DOTNET_JitDisasm':'do-not-leak'}):
            matrix.main()
        scenes=[('static-intercepted',1),('dynamic-intercepted',1),('static-intercepted',100),('dynamic-intercepted',100)]
        expected=[]
        for r in range(1,7):
            rotation=(r-1)%4
            for mode,count in scenes[rotation:]+scenes[:rotation]:
                for arm in (('baseline','candidate') if r%2 else ('candidate','baseline')):expected.append((mode,count,arm,r))
        self.assertEqual(expected,commands)
        args[args.index('--output')+1]=str(self.root/'failed-matrix')
        with patch.object(sys,'argv',args),patch.object(matrix,'execute_sample',return_value=7) as execute:
            with self.assertRaisesRegex(RuntimeError,'exit 7'):matrix.main()
            self.assertEqual(1,execute.call_count)
        self.assertEqual(1,len(json.loads((self.root/'failed-matrix/matrix-failures.json').read_text())))

    def fixture_matrix(self):
        results=self.root/'results';(results/'runs').mkdir(parents=True);(results/'ticks').mkdir()
        for mode,count,_ in validation.SCENES:
            for round_number in range(1,7):
                for arm in self.hashes:
                    name=f'{mode}-n{count}-c1-r{round_number}-{arm}'
                    (results/'runs'/f'{name}.json').write_text(json.dumps(identity(self.prepared,self.hashes,arm,mode,count)))
                    (results/'runs'/f'{name}.host.json').write_text(json.dumps(dict(status='success',exitCode=0)))
                    (results/'ticks'/f'{name}.json').write_text(json.dumps(dict(stopwatchFrequency=1000000000,operations=2,workers=1,samples=[dict(worker=0,durationTicks=[1000,2000])])))
        return results

    def test_all_48_complete_samples_pass(self):
        result=validation.validate_measurements(self.fixture_matrix(),self.prepared,self.hashes,self.runtime)
        self.assertEqual('Passed',result['status']);self.assertEqual(48,len(result['samples']))

    def test_missing_sample_stays_failed_and_raw_is_not_changed(self):
        results=self.fixture_matrix(); missing=next((results/'ticks').glob('*.json'));missing.unlink()
        before={str(p):p.read_bytes() for p in results.rglob('*.json')}
        report=validation.validate_measurements(results,self.prepared,self.hashes,self.runtime)
        self.assertEqual('Failed',report['status']);self.assertTrue(report['inventoryErrors'])
        for p,value in before.items():self.assertEqual(value,Path(p).read_bytes())
        self.assertFalse(missing.exists())

    def test_failed_shutdown_and_truncated_ticks_cannot_enter_summary(self):
        results=self.fixture_matrix(); host=next((results/'runs').glob('*.host.json'))
        host.write_text('{"status":"failed","exitCode":1}')
        tick=next((results/'ticks').glob('*.json')); row=json.loads(tick.read_text());row['samples'][0]['durationTicks']=[1];tick.write_text(json.dumps(row))
        report=validation.validate_measurements(results,self.prepared,self.hashes,self.runtime)
        self.assertEqual('Failed',report['status'])

    def test_unexpected_extra_sample_rejected_not_selected(self):
        results=self.fixture_matrix();(results/'ticks'/'extra.json').write_text('{}')
        self.assertEqual('Failed',validation.validate_measurements(results,self.prepared,self.hashes,self.runtime)['status'])

    def test_summary_requires_exact_four_cells_six_pairs_and_valid_inventory(self):
        results=self.root/'summary';results.mkdir()
        integrity=dict(evidenceValidation='Passed',inventoryErrors=[],failedSamples=[],incompletePairs=[],completedRawSamples=48)
        rows=[dict(mode=m,items=n,workers=w,pairs=6) for m,n,w in validation.SCENES]
        def save():
            (results/'summary-integrity.json').write_text(json.dumps(integrity));(results/'summary.json').write_text(json.dumps(rows))
        save();validation.validate_summary(results)
        rows[0]['pairs']=5;save()
        with self.assertRaises(ValueError): validation.validate_summary(results)
        rows[0]['pairs']=6;integrity['completedRawSamples']=47;save()
        with self.assertRaises(ValueError): validation.validate_summary(results)


if __name__=='__main__':unittest.main()
