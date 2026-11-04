#!/usr/bin/env python3
"""Synthetic fail-closed tests; no production assembly or RPC execution."""
import copy
import importlib.util
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from zipfile import ZipFile
sys.dont_write_bytecode = True
spec = importlib.util.spec_from_file_location('grants_runner', Path(__file__).with_name('run.py'))
r = importlib.util.module_from_spec(spec); spec.loader.exec_module(r)


class RunnerTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory(); self.addCleanup(self.tmp.cleanup)
        self.root = Path(self.tmp.name)
        self.paths = {}
        self.rows = []
        for name, expected in r.EXPECTED['assemblies'].items():
            path = self.root / (name + '.dll'); path.write_bytes(name.encode()); self.paths[name] = path
            self.rows.append(dict(FileName=path.name, Sha256=r.digest(path), Identity=dict(Name=name,Version='3.0.0.0'),
                InformationalVersion='3.0.0+'+r.EXPECTED['sourceCommit'], IsReferenceAssembly=False,
                InternalsVisibleTo=expected['internalsVisibleTo'][:], IgnoresAccessChecksTo=[]))
        self.inventory = dict(SchemaVersion=1, Assemblies=self.rows)

    def test_accept_exact_grant_sets(self):
        self.assertEqual(10, len(r.validate_inventory(self.inventory, self.paths)))

    def test_accept_emitted_duplicate_count_changes(self):
        for transform in [lambda values: sorted(set(values)), lambda values: values + values]:
            inventory=copy.deepcopy(self.inventory)
            for row in inventory['Assemblies']:
                row['InternalsVisibleTo']=transform(row['InternalsVisibleTo'])
            self.assertEqual(10,len(r.validate_inventory(inventory,self.paths)))

    def test_reject_explicit_all_internals_visible(self):
        for value in [True,False]:
            inventory=copy.deepcopy(self.inventory)
            inventory['Assemblies'][0]['RecognizedAttributes']=[dict(
                TypeName='System.Runtime.CompilerServices.InternalsVisibleToAttribute',
                AllInternalsVisible=value)]
            with self.assertRaises(ValueError):r.validate_inventory(inventory,self.paths)

    def test_reject_duplicate_identity(self):
        self.rows[-1] = copy.deepcopy(self.rows[0])
        with self.assertRaises(ValueError): r.validate_inventory(self.inventory,self.paths)

    def test_reject_identity_and_metadata_mutations(self):
        for key, value in [('FileName','Other.dll'), ('Sha256','0'*64), ('InformationalVersion','3.0.0+old'),
                           ('IsReferenceAssembly',True), ('IgnoresAccessChecksTo',['Unexpected'])]:
            with self.subTest(key=key):
                inventory=copy.deepcopy(self.inventory);inventory['Assemblies'][0][key]=value
                with self.assertRaises(ValueError):r.validate_inventory(inventory,self.paths)
        inventory=copy.deepcopy(self.inventory);inventory['Assemblies'][0]['Identity']['Version']='2.0.3.0'
        with self.assertRaises(ValueError):r.validate_inventory(inventory,self.paths)

    def test_reject_missing_grant_or_added_key(self):
        for value in [[x for x in self.rows[0]['InternalsVisibleTo'] if x != 'SharpLink.Runtime'],self.rows[0]['InternalsVisibleTo']+['Friend, PublicKey=abc']]:
            inventory=copy.deepcopy(self.inventory);inventory['Assemblies'][0]['InternalsVisibleTo']=value
            with self.assertRaises(ValueError):r.validate_inventory(inventory,self.paths)

    def test_reject_wrong_schema_or_missing_rows(self):
        for inventory in [dict(SchemaVersion=2,Assemblies=self.rows),dict(SchemaVersion=1,Assemblies=self.rows[:-1])]:
            with self.assertRaises(ValueError):r.validate_inventory(inventory,self.paths)

    def test_analyzer_unique_exact_path(self):
        exact='analyzers/dotnet/cs/SharpLink.Generator.dll'
        for index, paths in enumerate([[exact],[],[exact,exact],[exact,exact.upper()],[exact,'analyzers/dotnet/vb/Other.dll']]):
            with self.subTest(paths=paths):
                package=self.root/(str(index)+'.zip')
                with ZipFile(package,'w') as archive:
                    for path in paths:archive.writestr(path,b'fixture')
                if paths==[exact]:self.assertEqual(r.extract_analyzer(package,self.root/'out.dll')['assembly_sha256'],r.digest(self.root/'out.dll'))
                else:
                    with self.assertRaises(ValueError):r.extract_analyzer(package,self.root/'out.dll')

    def test_cli_usage_nonzero_no_pass(self):
        proc=subprocess.run([sys.executable,str(Path(r.__file__))],capture_output=True,text=True,timeout=10)
        self.assertEqual(1,proc.returncode);self.assertIn('category=ValueError',proc.stdout);self.assertNotIn('PACKAGE_GRANTS_PASS',proc.stdout)

    def test_cli_non_ci_checkout_nonzero_no_pass(self):
        proc=subprocess.run([sys.executable,str(Path(r.__file__)),str(self.root)],capture_output=True,text=True,timeout=10)
        self.assertEqual(1,proc.returncode);self.assertIn('PACKAGE_GRANTS_FAIL',proc.stdout);self.assertNotIn('PACKAGE_GRANTS_PASS',proc.stdout)

if __name__=='__main__':unittest.main()
