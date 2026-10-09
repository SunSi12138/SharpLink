#!/usr/bin/env python3
import copy
import hashlib
import importlib.util
import json
from pathlib import Path
import tarfile
import tempfile
import unittest
from unittest.mock import patch

HERE = Path(__file__).resolve().parent


def module(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    value = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(value)
    return value


runner = module('profile_runner', HERE / 'run.py')
packager = module('profile_packager', HERE / 'package.py')
old_tests = module('formal_fixture', HERE.parent / 'perf/test-read-ownership-harness.py')


def fixture(root):
    old_tests.HarnessTests().fixture(root)
    original = json.loads((root / 'tcp-add-c1-1-baseline.json').read_text())
    manifest = json.loads((HERE / 'revisions.json').read_text())
    original['SourceCommit'] = manifest['baseline']
    original['Runtime'] = manifest['runtimeVersion']
    original['Configuration'].update(DurationSeconds=15, TailObserver=False)
    original['Results'][0].update(MeasurementDurationSeconds=15, Qps=100 / 15,
                                  TailObserverFailure=0, TailObserverSampleCount=0, SendQueueBackpressureRetries=0)
    metadata = dict(schemaVersion=1, diagnosticOnly=True, workload='tcp-add-c1', runId='test',
                    processId=42, runtimeVersion='10.0.12', stopwatchFrequency=1000000000,
                    beginTicks=1000000000, startedTicks=1000000000, stoppedTicks=16000000000,
                    endTicks=16001000000, operations=100)
    return manifest, original, metadata


class ProfileTests(unittest.TestCase):
    def test_plan_exact_latin_and_pair_order(self):
        plan = runner.plan()
        self.assertEqual(len(plan), 18)
        self.assertEqual(len({p['runId'] for p in plan}), 18)
        for arm in runner.ARMS:
            for pos in (1, 2, 3):
                self.assertEqual(sum(p['arm'] == arm and p['armPosition'] == pos for p in plan), 2)
            for block in (1, 2, 3):
                pair = [p for p in plan if p['arm'] == arm and p['block'] == block]
                self.assertEqual([p['mode'] for p in pair], ['traced', 'untraced'] if block == 2 else ['untraced', 'traced'])

    def test_manifest_pins(self):
        valid = json.loads((HERE / 'revisions.json').read_text())
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'manifest.json'
            for change in ({}, {'normalized': 'dev'}, {'baseline': valid['safe']}, {'blocks': 6},
                           {'durationSeconds': 6}, {'warmupSeconds': 1}, {'retries': 1},
                           {'runtimeVersion': '.NET 10.0.11'}, {'traceToolVersion': 'latest'}):
                runner.write(path, valid | change)
                if change:
                    with self.assertRaises(ValueError): runner.load_manifest(path)
                else: runner.load_manifest(path)

    def test_report_marker_contract_and_negative_controls(self):
        with tempfile.TemporaryDirectory() as directory:
            manifest, doc, marker = fixture(Path(directory))
            result = runner.validate_report(doc, marker, manifest, manifest['baseline'], 'test', 42)
            self.assertEqual(result['operations'], 100)
            # Stopwatch.GetElapsedTime rounds to100ns TimeSpan ticks; preserve that exact conversion tolerance.
            rounded = copy.deepcopy(marker); rounded['stoppedTicks'] += 45; rounded['endTicks'] += 45
            runner.validate_report(doc, rounded, manifest, manifest['baseline'], 'test', 42)
            rounded['stoppedTicks'] += 1000
            with self.assertRaises(ValueError): runner.validate_report(doc, rounded, manifest, manifest['baseline'], 'test', 42)
            mutations = [lambda d, m: d.update(SourceCommit='bad'), lambda d, m: d.update(Runtime='.NET 10.0.11'),
                         lambda d, m: d.update(ProcessorCount=2), lambda d, m: d['Configuration'].update(TailObserver=True),
                         lambda d, m: d['Results'][0].update(Failure=1), lambda d, m: d['Results'][0].update(OperationsCompleted=99),
                         lambda d, m: d['Results'][0]['Evidence'].update(CpuMilliseconds=-1),
                         lambda d, m: m.update(runId='wrong'), lambda d, m: m.update(processId=43),
                         lambda d, m: m.update(operations=99), lambda d, m: m.update(stoppedTicks=17000000000),
                         lambda d, m: m.update(endTicks=19000000000), lambda d, m: m.update(stopwatchFrequency=0)]
            for mutation in mutations:
                d, m = copy.deepcopy(doc), copy.deepcopy(marker)
                mutation(d, m)
                with self.assertRaises(ValueError): runner.validate_report(d, m, manifest, manifest['baseline'], 'test', 42)

    def test_runtime_environment_does_not_publish_unknown_values(self):
        self.assertEqual(runner.tuning_environment({'DOTNET_NOLOGO': '1', 'OTHER_SECRET': 'hidden'}), {'DOTNET_NOLOGO': '1'})
        for name in ('DOTNET_TieredPGO', 'COMPlus_ReadyToRun', 'CORECLR_PROFILER_PATH', 'DOTNET_SECRET'):
            with self.assertRaises(ValueError) as error:
                runner.tuning_environment({name: 'SENSITIVE_VALUE'})
            self.assertNotIn('SENSITIVE_VALUE', str(error.exception))

    def test_boundary_replacement_fails_closed(self):
        self.assertEqual(runner.PREPARE.once('abc', 'b', 'd'), 'adc')
        for original in ('ac', 'abbc'):
            with self.assertRaises(ValueError): runner.PREPARE.once(original, 'b', 'd')

    def test_all_eighteen_raw_points_and_known_paired_math(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            manifest, original, original_marker = fixture(root)
            runner.write(root / 'provenance.json', {'manifest': manifest, 'plan': runner.plan(),
                         'arms': {a: {'commit': manifest[a]} for a in runner.ARMS}})
            entries = []
            for planned in runner.plan():
                row = dict(planned, state='completed', identity=manifest[planned['arm']],
                           directory='raw/' + planned['runId'], collection={'exitCode': 0, 'launcherPid': 42},
                           scenarioValidation={'valid': True}, parser={'exitCode': 0})
                directory = root / row['directory']; directory.mkdir(parents=True)
                doc, marker = copy.deepcopy(original), copy.deepcopy(original_marker)
                doc['SourceCommit'] = row['identity']; marker['runId'] = row['runId']
                if row['arm'] == 'normalized':
                    result = doc['Results'][0]
                    result.update(Success=200, OperationsStartedDuringMeasurement=200, OperationsCompleted=200,
                                  SampleCount=200, Qps=200 / 15, P99Us=40, P999Us=50)
                    result['Evidence'].update(CpuMilliseconds=4, AllocatedBytes=4000)
                    marker['operations'] = 200
                normal = runner.validate_report(doc, marker, manifest, row['identity'], row['runId'], 42)
                for name, data in [('report.json', doc), ('metadata.json', marker), ('normalized.json', normal)]:
                    runner.write(directory / name, data)
                (directory / 'collect.log').write_text('completed')
                if row['mode'] == 'traced':
                    (directory / 'profile.nettrace').write_bytes(b'synthetic fixture, never actual trace evidence')
                    (directory / 'parse.log').write_text('fixture')
                    runner.write(directory / 'profile.json', {'schemaVersion': 1, 'diagnosticOnly': True,
                                 'calibration': False, 'identity': row['identity'], 'runId': row['runId'],
                                 'processId': 42, 'operations': normal['operations'], 'runtimeVersion': '10.0.12',
                                 'rawEventsLost': 0, 'convertedEventsLost': 0})
                row['files'] = {str(p.relative_to(root)): {'sha256': runner.digest(p), 'bytes': p.stat().st_size}
                                for p in directory.iterdir()}
                entries.append(row)
            runner.write(root / 'execution-index.json', entries)
            runner.summarize(root)
            summary = json.loads((root / 'observer-controls.json').read_text())
            self.assertEqual(summary['completedProcesses'], 18)
            for key in ('qps', 'cpuMicrosecondsPerRpc', 'allocatedBytesPerRpc', 'p99Us'):
                self.assertEqual(summary['pairedArmContrasts']['untraced/normalized-versus-baseline'][key]['percentDelta']['points'], [100.0] * 3)
            self.assertTrue(summary['sourceCostAttributionInconclusiveDueToObserver'])  # safe/dev zero gap
            # Rehashing an edited normalized file must not hide disagreement with original raw data.
            path = root / entries[0]['directory'] / 'normalized.json'
            bad = json.loads(path.read_text()); bad['qps'] = 1e9; runner.write(path, bad)
            entries[0]['files'][str(path.relative_to(root))]['sha256'] = runner.digest(path)
            runner.write(root / 'execution-index.json', entries)
            with self.assertRaises(ValueError): runner.summarize(root)

    def test_parts_roundtrip_and_overflow_retained(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory); source = root / 'source'; source.mkdir()
            (source / 'a').write_bytes(bytes(range(256)) * 8)
            (source / 'b').write_text('retained raw evidence')
            with patch.object(packager, 'PART_BYTES', 128):
                index = packager.package(source, root / 'parts')
            archive = root / 'reassembled.tar.gz'
            archive.write_bytes(b''.join((root / 'parts' / p['file']).read_bytes() for p in index['parts']))
            self.assertEqual(packager.digest(archive), index['archiveSha256'])
            with tarfile.open(archive, 'r:gz') as tar:
                for item in tar:
                    self.assertEqual(tar.extractfile(item).read(), (source / item.name).read_bytes())
            with patch.object(packager, 'PART_BYTES', 128), patch.object(packager, 'MAX_PARTS', 1):
                with self.assertRaises(ValueError): packager.package(source, root / 'overflow')
            self.assertTrue((root / 'overflow/evidence.tar.gz').is_file())
            self.assertFalse(json.loads((root / 'overflow/index.json').read_text())['withinUploadPartLimit'])


if __name__ == '__main__':
    unittest.main()
