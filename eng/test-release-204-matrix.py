#!/usr/bin/env python3
"""Synthetic completeness tests. These do not substitute for production evidence."""
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
import sys
sys.path.insert(0, str(Path(__file__).parent))
from release_204_evidence import COMMON_BINARIES, SCRIPTS
spec = importlib.util.spec_from_file_location('validator', Path(__file__).with_name('validate-release-204-matrix.py'))
v = importlib.util.module_from_spec(spec)
spec.loader.exec_module(v)
SHA = 'a' * 40
CONTEXT = dict(SourceCommit=SHA, ToolingCommit='b'*40, RunId='12345', RunAttempt='1', FinalMain=True)
SCRIPTHASHES = {path: 'c'*64 for path in SCRIPTS}

class MatrixTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.root = Path(self.tmp.name)
        for shard, name, transport, profile, workload, pool, op, payload, concurrencies in v.expected():
            folder = self.root / shard
            (folder / 'raw').mkdir(parents=True, exist_ok=True)
            for metadata in ['environment.txt', 'canonical-scripts.sha256', 'dll-sha256.txt', 'source-sha.txt', 'completed.txt']:
                (folder / metadata).write_text(SHA)
            (folder / 'run.log').write_text('OK')
            (folder / 'provenance.json').write_text(json.dumps(dict(SchemaVersion=1, **CONTEXT, MainCommit=SHA, SourceTreeClean=True, Version='2.0.4')))
            (folder / 'canonical-scripts.sha256').write_text(''.join(f'{value}  {key}\n' for key, value in SCRIPTHASHES.items()))
            identities = [dict(Path=f'test/{name}/bin/Release/net10.0/{name}.dll', Name=name, AssemblyVersion='2.0.4.0',
                               InformationalVersion='2.0.4+'+SHA, Sha256='d'*64)
                          for name in sorted(COMMON_BINARIES | {'SharpLink.LoadTest', 'SharpLink.StreamLoadTest'})]
            (folder / 'dll-identities.json').write_text(json.dumps(dict(SchemaVersion=1, SourceCommit=SHA, Assemblies=identities)))
            (folder / 'dll-sha256.txt').write_text(''.join(f'{row["Sha256"]}  {row["Path"]}\n' for row in identities))
            config = dict(Mode=0, Transport=v.TRANSPORTS.index(transport), PerformanceProfile=v.PROFILES.index(profile),
                          MinConnections=1, MaxConnections=pool, Operation=op, ConcurrencyConfig=concurrencies,
                          WarmupSeconds=5, DurationSeconds=20, MaximumRecordedOperations=300000000,
                          MaxSendQueueBytes=None if workload == 'oneway-backpressure' else 67108864)
            config.update(dict(PayloadSize=payload, MetricsPort=0) if payload is not None else dict(StreamSize=256))
            rows = []
            for operation in (['unary', 'c2s', 's2c', 'duplex'] if workload == 'streams' else [op]):
                for concurrency in concurrencies:
                    rows.append(dict(Operation=operation, Concurrency=concurrency, Success=100, Failure=0,
                                     OperationsStartedDuringMeasurement=100, OperationsCompleted=100, SampleCount=100,
                                     MaximumSampleCapacity=300000000, FormalComparable=True, RecorderMode='formal',
                                     RecorderVersion='worker-local-shared-capacity-v4', P50Us=1, P95Us=2, P99Us=3, P999Us=4,
                                     MinUs=0.5, MaxUs=5, AvgUs=2, Qps=100/20.01, ErrorRatePercent=0, TopFailures='',
                                     ValidationFailure=0, Cancelled=0, TailObserverFailure=0, TailObserverSampleCount=0, SendQueueBackpressureRetries=0,
                                     MeasurementDurationSeconds=20.01, WarmupDurationSeconds=5.01, DrainDurationSeconds=0.1,
                                     StopwatchFrequency=1000000000, WorkerCount=concurrency))
            (folder / 'raw' / name).write_text(json.dumps(dict(SourceCommit=SHA, SchemaVersion=1, AssemblyVersion='2.0.4.0',
                Workload='SharpLink.StreamLoadTest' if workload == 'streams' else 'SharpLink.LoadTest', Configuration=config, Results=rows)))
    def tearDown(self):
        self.tmp.cleanup()
    def result(self):
        return v.validate(self.root, SHA, CONTEXT, SCRIPTHASHES)
    def mutate(self, fn, pattern='*/raw/*unary-0.json'):
        path = next(self.root.glob(pattern))
        data = json.loads(path.read_text())
        fn(data)
        path.write_text(json.dumps(data))
        return path
    def test_complete(self):
        r = self.result()
        self.assertTrue(r['MatrixPassed'], r['Failures'])
        self.assertEqual((r['Files'], r['Stages']), (729, 1863))
    def test_missing_file(self):
        next(self.root.glob('*/raw/*.json')).unlink()
        self.assertFalse(self.result()['MatrixPassed'])
    def test_duplicate_stage(self):
        self.mutate(lambda d: d['Results'].append(d['Results'][0]))
        self.assertFalse(self.result()['MatrixPassed'])
    def test_wrong_sha(self):
        self.mutate(lambda d: d.update(SourceCommit='b'*40))
        self.assertFalse(self.result()['MatrixPassed'])
    def test_short_measurement(self):
        self.mutate(lambda d: d['Configuration'].update(DurationSeconds=10))
        self.assertFalse(self.result()['MatrixPassed'])
    def test_incomplete_samples(self):
        self.mutate(lambda d: d['Results'][0].update(SampleCount=99))
        self.assertFalse(self.result()['MatrixPassed'])
    def test_unexpected_failure(self):
        self.mutate(lambda d: d['Results'][0].update(Failure=1))
        self.assertFalse(self.result()['MatrixPassed'])
    def test_apparent_saturation_is_not_reason_specific_proof(self):
        def change(d):
            d['Results'][0].update(Failure=2, OperationsCompleted=102, OperationsStartedDuringMeasurement=102,
                                   TopFailures='SharpLinkException[ResourceExhausted]:2')
        path=self.mutate(change, '*/raw/*oneway-backpressure.json')
        (path.parent.parent/'run.log').write_text('[FailureDetail:SharpLinkException[ResourceExhausted]] SharpLinkException: queue full (send_queue_capacity)\n')
        r=self.result()
        self.assertFalse(r['MatrixPassed'])
    def test_unclassified_resource_exhaustion(self):
        def change(d):
            d['Results'][0].update(Failure=2, OperationsCompleted=102, OperationsStartedDuringMeasurement=102,
                                   TopFailures='SharpLinkException[ResourceExhausted]:2')
        self.mutate(change, '*/raw/*oneway-backpressure.json')
        self.assertFalse(self.result()['MatrixPassed'])

    def test_detail_from_another_stage_cannot_authorize_failure(self):
        def change(d):
            d['Results'][1].update(Failure=2, OperationsCompleted=102, OperationsStartedDuringMeasurement=102,
                                   TopFailures='SharpLinkException[ResourceExhausted]:2')
        path = self.mutate(change, '*/raw/*oneway-backpressure.json')
        (path.parent.parent/'run.log').write_text('[Client] warmup 5s @ c=1\n[FailureDetail:SharpLinkException[ResourceExhausted]] queue full (send_queue_capacity)\n[Result] op=oneway c=32 fail=2\n')
        self.assertFalse(self.result()['MatrixPassed'])

    def test_fractional_counters(self):
        self.mutate(lambda d: d['Results'][0].update(Success=100.5, SampleCount=100.5, OperationsCompleted=100.5,
                                                     OperationsStartedDuringMeasurement=100.5, Qps=100.5/20.01))
        self.assertFalse(self.result()['MatrixPassed'])

    def test_nonfinite_qps(self):
        self.mutate(lambda d: d['Results'][0].update(Qps=float('nan')))
        self.assertFalse(self.result()['MatrixPassed'])

    def test_inconsistent_qps(self):
        self.mutate(lambda d: d['Results'][0].update(Qps=100))
        self.assertFalse(self.result()['MatrixPassed'])

    def test_wrong_error_rate(self):
        self.mutate(lambda d: d['Results'][0].update(ErrorRatePercent=100))
        self.assertFalse(self.result()['MatrixPassed'])

    def test_missing_stream_correctness(self):
        self.mutate(lambda d: d['Results'][0].pop('ValidationFailure'), '*/raw/*streams.json')
        self.assertFalse(self.result()['MatrixPassed'])

    def test_boolean_counter(self):
        self.mutate(lambda d: d['Results'][0].update(Failure=False))
        self.assertFalse(self.result()['MatrixPassed'])

    def metadata(self, name, mutate):
        path = next(self.root.glob('*/' + name))
        data = json.loads(path.read_text())
        mutate(data)
        path.write_text(json.dumps(data))

    def test_provenance_identity_mismatches(self):
        for key, value in [('SourceCommit', 'b'*40), ('ToolingCommit', 'a'*40), ('RunId', '9'),
                           ('RunAttempt', '2'), ('FinalMain', False), ('MainCommit', 'b'*40)]:
            with self.subTest(key=key):
                path = next(self.root.glob('*/provenance.json'))
                original = path.read_text()
                self.metadata('provenance.json', lambda d: d.update({key: value}))
                self.assertFalse(self.result()['MatrixPassed'])
                path.write_text(original)

    def test_wrong_completion_sha(self):
        next(self.root.glob('*/completed.txt')).write_text('b'*40)
        self.assertFalse(self.result()['MatrixPassed'])

    def test_garbage_script_manifest(self):
        next(self.root.glob('*/canonical-scripts.sha256')).write_text('garbage')
        self.assertFalse(self.result()['MatrixPassed'])

    def test_wrong_canonical_script_hash(self):
        path = next(self.root.glob('*/canonical-scripts.sha256'))
        path.write_text(path.read_text().replace('c'*64, 'e'*64))
        self.assertFalse(self.result()['MatrixPassed'])

    def test_garbage_dll_manifest(self):
        next(self.root.glob('*/dll-sha256.txt')).write_text('garbage')
        self.assertFalse(self.result()['MatrixPassed'])

    def test_wrong_actual_dll_sha(self):
        self.metadata('dll-identities.json', lambda d: d['Assemblies'][0].update(Sha256='e'*64))
        self.assertFalse(self.result()['MatrixPassed'])

    def test_wrong_actual_dll_version(self):
        self.metadata('dll-identities.json', lambda d: d['Assemblies'][0].update(InformationalVersion='2.0.4+'+'b'*40))
        self.assertFalse(self.result()['MatrixPassed'])

    def test_missing_actual_dll_identity(self):
        next(self.root.glob('*/dll-identities.json')).unlink()
        self.assertFalse(self.result()['MatrixPassed'])

    def test_duplicate_json_key(self):
        path = next(self.root.glob('*/raw/*unary-0.json'))
        path.write_text(path.read_text().replace('"SchemaVersion": 1', '"SchemaVersion": 1, "SchemaVersion": 1'))
        self.assertFalse(self.result()['MatrixPassed'])


class AggregateTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        spec = importlib.util.spec_from_file_location('aggregate', Path(__file__).with_name('aggregate-release-204-validation.py'))
        cls.module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(cls.module)

    def result(self, context=CONTEXT, result='success', missing=False):
        conclusions = {name: 'success' for name in self.module.REQUIRED_JOBS}
        conclusions['release-soak'] = result
        if missing:
            conclusions.pop('sampler')
        return self.module.aggregate(dict(**context, MatrixPassed=True), conclusions, context, [])

    def test_independent_failure_keeps_matrix_but_blocks_limited_gate(self):
        result = self.result(result='failure')
        self.assertTrue(result['MatrixPassed'])
        self.assertFalse(result['LimitedValidationPassed'])
        self.assertFalse(result['ReleaseEligible'])

    def test_missing_job_is_not_success(self):
        self.assertFalse(self.result(missing=True)['LimitedValidationPassed'])

    def test_candidate_only_cannot_be_final_evidence(self):
        result = self.result(context=dict(CONTEXT, FinalMain=False))
        self.assertTrue(result['LimitedValidationPassed'])
        self.assertFalse(result['FinalMainLimitedValidationPassed'])
        self.assertFalse(result['ReleaseEligible'])

    def test_external_release_gates_are_never_inferred(self):
        result = self.result()
        self.assertTrue(result['FinalMainLimitedValidationPassed'])
        self.assertFalse(result['ReleaseEligible'])
        self.assertTrue(result['UnverifiedExternalReleaseRequirements'])

    def test_missing_independent_reports_fail_closed(self):
        with tempfile.TemporaryDirectory() as directory:
            self.assertTrue(self.module.validate_independent_reports(Path(directory), SHA))

    def soak_report(self):
        return dict(Commit=SHA, WorkingTreeDirty=False, Status='Passed', IsFinal=True, ExitCode=0,
                    StartedUtc='2026-10-11T12:00:00+00:00', TimestampUtc='2026-10-11T12:10:00.1000000+00:00',
                    DurationSeconds=600, ActualElapsedSeconds=599.6, Concurrency=32, CheckpointIntervalSeconds=120,
                    RestartIntervalSeconds=60, Transport='Tcp', StopOnUnexpectedFailure=True, DumpOnFailure=True,
                    Drain=dict(Drained=True, Metrics={name: 0 for name in self.module.SOAK_METRICS}),
                    FinalMetrics={name: 0 for name in self.module.SOAK_METRICS}, UnexpectedFailures=0,
                    UnobservedTaskExceptions=0, ClientErrors=[], ServerErrors=[], Success=100,
                    ExpectedFailures=10, RestartCount=9, OperationAttempts={name: 10 for name in self.module.SOAK_OPERATIONS})

    def test_canonical_soak_wall_window_allows_599_6_post_warmup(self):
        failures, timing = self.module.validate_soak_report(self.soak_report(), SHA, 'Tcp')
        self.assertEqual(failures, [])
        self.assertEqual(timing['ActualElapsedSeconds'], 599.6)
        self.assertGreaterEqual(timing['WallElapsedSeconds'], 600)

    def test_short_total_soak_wall_window_fails(self):
        report = self.soak_report()
        report['TimestampUtc'] = '2026-10-11T12:09:59.9000000+00:00'
        self.assertTrue(self.module.validate_soak_report(report, SHA, 'Tcp')[0])

    def test_nonfinite_post_warmup_interval_fails(self):
        report = self.soak_report()
        report['ActualElapsedSeconds'] = float('nan')
        self.assertTrue(self.module.validate_soak_report(report, SHA, 'Tcp')[0])

    def test_missing_soak_operation_fails(self):
        report = self.soak_report()
        report['OperationAttempts'].pop('OneWay')
        self.assertTrue(self.module.validate_soak_report(report, SHA, 'Tcp')[0])

    def test_soak_resource_leak_fails_despite_drained_flag(self):
        report = self.soak_report()
        report['Drain']['Metrics']['sharplink.requests.pending'] = 1
        self.assertTrue(self.module.validate_soak_report(report, SHA, 'Tcp')[0])


