import copy
from fractions import Fraction
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
from unit_gate.record import expected_record, REQUIRED_ASSEMBLIES
from identity import FRAMEWORK, FRAMEWORK_PRODUCT_VERSION, IdentityError
from common import ARMS, CONTRACT, ROOT, median, nearest_rank, schedule, select_cpus, strict_json, inventory, write, source_inventory, gate_schedule
from validate import CHECKS, NORMAL_CHECKS, DRAIN_COUNTS, exact_metrics, summarize, validate_sample


def synthetic_unit_record(tool_revision='a' * 40):
    # Offline invented closure hashes only, never a real unit-pass claim.
    return dict(expected_record(tool_revision), assemblySha256={name:'b'*64 for name in REQUIRED_ASSEMBLIES})


def fixed_framework_policy(runtime=Path('/runtime/dotnet')):
    return {name:dict(path=str(runtime.parent/'shared/Microsoft.AspNetCore.App/10.0.14'/(name+'.dll')),
                      sha256=sha, mvid=mvid, productVersion=FRAMEWORK_PRODUCT_VERSION)
            for name,(sha,mvid) in FRAMEWORK.items()}


def synthetic_archive_policy(*_args):
    return {'shared/Microsoft.NETCore.App/10.0.14/System.Private.CoreLib.dll':'1'*64,
            'shared/Microsoft.AspNetCore.App/10.0.14/Microsoft.AspNetCore.dll':'2'*64}


