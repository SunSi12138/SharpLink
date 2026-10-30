#!/usr/bin/env python3
"""Pure controls, no CLR/MTP/MSBuild/socket execution."""
import ast
import contextlib
import copy
import hashlib
import io
import json
import os
from pathlib import Path
import subprocess
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import MagicMock, patch
import run as gate
import allocation
import provenance
import validate
from diagnostics import Diagnostics

def passed_report(uids):
    summary = dict(total=len(uids), passed=len(uids), failed=0, skipped=0, cancelled=0, timedOut=0, flaky=0)
    return dict(schemaVersion=1, assemblyName='SharpLink.UnitTests', tunitVersion='1.73.19.0', runtimeVersion='.NET 10.0.14',
                publicationGeneration='a' * 32, summary=dict(summary),
                groups=[dict(summary=dict(summary), tests=[dict(id=u, status='passed') for u in uids])])

class IsolationTests(unittest.TestCase):
    def test_environment_clears_aliases_and_upload_hooks_parent_cli_stays_explicit(self):
        with tempfile.TemporaryDirectory() as tmp:
            output = Path(tmp)
            value = gate.test_environment({'platformOptions:resultDirectory': 'bad', 'PLATFORMOPTIONS__RESULTDIRECTORY': 'bad',
                'commandLineOptions__report-html-filename': 'bad', 'commandLineOptionDefaults:results-directory': 'bad',
                'GITHUB_STEP_SUMMARY': 'bad', 'SAFE': 'keep'}, '/runtime/dotnet', output)
            self.assertEqual(value['platformOptions__resultDirectory'], str(output / 'child-reports'))
            self.assertEqual(value['TUNIT_DISABLE_ARTIFACT_UPLOAD'], 'true')
            self.assertNotIn('GITHUB_STEP_SUMMARY', value)
            self.assertEqual(value['SAFE'], 'keep')
            self.assertFalse(any(v == 'bad' for v in value.values()))
            # The frozen child uses ProcessStartInfo.Environment without clearing;
            # inherited env is not a claim of local CLR execution.
            child_env = dict(value)
            self.assertEqual(child_env['platformOptions__resultDirectory'], value['platformOptions__resultDirectory'])
            with self.assertRaises(FileExistsError): gate.test_environment({}, '/runtime/dotnet', output)

    def test_output_alias_and_existing_destination_fail_without_removal(self):
        for kind in ('parent_alias', 'target_link', 'file'):
            with self.subTest(kind=kind), tempfile.TemporaryDirectory() as tmp:
                root = Path(tmp); out = root / 'out'; out.mkdir()
                if kind == 'parent_alias':
                    alias = root / 'alias'; alias.symlink_to(out, target_is_directory=True); out = alias
                elif kind == 'target_link': (out / 'child-reports').symlink_to(root / 'missing')
                else: (out / 'child-reports').write_bytes(b'retain')
                with self.assertRaises((gate.GateError, FileExistsError)):
                    gate.test_environment({}, '/runtime/dotnet', out)
                if kind == 'file': self.assertEqual((out / 'child-reports').read_bytes(), b'retain')

    def test_dll_census_is_recursive_and_rejects_aliases(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp); (root / 'main.dll').write_bytes(b'original')
            sub = root / 'probe'; sub.mkdir(); (sub / 'nested.dll').write_bytes(b'probe')
            identity = gate.hashes(root); self.assertEqual(len(identity), 2)
            (sub / 'nested.dll').write_bytes(b'changed')
            self.assertNotEqual(gate.hashes(root), identity)
            (root / 'linked.dll').symlink_to(root / 'main.dll')
            with self.assertRaises(gate.GateError): gate.hashes(root)

    def test_process_timeout_leak_and_quiescence(self):
        for mode in ('pass', 'timeout', 'leak', 'unsafe'):
            process = MagicMock(pid=1234)
            process.wait.side_effect = [subprocess.TimeoutExpired(['test'], 120), -9] if mode == 'timeout' else [0]
            alive = [False, False] if mode in ('pass', 'timeout') else [True, False, False] if mode == 'leak' else [True] * 4
            with tempfile.TemporaryDirectory() as tmp, patch.object(gate.host.subprocess, 'Popen', return_value=process), \
                 patch.object(gate.host, 'group_alive', side_effect=alive), patch.object(gate.host.os, 'killpg') as kill, \
                 patch.object(gate.host.time, 'monotonic', side_effect=[0, 6]):
                if mode == 'unsafe':
                    with self.assertRaises(gate.host.UnsafeProcessGroup): gate.host.execute(['host'], Path(tmp), {}, 120, Path(tmp) / 'log')
                else:
                    result = gate.host.execute(['host'], Path(tmp), {}, 120, Path(tmp) / 'log')
                    self.assertEqual(result, (0, False) if mode == 'pass' else (-9, True) if mode == 'timeout' else (-1, False))
                self.assertEqual(kill.call_count, 0 if mode == 'pass' else 1)
                self.assertTrue((Path(tmp) / 'log.status.json').exists())

