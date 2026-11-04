import copy
import hashlib
import importlib.util
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
spec=importlib.util.spec_from_file_location('source_checker',Path(__file__).with_name('check_source.py'))
r=importlib.util.module_from_spec(spec);spec.loader.exec_module(r)

class SourceTests(unittest.TestCase):
    def test_frozen_bytes_and_exact_anchor(self):
        with tempfile.TemporaryDirectory() as td:
            root=Path(td); (root/'source.cs').write_bytes(b'  anchor\nother\n')
            manifest={'files':[dict(path='source.cs',sha256=hashlib.sha256((root/'source.cs').read_bytes()).hexdigest(),anchors=[dict(line=1,text='anchor')])]}
            self.assertEqual(1,r.check_files(root,manifest))
            for field,value in [('sha256','0'*64),('path','../outside'),('path','/absolute')]:
                bad=copy.deepcopy(manifest);bad['files'][0][field]=value
                with self.assertRaises(ValueError):r.check_files(root,bad)
            for line,text in [(0,'anchor'),(3,'anchor'),(2,'anchor'),(1,'wrong')]:
                bad=copy.deepcopy(manifest);bad['files'][0]['anchors']=[dict(line=line,text=text)]
                with self.assertRaises(ValueError):r.check_files(root,bad)
            (root/'source.cs').write_bytes(b'changed\n')
            with self.assertRaises(ValueError):r.check_files(root,manifest)

    def test_cli_failure_no_raw(self):
        for args in [[],['/nonexistent-root']]:
            result=subprocess.run([sys.executable,r.__file__,*args],capture_output=True,text=True,timeout=15)
            self.assertEqual(1,result.returncode);self.assertIn('"status": "failed"',result.stdout)
            self.assertEqual('',result.stderr);self.assertNotIn('/nonexistent-root',result.stdout)

if __name__=='__main__':unittest.main()
