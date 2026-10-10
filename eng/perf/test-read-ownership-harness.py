#!/usr/bin/env python3
import copy
import contextlib
import io
import hashlib
import importlib.util
import itertools
import json
from pathlib import Path
import tempfile
import unittest
import tarfile
from unittest.mock import patch

HERE = Path(__file__).resolve().parent


def module(name, filename):
    spec = importlib.util.spec_from_file_location(name, HERE / filename)
    value = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(value)
    return value


runner = module('runner', 'run-read-ownership.py')
summary = module('summary', 'summarize-read-ownership.py')


class HarnessTests(unittest.TestCase):
    def test_balanced_order(self):
        orders = list(itertools.permutations(runner.ARMS))
        for arm in runner.ARMS:
            for position in range(3):
                self.assertEqual(sum(order[position] == arm for order in orders), 2)
        for left, right in itertools.combinations(runner.ARMS, 2):
            self.assertEqual(sum(order.index(left) < order.index(right) for order in orders), 3)

    def test_manifest_rejects_imbalance_and_unpinned_head(self):
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / 'manifest.json'
            base = dict(baseline='a' * 40, safe='b' * 40, normalized='c' * 40,
                        rounds=6, warmupSeconds=2, durationSeconds=6, dispatchIterations=10000)
            path.write_text(json.dumps(base))
            runner.load_manifest(path)
            for patch in ({'rounds': 5}, {'rounds': 7}, {'normalized': 'dev'},
                          {'warmupSeconds': 0}, {'durationSeconds': 1}):
                path.write_text(json.dumps(base | patch))
                with self.assertRaises(ValueError):
                    runner.load_manifest(path)

    def fixture(self, root):
        manifest = dict(baseline='a' * 40, normalized='b' * 40, safe='c' * 40, rounds=6, warmupSeconds=2, durationSeconds=6, dispatchIterations=10000)
        provenance = {'manifest': manifest, 'arms': {arm: {
            'identity': manifest[arm], 'checkoutCommit': manifest[arm],
            'readerOrigin': manifest[arm], 'synthetic': False} for arm in runner.ARMS}}
        (root / 'provenance.json').write_text(json.dumps(provenance))
        index = []
        for scenario in summary.SCENARIOS:
            for round_number, order in enumerate(itertools.permutations(runner.ARMS), 1):
                for position, arm in enumerate(order, 1):
                    identity = manifest[arm]
                    filename = f'{scenario}-{round_number}-{arm}.json'
                    if scenario in ('dispatch', 'dispatch-backlog'):
                        suite = 'backlog' if scenario == 'dispatch-backlog' else 'main'
                        cases = ('late-continuation-backlog',) if suite == 'backlog' else summary.DISPATCH_CASES
                        doc = dict(schemaVersion=1, benchmark='read-ownership-dispatch', suite=suite,
                                   sourceCommit=identity, arm=arm, tieredCompilation='0',
                                   runtime='test', architecture='X64', processorCount=4, serverGc=False,
                                   allocationScope='process-wide-precise', iterations=10000, warmupIterations=5000,
                                   rows=[], construction=[dict(wrapped=wrapped, count=10000, allocatedBytes=1000000,
                                                               allocatedBytesPerReader=100) for wrapped in (False, True)])
                        for case in cases:
                            for wrapped in (False, True):
                                notifications = 10000 if case in ('explicit-late-unsafe-continuation',
                                                                'explicit-flow-execution-context',
                                                                'late-continuation-backlog') else 0
                                doc['rows'].append(dict(caseId=case, wrapped=wrapped, iterations=10000,
                                    category='ordinary' if case in summary.ORDINARY_DISPATCH else 'special',
                                    consumer=summary.DISPATCH_TOPOLOGY[case][0], backend=summary.DISPATCH_TOPOLOGY[case][1],
                                    reads=10000, resultConsumptions=10000, advances=10000, consumerCompletions=10000,
                                    innerCallbacksRegistered=0 if case == 'completion-before-ordinary-await' and not wrapped else 10000,
                                    innerCallbacksCompleted=0 if case == 'completion-before-ordinary-await' and not wrapped else 10000,
                                    notificationCallbacksExpected=notifications, notificationCallbacksCompleted=notifications,
                                    elapsedSeconds=.01, nanosecondsPerOperation=1000, operationsPerSecond=1000000, quiescenceSeconds=.00001,
                                    allocatedBytes=10000 if wrapped else 5000,
                                    allocatedBytesPerOperation=1 if wrapped else .5,
                                    cpuNanosecondsPerOperation=10000, gen0=0, gen1=0, gen2=0,
                                    specialContext=summary.DISPATCH_TOPOLOGY[case][2], backlog=suite == 'backlog', drained=True,
                                    contextObservation=dict(noSynchronizationContextCount=0 if case == 'custom-synchronization-context-await' else 10000,
                                        capturedSynchronizationContextCount=10000 if case == 'custom-synchronization-context-await' else 0,
                                        publisherSynchronizationContextCount=0, otherSynchronizationContextCount=0,
                                        defaultTaskSchedulerCount=0 if case == 'custom-task-scheduler-await' else 10000,
                                        capturedTaskSchedulerCount=10000 if case == 'custom-task-scheduler-await' else 0,
                                        otherTaskSchedulerCount=0, taskIdPresentCount=0, threadPoolThreadCount=0),
                                    threadPoolBlockVerified=suite == 'backlog'))
                    else:
                        concurrency = int(scenario.rsplit('-c', 1)[1])
                        stream = scenario.startswith('tcp-duplex')
                        operation = 'duplex' if stream else 'add'
                        config = dict(Mode=0, Transport=4 if scenario.startswith('shm-') else 0,
                                      Operation=operation, ConcurrencyConfig=[concurrency], DurationSeconds=6,
                                      WarmupSeconds=2, PerformanceProfile=0, MinConnections=1, MaxConnections=1,
                                      MaxSendQueueBytes=67108864, RecordingMode=1, MaximumRecordedOperations=30000000,
                                      DetailedSharedMemoryEvidence=False, StreamSize=256, MetricsPort=0,
                                      DrainTimeoutSeconds=30 if stream else 5)
                        evidence = dict(CpuMilliseconds=1, AllocatedBytes=1000,
                                        Gen0Collections=0, Gen1Collections=0, Gen2Collections=0)
                        result = dict(Operation=operation, Concurrency=concurrency, WorkerCount=concurrency,
                                      WarmupDurationSeconds=2, StopwatchFrequency=1000000000, RecorderVersion='worker-local-shared-capacity-v4', FormalComparable=True, RecorderMode='formal',
                                      Success=100, Failure=0, OperationsStartedDuringMeasurement=100,
                                      OperationsCompleted=100, SampleCount=100, MaximumSampleCapacity=1000,
                                      P50Us=10, P95Us=15, P99Us=20, P999Us=25, Qps=100 / 6, MeasurementDurationSeconds=6,
                                      DrainDurationSeconds=.001, Evidence=evidence)
                        doc = dict(SourceCommit=identity, Results=[result], Configuration=config,
                                   Workload='SharpLink.StreamLoadTest' if stream else 'SharpLink.LoadTest', SchemaVersion=1,
                                   OperatingSystem='linux', OsArchitecture='X64', ProcessArchitecture='X64',
                                   Runtime='test', ProcessorCount=4, ServerGc=False, GcLatencyMode='interactive')
                    path = root / filename
                    path.write_text(json.dumps(doc))
                    index.append(dict(scenario=scenario, round=round_number, position=position, arm=arm,
                                      file=filename, sha256=hashlib.sha256(path.read_bytes()).hexdigest()))
        (root / 'execution-index.json').write_text(json.dumps(index))
        return index

    def test_exact_arm_provenance_rejects_transplants_or_wrong_ref(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            self.fixture(root)
            path = root / 'provenance.json'
            original = json.loads(path.read_text())
            for patch in ({'synthetic': True}, {'checkoutCommit': 'wrong'},
                          {'readerOrigin': 'wrong'}, {'identity': 'wrong'}):
                changed = copy.deepcopy(original)
                changed['arms']['safe'].update(patch)
                path.write_text(json.dumps(changed))
                with self.assertRaises(ValueError):
                    summary.load_rows(root)

    def test_validator_rejects_corrupted_missing_unbalanced_and_failed_evidence(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            index = self.fixture(root)
            summary.load_rows(root)
            with contextlib.redirect_stdout(io.StringIO()):
                summary.summarize(root)
            self.assertTrue((root / 'comparison.json').is_file())
            self.assertTrue((root / 'comparison.md').is_file())
            index_path = root / 'execution-index.json'
            for update in ('missing', 'order', 'position-zero', 'duplicate-file', 'hash'):
                changed = copy.deepcopy(index)
                if update == 'missing':
                    changed.pop()
                elif update == 'order':
                    changed[0]['position'] = 2
                elif update == 'position-zero':
                    changed[0]['position'] = 0
                elif update == 'duplicate-file':
                    changed[0]['file'] = changed[1]['file']
                else:
                    changed[0]['sha256'] = 'wrong'
                index_path.write_text(json.dumps(changed))
                with self.assertRaises(ValueError):
                    summary.load_rows(root)
            index_path.write_text(json.dumps(index))
            entry = next(entry for entry in index if entry['scenario'] in summary.MACRO_SCENARIOS)
            path = root / entry['file']
            doc = json.loads(path.read_text())
            doc['Results'][0]['Failure'] = 1
            path.write_text(json.dumps(doc))
            entry['sha256'] = hashlib.sha256(path.read_bytes()).hexdigest()
            index_path.write_text(json.dumps(index))
            with self.assertRaises(ValueError):
                summary.load_rows(root)

    def test_validator_rejects_scenario_and_metric_corruption(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            index = self.fixture(root)
            entry = next(entry for entry in index if entry['scenario'] == 'tcp-add-c32')
            path = root / entry['file']
            original = json.loads(path.read_text())
            mutations = [
                lambda doc: doc['Configuration'].update(Transport=4),
                lambda doc: doc['Configuration'].update(ConcurrencyConfig=[1]),
                lambda doc: doc['Configuration'].update(Operation='echo'),
                lambda doc: doc['Configuration'].update(DurationSeconds=1),
                lambda doc: doc['Results'][0].update(Qps=1e12),
                lambda doc: doc['Results'][0].pop('Failure'),
                lambda doc: doc['Results'][0]['Evidence'].update(CpuMilliseconds=-1),
                lambda doc: doc['Results'][0]['Evidence'].update(AllocatedBytes=-1),
                lambda doc: doc['Results'][0]['Evidence'].update(AllocatedBytes=float('nan')),
                lambda doc: doc['Results'][0].update(P99Us=1),
                lambda doc: doc['Results'][0].update(DrainDurationSeconds=-1),
            ]
            for mutation in mutations:
                doc = copy.deepcopy(original)
                mutation(doc)
                path.write_text(json.dumps(doc))
                entry['sha256'] = hashlib.sha256(path.read_bytes()).hexdigest()
                (root / 'execution-index.json').write_text(json.dumps(index))
                with self.assertRaises(ValueError):
                    summary.load_rows(root)

    def test_known_answer_all_six_points_are_retained(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            index = self.fixture(root)
            self.assertEqual(len(index), 72)
            for entry in index:
                if entry['arm'] != 'normalized' or entry['scenario'] == 'dispatch':
                    continue
                path = root / entry['file']
                doc = json.loads(path.read_text())
                result = doc['Results'][0]
                for key in ('Success', 'OperationsStartedDuringMeasurement', 'OperationsCompleted', 'SampleCount'):
                    result[key] *= 2
                for key in ('Qps', 'P50Us', 'P95Us', 'P99Us', 'P999Us'):
                    result[key] *= 2
                result['Evidence']['CpuMilliseconds'] *= 4
                result['Evidence']['AllocatedBytes'] *= 4
                path.write_text(json.dumps(doc))
                entry['sha256'] = hashlib.sha256(path.read_bytes()).hexdigest()
            (root / 'execution-index.json').write_text(json.dumps(index))
            with contextlib.redirect_stdout(io.StringIO()):
                summary.summarize(root)
            result = json.loads((root / 'comparison.json').read_text())
            for scenario in summary.MACRO_SCENARIOS:
                for reference in ('baseline', 'safe'):
                    metrics = result['pairedComparisons'][f'{scenario}/normalized-versus-{reference}']
                    for metric in ('qps', 'cpuMicrosecondsPerRpc', 'allocatedBytesPerRpc', 'p99Us'):
                        self.assertEqual(metrics[metric]['percentDelta']['points'], [100.0] * 6)



    def test_dispatch_requires_all_thread_allocations_drain_and_complete_counts(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            index = self.fixture(root)
            entry = next(entry for entry in index if entry['scenario'] == 'dispatch')
            path = root / entry['file']
            original = json.loads(path.read_text())
            mutations = [
                lambda doc: doc.update(allocationScope='current-thread'),
                lambda doc: doc.update(suite='backlog'),
                lambda doc: doc['rows'].pop(),
                lambda doc: doc['rows'][0].update(drained=False),
                lambda doc: doc['rows'][0].update(reads=999),
                lambda doc: doc['rows'][0].update(innerCallbacksCompleted=999),
                lambda doc: doc['rows'][0].update(innerCallbacksRegistered=0, innerCallbacksCompleted=0),
                lambda doc: doc['rows'][0]['contextObservation'].update(noSynchronizationContextCount=0),
                lambda doc: doc['rows'][0].update(consumer='wrong'),
                lambda doc: doc['rows'][0].update(notificationCallbacksCompleted=1),
                lambda doc: doc['rows'][0].update(allocatedBytes=-1),
                lambda doc: doc['rows'][0].update(allocatedBytesPerOperation=0),
                lambda doc: doc['rows'][0].update(operationsPerSecond=1),
                lambda doc: doc['rows'][0].update(nanosecondsPerOperation=1),
                lambda doc: doc['rows'][0].update(quiescenceSeconds=1),
                lambda doc: doc['rows'][0].update(cpuNanosecondsPerOperation=float('inf')),
                lambda doc: doc['rows'][0].update(threadPoolBlockVerified=True),
                lambda doc: doc['rows'][0].update(category='special'),
            ]
            for mutation in mutations:
                doc = copy.deepcopy(original)
                mutation(doc)
                path.write_text(json.dumps(doc))
                entry['sha256'] = hashlib.sha256(path.read_bytes()).hexdigest()
                (root / 'execution-index.json').write_text(json.dumps(index))
                with self.assertRaises(ValueError):
                    summary.load_rows(root)

    def test_dispatch_incremental_allocation_keeps_all_signed_points(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            self.fixture(root)
            with contextlib.redirect_stdout(io.StringIO()):
                summary.summarize(root)
            result = json.loads((root / 'comparison.json').read_text())
            for arm in runner.ARMS:
                metric = result['observations'][f'dispatch-ordinary-await-before-completion-incremental/{arm}']
                self.assertEqual(metric['allocatedBytesPerOperation']['points'], [.5] * 6)
            index = json.loads((root / 'execution-index.json').read_text())
            for entry in index:
                if entry['scenario'] not in ('dispatch', 'dispatch-backlog'):
                    continue
                path = root / entry['file']
                doc = json.loads(path.read_text())
                for cell in doc['rows']:
                    if cell['wrapped']:
                        cell['allocatedBytes'] = 2500
                        cell['allocatedBytesPerOperation'] = .25
                path.write_text(json.dumps(doc))
                entry['sha256'] = hashlib.sha256(path.read_bytes()).hexdigest()
            (root / 'execution-index.json').write_text(json.dumps(index))
            with contextlib.redirect_stdout(io.StringIO()):
                summary.summarize(root)
            result = json.loads((root / 'comparison.json').read_text())
            for arm in runner.ARMS:
                metric = result['observations'][f'dispatch-ordinary-await-before-completion-incremental/{arm}']
                self.assertEqual(metric['allocatedBytesPerOperation']['points'], [-.25] * 6)
            for contrast in ('normalized-versus-baseline', 'normalized-versus-safe', 'safe-versus-baseline'):
                metric = result['pairedComparisons']['dispatch-ordinary-await-before-completion-incremental/' + contrast]
                self.assertIsNone(metric['allocatedBytesPerOperation']['percentDelta'])


    def test_transfer_parts_reconstruct_every_original(self):
        package = module('packager', 'package-read-ownership.py')
        self.assertEqual(package.PART_BYTES, 15 * 1024 * 1024)
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary); source = root / 'source'; source.mkdir()
            (source / 'raw.json').write_text('{"retained":true}')
            (source / 'log.txt').write_bytes(bytes(range(256)) * 8)
            with patch.object(package, 'PART_BYTES', 128):
                manifest = package.package(source, root / 'parts')
            archive = root / 'reconstructed.tar.gz'
            archive.write_bytes(b''.join((root / 'parts' / p['file']).read_bytes() for p in manifest['parts']))
            self.assertEqual(package.digest(archive), manifest['archiveSha256'])
            with tarfile.open(archive, 'r:gz') as tar:
                self.assertEqual(set(tar.getnames()), set(manifest['files']))
                for member in tar:
                    self.assertEqual(tar.extractfile(member).read(), (source / member.name).read_bytes())
            with patch.object(package, 'PART_BYTES', 128), patch.object(package, 'MAX_PARTS', 1):
                with self.assertRaises(ValueError): package.package(source, root / 'overflow')
            self.assertTrue((root / 'overflow/evidence.tar.gz').is_file())
            self.assertFalse(json.loads((root / 'overflow/index.json').read_text())['withinUploadPartLimit'])



if __name__ == '__main__':
    unittest.main()