class QuiescenceFailureTests(unittest.TestCase):
    def test_monitor_cleanup_and_wait_errors_abort_before_next_case_with_status(self):
        for kind in ('ps', 'kill', 'poll', 'recheck', 'wait', 'timeout-kill'):
            with self.subTest(kind=kind), tempfile.TemporaryDirectory() as tmp:
                root = Path(tmp); process = MagicMock(pid=1234); process.wait.return_value = 0
                error = subprocess.CalledProcessError(1, ['ps']) if kind == 'ps' else OSError('RAW_MONITOR_FAILURE')
                alive = [False, False]
                if kind == 'ps': alive = [error]
                if kind == 'kill': alive = [True]
                if kind == 'poll': alive = [True, error]
                if kind == 'recheck': alive = [False, error]
                if kind == 'wait': process.wait.side_effect = error
                if kind == 'timeout-kill': process.wait.side_effect = subprocess.TimeoutExpired(['host'], 1)
                next_cases = []; stages = []
                def campaign():
                    gate.host.execute(['host'], root, {}, 1, root / 'execution.log')
                    next_cases.append('must-not-run')
                with patch.object(gate.host.subprocess, 'Popen', return_value=process), \
                     patch.object(gate.host, 'group_alive', side_effect=alive), \
                     patch.object(gate.host.os, 'killpg', side_effect=error if kind in ('kill', 'timeout-kill') else None), \
                     contextlib.redirect_stdout(io.StringIO()) as stdout:
                    with self.assertRaises(gate.host.UnsafeProcessGroup): campaign()
                self.assertEqual(next_cases, [])
                status = json.loads((root / 'execution.log.status.json').read_text())
                self.assertFalse(status['processGroupQuiescent'])
                self.assertEqual(status['monitorError']['type'], type(error).__name__)
                self.assertEqual(status['monitorError']['detail'], str(error))
                self.assertNotIn('RAW_MONITOR_FAILURE', stdout.getvalue())
                self.assertFalse(any(s['stage'] == 'next' for s in stages))

    def test_status_write_failure_cannot_downgrade_unknown_group_to_recoverable(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp); process = MagicMock(pid=1234); process.wait.return_value = 0
            original_open = Path.open
            def open_path(path, *args, **kwargs):
                if str(path).endswith('.status.json'): raise OSError('status storage failed')
                return original_open(path, *args, **kwargs)
            with patch.object(gate.host.subprocess, 'Popen', return_value=process), \
                 patch.object(gate.host, 'group_alive', side_effect=OSError('ps failed')), patch.object(Path, 'open', open_path):
                with self.assertRaises(gate.host.UnsafeProcessGroup):
                    gate.host.execute(['host'], root, {}, 1, root / 'execution.log')




def allocation_discovery():
    return dict(schemaVersion=1, tests=[dict(uid=uid, type=dict(namespace='SharpLink.UnitTests.Runtime',
        typeName='ReadOwnershipPipeReaderLifetimeTests', methodName=method),
        location=dict(file='/source/' + allocation.SOURCE_PATH)) for uid, method in zip(allocation.UIDS, allocation.METHODS)])


