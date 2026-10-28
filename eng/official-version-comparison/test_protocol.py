import copy
from fractions import Fraction
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
from common import ARMS, CONTRACT, ROOT, median, nearest_rank, schedule, select_cpus, strict_json, inventory, write
from prepare import materialize, package_identity
from validate import CHECKS, exact_metrics, summarize, validate_sample


class ProtocolTests(unittest.TestCase):
    def test_exact_balanced_schedule(self):
        rows = schedule()
        self.assertEqual(48, len(rows))
        self.assertEqual(48, len({r['id'] for r in rows}))
        for mode in CONTRACT['modes']:
            for items in CONTRACT['items']:
                cell = [r for r in rows if (r['mode'], r['items']) == (mode, items)]
                self.assertEqual(12, len(cell))
                self.assertEqual(['official', 'dev', 'dev', 'official'] * 3, [r['arm'] for r in cell])
                for pair in range(1, 7):
                    self.assertEqual(set(ARMS), {r['arm'] for r in cell if r['pair'] == pair})

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
        data = dict(operations=100, elapsedTicks=10000, stopwatchFrequency=1000, arm='official-2.0.3', mode='static-intercepted', itemCount=1, requestedSeconds=6, elapsedSeconds=10, operationsPerSecond=10, p50Us=50000, p95Us=95000, p99Us=99000, p999Us=100000, maxUs=100000)
        ticks = dict(data, workers=1, measurementStartTick=90, measurementEndTick=10090,
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

    def sample(self, arm='official', mode='static-intercepted'):
        prepared = Path('/prepared')
        files = {n + '.dll': n for n in ('SharpLink.Abstractions', 'SharpLink.Client', 'SharpLink.Runtime',
                                        'SharpLink.Sdk', 'SharpLink.Server', 'PhysicalTcpProbe', 'ScalarStreamModule')}
        version = CONTRACT[arm + 'Source']
        data = dict(arm={'official': 'official-2.0.3', 'dev': 'dev-57f06f1'}[arm], phase='measure',
                    sourceRevision=version, expectedSource=version, apiVersion=CONTRACT[arm+'Api'],
                    mode=mode, itemCount=1, workers=1, runtimeVersion='10.0.14', processorCount=2,
                    processorAffinity=5, sameVersionServerClient=True, shutdownCompleted=True, disposeCompleted=True,
                    generatedManifest=dict(api=CONTRACT[arm+'Api'], sourceRevision=version),
                    tieredCompilation='default', tieredPgo='default', readyToRun='default',
                    frameworkLocations=['/runtime/shared/Microsoft.NETCore.App/10.0.14/core.dll', '/runtime/shared/Microsoft.AspNetCore.App/10.0.14/asp.dll'],
                    transport='tcp-loopback', connectionPoolMin=1, connectionPoolMax=1, readyConnections=1,
                    requestTimeout='disabled', heartbeatIntervalSeconds=3600, clientInterceptors=1, serverInterceptors=0,
                    binaryHashes=files, validationFailures=0, correctness={k: True for k in CHECKS},
                    wrapperTypes=['+LogicalInvocationAsyncEnumerable`1', '+InterceptedAsyncEnumerable`1', 'PooledAsyncStreamDispatcher`1'],
                    interceptorObserved=True, channelType='SharpLink.Client.SharpLinkClient',
                    streamType='+LogicalInvocationAsyncEnumerable`1',
                    logicalPathProof=dict(generalLogicalObserved=True, physicalDispatcherObserved=True, observedBeforeWarmup=True,
                                          clientActivityListeners=False, clientCallsEnabled=False, telemetryWrapperObserved=False, completionOwnedObserved=False),
                    warmupSeconds=3, requestedSeconds=6, operations=100, elapsedTicks=10000, stopwatchFrequency=1000)
        data['loadedAssemblies'] = [dict(name=n[:-4], location=str(prepared / arm / n), mvid='nonempty', sha256=h,
                                        productVersion='test+'+version) for n,h in files.items()]
        if mode == 'dynamic-intercepted':
            data.update(channelType='SharpLink.Client.SharpLinkModuleRpcChannel', streamType='InvokeServerStreamingDeferred')
        manifest = {'arms': {arm: {'snapshot': files}}}
        slot = dict(arm=arm, mode=mode, items=1, affinity=[0,2])
        return data, slot, prepared, manifest, Path('/runtime/dotnet')

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
        for check in CHECKS:
            args = list(self.sample()); args[0]['correctness'][check] = False
            with self.subTest(check=check), self.assertRaises(ValueError): validate_sample(*args)

    def test_published_provenance_pins(self):
        cache = ROOT / 'published-cache'
        provenance = strict_json((cache / 'provenance.json').read_text())
        self.assertEqual('2.0.3', provenance['version'])
        self.assertEqual(CONTRACT['officialSource'], provenance['source_commit'])
        self.assertEqual(5, len(provenance['packages']))
        for name, entry in provenance['packages'].items():
            self.assertEqual(f'https://api.nuget.org/v3-flatcontainer/{name.lower()}/2.0.3/{name.lower()}.2.0.3.nupkg', entry['package_url'])
            for key in ('package_sha256', 'dll_sha256'):
                self.assertEqual(64, len(entry[key])); int(entry[key], 16)
        self.assertEqual(64, len(provenance['packages']['SharpLink.Sdk']['generator_sha256']))

    def test_json_rejects_duplicate_and_nonfinite(self):
        for text in ('{"arm": "official", "arm": "dev"}', '{"operations": NaN}', '{"x": Infinity}'):
            with self.assertRaises(ValueError): strict_json(text)

    def test_full_synthetic_campaign_and_failure_suppression(self):
        with tempfile.TemporaryDirectory() as td:
            base = Path(td); root = base / 'run'; prepared = base / 'prepared'
            root.mkdir(); prepared.mkdir()
            for folder in ('cold', 'runs', 'ticks'): (root / folder).mkdir()
            manifest = {'contract': CONTRACT, 'arms': {}}
            for arm in ARMS:
                (prepared / arm).mkdir()
                data = self.sample(arm)[0]
                for name in data['binaryHashes']: (prepared / arm / name).write_bytes(name.encode())
                manifest['arms'][arm] = {'snapshot': inventory(prepared / arm)}
            write(prepared / 'prepared.json', manifest); write(root / 'prepared.json', manifest)
            write(root / 'topology-selection.json', dict(mapping='0,0,0,Y\n1,0,0,Y\n2,1,0,Y\n', allowed=[0,1,2], selected=[0,2]))
            write(root / 'cold-gate.json', dict(status='Passed', failures=[]))
            def data_for(arm, mode, items):
                data = self.sample(arm, mode)[0]
                data['itemCount'] = items
                data['binaryHashes'] = manifest['arms'][arm]['snapshot']
                for a in data['loadedAssemblies']:
                    name = a['name'] + '.dll'
                    a['location'] = str(prepared / arm / name)
                    a['sha256'] = data['binaryHashes'][name]
                return data
            host = dict(status='success', exitCode=0)
            for count in CONTRACT['items']:
                for mode in CONTRACT['modes']:
                    for arm in ARMS:
                        name = f'{mode}-n{count}-{arm}'
                        data = data_for(arm, mode, count)
                        data.update(phase='validate', warmupSeconds=0, requestedSeconds=0, operations=0, elapsedTicks=0)
                        write(root / 'cold' / (name+'.json'), data)
                        write(root / 'cold' / (name+'.host.json'), host)
            for slot in schedule():
                name = slot['id']; data = data_for(slot['arm'], slot['mode'], slot['items'])
                data.update(elapsedSeconds=10, operationsPerSecond=10, p50Us=50000, p95Us=95000,
                            p99Us=99000, p999Us=100000, maxUs=100000)
                ticks = {k:data[k] for k in ('arm','mode','itemCount','operations','elapsedTicks','stopwatchFrequency','workers')}
                ticks.update(measurementStartTick=100, measurementEndTick=10100, samples=[dict(worker=0,durationTicks=list(range(1,101)))])
                write(root / 'runs' / (name+'.json'), data)
                write(root / 'runs' / (name+'.host.json'), host)
                write(root / 'ticks' / (name+'.json'), ticks)
            result = summarize(root, prepared, Path('/runtime/dotnet'))
            self.assertEqual('DescriptiveOnly', result['status'], result)
            self.assertTrue(all(r['completePairs'] == 6 and 'observedMedianTargetsReached' in r for r in result['cells']))
            for row in result['cells']:
                self.assertEqual(10, row['qpsTargetGapPercentagePoints']['decimal'])
                self.assertEqual(20, row['p99TargetGapPercentagePoints']['decimal'])
            # Preserve the raw file, mutate the process failure; no replacement/drop.
            failed = schedule()[0]['id']
            write(root / 'runs' / (failed+'.host.json'), dict(status='failed', exitCode=1))
            result = summarize(root, prepared, Path('/runtime/dotnet'))
            self.assertEqual('Invalid', result['status'])
            self.assertEqual(1, len(result['failures']))
            self.assertFalse(any('observedMedianTargetsReached' in r for r in result['cells']))

    def test_missing_all_retains_every_slot(self):
        with tempfile.TemporaryDirectory() as td:
            root = Path(td)
            (root / 'prepared.json').write_text(json.dumps({'contract': CONTRACT}))
            result = summarize(root, root / 'prepared', root / 'dotnet')
            self.assertEqual('Invalid', result['status'])
            self.assertEqual(48, len(result['failures']))
            self.assertEqual(4, len(result['cells']))
            self.assertTrue(all(r['missingPairs'] == list(range(1, 7)) for r in result['cells']))
            self.assertFalse(any('observedMedianTargetsReached' in r for r in result['cells']))


if __name__ == '__main__':
    unittest.main()
