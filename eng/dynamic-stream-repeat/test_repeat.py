import ast
import copy
import hashlib
import json
from pathlib import Path
import unittest
from unittest import mock
import xml.etree.ElementTree as ET
import plan
import process_owner as owner
import results as r
import run
from test_short import sample

KIT=Path(__file__).parent

class RepeatControls(unittest.TestCase):
    def test_fixed_plan_exact_order_counts_unique_paths(self):
        cases=plan.cases()
        self.assertEqual(len(cases),300)
        self.assertEqual([(c.round,c.mode) for c in cases],[(n,m) for n in range(1,101) for m in r.MODES])
        paths=[c.label+suffix for c in cases for suffix in ('-command','-pipe','-report','-stages')]
        self.assertEqual(len(set(paths)),1200)
        self.assertLess(max(len('/tmp/slr-12345678/'+c.label+'-pipe')+40 for c in cases),103)
    def test_all_300_passes_required(self):
        seen=[]
        result=plan.execute(0,seen.append,clock=lambda:0)
        self.assertEqual(result,seen);self.assertEqual(len(result),300)
        plan.require_complete(result)
        for bad in (result[:-1],result+[result[-1]],result[1:]+result[:1],result[:1]*300):
            with self.assertRaisesRegex(ValueError,'Missing selected cases'):plan.require_complete(bad)
    def test_insufficient_round_budget_no_command(self):
        attempted=[];admitted=[]
        now=plan.TOTAL_SECONDS-3*166-plan.FINAL_RESERVE_SECONDS
        with self.assertRaisesRegex(ValueError,'Insufficient owned command budget'):
            plan.execute(0,attempted.append,clock=lambda:now,before_case=admitted.append)
        self.assertEqual(attempted,[]);self.assertEqual(admitted,[plan.cases()[0]])
    def test_case_budget_expiry_stops_mid_round_nonpass(self):
        attempted=[];admitted=[]
        def clock():return 0 if not attempted else 3600-166-60
        with self.assertRaises(ValueError):plan.execute(0,attempted.append,clock=clock,before_case=admitted.append)
        self.assertEqual(attempted,[plan.cases()[0]]);self.assertEqual(admitted,list(plan.cases()[:2]))
    def test_round_boundary_budget_stops_without_retry(self):
        attempted=[]
        def clock():return 0 if len(attempted)<3 else 3100
        with self.assertRaises(ValueError):plan.execute(0,attempted.append,clock=clock)
        self.assertEqual(attempted,list(plan.cases()[:3]))
    def test_timeout_and_owner_failure_never_advance(self):
        for stop in (1,2,3,5,299,300):
            for error in (TimeoutError(),owner.CommandFailure({'timedOut':True},[]),ValueError('oracle'),OSError('guard')):
                seen=[]
                def fail(case):
                    seen.append(case)
                    if len(seen)==stop:raise error
                with self.assertRaises(type(error)) as cm:plan.execute(0,fail,clock=lambda:0)
                self.assertIs(cm.exception,error);self.assertEqual(seen,list(plan.cases()[:stop]))
    def test_budget_reserves_are_not_active_budget_promise(self):
        self.assertEqual((plan.TOTAL_SECONDS,plan.FINAL_RESERVE_SECONDS,plan.CLEANUP_SECONDS),(3600,60,16))
        self.assertGreater(sum(s for _,s in run.LIMITS)+len(run.LIMITS)*16,3600)
        for now in (-1,float('inf'),float('nan'),3600):
            with self.assertRaises(ValueError):plan.require_budget(0,166,clock=lambda:now)
        plan.require_budget(0,166,clock=lambda:3373)
        with self.assertRaises(ValueError):plan.require_budget(0,166,clock=lambda:3374)
    def test_seen_generations_span_all_rounds_same_uid_allowed(self):
        native,xml,row=sample();seen=set();runtime=None
        for n in range(300):
            native['publicationGeneration']=f'{n:032x}'
            verdict=r.passed(native,ET.tostring(xml).decode(),row,seen,runtime)
            seen.add(verdict['generation']);runtime=verdict['runtime']
        self.assertEqual(len(seen),300)
        with self.assertRaisesRegex(ValueError,'Native generation'):r.passed(native,ET.tostring(xml).decode(),row,seen,runtime)
    def test_production_wiring_stage_reset_and_final_count(self):
        tree=ast.parse((KIT/'run.py').read_text())
        funcs={n.name:n for n in ast.walk(tree) if isinstance(n,ast.FunctionDef)}
        begin=ast.unparse(funcs['begin_case'])
        self.assertIn('stages = None',begin);self.assertIn('current = case.label',begin)
        self.assertIn("phase='repeat-admission'",begin)
        body=ast.unparse(funcs['main'])
        self.assertIn('plan.execute(started, execute_case, before_case=begin_case)',body)
        self.assertIn("[(q['round'], q['mode']) for q in qualified] == [(c.round, c.mode) for c in plan.cases()]",body)
        self.assertIn('len(seen) == len(plan.cases())',body)
        self.assertIn("'case': current",body)
        self.assertIn("stages = work / (label + '-stages')",body)
        self.assertIn("report = work / (label + '-report')",body)
        self.assertEqual(body.count('plan.require_budget(started, plan.command_reserve(label, seconds))'),2)
    def test_safe_failure_has_exact_case_and_no_raw_message(self):
        context=dict(stage='r100-consumer-break',phase='repeat-admission',ownerInvoked=False,terminal=None)
        out=run.failure_diagnostic([ValueError('Insufficient owned command budget')],context,[],set())
        self.assertEqual(out['stage'],'r100-consumer-break');self.assertFalse(out['ownerInvoked'])
        self.assertEqual(out['leaves'][0]['fixedReason'],'Insufficient owned command budget')
    def test_round_reserve_rechecked_after_guard(self):
        self.assertEqual(plan.command_reserve('r100-consumer-break',150),498)
        self.assertEqual(plan.command_reserve('r100-normal',150),166)
        self.assertEqual(plan.command_reserve('build',600),616)
        plan.require_budget(0,498,clock=lambda:3041)
        with self.assertRaises(ValueError):plan.require_budget(0,498,clock=lambda:3042)
        tree=ast.parse((KIT/'run.py').read_text())
        nested=next(n for n in ast.walk(tree) if isinstance(n,ast.FunctionDef) and n.name=='run')
        text=ast.unparse(nested)
        self.assertLess(text.index('guard()'),text.index('plan.require_budget'))
        self.assertLess(text.rindex('plan.require_budget'),text.index('owner.run_owned'))
    def test_completion_accounting_after_guard_and_reporting(self):
        tree=ast.parse((KIT/'run.py').read_text())
        funcs={n.name:n for n in ast.walk(tree) if isinstance(n,ast.FunctionDef)}
        case=ast.unparse(funcs['execute_case'])
        self.assertGreater(case.index('qualified.append'),case.rindex('guard()'))
        self.assertGreater(case.index('qualified.append'),case.rindex('print('))
        guard=funcs['guard']
        self.assertIn('time.monotonic() - started < plan.TOTAL_SECONDS',ast.unparse(guard.body[-1]))
        main=ast.unparse(funcs['main'])
        self.assertGreater(main.rindex('time.monotonic() - started < plan.TOTAL_SECONDS'),main.index('os.close(fd)'))
        self.assertLess(main.rindex('time.monotonic() - started < plan.TOTAL_SECONDS'),main.index("'REPEAT_300_CASE_PASS_NOT_FULL_NIGHTLY_OR_SOAK'"))
    def test_manifest_all_payload_bytes(self):
        ci=json.loads((KIT/'CI.json').read_text())
        actual={str(p.relative_to(KIT)) for p in KIT.rglob('*') if p.is_file() and '__pycache__' not in p.parts and p.name!='CI.json'}
        self.assertEqual(set(ci['code']),actual)
        for name,digest in ci['code'].items():self.assertEqual(hashlib.sha256((KIT/name).read_bytes()).hexdigest(),digest)
    def test_workflow_bounded_independent_and_no_upload(self):
        local=KIT.parent/'workflow/validation-dynamic-stream-repeat.yml'
        deployed=KIT.parents[1]/'.github/workflows/validation-dynamic-stream-repeat.yml'
        text=(local if local.is_file() else deployed).read_text()
        self.assertIn("'validation/dynamic-stream-repeat-20261104'",text)
        self.assertNotIn('wip/775',text);self.assertNotIn('upload-artifact',text)
        self.assertNotIn('nightly.yml',text);self.assertNotIn('dynamic-stream-short',text)
        self.assertIn('timeout-minutes: 75',text)
        self.assertIn('ref: 9d1635c8988870ee295098fe9e65cb49924013d2',text)
        self.assertEqual(text.count(hashlib.sha256((KIT/'CI.json').read_bytes()).hexdigest()),2)

if __name__=='__main__':unittest.main()