class SourceOracleTests(unittest.TestCase):
    def test_actual_frozen_dev_source_and_synchronous_uids(self):
        allocation.verify_source(gate.HERE.parents[1])
        allocation.verify_discovery(allocation_discovery())
        allocation.verify_result(list(allocation.UIDS), passed_report(allocation.UIDS))
        self.assertTrue(all('CancellationToken' not in uid for uid in allocation.UIDS))

    def test_hash_guards_exact_zero_gen2_parallel_and_child_fixture_changes(self):
        raw = (gate.HERE.parents[1] / allocation.SOURCE_PATH).read_text()
        for change in ('allocated == 0', 'gen2After == gen2Before', '[NotInParallel]', 'public void SuccessfulSuspensionsShouldNotAllocateAfterWarmup()'):
            with self.subTest(change=change), tempfile.TemporaryDirectory() as tmp:
                root = Path(tmp); source = root / allocation.SOURCE_PATH; source.parent.mkdir(parents=True)
                source.write_text(raw.replace(change, 'CHANGED', 1))
                with self.assertRaises(gate.GateError): allocation.verify_source(root)
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp); source = root / allocation.SOURCE_PATH; source.parent.mkdir(parents=True)
            source.write_text(raw + '\nREAD_OWNERSHIP_ALLOCATION_VERIFIED|{}\n')
            with self.assertRaises(gate.GateError): allocation.verify_source(root)

    def test_wrong_931_uid_mapping_missing_failed_and_duplicate_rejected(self):
        for kind in ('parameterized', 'mapping', 'missing', 'failed', 'duplicate'):
            d = allocation_discovery(); r = passed_report(allocation.UIDS)
            if kind == 'parameterized': d['tests'][0]['uid'] = d['tests'][0]['uid'].replace('.1.1.0', '(System.Threading.CancellationToken).1.1.0')
            if kind == 'mapping': d['tests'][0]['location']['file'] = '/wrong.cs'
            if kind == 'missing': r['groups'][0]['tests'].pop()
            if kind == 'failed': r['groups'][0]['tests'][0]['status'] = 'error'
            if kind == 'duplicate': r['groups'][0]['tests'].append(r['groups'][0]['tests'][0])
            with self.subTest(kind=kind), self.assertRaises(gate.GateError):
                if kind in ('parameterized', 'mapping'): allocation.verify_discovery(d)
                else: allocation.verify_result(list(allocation.UIDS), r)


class StripObservation(ast.NodeTransformer):
    def visit_FunctionDef(self, node):
        if node.name == 'observe': return None
        if node.name == 'validate_run':
            node.args.args = node.args.args[:-1]; node.args.defaults = node.args.defaults[:-1]
        return self.generic_visit(node)
    def visit_Expr(self, node):
        if isinstance(node.value, ast.Call) and isinstance(node.value.func, ast.Name) and node.value.func.id == 'observe': return None
        return self.generic_visit(node)


