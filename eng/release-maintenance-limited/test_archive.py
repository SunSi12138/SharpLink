import hashlib,json,tempfile,unittest
from pathlib import Path
from archive import archive_closure,validate_archive,CONFIGS

class ArchiveTests(unittest.TestCase):
    def fixture(self,root):
        build=root/'build';output=root/'output';output.mkdir();names=['HotCostProbe/release/SharpLink.UnitTests.dll','HotCostDynamic.Services/release/HotCostDynamic.Services.dll',*CONFIGS]
        binaries={}
        for name in names:
            path=build/'build/bin'/name;path.parent.mkdir(parents=True,exist_ok=True);path.write_bytes(name.encode());binaries[name]=hashlib.sha256(name.encode()).hexdigest()
        rows=[{'Network':{'LoadedAssemblies':[{'Path':str(build/'build/bin'/name),'Sha256':binaries[name]} for name in names if name.endswith('.dll')]}}]
        return build,output,binaries,rows
    def test_exact_original_bytes_and_configs(self):
        with tempfile.TemporaryDirectory() as temp:
            build,out,binaries,rows=self.fixture(Path(temp));archive_closure(rows,build,out,binaries);validate_archive(rows,str(build),out,binaries)
            self.assertEqual(json.loads((out/'archived-binary-closure.json').read_text()),binaries)
    def test_missing_tampered_extra_archive_rejected(self):
        for mutation in ('missing','tampered','extra','manifest'):
            with self.subTest(mutation=mutation),tempfile.TemporaryDirectory() as temp:
                build,out,binaries,rows=self.fixture(Path(temp));archive_closure(rows,build,out,binaries);target=out/'actual-binaries'/next(iter(binaries))
                if mutation=='missing':target.unlink()
                elif mutation=='tampered':target.write_bytes(b'bad')
                elif mutation=='extra':(out/'actual-binaries/extra.dll').write_bytes(b'bad')
                else:(out/'archived-binary-closure.json').write_text('{}')
                with self.assertRaises(ValueError):validate_archive(rows,str(build),out,binaries)
    def test_source_drift_and_missing_config_rejected(self):
        for mutation in ('source','config','empty','path'):
            with self.subTest(mutation=mutation),tempfile.TemporaryDirectory() as temp:
                build,out,binaries,rows=self.fixture(Path(temp))
                if mutation=='source':(build/'build/bin'/next(iter(binaries))).write_bytes(b'bad')
                elif mutation=='config':del binaries[CONFIGS[0]]
                elif mutation=='empty':rows[0]['Network']['LoadedAssemblies']=[]
                else:rows[0]['Network']['LoadedAssemblies'][0]['Path']=str(build/'build/bin/../escape.dll')
                with self.assertRaises(ValueError):archive_closure(rows,build,out,binaries)