class DependencyTests(unittest.TestCase):
    def setUp(self):
        spec = importlib.util.spec_from_file_location('dependencies', Path(__file__).with_name('validate-release-204-dependencies.py'))
        self.module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(self.module)
        self.tmp = tempfile.TemporaryDirectory()
        self.root = Path(self.tmp.name)
        (self.root / 'Sharplink.slnx').write_text('<Solution><Project Path="src/Probe.csproj" /></Solution>')
        for kind in ['vulnerable', 'deprecated']:
            (self.root / (kind + '.json')).write_text(json.dumps(dict(version=1, parameters='--include-transitive --' + kind,
                sources=['https://api.nuget.org/v3/index.json'], projects=[dict(path='src/Probe.csproj', frameworks=[dict(framework='net10.0')])])))
            (self.root / (kind + '.stderr')).write_text('')

    def tearDown(self):
        self.tmp.cleanup()

    def change(self, kind, callback):
        path = self.root / (kind + '.json')
        data = json.loads(path.read_text())
        callback(data)
        path.write_text(json.dumps(data))

    def result(self):
        return self.module.validate(self.root, self.root)

    def test_clean_complete_scan(self):
        self.assertTrue(self.result()['DependencyScansPassed'])

    def test_vulnerable_transitive_package(self):
        self.change('vulnerable', lambda d: d['projects'][0]['frameworks'][0].update(transitivePackages=[dict(id='Bad.Package', vulnerabilities=[dict(severity='Low')])]))
        self.assertFalse(self.result()['DependencyScansPassed'])

    def test_deprecated_dependency(self):
        self.change('deprecated', lambda d: d['projects'][0]['frameworks'][0].update(topLevelPackages=[dict(id='Old.Package', deprecationReasons=['Legacy'])]))
        self.assertFalse(self.result()['DependencyScansPassed'])

    def test_missing_project(self):
        self.change('vulnerable', lambda d: d.update(projects=[]))
        self.assertFalse(self.result()['DependencyScansPassed'])

    def test_scan_error(self):
        self.change('vulnerable', lambda d: d.update(problems=[dict(level='Error', text='source unavailable')]))
        self.assertFalse(self.result()['DependencyScansPassed'])

    def test_scan_stderr(self):
        (self.root / 'vulnerable.stderr').write_text('warning: audit source unreachable')
        self.assertFalse(self.result()['DependencyScansPassed'])

if __name__ == '__main__':
    unittest.main()
