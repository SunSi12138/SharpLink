#!/usr/bin/env python3
"""Pure Python controls only: never runs CLR, MTP, MSBuild, sockets or IPC."""
import contextlib
import copy
import io
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import MagicMock, patch
import run as gate
import provenance as provenance
from baseline import BASELINE_ASSERTION
# Existing calibrated baseline assertions reused without inventing new negatives.
gate.BASELINE_ASSERTION = BASELINE_ASSERTION
def baseline_report():
    summary = dict(total=1, passed=0, failed=1, skipped=0, cancelled=0, timedOut=0, flaky=0)
    return dict(schemaVersion=1, assemblyName='SharpLink.UnitTests', tunitVersion='1.73.19.0', runtimeVersion='.NET 10.0.14',
        publicationGeneration='a' * 32, summary=summary, groups=[dict(summary=dict(summary), tests=[dict(id=gate.BASELINE_UID,
            status='error', exception=dict(type='System.InvalidOperationException', message=gate.BASELINE_ASSERTION,
            stackTrace='SendPumpCleanupProgressTests.ThrowingBatchReturnMustNotReleaseRerentedOwnerOrAbandonEitherQueue()', innerException=None))])])

class BaselineTests(unittest.TestCase):
    def test_one_exact_assertion_failure_is_required(self):
            gate.validate_baseline(baseline_report(), 2, False)
            gate.validate_baseline(baseline_report(), 1, False)

    def test_exit_timeout_crash_and_unexpected_pass_inconclusive(self):
            for code, timed_out in ((0, False), (3, False), (-9, True), (2, True)):
                with self.subTest(code=code, timeout=timed_out), self.assertRaises(gate.GateError):
                    gate.validate_baseline(baseline_report(), code, timed_out)

    def test_internal_timeout_and_old_generic_assertion_are_inconclusive(self):
            # The former test could swallow an internal WaitAsync timeout and then
            # emit this unrelated aggregate-membership assertion. Never accept it.
            for message, kind in (
                ('every distinct owner-cleanup failure must remain observable', 'System.InvalidOperationException'),
                ('The operation has timed out.', 'System.TimeoutException'),
                (gate.BASELINE_ASSERTION, 'System.TimeoutException'),
                ('cleanup operation did not complete', 'System.InvalidOperationException'),
                (gate.BASELINE_ASSERTION.replace('completed cleanup', 'incomplete cleanup'), 'System.InvalidOperationException'),
                (gate.BASELINE_ASSERTION.replace('abandoned later batch and queued owners', 'unrelated failure'), 'System.InvalidOperationException'),
            ):
                r = baseline_report(); error = r['groups'][0]['tests'][0]['exception']
                error.update(type=kind, message=message)
                with self.subTest(message=message, kind=kind), self.assertRaises(gate.GateError):
                    gate.validate_baseline(r, 2, False)
            r = baseline_report()
            r['groups'][0]['tests'][0]['exception']['innerException'] = dict(type='System.TimeoutException')
            with self.assertRaises(gate.GateError):
                gate.validate_baseline(r, 2, False)

    def test_not_any_failed_test_is_causal(self):
            for mutation in ('uid', 'skip', 'failed-status', 'type', 'message', 'stack', 'inner', 'retry', 'runtime', 'count', 'duplicate'):
                r = baseline_report(); t = r['groups'][0]['tests'][0]; e = t['exception']
                if mutation == 'uid': t['id'] = gate.NEW_UIDS[0]
                if mutation == 'skip': t['status'] = 'skipped'
                if mutation == 'failed-status': t['status'] = 'failed'
                if mutation == 'type': e['type'] = 'System.TimeoutException'
                if mutation == 'message': e['message'] = 'unrelated assertion'
                if mutation == 'stack': e['stackTrace'] = 'infrastructure failure'
                if mutation == 'inner': e['innerException'] = dict(type='SocketException')
                if mutation == 'retry': t['retryAttempt'] = 1
                if mutation == 'runtime': r['runtimeVersion'] = '.NET 10.0.2'
                if mutation == 'count': r['summary']['failed'] = True
                if mutation == 'duplicate': r['groups'][0]['tests'].append(copy.deepcopy(t))
                with self.subTest(mutation=mutation), self.assertRaises(gate.GateError): gate.validate_baseline(r, 2, False)


