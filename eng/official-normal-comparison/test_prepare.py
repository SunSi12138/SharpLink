"""Offline package/provenance controls; synthetic ZIPs only, never build or restore."""
import hashlib
from pathlib import Path
import tempfile
import unittest
import zipfile
from common import PRODUCTS
from prepare import materialize, package_identity, pin_runtime
from fetch_published import verify


class PreparationTests(unittest.TestCase):
    def package(self, root, name='Sdk', version='2.0.3', source='a' * 40):
        path = root / f'sharplink.{name.lower()}.{version}.nupkg'
        with zipfile.ZipFile(path, 'w') as archive:
            archive.writestr(f'SharpLink.{name}.nuspec', f'<package><metadata><id>SharpLink.{name}</id><version>{version}</version><repository commit="{source}" /></metadata></package>')
            archive.writestr(f'lib/net10.0/SharpLink.{name}.dll', ('dll-' + name + source).encode())
            if name == 'Sdk': archive.writestr('analyzers/dotnet/cs/SharpLink.Generator.dll', ('generator-' + source).encode())
        return path

    def test_wrong_package_version_source_and_name_rejected(self):
        with tempfile.TemporaryDirectory() as temp:
            path = self.package(Path(temp))
            package_identity(path, 'Sdk', '2.0.3', 'a' * 40)
            for name, version, source in [('Client','2.0.3','a'*40), ('Sdk','3.0.0','a'*40), ('Sdk','2.0.3','b'*40)]:
                with self.assertRaises(ValueError): package_identity(path, name, version, source)

    def test_own_generator_and_published_byte_provenance(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp); provenance = {'packages': {}}
            for name in PRODUCTS:
                path = self.package(root, name)
                dll, generator = package_identity(path, name, '2.0.3', 'a'*40)
                entry = dict(package_sha256=hashlib.sha256(path.read_bytes()).hexdigest(), dll_sha256=hashlib.sha256(dll).hexdigest())
                if generator: entry['generator_sha256'] = hashlib.sha256(generator).hexdigest()
                provenance['packages']['SharpLink.' + name] = entry
            materialize(root, root/'good', '2.0.3', 'a'*40, provenance)
            self.assertEqual(b'generator-'+b'a'*40, (root/'good/SharpLink.Generator.dll').read_bytes())
            provenance['packages']['SharpLink.Sdk']['generator_sha256'] = '0'*64
            with self.assertRaisesRegex(ValueError, 'generator_sha256'):
                materialize(root, root/'mixed-generator', '2.0.3', 'a'*40, provenance)

    def test_missing_or_duplicate_packages_rejected(self):
        with tempfile.TemporaryDirectory() as temp:
            root=Path(temp)
            with self.assertRaises(ValueError): materialize(root, root/'missing', '2.0.3', 'a'*40)
            path=self.package(root, 'Abstractions')
            (root/'duplicate').mkdir(); (root/'duplicate'/path.name).write_bytes(path.read_bytes())
            with self.assertRaises(ValueError): materialize(root, root/'ambiguous', '2.0.3', 'a'*40)

    def test_exact_two_framework_runtime_pin(self):
        import json
        with tempfile.TemporaryDirectory() as temp:
            root=Path(temp); path=root/'PhysicalTcpProbe.runtimeconfig.json'
            def save(frameworks): path.write_text(json.dumps({'runtimeOptions': {'frameworks': frameworks}}))
            core={'name':'Microsoft.NETCore.App','version':'10.0.2'}
            asp={'name':'Microsoft.AspNetCore.App','version':'10.0.2'}
            save([core,asp]); pin_runtime(root)
            result=json.loads(path.read_text())['runtimeOptions']
            self.assertEqual('Disable',result['rollForward']); self.assertEqual(['10.0.14']*2,[f['version'] for f in result['frameworks']])
            for wrong in ([core], [core,asp,core], [core,{'name':'unknown','version':'10.0.2'}]):
                save(wrong)
                with self.assertRaises(ValueError): pin_runtime(root)

if __name__=='__main__': unittest.main()
