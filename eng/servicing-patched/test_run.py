import copy
import hashlib
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import run


class Controls(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.summary = run.load_module('test_summary', run.HERE / 'summarize-codec-servicing-evidence.py', run.SUMMARY_HASH)
        self.policy = {'Byte': ('no-padding', False, False), 'Int16': ('no-padding', False, True)}
        self.ids = {'baseline': ('10.0.114', '10.0.14'), 'latest': ('10.0.200', '10.0.14')}
        self.make_evidence()

    def make_evidence(self):
        self.manifests = {}
        for lane in run.LANES:
            directory = self.root / 'producers' / lane
            directory.mkdir(parents=True)
            manifest = dict(schemaVersion=1, sharpLinkCommit=run.SOURCE, sdkVersion=self.ids[lane][0], runtimeVersion=self.ids[lane][1],
                frameworkDescription='.NET 10.0.14', osVersion='Linux test', osArchitecture='x64', compilationMode='JIT',
                targetFramework='net10.0', runtimeFamily='CoreCLR', runtimeFamilySource='runtime-probe', runtimeIdentifier='linux-x64',
                executionEnvironment='hosted-desktop', os='linux', processArchitecture='x64', pointerSize=8, isLittleEndian=True,
                platformTag=self.summary.PLATFORM_TAG, cases=[], fixtureRegistry=[])
            for index, (key, (category, native, segmented)) in enumerate(self.policy.items(), 1):
                wire = bytes([index] * index)
                (directory / (key + '.bin')).write_bytes(wire)
                manifest['cases'].append(dict(id=key, category=category, codecPath='production', size=index,
                    fieldOffsets={}, wireFile=key + '.bin', wireSha256=hashlib.sha256(wire).hexdigest(), expectedLogicalValue=key))
                manifest['fixtureRegistry'].append(dict(id=key, category=category, nativeWidth=native))
            self.manifests[lane] = manifest
            (directory / 'manifest.json').write_text(json.dumps(manifest))
        for consumer in run.LANES:
            for producer in run.LANES:
                rows = []
                for case in self.manifests[producer]['cases']:
                    rows.append(dict(fixture=case['id'], producer=self.summary.PLATFORM_TAG, consumer=self.summary.PLATFORM_TAG,
                        category=case['category'], codecPath=case['codecPath'], producerSize=case['size'], producerFieldOffsets={},
                        producerWireHash=case['wireSha256'], expectedLogicalValue=case['expectedLogicalValue'],
                        producerPointerSize=8, consumerPointerSize=8, blocking=False, crossDeserializeResult=True,
                        logicalEquality=True, segmentedCrossDeserializeResult=True, segmentedLogicalEquality=True,
                        byteForByteEquality=True, classification='IDENTICAL_BYTES_AND_COMPATIBLE', firstDifferingByteOffset=None))
                self.write_report(consumer, producer, dict(schemaVersion=1, consumer=self.manifests[consumer], results=rows))

    def report_path(self, consumer='latest', producer='baseline'):
        return self.root / 'verifications' / consumer / ('from-' + producer) / 'verification.json'

    def write_report(self, consumer, producer, value):
        path = self.report_path(consumer, producer)
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(json.dumps(value))

    def validate(self):
        return run.validate_evidence(self.root, self.ids, self.policy, self.summary)

    def test_positive_same_runtime_distinct_sdk(self):
        result = self.validate()
        self.assertTrue(result['distinctSdkVersion'])
        self.assertFalse(result['distinctRuntimeVersion'])
        self.assertEqual((result['edgeCount'], result['resultCount']), (4, 8))
        self.assertNotIn('expectedLogicalValue', json.dumps(result))

    def test_semantic_segmented_blocking_identity_and_classification_controls(self):
        path = self.report_path()
        original = json.loads(path.read_text())
        for key, value in [('logicalEquality', False), ('crossDeserializeResult', False),
                           ('segmentedCrossDeserializeResult', False), ('segmentedLogicalEquality', False),
                           ('blocking', True), ('classification', 'WRONG'), ('producerWireHash', 'wrong'),
                           ('producerSize', 3), ('firstDifferingByteOffset', 0)]:
            with self.subTest(key=key):
                report = copy.deepcopy(original)
                report['results'][1][key] = value
                path.write_text(json.dumps(report))
                with self.assertRaises(ValueError):
                    self.validate()
        path.write_text(json.dumps(original))

    def test_compatible_byte_difference_is_counted(self):
        path = self.report_path()
        report = json.loads(path.read_text())
        row = report['results'][1]
        row.update(byteForByteEquality=False, classification='DIFFERENT_BYTES_BUT_CROSS_COMPATIBLE', firstDifferingByteOffset=0)
        path.write_text(json.dumps(report))
        result = self.validate()
        count = sum(edge['classifications']['DIFFERENT_BYTES_BUT_CROSS_COMPATIBLE'] for edge in result['edges'])
        self.assertEqual(count, 1)

    def test_missing_duplicate_and_unknown_results(self):
        path = self.report_path()
        original = json.loads(path.read_text())
        for rows in [original['results'][:1], [original['results'][0]] * 2, []]:
            value = copy.deepcopy(original)
            value['results'] = rows
            path.write_text(json.dumps(value))
            with self.assertRaises(ValueError):
                self.validate()

    def test_missing_edge(self):
        self.report_path().unlink()
        with self.assertRaises(ValueError):
            self.validate()

    def test_wire_tampering(self):
        (self.root / 'producers/baseline/Int16.bin').write_bytes(b'BAD')
        with self.assertRaises(ValueError):
            self.validate()

    def test_registry_omission_and_runtime_mismatch(self):
        original = self.manifests['baseline']
        for key, value in [('fixtureRegistry', original['fixtureRegistry'][:1]), ('runtimeVersion', '10.0.13'), ('sharpLinkCommit', 'other')]:
            changed = copy.deepcopy(original)
            changed[key] = value
            with self.assertRaises(ValueError):
                run.guard_manifest(changed, self.ids['baseline'], self.policy)

    def test_consumer_identity(self):
        path = self.report_path()
        value = json.loads(path.read_text())
        value['consumer']['runtimeVersion'] = '10.0.13'
        path.write_text(json.dumps(value))
        with self.assertRaises(ValueError):
            self.validate()

    def test_stable_patched_versions_only(self):
        for bad in ['10.0.114-preview', '11.0.1', '10.0.14\n', 'x']:
            with self.assertRaises(ValueError):
                run.version(bad)

    def install_metadata(self, sdk='10.0.114', runtime='10.0.14'):
        root = self.root / ('sdk-' + sdk)
        directory = root / 'sdk' / sdk
        directory.mkdir(parents=True)
        (root / 'dotnet').touch()
        (directory / 'Microsoft.NETCoreSdk.BundledVersions.props').write_text('<Project><PropertyGroup><BundledNETCoreAppPackageVersion>' + runtime + '</BundledNETCoreAppPackageVersion></PropertyGroup></Project>')
        (directory / 'dotnet.runtimeconfig.json').write_text(json.dumps({'runtimeOptions': {'framework': {'name': 'Microsoft.NETCore.App', 'version': runtime}}}))
        shared = root / 'shared/Microsoft.NETCore.App' / runtime
        shared.mkdir(parents=True)
        (shared / 'libcoreclr.so').touch()
        return root

    def test_sdk_floor_without_execution(self):
        self.assertEqual(run.sdk_identity(self.install_metadata(), True), self.ids['baseline'])
        for sdk, runtime in [('10.0.102', '10.0.2'), ('10.0.115', '10.0.13')]:
            with self.assertRaises(ValueError):
                run.sdk_identity(self.install_metadata(sdk, runtime))

    def test_polluted_environment_excluded(self):
        with patch.dict('os.environ', {'SECRET': 'canary', 'MSBuildSDKsPath': '/bad', 'DOTNET_STARTUP_HOOKS': '/bad'}):
            env = run.clean_env(self.root / 'home')
        self.assertNotIn('SECRET', env)
        self.assertNotIn('MSBuildSDKsPath', env)
        self.assertNotIn('DOTNET_STARTUP_HOOKS', env)
        self.assertEqual(env['MSBUILDDISABLENODEREUSE'], '1')

    def test_only_global_changes(self):
        source = self.root / 'source'
        source.mkdir()
        (source / 'global.json').write_text(json.dumps({'sdk': {'version': '10.0.102', 'allowPrerelease': False}, 'test': {'runner': 'unchanged'}}))
        (source / 'code.cs').write_text('unchanged')
        run.pin_copy(source, self.root / 'copy', ['global.json', 'code.cs'], '10.0.114')
        self.assertEqual((self.root / 'copy/code.cs').read_bytes(), (source / 'code.cs').read_bytes())
        self.assertEqual(json.loads((self.root / 'copy/global.json').read_text())['test'], {'runner': 'unchanged'})

    def test_owned_process_success_and_timeout(self):
        owner = run.load_module('owner_controls', run.HERE / 'package-smoke-process.py', run.HELPER_HASH)
        owner.enable_subreaper()
        logs = self.root / 'logs'
        logs.mkdir()
        env = run.clean_env(self.root / 'owner-home')
        out, err = owner.run_owned('success', ['/usr/bin/python3', '-c', 'print("private-canary")'], self.root, env, logs, 5)
        self.assertEqual(out.read_text().strip(), 'private-canary')
        with self.assertRaises(owner.CommandFailure) as caught:
            owner.run_owned('timeout', ['/usr/bin/python3', '-c', 'import time; time.sleep(60)'], self.root, env, logs, .1)
        self.assertTrue(caught.exception.outcome['timedOut'])
        self.assertFalse(caught.exception.outcome['remainingOwned'])

    def test_owned_process_nonzero_and_orphan_fail_closed(self):
        owner = run.load_module('owner_controls2', run.HERE / 'package-smoke-process.py', run.HELPER_HASH)
        owner.enable_subreaper()
        logs = self.root / 'logs'
        logs.mkdir()
        env = run.clean_env(self.root / 'owner-home')
        for label, code in [('nonzero', 'raise SystemExit(3)'), ('orphan', 'import subprocess; subprocess.Popen(["/usr/bin/python3", "-c", "import time; time.sleep(60)"])')]:
            with self.assertRaises(owner.CommandFailure) as caught:
                owner.run_owned(label, ['/usr/bin/python3', '-c', code], self.root, env, logs, 5)
            self.assertFalse(caught.exception.outcome['remainingOwned'])

    def test_failure_diagnostics_are_bounded_and_allowlisted(self):
        logs = self.root / 'diagnostics'
        logs.mkdir()
        (logs / 'baseline-build.stdout').write_text('SECRET /private/path CS1234 NU1903 error arbitrary-text')
        (logs / 'baseline-build.stderr').write_text('NETSDK1004 MSB1009')
        class Failure(Exception):
            outcome = {'exitCode': 1, 'timedOut': True, 'errors': ['SECRET'], 'cleanupErrors': ['PRIVATE'], 'remainingOwned': {123: 'hidden'}}
        run.stage('baseline-build')
        run.command_failure(Failure('SECRET'), logs, 'baseline-build')
        encoded = json.dumps(run.FAILURE)
        self.assertNotIn('SECRET', encoded)
        self.assertNotIn('private', encoded)
        self.assertNotIn('hidden', encoded)
        self.assertEqual(run.FAILURE['diagnosticCodes'], ['CS1234', 'MSB1009', 'NETSDK1004', 'NU1903'])
        self.assertEqual(run.FAILURE['cleanupErrorsCount'], 1)
        with self.assertRaises(ValueError):
            run.stage('arbitrary SECRET')
        run.stage('preflight')

    def test_pinned_helper_and_summary(self):
        run.load_module('owner_test', run.HERE / 'package-smoke-process.py', run.HELPER_HASH)
        with self.assertRaises(ValueError):
            run.load_module('wrong', run.HERE / 'run.py', run.HELPER_HASH)


if __name__ == '__main__':
    suite = unittest.defaultTestLoader.loadTestsFromTestCase(Controls)
    # Keep even failed test exception details out of public CI output.
    import io
    result = unittest.TextTestRunner(stream=io.StringIO()).run(suite)
    print(json.dumps({'controls': result.testsRun, 'failures': len(result.failures), 'errors': len(result.errors)}))
    raise SystemExit(not result.wasSuccessful())
