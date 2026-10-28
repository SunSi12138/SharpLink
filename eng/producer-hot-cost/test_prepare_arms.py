#!/usr/bin/env python3
import json,subprocess,sys,tempfile,unittest
from pathlib import Path
from prepare_arms import prepare_snapshot,git,profile_settings,EXPECTED,PUBLIC_BASELINE
class ReconstructionTests(unittest.TestCase):
    def setUp(self):
        self.temp=tempfile.TemporaryDirectory();self.addCleanup(self.temp.cleanup);self.repo=Path(self.temp.name)/'repo';self.repo.mkdir()
        subprocess.run(['git','init','-q',str(self.repo)],check=True)
        (self.repo/'src').mkdir();(self.repo/'src/sentinel.cs').write_text('original source\n')
        for f in ['Directory.Build.props','Directory.Packages.props','global.json']:(self.repo/f).write_text('{}\n')
        git(self.repo,'add','.');git(self.repo,'-c','user.name=Fixture','-c','user.email=fixture@example.invalid','commit','-qm','fixture')
        self.commit=git(self.repo,'rev-parse','HEAD').decode().strip();self.tree=git(self.repo,'rev-parse','HEAD:src').decode().strip()
    def test_archive_inside_outer_repository(self):
        dest=self.repo/'nested/evidence/arms/baseline';proof=prepare_snapshot(self.repo,self.commit,self.tree,dest)
        self.assertEqual((dest/'src/sentinel.cs').read_text(),'original source\n');self.assertEqual(proof['SourceTree'],self.tree)
        self.assertEqual((self.repo/'src/sentinel.cs').read_text(),'original source\n')
    def test_wrong_source_tree_is_rejected(self):
        dest=self.repo/'nested/arms/wrong'
        with self.assertRaisesRegex(ValueError,'Source tree mismatch'):prepare_snapshot(self.repo,self.commit,'0'*40,dest)
        self.assertFalse(dest.exists())
    def test_cli_failure_retains_diagnostic(self):
        output=self.repo/'nested/failed-evidence'
        run=subprocess.run([sys.executable,str(Path(__file__).with_name('prepare_arms.py')),'--repo',str(self.repo),'--root',str(output),'--baseline',self.commit,'--candidate',self.commit],capture_output=True,text=True)
        self.assertNotEqual(run.returncode,0)
        proof=json.loads((output/'source-provenance.json').read_text())
        self.assertEqual(proof['Status'],'Failed');self.assertIn('Source tree mismatch',proof['Error'])
    def test_profiles_preserve_historical_and_bind_integration(self):
        self.assertEqual((PUBLIC_BASELINE,EXPECTED),profile_settings('historical'))
        config=json.loads((Path(__file__).resolve().parent.parent/'maintenance-integration-sources.json').read_text())
        baseline,expected=profile_settings('integrated')
        self.assertEqual(config['arms']['previous-maintenance']['commit'],baseline)
        self.assertEqual(config['arms']['previous-maintenance']['source_tree'],expected['baseline'])
        self.assertEqual(config['candidate_source_tree'],expected['candidate-final'])
        self.assertNotEqual(EXPECTED,expected)
if __name__=='__main__':unittest.main()