class ValidatorDiagnosticTests(unittest.TestCase):
    def test_acceptance_ast_unchanged_from_prior_run_oracle(self):
        current = StripObservation().visit(ast.parse(Path(validate.__file__).read_text()))
        self.assertEqual(hashlib.sha256(ast.dump(current, include_attributes=False).encode()).hexdigest(),
                         '6cb567d125c47b53ba6ca0bfa09fae2c35a26524e831e091b5e52dc0256fd584')

    def test_failed_uid_counts_and_precise_stage_are_visible_but_failure_still_fails(self):
        value = passed_report(allocation.UIDS); value['groups'][0]['tests'][0]['status'] = 'error'
        d = Diagnostics()
        with contextlib.redirect_stdout(io.StringIO()) as stdout:
            d.observe_report(value, list(allocation.UIDS), 2, False)
            with self.assertRaises(gate.GateError):
                gate.validate_run(list(allocation.UIDS), value, 2, on_stage=d.stage)
        self.assertEqual(d.current, 'VALIDATION_STATUS')
        self.assertIn(allocation.UIDS[0], stdout.getvalue())
        self.assertEqual(next(r['nonPassing'] for r in d.rows if 'nonPassing' in r), 1)

    def test_every_oracle_failure_keeps_its_acceptance_and_stage(self):
        cases = [('schema', 'VALIDATION_SCHEMA'), ('groups', 'VALIDATION_GROUPS'), ('uid', 'VALIDATION_UID'),
                 ('status', 'VALIDATION_STATUS'), ('retry', 'VALIDATION_RETRY'), ('counters', 'VALIDATION_COUNTERS'),
                 ('exit', 'VALIDATION_EXIT')]
        for kind, expected_stage in cases:
            value = passed_report(allocation.UIDS); code = 0
            if kind == 'schema': value['schemaVersion'] = 2
            if kind == 'groups': value['groups'] = []
            if kind == 'uid': value['groups'][0]['tests'][0]['id'] = 'other'
            if kind == 'status': value['groups'][0]['tests'][0]['status'] = 'skipped'
            if kind == 'retry': value['groups'][0]['tests'][0]['retryAttempt'] = 1
            if kind == 'counters': value['summary']['passed'] = True
            if kind == 'exit': code = 1
            d = Diagnostics()
            with self.subTest(kind=kind), contextlib.redirect_stdout(io.StringIO()), self.assertRaises(gate.GateError):
                gate.validate_run(list(allocation.UIDS), value, code, on_stage=d.stage)
            self.assertEqual(d.current, expected_stage)

    def test_observation_bounds_and_unknown_uid_redaction(self):
        value = passed_report(['secret-token-' + str(i) for i in range(20)])
        for t in value['groups'][0]['tests']: t['status'] = 'SECRET_STATUS'
        d = Diagnostics()
        with contextlib.redirect_stdout(io.StringIO()) as out: d.observe_report(value, [], 1, False)
        self.assertNotIn('secret-token', out.getvalue()); self.assertNotIn('SECRET_STATUS', out.getvalue())
        self.assertEqual(sum('uidSHA256' in r for r in d.rows), 16)
        self.assertEqual(d.rows[-1]['omittedNonPassing'], 4)

    def test_frozen_census_has_exact_prior_hashes_and_original_five_workers(self):
        workers = gate.explicit_workers(gate.HERE)
        raw = [w['uid'] for w in workers] + list(allocation.UIDS) + ['fake.' + str(i) for i in range(2192)]
        document = dict(schemaVersion=1, tests=[dict(uid=uid) for uid in raw])
        self.assertEqual(len(raw), 2199)
        with patch.object(gate, 'verify_explicit_sources'), patch.object(allocation, 'verify_discovery'), \
             patch.object(gate, 'uid_digest', side_effect=[gate.RAW_SHA256, gate.ORDINARY_SHA256]):
            self.assertEqual(len(gate.frozen_census(Path('/source'), document)), 2194)
        with self.assertRaises(gate.GateError): gate.frozen_census(Path('/source'), document)
        self.assertEqual(len(workers), 5)


class OneExecutionTests(unittest.TestCase):
    def test_exactly_one_ordinary_after_build_discovery_failures_remain_artifact_visible(self):
        for kind in ('pass', 'case-fail', 'unsafe', 'wrong-runtime', 'wrong-witness-source'):
            with self.subTest(kind=kind), tempfile.TemporaryDirectory() as tmp:
                root = Path(tmp); source=root/'source'; source.mkdir(); release=root/'release'; release.mkdir()
                output=root/'output'; ordinary=list(allocation.UIDS)+['other.test']; calls=[]
                def execute(command, cwd, env, timeout, log):
                    calls.append(list(map(str, command))); log.write_text('retained local process data')
                    if '--list-tests' in command: log.write_text('{}')
                    elif 'build' not in command:
                        if kind == 'unsafe': raise gate.host.UnsafeProcessGroup('monitor failed')
                        report=passed_report(ordinary)
                        if kind == 'case-fail': report['groups'][0]['tests'][-1]['status']='error'
                        if kind == 'wrong-runtime': report['runtimeVersion']='.NET 10.0.2'
                        (log.parent/'result.tunit-report.json').write_text(json.dumps(report))
                    return 0,False
                def source_oracle(path):
                    if kind == 'wrong-witness-source': raise gate.GateError('wrong #931 child-probe fixture')
                with patch.object(gate,'verify_release',return_value={}), patch.object(gate,'verify_source'), \
                     patch.object(allocation,'verify_source',side_effect=source_oracle), \
                     patch.object(gate.host,'verify_hosts',return_value=('sdk','runtime')), \
                     patch.object(gate.host,'execute',side_effect=execute), \
                     patch.object(gate,'hashes',return_value={'SharpLink.UnitTests.dll':'a'}), \
                     patch.object(gate,'frozen_census',return_value=ordinary), \
                     patch.object(gate.tempfile,'mkdtemp',return_value='/tmp/synthetic-only'), \
                     patch.dict(os.environ,{'GITHUB_RUN_ATTEMPT':'1'}), contextlib.redirect_stdout(io.StringIO()):
                    result=gate.run(SimpleNamespace(release_root=release,source_root=source,output=output))
                self.assertEqual(result,0 if kind=='pass' else 1)
                self.assertEqual(len(calls),0 if kind=='wrong-witness-source' else 3)
                self.assertTrue(all('--filter-uid' not in c for c in calls))
                diag=json.loads((output/'diagnostic.json').read_text())
                self.assertEqual(bool(diag['failures']),kind!='pass')
                if kind=='case-fail':
                    self.assertEqual(diag['failures'][0]['stage'],'VALIDATION_STATUS')
                    self.assertTrue((output/'ordinary/result.tunit-report.json').exists())
                if kind=='unsafe': self.assertEqual(diag['failures'][0]['type'],'UnsafeProcessGroup')