def discovery(raw):
    workers = {w['uid']: w for w in gate.explicit_workers(gate.HERE)}
    tests = []
    for uid in raw:
        item = dict(uid=uid)
        if uid in workers:
            w = workers[uid]
            item.update(type=dict(namespace=w['namespace'], typeName=w['type_name'], methodName=w['method_name']),
                        location=dict(file='/source/' + w['source_path']))
        if uid in gate.NEW_UIDS:
            item.update(type=dict(namespace='SharpLink.UnitTests.Runtime', typeName='SendPumpCleanupProgressTests'),
                        location=dict(file='/source/' + gate.TEST_PATH))
        tests.append(item)
    return dict(schemaVersion=1, tests=tests)


def original_uids():
    return sorted([w['uid'] for w in gate.explicit_workers(gate.HERE)] + ['synthetic.' + str(i) for i in range(2188)])


def passed_report(uids):
    summary = dict(total=len(uids), passed=len(uids), failed=0, skipped=0, cancelled=0, timedOut=0, flaky=0)
    return dict(schemaVersion=1, assemblyName='SharpLink.UnitTests', tunitVersion='1.73.19.0', runtimeVersion='.NET 10.0.14',
                publicationGeneration='a' * 32, summary=dict(summary),
                groups=[dict(summary=dict(summary), tests=[dict(id=u, status='passed') for u in uids])])


class CensusTests(unittest.TestCase):
    def test_raw_exact_delta_and_five_worker_exclusions(self):
        raw = original_uids()
        with patch.object(gate, 'verify_explicit_sources'), contextlib.redirect_stdout(io.StringIO()):
            self.assertEqual(len(gate.census(Path('/source'), discovery(raw))[1]), 2188)
            self.assertEqual(len(gate.census(Path('/source'), discovery(raw + gate.NEW_UIDS), raw)[1]), 2194)
            for kind in ('missing', 'extra', 'replacement', 'duplicate', 'mapping', 'old_new_overlap'):
                new = discovery(raw + gate.NEW_UIDS); original = list(raw)
                if kind == 'missing': new['tests'].pop()
                if kind == 'extra': new['tests'].append(dict(uid='extra'))
                if kind == 'replacement': new['tests'][0]['uid'] = 'replacement'
                if kind == 'duplicate': new['tests'][-1] = dict(new['tests'][-2])
                if kind == 'mapping': new['tests'][-1]['location']['file'] = '/wrong.cs'
                if kind == 'old_new_overlap': original[0] = gate.NEW_UIDS[0]
                with self.subTest(kind=kind), self.assertRaises(gate.GateError):
                    gate.census(Path('/source'), new, original)

    def test_exact_explicit_class_markers_and_discovery_source(self):
        workers = gate.explicit_workers(gate.HERE)
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            for w in workers:
                path = root / w['source_path']; path.parent.mkdir(parents=True, exist_ok=True)
                path.write_text('[Explicit]\npublic sealed class ' + w['type_name'] + '\n')
            document = discovery(original_uids())
            gate.verify_explicit_sources(root, document, workers)
            path = root / 'test/SharpLink.UnitTests/Extra.cs'; path.write_text('[Explicit]')
            with self.assertRaises(gate.GateError): gate.verify_explicit_sources(root, document, workers)

    def test_all_pass_requires_exact_runtime_status_counters_and_no_retries(self):
        for kind in (None, 'skip', 'error', 'missing', 'retry', 'attempts', 'counter', 'exit', 'timeout'):
            value = passed_report(gate.NEW_UIDS)
            code, timed = 0, False
            if kind in ('skip', 'error'): value['groups'][0]['tests'][0]['status'] = kind
            if kind == 'missing': value['groups'][0]['tests'].pop()
            if kind == 'retry': value['groups'][0]['tests'][0]['retryAttempt'] = 1
            if kind == 'attempts': value['groups'][0]['tests'][0]['attempts'] = [{}]
            if kind == 'counter': value['summary']['passed'] = True
            if kind == 'exit': code = 1
            if kind == 'timeout': timed = True
            if kind is None: gate.validate_run(gate.NEW_UIDS, value, code, timed)
            else:
                with self.subTest(kind=kind), self.assertRaises(gate.GateError): gate.validate_run(gate.NEW_UIDS, value, code, timed)
        with contextlib.redirect_stdout(io.StringIO()), self.assertRaises(gate.GateError):
            gate.host.require_test_runtime(dict(runtimeVersion='.NET 10.0.2'))

    def test_full_and_focused_cli_unchanged_except_dev_count(self):
        full = gate.command('/runtime/dotnet', Path('/source/tests.dll'), Path('/output'))
        self.assertEqual(full[full.index('--minimum-expected-tests') + 1], '2194')
        self.assertEqual(full[full.index('--maximum-parallel-tests') + 1], '4')
        focus = gate.command('/runtime/dotnet', Path('/source/tests.dll'), Path('/output'), gate.BASELINE_UID)
        self.assertEqual(focus[-2:], ['--filter-uid', gate.BASELINE_UID])
        self.assertNotIn('build', full)


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