class ProtocolTests(unittest.TestCase):
    def test_exact_balanced_schedule(self):
        rows = schedule()
        self.assertEqual(32, len(rows))
        self.assertEqual(32, len({r['id'] for r in rows}))
        for mode in CONTRACT['modes']:
            for items in CONTRACT['items']:
                cell = [r for r in rows if (r['mode'], r['items']) == (mode, items)]
                self.assertEqual(8, len(cell))
                self.assertEqual(['baseline', 'candidate', 'candidate', 'baseline'] * 2, [r['arm'] for r in cell])
                for pair in range(1, 5):
                    self.assertEqual(set(ARMS), {r['arm'] for r in cell if r['pair'] == pair})

    def test_four_pair_rotation_and_symmetric_strict_cells(self):
        cells=[(mode,n) for n in CONTRACT['items'] for mode in CONTRACT['modes']]
        for pair in range(1,5):
            rows=[slot for slot in schedule() if slot['pair']==pair]
            self.assertEqual(cells[pair-1:]+cells[:pair-1],[(slot['mode'],slot['items']) for slot in rows[::2]])
            self.assertEqual(8,len(rows))
        expected={(mode,n,arm) for mode,n in cells for arm in ARMS}
        for phase in ('strict-cleanup','normal-validate'):
            slots=gate_schedule(phase)
            self.assertEqual(8,len(slots))
            self.assertEqual(expected,{(s['mode'],s['items'],s['arm']) for s in slots})

    def test_integer_quantile_boundary_and_ties(self):
        self.assertEqual(99, nearest_rank(list(range(1, 101)), 99, 100))
        self.assertEqual(999, nearest_rank(list(range(1, 1001)), 999, 1000))
        self.assertEqual(1, nearest_rank([1], 999, 1000))
        self.assertEqual(7, nearest_rank([7] * 100, 99, 100))
        self.assertEqual(101, nearest_rank(list(range(1, 102)), 999, 1000))
        for values in ([], [1.0], [-1], [True]):
            with self.assertRaises(ValueError): nearest_rank(values, 99, 100)

    def test_exact_median(self):
        self.assertEqual(Fraction(11, 10), median([Fraction(1), Fraction(6, 5)]))

    def test_topology_uses_distinct_guest_core(self):
        mapping = '# CPU,Core,Socket,Online\n0,0,0,Y\n1,0,0,Y\n2,1,0,Y\n3,1,0,N\n'
        self.assertEqual([0, 2], select_cpus(mapping, {0, 1, 2, 3}))
        self.assertEqual([1, 2], select_cpus(mapping, {1, 2}))
        for bad in ('', '0,-,-,Y', '0,0,0,Y\n1,0,0,Y', 'bad'):
            with self.assertRaises(ValueError): select_cpus(bad, {0, 1})

    def test_tick_integrity(self):
        data = dict(cpuUsPerOperation=0.25, allocatedBytesPerOperation=128.5, operations=100, elapsedTicks=10000, stopwatchFrequency=1000, arm='baseline-3d76a3a8', mode='static-intercepted', itemCount=1, requestedSeconds=6, elapsedSeconds=10, operationsPerSecond=10, p50Us=50000, p95Us=95000, p99Us=99000, p999Us=100000, maxUs=100000)
        ticks = dict(data, schemaVersion=2, workers=1, measurementStartTick=90, measurementEndTick=10090,
                     samples=[dict(worker=0, durationTicks=list(range(1, 101)))])
        result = exact_metrics(data, ticks)
        self.assertEqual(Fraction(10), result['qps'])
        self.assertEqual(Fraction(99, 1000), result['p99Seconds'])
        for field in ('operations', 'elapsedTicks', 'stopwatchFrequency', 'measurementEndTick'):
            bad = copy.deepcopy(ticks); bad[field] += 1
            with self.assertRaises(ValueError): exact_metrics(data, bad)
        for field in ('arm', 'mode', 'itemCount'):
            bad = copy.deepcopy(ticks); bad[field] = 'wrong'
            with self.assertRaises(ValueError): exact_metrics(data, bad)
        for value in (0, -1, 10000, 10001, 1.0, True):
            bad = copy.deepcopy(ticks); bad['samples'][0]['durationTicks'][0] = value
            with self.assertRaises(ValueError): exact_metrics(data, bad)
        bad = copy.deepcopy(ticks); bad['samples'][0]['durationTicks'].pop()
        with self.assertRaises(ValueError): exact_metrics(data, bad)

    def sample(self, arm='baseline', mode='static-intercepted'):
        prepared = Path('/prepared')
        files = {n + '.dll': n for n in ('SharpLink.Abstractions', 'SharpLink.Client', 'SharpLink.Runtime',
                                        'SharpLink.Sdk', 'SharpLink.Server', 'PhysicalTcpProbe', 'ScalarStreamModule')}
        files.update({name+'.dll':'private12-reference-'+name for name in FRAMEWORK})
        version = CONTRACT[arm + 'Source']
        data = dict(arm={'baseline': 'baseline-3d76a3a8', 'candidate': 'candidate-279fdb2a'}[arm], phase='normal-measure', schemaVersion=2, strictCleanupExecuted=False,
                    sourceRevision=version, expectedSource=version, apiVersion=CONTRACT[arm+'Api'],
                    mode=mode, itemCount=1, workers=1, runtimeVersion='10.0.14', processorCount=2,
                    processorAffinity=5, sameVersionServerClient=True, shutdownCompleted=True, disposeCompleted=True,
                    generatedManifest=dict(api=CONTRACT[arm+'Api'], sourceRevision=version,
                        arm={'baseline':'baseline-3d76a3a8','candidate':'candidate-279fdb2a'}[arm], protocol=2,
                        generator=CONTRACT[arm+'Version'], rpcAssemblyHash='1'*32,
                        abi={'baseline':'sharplink-3.0-api5-owner-bound-unsafe-blit-v1',
                             'candidate':'sharplink-3.0-api5-owner-bound-unsafe-blit-v1'}[arm]),
                    tieredCompilation='default', tieredPgo='default', readyToRun='default',
                    frameworkLocations=['/runtime/shared/Microsoft.NETCore.App/10.0.14/System.Private.CoreLib.dll', '/runtime/shared/Microsoft.AspNetCore.App/10.0.14/Microsoft.AspNetCore.dll'],
                    transport='tcp-loopback', connectionPoolMin=1, connectionPoolMax=1, readyConnections=1,
                    requestTimeout='disabled', heartbeatIntervalSeconds=3600, clientInterceptors=1, serverInterceptors=0,
                    binaryHashes=files, validationFailures=0, correctness={}, normalCorrectness={k: True for k in NORMAL_CHECKS}, beforeWarmupDrain=DRAIN_COUNTS.copy(), afterWorkDrain=DRAIN_COUNTS.copy(),
                    wrapperTypes=['+LogicalInvocationAsyncEnumerable`1', '+InterceptedAsyncEnumerable`1', 'PooledAsyncStreamDispatcher`1'],
                    interceptorObserved=True, channelType='SharpLink.Client.SharpLinkClient',
                    streamType='+LogicalInvocationAsyncEnumerable`1',
                    logicalPathProof=dict(generalLogicalObserved=True, physicalDispatcherObserved=True, observedBeforeWarmup=True,
                                          clientActivityListeners=False, clientCallsEnabled=False, telemetryWrapperObserved=False, completionOwnedObserved=False),
                    cpuUsPerOperation=0.25, allocatedBytesPerOperation=128.5,
                    warmupSeconds=3, requestedSeconds=6, operations=100, elapsedTicks=10000, stopwatchFrequency=1000)
        data['loadedAssemblies'] = [dict(name=n[:-4], location=str(prepared / arm / n), mvid='01234567-89ab-cdef-0123-456789abcdef', sha256=h,
                                        productVersion='test+'+version) for n,h in files.items()]
        policy = fixed_framework_policy()
        for assembly in data['loadedAssemblies']:
            if assembly['name'] in FRAMEWORK:
                entry=policy[assembly['name']]
                assembly.update(location=entry['path'],sha256=entry['sha256'],mvid=entry['mvid'],productVersion=entry['productVersion'])
        data['frameworkAssemblies'] = [dict(name=Path(p).stem, location=str(Path('/runtime')/p), sha256=h) for p,h in synthetic_archive_policy().items()]
        data['loadedAssemblyCount'] = len(data['loadedAssemblies']) + len(data['frameworkAssemblies'])
        if mode == 'dynamic-intercepted':
            data.update(channelType='SharpLink.Client.SharpLinkModuleRpcChannel', streamType='InvokeServerStreamingDeferred')
        manifest = {'arms': {arm: {'snapshot': files}}}
        slot = dict(arm=arm, mode=mode, items=1, affinity=[0,2])
        return data, slot, prepared, manifest, Path('/runtime/dotnet'), policy, synthetic_archive_policy()

    def test_each_arm_positive_paths(self):
        for arm in ARMS:
            for mode in CONTRACT['modes']:
                validate_sample(*self.sample(arm, mode))

    def test_functional_and_identity_mutations_rejected(self):
        for field, value in [('sourceRevision','wrong'), ('apiVersion',99), ('processorAffinity',3),
                             ('sameVersionServerClient',False), ('shutdownCompleted',False), ('disposeCompleted',False),
                             ('runtimeVersion','10.0.2'), ('readyConnections',2), ('interceptorObserved',False),
                             ('wrapperTypes',[]), ('channelType','wrong'), ('binaryHashes',{}), ('phase','validate')]:
            args = list(self.sample()); args[0][field] = value
            with self.subTest(field=field), self.assertRaises(ValueError): validate_sample(*args)
        for check in NORMAL_CHECKS:
            args = list(self.sample()); args[0]['normalCorrectness'][check] = False
            with self.subTest(check=check), self.assertRaises(ValueError): validate_sample(*args)

    def test_exact_incremental_source_contract(self):
        self.assertEqual('3d76a3a8b55a37e24c62a073fe588cff3d3867be',CONTRACT['baselineSource'])
        self.assertEqual('279fdb2a93736eb25bec948c3acd65f2ed4830d9',CONTRACT['candidateSource'])
        self.assertEqual((5,5),(CONTRACT['baselineApi'],CONTRACT['candidateApi']))
        self.assertEqual(('3.0.0','3.0.0'),(CONTRACT['baselineVersion'],CONTRACT['candidateVersion']))
        self.assertNotIn('knownNegative',CONTRACT)
        self.assertEqual([11,10],CONTRACT['qpsTargetRatio']);self.assertEqual([4,5],CONTRACT['p99TargetRatio'])
        self.assertIn('incremental',CONTRACT['targetInterpretation'])

    def test_json_rejects_duplicate_and_nonfinite(self):
        for text in ('{"arm": "baseline", "arm": "candidate"}', '{"operations": NaN}', '{"x": Infinity}'):
            with self.assertRaises(ValueError): strict_json(text)

    @patch('validate.archive_framework_policy', side_effect=synthetic_archive_policy)
    @patch('validate.materialize_framework_policy', side_effect=fixed_framework_policy)
    def test_full_synthetic_campaign_and_failure_suppression(self, _disk_policy, _archive_policy):
        with tempfile.TemporaryDirectory() as td:
            base = Path(td); root = base / 'run'; prepared = base / 'prepared'
            root.mkdir(); prepared.mkdir()
            for folder in ('strict', 'normal', 'runs', 'ticks'): (root / folder).mkdir()
            manifest = {'contract': CONTRACT, 'toolRevision':'a'*40, 'arms': {}, 'fixturePurpose':'connection-fanout-comparison',
                        'toolSourceHashes':source_inventory(ROOT), 'fixtureSourceHashes':source_inventory(ROOT / 'fixture')}
            for arm in ARMS:
                (prepared / arm).mkdir()
                data = self.sample(arm)[0]
                for name in data['binaryHashes']: (prepared / arm / name).write_bytes(name.encode())
                manifest['arms'][arm] = {'snapshot': inventory(prepared / arm)}
            write(prepared / 'prepared.json', manifest); write(root / 'prepared.json', manifest)
            write(root / 'topology-selection.json', dict(mapping='0,0,0,Y\n1,0,0,Y\n2,1,0,Y\n', allowed=[0,1,2], selected=[0,2]))
            write(root / 'framework-policy.json', fixed_framework_policy())
            write(root / 'framework-archive-policy.json', synthetic_archive_policy())
            write(root / 'unit-gate.json', synthetic_unit_record(manifest['toolRevision']))
            write(root / 'strict-gate.json', dict(status='Passed', failures=[]))
            write(root / 'normal-gate.json', dict(status='Passed', failures=[]))
            write(root / 'schedule.json', schedule())
            plan = []
            for phase in ('strict-cleanup','normal-validate','normal-measure'):
                for slot in schedule() if phase == 'normal-measure' else gate_schedule(phase):
                    plan.append(dict(slot, phase=phase, launchIndex=len(plan)))
            write(root / 'phase-schedule.json', plan)
            def data_for(arm, mode, items):
                data = self.sample(arm, mode)[0]
                data['itemCount'] = items
                data['binaryHashes'] = manifest['arms'][arm]['snapshot']
                for a in data['loadedAssemblies']:
                    if a['name'] in FRAMEWORK: continue
                    name = a['name'] + '.dll'
                    a['location'] = str(prepared / arm / name)
                    a['sha256'] = data['binaryHashes'][name]
                return data
            def host_for(slot, phase, index):
                directory = {'strict-cleanup':'strict', 'normal-validate':'normal', 'normal-measure':'runs'}[phase]
                warmup, seconds = (3,6) if phase == 'normal-measure' else (0,0)
                command = ['taskset','-c','0,2','/runtime/dotnet',str(prepared / slot['arm'] / 'PhysicalTcpProbe.dll'),
                           slot['mode'],str(slot['items']),'1',str(warmup),str(seconds),
                           str(root / directory / (slot['id']+'.json')),phase]
                if phase == 'normal-measure': command.append(str(root/'ticks'/(slot['id']+'.json')))
                return dict(status='success',exitCode=0,processGroupQuiescent=True,pid=1000+index,slotId=slot['id'],phase=phase,launchIndex=index,command=command)
            for phase, directory, start in [('strict-cleanup','strict',0), ('normal-validate','normal',8)]:
                for index, slot in enumerate(gate_schedule(phase), start=start):
                    name = slot['id']; data = data_for(slot['arm'], slot['mode'], slot['items'])
                    data.update(phase=phase, warmupSeconds=0, requestedSeconds=0, operations=0, elapsedTicks=0,
                                strictCleanupExecuted=phase=='strict-cleanup',
                                correctness={k:True for k in CHECKS} if phase=='strict-cleanup' else {})
                    write(root / directory / (name+'.json'), data)
                    write(root / directory / (name+'.host.json'), host_for(slot,phase,index))
            for index, slot in enumerate(schedule(), start=16):
                name = slot['id']; data = data_for(slot['arm'], slot['mode'], slot['items'])
                data.update(elapsedSeconds=10, operationsPerSecond=10, p50Us=50000, p95Us=95000,
                            p99Us=99000, p999Us=100000, maxUs=100000)
                ticks = {k:data[k] for k in ('arm','mode','itemCount','operations','elapsedTicks','stopwatchFrequency','workers')}
                ticks.update(schemaVersion=2, measurementStartTick=100, measurementEndTick=10100, samples=[dict(worker=0,durationTicks=list(range(1,101)))])
                write(root / 'runs' / (name+'.json'), data)
                write(root / 'runs' / (name+'.host.json'), host_for(slot, 'normal-measure', index))
                write(root / 'ticks' / (name+'.json'), ticks)
            result = summarize(root, prepared, Path('/runtime/dotnet'))
            self.assertEqual('DescriptiveOnly', result['status'], result)
            self.assertTrue(all(r['completePairs'] == 4 and 'observedMedianTargetsReached' in r for r in result['cells']))
            for row in result['cells']:
                self.assertEqual(10, row['qpsTargetGapPercentagePoints']['decimal'])
                self.assertEqual(20, row['p99TargetGapPercentagePoints']['decimal'])
            # Preserve the raw file, mutate the process failure; no replacement/drop.
            failed = schedule()[0]['id']
            original_host = (root / 'runs' / (failed+'.host.json')).read_text()
            write(root / 'runs' / (failed+'.host.json'), dict(status='failed', exitCode=1))
            result = summarize(root, prepared, Path('/runtime/dotnet'))
            self.assertEqual('Invalid', result['status'])
            self.assertEqual(1, len(result['failures']))
            self.assertFalse(any('observedMedianTargetsReached' in r for r in result['cells']))
            (root / 'runs' / (failed+'.host.json')).write_text(original_host)
            # Each adversarial campaign must suppress ALL four target observations.
            def rejects():
                result = summarize(root, prepared, Path('/runtime/dotnet'))
                self.assertEqual('Invalid', result['status'])
                self.assertFalse(any('observedMedianTargetsReached' in row for row in result['cells']))
            for path in [root/'unit-gate.json', root/'framework-archive-policy.json', root/'framework-policy.json', root/'ticks'/(failed+'.json'), root/'normal'/'static-intercepted-n1-baseline.json',
                         root/'strict'/'static-intercepted-n1-candidate.json', root/'strict'/'static-intercepted-n1-baseline.json']:
                original=path.read_text(); path.unlink(); rejects(); path.write_text(original)
            for directory in ('strict','normal','runs','ticks'):
                extra=root/directory/'undeclared.json'; extra.write_text('{}'); rejects(); extra.unlink()
            for path in [root/'schedule.json',root/'phase-schedule.json']:
                original=path.read_text(); changed=strict_json(original); changed[0],changed[1]=changed[1],changed[0]
                write(path,changed); rejects(); path.write_text(original)
            for filename, mutation in [
                ('unit-gate.json',lambda v:v.update(candidateSource=CONTRACT['baselineSource'])),
                ('unit-gate.json',lambda v:v.update(candidateTree=CONTRACT['baselineTree'])),
                ('unit-gate.json',lambda v:v.update(runtime='10.0.12')),
                ('unit-gate.json',lambda v:v.update(rawCount=2327)),
                ('unit-gate.json',lambda v:v.update(ordinaryCount=2322)),
                ('unit-gate.json',lambda v:v.update(rawUidSha256='0'*64)),
                ('unit-gate.json',lambda v:v.update(ordinaryUidSha256='0'*64)),
                ('unit-gate.json',lambda v:v.update(executedUidSha256='0'*64)),
                ('unit-gate.json',lambda v:v.update(toolRevision='b'*40)),
                ('unit-gate.json',lambda v:v['allocationChildren'][0].update(passed=False)),
                ('prepared.json', lambda v:v.update(fixturePurpose='cleanup-diagnostic')),
                ('prepared.json', lambda v:v.update(toolSourceHashes={})),
                ('prepared.json', lambda v:v.update(fixtureSourceHashes={})),
                ('framework-archive-policy.json',lambda v:v.update({'shared/Microsoft.NETCore.App/10.0.14/Foo.dll':'f'*64})),
                ('framework-policy.json',lambda v:v[next(iter(v))].update(sha256='wrong')),
                ('framework-policy.json',lambda v:v[next(iter(v))].update(path='/unapproved/runtime/dependency.dll')),
                ('strict-gate.json', lambda v:v.update(status='Failed')),
                ('strict/static-intercepted-n1-baseline.json',lambda v:v['correctness'].update(earlyBreak=False)),
                ('strict/static-intercepted-n1-candidate.json',lambda v:v['correctness'].update(earlyBreak=False)),
                ('runs/'+failed+'.json',lambda v:v.update(sourceRevision=CONTRACT['candidateSource'])),
                ('normal-gate.json', lambda v:v.update(status='Failed')),
                ('runs/'+failed+'.host.json',lambda v:v.update(launchIndex=17)),
                ('runs/'+failed+'.host.json',lambda v:v.update(processGroupQuiescent=False)),
                ('ticks/'+failed+'.json',lambda v:v.update(schemaVersion=1))]:
                path=root/filename; original=path.read_text(); changed=strict_json(original); mutation(changed)
                write(path,changed); rejects(); path.write_text(original)
            # Disk policy materialization is the ONLY patched boundary; fixed policy validation
            # and all loaded assembly/sample checks above remain real throughout this test.
            _disk_policy.side_effect=IdentityError('framework_disk_hash','PRIVATE_DISK_FAILURE')
            result=summarize(root,prepared,Path('/runtime/dotnet'))
            self.assertEqual('Invalid',result['status'])
            self.assertEqual(1,result['failureReasonCounts']['framework_disk_hash'])
            self.assertFalse(any('observedMedianTargetsReached' in row for row in result['cells']))
            _disk_policy.side_effect=fixed_framework_policy

    def test_missing_all_retains_every_slot(self):
        with tempfile.TemporaryDirectory() as td:
            root = Path(td)
            (root / 'prepared.json').write_text(json.dumps({'contract': CONTRACT}))
            result = summarize(root, root / 'prepared', root / 'dotnet')
            self.assertEqual('Invalid', result['status'])
            self.assertEqual(32, len(result['failures']))
            self.assertEqual(4, len(result['cells']))
            self.assertTrue(all(r['missingPairs'] == list(range(1, 5)) for r in result['cells']))
            self.assertFalse(any('observedMedianTargetsReached' in r for r in result['cells']))


if __name__ == '__main__':
    unittest.main()
