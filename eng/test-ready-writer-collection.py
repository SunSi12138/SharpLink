#!/usr/bin/env python3
"""Failure collection tests use synthetic fixtures only, not performance evidence."""
import importlib.util
import json
from pathlib import Path
import subprocess
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch

ROOT=Path(__file__).resolve().parents[1]


def load(name):
    spec=importlib.util.spec_from_file_location(name,ROOT/'eng'/(name+'.py'))
    module=importlib.util.module_from_spec(spec);spec.loader.exec_module(module);return module


runner=load('run-ready-writer-budget')
coverage=load('report-ready-writer-coverage')
fixtures=load('test-ready-writer-native')


class CollectionTests(unittest.TestCase):
    def populate(self,root):
        (root/'provenance.json').write_text(json.dumps(dict(source_tree='a'*40,host_sha256='b'*64,plan=runner.plan(),budgets=[0,8192,16384],allocation_diagnostic=False,rounds=4,slots=16,quanta=[1,16],cpu_affinity=[0,1,2,3])))
        for index,case in enumerate(runner.plan()):
            doc=fixtures.NativeEvidenceTests().fixture(case)
            doc['metadata'].update(Pgo=str(case[1]),DynamicCodeSupported=True,preparedByteBudget=case[8])
            for row in doc['samples']:row['ReadyWriterMetrics']['PreparedByteBudgetPerStream']=case[8]
            target=root/runner.name(index,case);target.write_text(json.dumps(doc))
            target.with_suffix('.exit').write_text('{"code":0}')

    def test_collection_attempts_each_case_once_after_failures(self):
        with tempfile.TemporaryDirectory() as temp:
            with patch.object(runner,'run_case',side_effect=[5,0,124]) as invoke:
                failed=runner.run_selected(Path(temp),dict(source_tree='a'*40,cpu_affinity=[0,1,2,3]),Path('test.dll'),0,3)
            self.assertEqual(failed,[(0,5),(2,124)])
            self.assertEqual(invoke.call_count,3)
            self.assertEqual(len({str(call.args[1]) for call in invoke.call_args_list}),3)

    def test_preflight_refuses_existing_failure_without_running_any_case(self):
        with tempfile.TemporaryDirectory() as temp:
            root=Path(temp);(root/runner.name(1,runner.plan()[1])).with_suffix('.exit').write_text('{"code":124}')
            with patch.object(runner,'run_case') as invoke:
                with self.assertRaises(FileExistsError):runner.run_selected(root,{},Path('test.dll'),0,3)
                invoke.assert_not_called()

    def test_process_timeout_launch_failure_and_exit_are_preserved(self):
        for result,expected in ((subprocess.TimeoutExpired('test',180),124),(OSError('controlled launch failure'),127),(SimpleNamespace(returncode=-6),-6)):
            with tempfile.TemporaryDirectory() as temp:
                target=Path(temp)/'case.json'
                options={'side_effect':result} if isinstance(result,Exception) else {'return_value':result}
                with patch.object(runner.subprocess,'run',**options) as invoke:
                    self.assertEqual(runner.run_case(['test'],target,{}),expected)
                    invoke.assert_called_once();self.assertEqual(invoke.call_args.kwargs['timeout'],180)
                self.assertEqual(json.loads(target.with_suffix('.exit').read_text())['code'],expected)

    def test_complete_population(self):
        with tempfile.TemporaryDirectory() as temp:
            root=Path(temp);self.populate(root);data,text=coverage.inventory(root)
            self.assertEqual(data['complete'],48);self.assertEqual(len(data['cases']),48)
            self.assertEqual(sum(x['retained_samples'] for x in data['cases']),768)
            self.assertIn('not a performance acceptance report',text)

    def test_missing_exit_is_not_success(self):
        with tempfile.TemporaryDirectory() as temp:
            root=Path(temp);self.populate(root)
            (root/runner.name(24,runner.plan()[24])).with_suffix('.exit').unlink()
            data,_=coverage.inventory(root)
            self.assertEqual(data['complete'],47);self.assertEqual(data['cases'][24]['outcome'],'missing')

    def test_nonzero_exit_does_not_count_retained_rows_as_success(self):
        with tempfile.TemporaryDirectory() as temp:
            root=Path(temp);self.populate(root);target=root/runner.name(24,runner.plan()[24])
            target.with_suffix('.exit').write_text('{"code":-6}')
            data,_=coverage.inventory(root);row=data['cases'][24]
            self.assertEqual(data['complete'],47);self.assertEqual(row['outcome'],'failed');self.assertEqual(row['retained_samples'],16)

    def test_zero_exit_with_wrong_source_or_partial_report_is_invalid(self):
        for mutate in (lambda doc:doc['metadata'].update(Source='b'*40),lambda doc:doc.update(samples=doc['samples'][:-1])):
            with tempfile.TemporaryDirectory() as temp:
                root=Path(temp);self.populate(root);target=root/runner.name(0,runner.plan()[0])
                doc=json.loads(target.read_text());mutate(doc);target.write_text(json.dumps(doc))
                data,_=coverage.inventory(root)
                self.assertEqual(data['complete'],47);self.assertEqual(data['cases'][0]['outcome'],'invalid')

    def test_missing_json_or_boolean_exit_is_invalid(self):
        for missing in (True,False):
            with tempfile.TemporaryDirectory() as temp:
                root=Path(temp);self.populate(root);target=root/runner.name(0,runner.plan()[0])
                if missing:target.unlink()
                else:target.with_suffix('.exit').write_text('{"code":false}')
                data,_=coverage.inventory(root)
                self.assertEqual(data['complete'],47);self.assertEqual(data['cases'][0]['outcome'],'invalid')


if __name__=='__main__':unittest.main()
