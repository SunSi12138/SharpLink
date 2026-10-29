"""Offline dual-source/package controls. Synthetic ZIPs and mocked commands only."""
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import zipfile
import prepare
from common import PRODUCTS
from prepare import materialize, package_identity, pin_runtime, verify_source, pack_arm

ARMS = ('baseline', 'candidate')
PINS = {'baselineSource': '3d76a3a8b55a37e24c62a073fe588cff3d3867be',
        'baselineTree': '1c24da3af090acd4d6cdbfb005a9e3bd95304e3a',
        'candidateSource': 'b3cf84da2853b8dc8d107a03755d3e415988485d',
        'candidateTree': '010116dd13bd0d5ed7cfe24771c69dc7e6b075f9'}


class PreparationTests(unittest.TestCase):
    def package(self, root, name='Sdk', version='3.0.0', source='a' * 40, generator=True):
        path = root / f'sharplink.{name.lower()}.{version}.nupkg'
        with zipfile.ZipFile(path, 'w') as archive:
            archive.writestr(f'SharpLink.{name}.nuspec', f'<package><metadata><id>SharpLink.{name}</id><version>{version}</version><repository commit="{source}" /></metadata></package>')
            archive.writestr(f'lib/net10.0/SharpLink.{name}.dll', ('dll-' + name + source).encode())
            if name == 'Sdk' and generator:
                archive.writestr('analyzers/dotnet/cs/SharpLink.Generator.dll', ('generator-' + source).encode())
        return path

    def test_wrong_package_version_source_and_name_rejected(self):
        with tempfile.TemporaryDirectory() as temp:
            path = self.package(Path(temp))
            package_identity(path, 'Sdk', '3.0.0', 'a' * 40)
            for name, version, source in [('Client','3.0.0','a'*40), ('Sdk','2.0.3','a'*40), ('Sdk','3.0.0','b'*40)]:
                with self.assertRaises(ValueError):
                    package_identity(path, name, version, source)

    def test_own_packages_and_generator_both_arms(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            results = {}
            for arm in ARMS:
                packages = root / (arm + '-packages'); packages.mkdir()
                source = PINS[arm + 'Source']
                for name in PRODUCTS:
                    self.package(packages, name, source=source)
                destination = root / arm
                results[arm] = materialize(packages, destination, '3.0.0', source)
                self.assertEqual(('generator-' + source).encode(), (destination/'SharpLink.Generator.dll').read_bytes())
                self.assertTrue(all(str(packages) in results[arm][name]['package'] for name in PRODUCTS))
            self.assertNotEqual(results['baseline']['Sdk']['generatorSha256'], results['candidate']['Sdk']['generatorSha256'])
            with self.assertRaisesRegex(ValueError, 'source commit'):
                materialize(root/'baseline-packages', root/'mixed-arm', '3.0.0', PINS['candidateSource'])

    def test_generator_required_in_own_sdk_package(self):
        with tempfile.TemporaryDirectory() as temp:
            path = self.package(Path(temp), generator=False)
            with self.assertRaises(KeyError):
                package_identity(path, 'Sdk', '3.0.0', 'a'*40)

    def test_multiple_nuspecs_rejected(self):
        with tempfile.TemporaryDirectory() as temp:
            path = self.package(Path(temp))
            with zipfile.ZipFile(path, 'a') as archive:
                archive.writestr('extra.nuspec', '<package/>')
            with self.assertRaisesRegex(ValueError, 'one package nuspec'):
                package_identity(path, 'Sdk', '3.0.0', 'a'*40)

    def test_missing_or_duplicate_packages_rejected(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            with self.assertRaises(ValueError):
                materialize(root, root/'missing', '3.0.0', 'a'*40)
            path = self.package(root, 'Abstractions')
            (root/'duplicate').mkdir(); (root/'duplicate'/path.name).write_bytes(path.read_bytes())
            with self.assertRaises(ValueError):
                materialize(root, root/'ambiguous', '3.0.0', 'a'*40)

    def test_exact_source_commit_tree_and_cleanliness_each_arm(self):
        with patch.object(prepare, 'ARMS', ARMS), patch.object(prepare, 'CONTRACT', PINS):
            for arm in ARMS:
                good = [PINS[arm+'Source'], PINS[arm+'Tree'], '']
                with patch.object(prepare, 'capture', side_effect=good):
                    verify_source(Path('/synthetic/'+arm), arm)
                for values in [('0'*40,), (good[0], '0'*40), (good[0], good[1], ' M src/file.cs')]:
                    with patch.object(prepare, 'capture', side_effect=values), self.assertRaises(ValueError):
                        verify_source(Path('/synthetic/'+arm), arm)
            with self.assertRaisesRegex(ValueError, 'Unknown'):
                verify_source(Path('/synthetic/unknown'), 'official')

    def test_pack_commands_are_independent_exact_source_and_serial(self):
        with tempfile.TemporaryDirectory() as temp, patch.object(prepare, 'CONTRACT', PINS), patch.object(prepare, 'verify_source') as verify, patch.object(prepare, 'run') as run:
            root = Path(temp)
            for arm in ARMS:
                packages = pack_arm(arm, root/('checkout-'+arm), Path('/synthetic/dotnet'), root/'cache', root, root/'offline.config')
                self.assertEqual(root/(arm+'-packages'), packages)
            calls = [list(map(str, call.args[0])) for call in run.call_args_list]
            packs = [command for command in calls if command[1] == 'pack']
            self.assertEqual(10, len(packs))
            for arm in ARMS:
                own = [command for command in packs if f'/{arm}-source/' in command[2]]
                self.assertEqual(5, len(own))
                for command in own:
                    self.assertIn(f'-p:RepositoryCommit={PINS[arm+"Source"]}', command)
                    self.assertIn(f'-p:SourceRevisionId={PINS[arm+"Source"]}', command)
                    self.assertIn(str(root/(arm+'-packages')), command)
                    self.assertIn('-m:1', command); self.assertIn('-nr:false', command)
                    self.assertIn('-p:UseSharedCompilation=false', command)
                archive = next(command for command in calls if command[:2] == ['git', 'archive'] and command[-1] == PINS[arm+'Source'])
                self.assertIn(str(root/(arm+'-source.tar')), archive)
            self.assertEqual(4, verify.call_count)

    def test_exact_two_framework_runtime_pin(self):
        import json
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp); path = root/'PhysicalTcpProbe.runtimeconfig.json'
            def save(frameworks):
                path.write_text(json.dumps({'runtimeOptions': {'frameworks': frameworks}}))
            core = {'name':'Microsoft.NETCore.App','version':'10.0.2'}
            asp = {'name':'Microsoft.AspNetCore.App','version':'10.0.2'}
            save([core, asp]); pin_runtime(root)
            result = json.loads(path.read_text())['runtimeOptions']
            self.assertEqual('Disable', result['rollForward'])
            self.assertEqual(['10.0.14']*2, [f['version'] for f in result['frameworks']])
            for wrong in ([core], [core, asp, core], [core, {'name':'unknown','version':'10.0.2'}]):
                save(wrong)
                with self.assertRaises(ValueError):
                    pin_runtime(root)

    def test_fixture_arm_contract_and_no_candidate_only_strict_guard(self):
        root = Path(__file__).parent
        adapter = (root/'fixture/probe/ArmAdapter.cs').read_text()
        for label in ('baseline-3d76a3a8', 'candidate-b3cf84da'):
            self.assertIn(label, adapter)
        self.assertIn('GeneratedApi = 5;', adapter)
        self.assertNotIn('GeneratedApi = 4;', adapter)
        self.assertIn('#if BASELINE && CANDIDATE', adapter)
        targets = (root/'fixture/Directory.Build.targets').read_text()
        self.assertIn("'$(Arm)' != 'BASELINE' and '$(Arm)' != 'CANDIDATE'", targets)
        program = (root/'fixture/probe/Program.cs').read_text()
        self.assertNotIn('strictCleanupExecuted && ArmAdapter.Name', program)
        self.assertIn('? await ColdChecks.Run', program)

    def test_preparation_has_no_published_package_dependency(self):
        source = Path(prepare.__file__).read_text()
        for obsolete in ('official-cache', 'official-provenance', 'dev-source', 'fetch_published', 'published.provenance'):
            self.assertNotIn(obsolete, source)
        for flag in ('baseline-source', 'candidate-source', 'dependency-cache', 'dotnet', 'output'):
            self.assertIn(f"'--{flag}'", source)
        warm = (Path(prepare.__file__).parent/'warm_cache.py').read_text()
        self.assertIn("verify_source(source, 'candidate')", warm)
        self.assertIn("CONTRACT['candidateSource']", warm)
        self.assertIn("'--disable-parallel', '-m:1', '-nr:false'", warm)
        self.assertIn('NUGET_HTTP_CACHE_PATH', warm); self.assertIn('NUGET_SCRATCH', warm)
        self.assertNotIn('subprocess.DEVNULL', warm)


if __name__ == '__main__':
    unittest.main()