class OrchestrationTests(unittest.TestCase):
    def test_no_retry_all_candidate_stages_run_after_safe_failures(self):
        for failure in (None, 'baseline', 'focused', 'ordinary', 'unsafe'):
            with self.subTest(failure=failure), tempfile.TemporaryDirectory() as tmp:
                root = Path(tmp); roots = {p: root / p for p in gate.IDENTITIES}; out = root / 'logs'; out.mkdir()
                calls = []; stages = []
                def prep(source, phase, sdk, runtime, env, output, original):
                    return source / 'unit.dll', {'unit.dll': 'a'}, original_uids(), ['ordinary']
                def case(source, assembly, identity, runtime, env, output, uid, ordinary=None, baseline=False):
                    calls.append((baseline, uid))
                    if (failure in ('baseline', 'unsafe') and baseline) or (failure == 'focused' and uid == gate.NEW_UIDS[0]) or (failure == 'ordinary' and uid is None):
                        raise gate.host.UnsafeProcessGroup('unsafe') if failure == 'unsafe' else gate.GateError('RAW_PRIVATE_FAILURE')
                with patch.object(gate, 'prepare', side_effect=prep), patch.object(gate, 'execute_case', side_effect=case), \
                     patch.object(gate, 'hashes', return_value={'unit.dll': 'a'}), patch.object(gate, 'verify_source'), \
                     contextlib.redirect_stdout(io.StringIO()) as stdout:
                    if failure is None: gate.execute_all(roots, 'sdk', 'runtime', {}, out, stages)
                    else:
                        with self.assertRaises(gate.GateError): gate.execute_all(roots, 'sdk', 'runtime', {}, out, stages)
                self.assertNotIn('RAW_PRIVATE_FAILURE', stdout.getvalue())
                self.assertEqual(len(calls), 1 if failure == 'unsafe' else 8)
                if failure != 'unsafe':
                    self.assertEqual(len(stages), 14)
                    self.assertEqual(calls[1:7], [(False, uid) for uid in gate.NEW_UIDS])
                    self.assertEqual(sum(not s['passed'] for s in stages), 0 if failure is None else 1)

    def test_build_is_normal_release_no_audit_or_warning_relaxation(self):
        with tempfile.TemporaryDirectory() as tmp:
            out = Path(tmp); command = []
            def execute(args, *rest): command.extend(args); return 1, False
            with patch.object(gate, 'verify_source'), patch.object(gate.host, 'execute', side_effect=execute), self.assertRaises(gate.GateError):
                gate.prepare(Path('/source'), 'candidate', 'sdk', 'runtime', {}, out)
            self.assertEqual(command, ['sdk', 'build', gate.host.PROJECT, '-c', 'Release', '-m:1', '-nr:false',
                '--disable-build-servers', '-p:UseSharedCompilation=false', '-v', 'minimal'])


