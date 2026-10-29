#!/usr/bin/env python3
"""Offline positive/negative controls for only the new provenance wrapper."""
import copy
import json
import os
import shutil
import sys
from zipfile import ZipFile
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch
import gate


class GateTests(unittest.TestCase):
    def setUp(self):
        self.value = dict(schema_version=1, source_sha=gate.SOURCE, source_tree=gate.TREE, tools_sha='a' * 40)

    def test_exact_request(self):
        self.assertEqual(gate.request(json.dumps(self.value)), self.value)

    def test_request_rejects_identity_schema_and_extra_fields(self):
        for key, value in [('source_sha', 'b' * 40), ('source_tree', 'b' * 40),
                           ('schema_version', True), ('schema_version', 2),
                           ('tools_sha', 'main'), ('tools_sha', None), ('unexpected', 'value')]:
            altered = {**self.value, key: value}
            with self.subTest(key=key, value=value), self.assertRaises(ValueError):
                gate.request(json.dumps(altered))

    def test_missing_fields_rejected(self):
        for key in self.value:
            altered = copy.copy(self.value)
            del altered[key]
            with self.subTest(key=key), self.assertRaises(ValueError):
                gate.request(json.dumps(altered))

    def test_duplicate_keys_rejected(self):
        text = json.dumps(self.value)[:-1] + ', "source_sha": "' + gate.SOURCE + '"}'
        with self.assertRaisesRegex(ValueError, 'duplicate'):
            gate.request(text)

    def test_source_exact_clean_identity(self):
        with patch.object(gate, 'git', side_effect=[gate.SOURCE, gate.TREE, '']):
            gate.verify_source(Path('.'))

    def test_source_sha_tree_dirty_rejected(self):
        for answers in [('bad',), (gate.SOURCE, 'bad'), (gate.SOURCE, gate.TREE, ' M src/foo.cs'),
                        (gate.SOURCE, gate.TREE, '?? untracked.py')]:
            with self.subTest(answers=answers), patch.object(gate, 'git', side_effect=answers), self.assertRaises(ValueError):
                gate.verify_source(Path('.'))

    def fixture(self, root):
        def run(*args):
            return subprocess.check_output(['git', '-C', str(root), *args], text=True, stderr=subprocess.DEVNULL).strip()
        run('init', '-q')
        run('config', 'user.name', 'Offline Fixture')
        run('config', 'user.email', 'fixture@example.invalid')
        (root / 'tool.py').write_text('# reviewed tool\n')
        run('add', '.')
        run('commit', '-qm', 'tools')
        tools_sha = run('rev-parse', 'HEAD')
        self.value['tools_sha'] = tools_sha
        path = root / gate.REQUEST
        path.parent.mkdir(parents=True)
        path.write_text(json.dumps(self.value))
        run('add', '.')
        run('commit', '-qm', 'request only')
        return run

    def test_real_request_only_commit(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            run = self.fixture(root)
            self.assertEqual(gate.verify_tools(root, run('rev-parse', 'HEAD'), gate.BRANCH), self.value)

    def test_wrong_branch_and_workflow_checkout_rejected(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            run = self.fixture(root)
            for sha, ref in [(run('rev-parse', 'HEAD'), 'refs/heads/main'), ('c' * 40, gate.BRANCH)]:
                with self.subTest(sha=sha, ref=ref), self.assertRaises(ValueError):
                    gate.verify_tools(root, sha, ref)

    def test_request_cannot_smuggle_product_or_tool_edit(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            run = self.fixture(root)
            (root / 'tool.py').write_text('# replaced after review\n')
            run('add', '.')
            run('commit', '--amend', '--no-edit', '-q')
            with self.assertRaisesRegex(ValueError, 'only add request'):
                gate.verify_tools(root, run('rev-parse', 'HEAD'), gate.BRANCH)

    def test_dirty_tools_rejected(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            run = self.fixture(root)
            (root / 'untracked').write_text('unexpected')
            with self.assertRaisesRegex(ValueError, 'dirty'):
                gate.verify_tools(root, run('rev-parse', 'HEAD'), gate.BRANCH)

    def test_wrong_tools_parent_rejected(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            run = self.fixture(root)
            self.value['tools_sha'] = 'a' * 40
            (root / gate.REQUEST).write_text(json.dumps(self.value))
            run('add', '.')
            run('commit', '--amend', '--no-edit', '-q')
            with self.assertRaisesRegex(ValueError, 'reviewed tools parent'):
                gate.verify_tools(root, run('rev-parse', 'HEAD'), gate.BRANCH)


class SummaryTests(unittest.TestCase):
    def fixture(self, root):
        source = Path(__file__).resolve().parents[2]
        (root / 'eng').mkdir()
        for name in ('run-packaged-trim-smoke.py', 'verify-package-identities.py'):
            shutil.copyfile(source / 'eng' / name, root / 'eng' / name)
        def git(*args):
            return subprocess.check_output(['git', '-C', str(root), *args], text=True, stderr=subprocess.DEVNULL).strip()
        git('init', '-q')
        git('config', 'user.name', 'Offline Fixture')
        git('config', 'user.email', 'fixture@example.invalid')
        git('add', '.')
        git('commit', '-qm', 'synthetic fixture, not product evidence')
        commit = git('rev-parse', 'HEAD')
        packages = ('Abstractions','Client','Compression.Zstd','GenerationControl','Hosting','Runtime','Sdk','Serializer.SharpPack','Server')
        feed = root / 'artifacts/nuget'
        inventory = root / 'artifacts/public-api/current'
        feed.mkdir(parents=True)
        inventory.mkdir(parents=True)
        for suffix in packages:
            name = 'SharpLink.' + suffix
            for extension in ('nupkg','snupkg'):
                with ZipFile(feed / (name + '.3.0.0.' + extension), 'w') as archive:
                    archive.writestr(name + '.nuspec', f'<package><metadata><id>{name}</id><version>3.0.0</version><repository commit="{commit}" /></metadata></package>')
                    archive.writestr('lib/net10.0/' + name + '.dll', b'synthetic assembly; no PE parsing claimed')
            (inventory / (name + '.assembly.json')).write_text(json.dumps(dict(Name=name, Version='3.0.0.0', InformationalVersion='3.0.0+'+commit)))
        report = root / 'artifacts/packaged-trim/linux-x64-fixture/evidence.json'
        report.parent.mkdir(parents=True)
        work = root / 'artifacts/consumers'
        consumers = []
        for name, mode, aot in (('PackageTrimSmoke','full',False),('PackagePartialTrimSmoke','partial',False),('PackageAotSmoke','full',True)):
            assets = work / name / 'build/project.assets.json'
            assets.parent.mkdir(parents=True)
            assets.write_text('{}')
            consumers.append(dict(name='SharpLink.'+name, trimMode=mode, publishAot=aot, executableSha256='0'*64, status='passed'))
        report.write_text(json.dumps(dict(commit=commit, version='3.0.0', rid='linux-x64', status='passed', workDirectory=str(work), consumers=consumers)))
        fake = root / 'fake-dotnet'
        fake.write_text('#!' + sys.executable + '\nimport json\nprint(json.dumps({"Items":{kind:[{"Identity":kind,"NuGetPackageId":kind,"NuGetPackageVersion":"10.0.2"}] for kind in ("ResolvedILCompilerPack","ResolvedRuntimePack")}}))\n')
        fake.chmod(0o700)
        return report, fake

    def run_summary(self, root, fake):
        return subprocess.run([sys.executable, str(Path(__file__).with_name('summary.py')), str(root)],
                              env={**os.environ, 'DOTNET':str(fake), 'PYTHONDONTWRITEBYTECODE':'1'}, text=True, capture_output=True)

    def test_synthetic_complete_summary(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            report, fake = self.fixture(root)
            result = self.run_summary(root, fake)
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
            self.assertEqual(result.stdout.count('PACKAGE_IDENTITY '), 18)
            self.assertEqual(result.stdout.count('ASSEMBLY_IDENTITY '), 9)
            self.assertEqual(result.stdout.count('CONSUMER_RESULT '), 3)

    def test_wrong_evidence_and_duplicate_consumers_rejected(self):
        for kind in ('commit','duplicate','failed'):
            with self.subTest(kind=kind), tempfile.TemporaryDirectory() as temporary:
                root = Path(temporary)
                report, fake = self.fixture(root)
                value = json.loads(report.read_text())
                if kind == 'commit':
                    value['commit'] = 'b'*40
                elif kind == 'duplicate':
                    value['consumers'][2] = value['consumers'][0]
                else:
                    value['status'] = 'failed'
                report.write_text(json.dumps(value))
                self.assertNotEqual(self.run_summary(root, fake).returncode, 0)

    def test_missing_pack_version_rejected(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            report, fake = self.fixture(root)
            fake.write_text(fake.read_text().replace(',"NuGetPackageVersion":"10.0.2"',''))
            result = self.run_summary(root, fake)
            self.assertNotEqual(result.returncode, 0)
            self.assertIn('lacks actual package ID/version', result.stderr)


    def test_missing_duplicate_assets_and_wrong_assembly_rejected(self):
        for kind in ('missing-assets','duplicate-assets','wrong-assembly'):
            with self.subTest(kind=kind), tempfile.TemporaryDirectory() as temporary:
                root = Path(temporary)
                report, fake = self.fixture(root)
                assets = root / 'artifacts/consumers/PackageTrimSmoke/build/project.assets.json'
                if kind == 'missing-assets':
                    assets.unlink()
                elif kind == 'duplicate-assets':
                    other = assets.parent / 'extra/project.assets.json'
                    other.parent.mkdir()
                    other.write_text('{}')
                else:
                    identity = root / 'artifacts/public-api/current/SharpLink.Runtime.assembly.json'
                    value = json.loads(identity.read_text())
                    value['InformationalVersion'] = '3.0.0+'+'b'*40
                    identity.write_text(json.dumps(value))
                self.assertNotEqual(self.run_summary(root, fake).returncode, 0)

    def test_missing_runtime_compiler_malformed_and_nonzero_msbuild_rejected(self):
        for kind in ('runtime','compiler','malformed','nonzero'):
            with self.subTest(kind=kind), tempfile.TemporaryDirectory() as temporary:
                root = Path(temporary)
                report, fake = self.fixture(root)
                program = fake.read_text()
                if kind == 'runtime':
                    program = program.replace('("ResolvedILCompilerPack","ResolvedRuntimePack")','("ResolvedILCompilerPack",)')
                elif kind == 'compiler':
                    program = program.replace('("ResolvedILCompilerPack","ResolvedRuntimePack")','("ResolvedRuntimePack",)')
                elif kind == 'malformed':
                    program = '#!' + sys.executable + '\nprint("invalid JSON")\n'
                else:
                    program = '#!' + sys.executable + '\nprint("native evaluation failed")\nraise SystemExit(17)\n'
                fake.write_text(program)
                self.assertNotEqual(self.run_summary(root, fake).returncode, 0)


if __name__ == '__main__':
    unittest.main(verbosity=2)