class ProvenanceTests(unittest.TestCase):
    def test_actual_chain_source_and_tool_request_layout(self):
        root=gate.HERE.parents[1]; provenance.verify_candidate_chain(root)
        if (root/provenance.REQUEST).exists():
            provenance.verify_release(root,provenance.git(root,'rev-parse','HEAD'),os.environ.get('GITHUB_REF',provenance.BRANCH))
        files=['.github/workflows/sendpump-cleanup-dev-ordinary-diagnostic.yml']+[str(p.relative_to(root)) for p in gate.HERE.iterdir() if p.is_file()]
        self.assertEqual(sorted(p for p in files if p!=provenance.REQUEST),provenance.TOOL_FILES)

    def test_request_binding_rejects_wrong_fields_parent_tree_or_extra_change(self):
        with tempfile.TemporaryDirectory() as tmp:
            root=Path(tmp); p=root/provenance.REQUEST;p.parent.mkdir(parents=True)
            request=dict(schema_version=2,source_sha=provenance.SOURCE_SHA,tools_sha='a'*40,tools_tree='b'*40)
            p.write_text(json.dumps(request))
            answers={('rev-parse','HEAD'):'c'*40,('status','--porcelain','--untracked-files=all'):'',
                ('show','-s','--format=%P','HEAD'):'a'*40,('rev-parse','a'*40+'^{tree}'):'b'*40,
                ('diff','--name-status','--no-renames','a'*40,'HEAD'):'A\t'+provenance.REQUEST,
                ('show','-s','--format=%P','a'*40):provenance.SOURCE_SHA,
                ('diff','--name-status','--no-renames',provenance.SOURCE_SHA,'a'*40):'\n'.join(sorted('A\t'+f for f in provenance.TOOL_FILES)),
                ('rev-parse',provenance.SOURCE_SHA+'^{tree}'):provenance.SOURCE_TREE,
                ('rev-parse',provenance.BASE_SHA+'^{tree}'):provenance.BASE_TREE}
            with patch.object(provenance,'verify_candidate_chain'),patch.object(provenance,'git',side_effect=lambda _,*args:answers[args]):
                provenance.verify_release(root,'c'*40,provenance.BRANCH)
                for key in answers:
                    old=answers[key];answers[key]='wrong'
                    with self.subTest(key=key),self.assertRaises(gate.GateError):provenance.verify_release(root,'c'*40,provenance.BRANCH)
                    answers[key]=old
                for bad in (dict(request,source_sha='d'*40),dict(request,schema_version=1),dict(request,schema_version=True),dict(request,tools_sha=1),dict(request,extra=1)):
                    p.write_text(json.dumps(bad))
                    with self.assertRaises(gate.GateError):provenance.verify_release(root,'c'*40,provenance.BRANCH)

    def test_workflow_only_one_candidate_and_narrow_always_temporary_reports(self):
        root=gate.HERE.parents[1];w=(root/'.github/workflows/sendpump-cleanup-dev-ordinary-diagnostic.yml').read_text()
        self.assertIn('retention-days: 3',w);self.assertIn('if: ${{ always() }}',w)
        self.assertEqual(w.count('actions/upload-artifact@'),1)
        self.assertNotIn('actions/cache@',w);self.assertNotIn('--baseline-root',w)
        self.assertNotIn('--original-root',w);self.assertNotIn('run-performance-matrix',w)
        self.assertIn('/ordinary/*.tunit-report.json',w);self.assertIn('/diagnostic.json',w)
        self.assertIn('github.run_attempt == 1',w)


if __name__=='__main__':unittest.main(verbosity=2)
