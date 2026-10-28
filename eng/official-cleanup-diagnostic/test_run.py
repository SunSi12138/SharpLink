import importlib.util
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location('cleanup_runner_test', ROOT/'run.py')
runner = importlib.util.module_from_spec(spec)
spec.loader.exec_module(runner)


class ScheduleTests(unittest.TestCase):
    def test_six_fixed_untimed_slots(self):
        rows=runner.schedule()
        self.assertEqual(6,len(rows));self.assertEqual(6,len({r['id'] for r in rows}))
        self.assertEqual([('static-none','official'),('static-none','dev'),('static-cancellable','dev'),
                          ('static-cancellable','official'),('dynamic-none','official'),('dynamic-none','dev')],
                         [(r['condition'],r['arm']) for r in rows])
        self.assertEqual(0,runner.PROTOCOL['warmupSeconds']);self.assertEqual(0,runner.PROTOCOL['measureSeconds'])
        self.assertEqual(15,runner.PROTOCOL['cleanupGuardSeconds'])
        self.assertEqual(120,runner.PROTOCOL['processTimeoutSeconds'])
        self.assertEqual(30,runner.PROTOCOL['businessRpcBudgetTotal'])

    def test_no_capture_privilege_or_relay_execution(self):
        source=(ROOT/'run.py').read_text()
        for command in ('tcpdump','setcap','sudo','tshark','run_matrix.py'):
            self.assertNotIn(command,source)
        self.assertIn("'--fixture-purpose'",(ROOT.parent/'official-version-comparison/prepare.py').read_text())


if __name__=='__main__':unittest.main()