class ProvenanceTests(unittest.TestCase):
    def test_candidate_exact_three_commit_chain_and_fixture_only_baseline(self):
        for phase in ('original', 'baseline', 'candidate'):
            sha, tree = provenance.IDENTITIES[phase]
            answers = {('rev-parse', 'HEAD'): sha, ('rev-parse', 'HEAD^{tree}'): tree,
                ('status', '--porcelain', '--untracked-files=all'): '',
                ('show', '-s', '--format=%P', 'HEAD'): provenance.BASE_SHA,
                ('diff', '--name-status', '--no-renames', provenance.BASE_SHA, 'HEAD'): 'A\t' + provenance.TEST_PATH,
                ('rev-parse', 'HEAD:' + provenance.TEST_PATH): provenance.TEST_BLOB}
            with tempfile.TemporaryDirectory() as tmp, patch.object(provenance, 'verify_candidate_chain') as chain, \
                 patch.object(provenance, 'git', side_effect=lambda _, *args: answers[args]):
                provenance.verify_source(Path(tmp), phase)
                self.assertEqual(chain.call_count, 1 if phase == 'candidate' else 0)
                for key in (('rev-parse', 'HEAD'), ('rev-parse', 'HEAD^{tree}'), ('status', '--porcelain', '--untracked-files=all')):
                    saved = answers[key]; answers[key] = 'wrong'
                    with self.assertRaises(gate.GateError): provenance.verify_source(Path(tmp), phase)
                    answers[key] = saved
                if phase == 'baseline':
                    answers[('diff', '--name-status', '--no-renames', provenance.BASE_SHA, 'HEAD')] += '\nM\t' + provenance.PRODUCT_PATH
                    with self.assertRaises(gate.GateError): provenance.verify_source(Path(tmp), phase)

    def test_request_only_tool_parent_and_tree_binding(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp); path = root / provenance.REQUEST; path.parent.mkdir(parents=True)
            request = dict(schema_version=1, source_sha=provenance.SOURCE_SHA, baseline_sha=provenance.OVERLAY_SHA,
                tools_sha='a' * 40, tools_tree='b' * 40)
            path.write_text(json.dumps(request))
            answers = {('rev-parse', 'HEAD'): 'c' * 40, ('status', '--porcelain', '--untracked-files=all'): '',
                ('show', '-s', '--format=%P', 'HEAD'): 'a' * 40,
                ('rev-parse', 'a' * 40 + '^{tree}'): 'b' * 40,
                ('diff', '--name-status', '--no-renames', 'a' * 40, 'HEAD'): 'A\t' + provenance.REQUEST,
                ('show', '-s', '--format=%P', 'a' * 40): provenance.SOURCE_SHA,
                ('diff', '--name-status', '--no-renames', provenance.SOURCE_SHA, 'a' * 40): '\n'.join(sorted('A\t' + p for p in provenance.TOOL_FILES)),
                ('rev-parse', provenance.SOURCE_SHA + '^{tree}'): provenance.SOURCE_TREE,
                ('rev-parse', provenance.BASE_SHA + '^{tree}'): provenance.BASE_TREE}
            with patch.object(provenance, 'verify_candidate_chain'), patch.object(provenance, 'git', side_effect=lambda _, *args: answers[args]):
                provenance.verify_release(root, 'c' * 40, provenance.BRANCH)
                for key in answers:
                    saved = answers[key]; answers[key] = 'wrong'
                    with self.subTest(key=key), self.assertRaises(gate.GateError): provenance.verify_release(root, 'c' * 40, provenance.BRANCH)
                    answers[key] = saved
                for key in ('source_sha', 'baseline_sha'):
                    bad = dict(request); bad[key] = 'd' * 40; path.write_text(json.dumps(bad))
                    with self.assertRaises(gate.GateError): provenance.verify_release(root, 'c' * 40, provenance.BRANCH)
                path.write_text(json.dumps(request))
                with self.assertRaises(gate.GateError): provenance.verify_release(root, 'c' * 40, 'refs/heads/dev')

    def test_actual_frozen_candidate_chain_in_tool_checkout(self):
        # git reads only; no product/runtime execution. The checkout contains the
        # fixed ancestry (workflow fetch-depth=8 includes it after request/tools).
        root = gate.HERE.parents[1]
        provenance.verify_candidate_chain(root)
        self.assertEqual(provenance.git(root, 'rev-parse', provenance.SOURCE_SHA + '^{tree}'), provenance.SOURCE_TREE)

    def test_workflow_and_tool_scope_no_upload_perf_or_tcp_fixture(self):
        root = gate.HERE.parents[1]
        workflow = (root / '.github/workflows/sendpump-cleanup-dev-no-upload.yml').read_text()
        for bad in ('actions/upload-artifact@', 'actions/cache@', 'run-performance-matrix', 'static-stream-cleanup-regression', 'sudo ', 'pull_request:'):
            self.assertNotIn(bad, workflow)
        self.assertIn('github.run_attempt == 1', workflow)
        self.assertIn('contents: read', workflow)
        self.assertIn('persist-credentials: false', workflow)
        files = ['.github/workflows/sendpump-cleanup-dev-no-upload.yml'] + [str(p.relative_to(root)) for p in gate.HERE.iterdir() if p.is_file()]
        self.assertEqual(sorted(files), provenance.TOOL_FILES)



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
                    gate.stage('unsafe', root, stages, lambda: gate.host.execute(['host'], root, {}, 1, root / 'execution.log'))
                    gate.stage('next', root, stages, lambda: next_cases.append('must-not-run'))
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

    def test_baseline_group_counters_and_retry_are_strict_integers(self):
        for kind in ('group-bool', 'retry-bool', 'retry-float'):
            value = baseline_report()
            if kind == 'group-bool': value['groups'][0]['summary']['total'] = True
            else: value['groups'][0]['tests'][0]['retryAttempt'] = False if kind == 'retry-bool' else 0.0
            with self.subTest(kind=kind), self.assertRaises(gate.GateError): gate.validate_baseline(value, 2, False)

    def test_final_request_recheck_prevents_pass_after_tools_change(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp); output = root / 'logs'; output.mkdir()
            roots = {phase: root / phase for phase in gate.IDENTITIES}
            for path in roots.values(): path.mkdir()
            argv = ['run.py', '--release-root', str(root / 'release')]
            for phase, path in roots.items(): argv += ['--' + phase + '-root', str(path)]
            with patch.object(gate.sys, 'argv', argv), patch.dict(os.environ, {'GITHUB_RUN_ATTEMPT': '1'}), \
                 patch.object(gate, 'verify_release', side_effect=[{}, gate.GateError('tool changed')]) as verify, \
                 patch.object(gate.host, 'verify_hosts', return_value=('sdk', 'runtime')), \
                 patch.object(gate.tempfile, 'mkdtemp', return_value=str(output)), patch.object(gate, 'execute_all'), \
                 contextlib.redirect_stdout(io.StringIO()) as stdout:
                self.assertEqual(gate.main(), 1)
            self.assertEqual(verify.call_count, 2)
            self.assertNotIn('DEV_CLEANUP_GATE_PASS', stdout.getvalue())
            self.assertEqual(json.loads((output / 'failure.json').read_text())['detail'], 'tool changed')

if __name__ == '__main__': unittest.main(verbosity=2)
