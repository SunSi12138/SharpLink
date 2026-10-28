#!/usr/bin/env python3
"""Offline harness tests: fake dotnet is a Python fixture, never starts CLR."""
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest
from validate_correctness import EXPECTED, validate

HERE = Path(__file__).resolve().parent

class HarnessTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        (self.root/'research').mkdir()
        (self.root/'eng').mkdir()
        (self.root/'sdk').mkdir()
        for name in ['run-correctness.sh', 'validate_correctness.py']:
            shutil.copy(HERE/name, self.root/'research'/name)
        (self.root/'eng'/'check-project-reference-boundaries.py').write_text(
            'import os,sys\nsys.exit(7 if os.getenv("FAIL_STAGE")=="architecture" else 0)\n')
        (self.root/'eng'/'check-maintainability.sh').write_text(
            '#!/bin/sh\n[ "${FAIL_STAGE:-}" != maintainability ]\n')
        mock = self.root/'sdk'/'dotnet'
        mock.write_text('''#!/usr/bin/env python3
import os,sys
expected = '''+repr(EXPECTED)+'''
args=sys.argv[1:]
if args[0]=='--info': sys.exit(4 if os.getenv('FAIL_STAGE')=='dotnet-info' else 0)
if args[0]=='build': sys.exit(5 if os.getenv('FAIL_STAGE')=='build' else 0)
name=args[-1].split('/')[3]
n=expected[name]
if os.getenv('FAIL_STAGE')=='zero-tests': n=0
if os.getenv('FAIL_STAGE')=='partial-tests': n=max(0,n-1)
print(f'Test run summary: Passed!\\n total: {n}\\n failed: 0\\n succeeded: {n}\\n skipped: 0')
if os.getenv('FAIL_STAGE')=='test-exit': sys.exit(6)
''')
        mock.chmod(0o755)

    def tearDown(self): self.temp.cleanup()

    def run_fixture(self, failure=''):
        env=dict(os.environ, DOTNET_ROOT=str(self.root/'sdk'), FAIL_STAGE=failure)
        return subprocess.run(['bash',str(self.root/'research'/'run-correctness.sh'),'evidence'],cwd=self.root,env=env,capture_output=True,text=True)

    def test_success(self):
        result=self.run_fixture()
        self.assertEqual(result.returncode,0,result.stdout+result.stderr)
        self.assertTrue(validate(self.root/'evidence')['passed'])

    def test_guard_info_build_and_test_failures_propagate(self):
        for failure in ['architecture','maintainability','dotnet-info','build','test-exit','zero-tests','partial-tests']:
            with self.subTest(failure=failure):
                result=self.run_fixture(failure)
                self.assertNotEqual(result.returncode,0,result.stdout+result.stderr)
                shutil.rmtree(self.root/'evidence')

    def test_existing_evidence_never_overwritten(self):
        (self.root/'evidence').mkdir()
        marker=self.root/'evidence'/'keep'
        marker.write_text('preserve')
        self.assertEqual(self.run_fixture().returncode,2)
        self.assertEqual(marker.read_text(),'preserve')

    def test_missing_duplicate_skipped_and_invalid_evidence_fail_closed(self):
        self.assertEqual(self.run_fixture().returncode,0)
        name=next(iter(EXPECTED)); path=self.root/'evidence'/f'{name}.log'; original=path.read_text()
        for bad in ['',original+original,original.replace('skipped: 0','skipped: 1')]:
            with self.subTest(text=bad):
                path.write_text(bad)
                self.assertFalse(validate(self.root/'evidence')['passed'])
        path.write_text(original)
        status=self.root/'evidence'/'architecture.exit';status.write_text('not-an-exit')
        self.assertFalse(validate(self.root/'evidence')['passed'])
        status.unlink()
        self.assertFalse(validate(self.root/'evidence')['passed'])

if __name__=='__main__': unittest.main()
