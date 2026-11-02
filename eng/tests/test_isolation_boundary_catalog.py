"""Pure catalog/evidence/privacy controls; no SDK or C# body is executed."""
import contextlib
import copy
import importlib.util
import io
import json
import os
import subprocess
import sys
from pathlib import Path
import tempfile
import unittest
from unittest import mock

ROOT = Path(__file__).resolve().parents[2]

def load(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    result = importlib.util.module_from_spec(spec); spec.loader.exec_module(result)
    return result

lane = load('isolation_nine_runner', ROOT / 'eng/run-isolation-boundary-controls.py')
previous = load('existing_retirement_synthetics', ROOT / 'eng/tests/test_retirement_results.py')


def summary():
    return {group: dict(PASS=count, FAILED_OR_INCOMPLETE=0, INCOMPLETE=0, UNRUN=0)
            for group, count in [('ABScope', 6), ('B2a', 2), ('B2c', 1)]}


class CatalogTests(unittest.TestCase):
    def setUp(self):
        self.catalog = lane.results.read_json(ROOT / 'eng/isolation-boundary-controls.json')

    def test_exact_nine_and_static_spans(self):
        lane.verify_graph(ROOT, self.catalog)
        self.assertEqual([len(s['methods']) for s in self.catalog['scopes']], [6, 2, 1])
        self.assertEqual([s['discovery'] for s in self.catalog['scopes']], ['class', 'methods', 'methods'])

    def test_catalog_extra_missing_duplicate_or_wrong_method(self):
        for change in ('extra', 'missing', 'duplicate', 'different'):
            value = copy.deepcopy(self.catalog); methods = value['scopes'][0]['methods']
            if change == 'extra': methods.append(copy.deepcopy(methods[0]))
            elif change == 'missing': methods.pop()
            elif change == 'duplicate': methods[1] = copy.deepcopy(methods[0])
            else: methods[0]['name'] = 'Unselected'
            with self.subTest(change=change), self.assertRaises(ValueError): lane.verify_graph(ROOT, value)

    def test_actual_three_token_signatures_cannot_be_erased(self):
        for scope in (1, 2):
            value = copy.deepcopy(self.catalog); value['scopes'][scope]['methods'][0]['parameters'] = []
            with self.assertRaises(ValueError): lane.verify_graph(ROOT, value)

    def test_no_broad_b2_class_selector(self):
        for scope in (1, 2):
            value = copy.deepcopy(self.catalog); value['scopes'][scope]['discovery'] = 'class'
            with self.assertRaises(ValueError): lane.verify_graph(ROOT, value)

    def test_exact_source_hash_and_span_not_suffix_guess(self):
        for change in ('hash', 'span'):
            value = copy.deepcopy(self.catalog)
            if change == 'hash': value['scopes'][0]['sourceSha256'] = '0' * 64
            else: value['scopes'][0]['methods'][0]['expectedSpan'][2] += 1
            with self.assertRaises(ValueError): lane.verify_graph(ROOT, value)


class EvidenceTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.work = Path(self.temp.name)
        self.seals = lane.EvidenceSeals(self.work)
        self.report = self.work / 'accepted'; self.report.mkdir()
        self.native = self.report / 'session.tunit-report.json'
        self.junit = self.report / 'junit.xml'
        # An actual invocation of the unchanged pure classifier establishes the
        # synthetic accepted observation before any tamper experiment.
        fixture = previous.ClassifierTests('test_exact_positive'); fixture.setUp()
        self.addCleanup(fixture.doCleanups)
        self.native.write_text(json.dumps(fixture.native)); self.junit.write_text(fixture.xml)
        self.seals.register(self.report)
        self.verdict = lane.results.passed(lane.results.read_json(self.native), self.junit.read_text(),
                                           fixture.row, fixture.source, '1.73.19.0')
        self.seals.verify()
        self.assertEqual(self.verdict['status'], 'PASS')

    def test_accepted_report_stable_positive(self):
        self.seals.verify()

    def test_changed_native_and_deleted_junit_rejected(self):
        self.native.write_text('{}')
        with self.assertRaises(BaseExceptionGroup): self.seals.verify()
        self.junit.unlink()
        with self.assertRaises(BaseExceptionGroup): self.seals.verify()
        self.assertEqual(self.verdict['status'], 'PASS')  # historical observation only

    def test_identical_bytes_new_inode_rejected(self):
        replacement = self.work / 'replacement'
        replacement.write_bytes(self.native.read_bytes())
        replacement.replace(self.native)
        with self.assertRaises(BaseExceptionGroup): self.seals.verify()

    def test_added_member_and_directory_rejected(self):
        (self.report / 'extra').write_text('extra')
        with self.assertRaises(BaseExceptionGroup): self.seals.verify()
        (self.report / 'extra-directory').mkdir()
        with self.assertRaises(BaseExceptionGroup): self.seals.verify()

    def test_symlink_hardlink_and_directory_substitution_rejected(self):
        self.native.unlink(); self.native.symlink_to(self.junit)
        with self.assertRaises(BaseExceptionGroup): self.seals.verify()
        self.native.unlink(); os.link(self.junit, self.native)
        with self.assertRaises(BaseExceptionGroup): self.seals.verify()
        self.native.unlink(); self.native.mkdir()
        with self.assertRaises(BaseExceptionGroup): self.seals.verify()

    def test_root_directory_or_mode_replacement_rejected(self):
        original_mode = self.native.stat().st_mode & 0o777
        self.native.chmod(original_mode ^ 0o040)
        with self.assertRaises(BaseExceptionGroup): self.seals.verify()
        moved = self.work / 'moved'; self.report.rename(moved)
        self.report.mkdir()
        for path in moved.iterdir(): (self.report / path.name).write_bytes(path.read_bytes())
        with self.assertRaises(BaseExceptionGroup): self.seals.verify()

    def test_private_write_failure_keeps_original_and_seals_partial_bytes(self):
        marker = OSError('/private/write-failure')
        with mock.patch.object(lane.json, 'dump', side_effect=marker):
            with self.assertRaises(BaseExceptionGroup) as caught:
                lane.write_sealed_json(self.work, 'partial-report', {}, self.seals)
        self.assertIs(caught.exception.exceptions[0], marker)
        self.assertTrue(self.seals.contains(self.work / 'partial-report'))
        self.seals.verify()
        (self.work / 'partial-report/partial-report.json').write_text('late replacement contents')
        with self.assertRaises(BaseExceptionGroup): self.seals.verify()

    def test_prior_listing_ledger_and_owner_receipts_are_sealed(self):
        for index, filename in enumerate(('list-0-0.stdout', 'actual-ledger.json', 'case-0.json')):
            root = self.work / str(index); root.mkdir(); path = root / filename; path.write_text('original')
            self.seals.register(root, {filename}); path.write_text('changed')
            with self.assertRaises(BaseExceptionGroup): self.seals.verify()

    def test_missing_receipt_preserves_partial_set(self):
        root = self.work / 'partial'; root.mkdir(); (root / 'stage.stdout').write_text('original')
        with self.assertRaises(ValueError):
            self.seals.register(root, {'stage.stdout', 'stage.stderr', 'stage.json'})
        self.assertTrue(self.seals.contains(root))
        (root / 'stage.stderr').write_text('late')
        with self.assertRaises(BaseExceptionGroup): self.seals.verify()

    def test_final_only_tamper_revokes_integrity_not_observation(self):
        self.native.write_text('tampered after accepted observation')
        failures = lane.FailureLedger()
        lane.persist_final(self.work, self.seals, failures, [{'status': 'PASS'}], summary())
        self.assertTrue(failures.occurrences)
        console = io.StringIO()
        with contextlib.redirect_stdout(console): lane.render_terminal(failures, summary())
        self.assertIn('"integrity": "FAILED"', console.getvalue())
        self.assertEqual(self.verdict['status'], 'PASS')

    def test_original_and_summary_graph_seal_failures_all_remain(self):
        original = RuntimeError('/private/original')
        write = OSError('/private/report')
        seal = OSError('/private/seal')
        failures = lane.FailureLedger(); failures.add('execution', original)
        with mock.patch.object(lane, 'write_sealed_json', side_effect=write), mock.patch.object(self.seals, 'verify', side_effect=seal):
            lane.persist_final(self.work, self.seals, failures, [], summary())
        self.assertIs(failures.occurrences[0][1], original)
        self.assertTrue(any(error is write for _, error in failures.occurrences))
        self.assertTrue(any(error is seal for _, error in failures.occurrences))
        self.assertIn('terminal-graph', [stage for stage, _ in failures.occurrences])


class PrivacyTests(unittest.TestCase):
    def test_private_path_error_graph_preserves_objects_and_console_is_safe(self):
        sentinel = '/private/DO_NOT_PRINT_ME'
        try: raise FileNotFoundError(2, sentinel, sentinel + '/file')
        except FileNotFoundError as caught: original = caught
        context = ValueError(sentinel + '/context'); cause = OSError(sentinel + '/cause')
        original.__cause__ = cause; original.__context__ = context
        group = BaseExceptionGroup(sentinel, [original, original])
        failures = lane.FailureLedger(); failures.add('execution', group); failures.add('final-evidence', original)
        graph, formatting = failures.graph()
        self.assertEqual(formatting, [])
        self.assertIs(failures.occurrences[0][1], group)
        self.assertIsNotNone(original.__traceback__)
        self.assertEqual([edge['node'] for edge in graph['nodes'][0]['edges'] if edge['kind'] == 'group'], [1, 1])
        self.assertIn(sentinel, json.dumps(graph))
        output = io.StringIO()
        with contextlib.redirect_stdout(output): lane.render_terminal(failures, summary())
        self.assertNotIn(sentinel, output.getvalue())
        self.assertNotIn('FileNotFoundError', output.getvalue())

    def test_cycle_and_same_raw_multiple_stage_occurrences_survive(self):
        first = ValueError('one'); second = ValueError('two')
        first.__cause__ = second; second.__context__ = first
        failures = lane.FailureLedger(); failures.add('execution', first); failures.add('final-source', first)
        graph, formatting = failures.graph()
        self.assertEqual(len(graph['nodes']), 2)
        self.assertEqual([x['node'] for x in graph['occurrences']], [0, 0])
        self.assertFalse(formatting)

    def test_command_custom_causes_and_raw_stderr_stay_private(self):
        sentinel = '/private/STDERR_NOT_CONSOLE'
        cause = OSError(sentinel)
        row = dict(stage='build', exitCode=1, timedOut=False, errors=[sentinel], cleanupErrors=[], reportingErrors=[], remainingOwned={})
        error = lane.owner.CommandFailure(row, [cause])
        error.__cause__ = cause
        failures = lane.FailureLedger(); failures.add('execution', error)
        graph, _ = failures.graph()
        self.assertEqual(graph['nodes'][0]['commandOutcome'], row)
        self.assertTrue(any(e['kind'] == 'custom-cause' for e in graph['nodes'][0]['edges']))
        output = io.StringIO()
        with contextlib.redirect_stdout(output): lane.render_terminal(failures, summary())
        self.assertNotIn(sentinel, output.getvalue())
        source = (ROOT / 'eng/run-isolation-boundary-controls.py').read_text()
        self.assertNotIn("print('diagnostic:", source)
        self.assertNotIn('summarize_failure', source)

    def test_message_format_failure_is_additional_private_graph_edge(self):
        marker = RuntimeError('/private/format')
        class BrokenMessage(Exception):
            def __str__(self): raise marker
        original = BrokenMessage()
        failures = lane.FailureLedger(); failures.add('execution', original)
        graph, formatting = failures.graph()
        self.assertEqual(formatting, [marker])
        self.assertIs(failures.occurrences[0][1], original)
        self.assertTrue(any(edge['kind'] == 'message-format' for edge in graph['nodes'][0]['edges']))
        self.assertEqual(graph['occurrences'][-1]['stage'], 'graph-format')

    def test_renderer_failure_does_not_erase_source_or_print_exception(self):
        source = ValueError('/private/source'); renderer = OSError('/private/renderer')
        failures = lane.FailureLedger(); failures.add('execution', source)
        writes = []
        with mock.patch.object(lane, 'emit', side_effect=renderer), mock.patch.object(lane.os, 'write', side_effect=lambda fd, data: writes.append(data)):
            lane.render_terminal(failures, summary())
        self.assertIs(failures.occurrences[0][1], source)
        self.assertIs(failures.occurrences[-1][1], renderer)
        self.assertNotIn(b'/private', b''.join(writes))

    def test_top_level_unexpected_exception_is_safe_nonzero(self):
        original = FileNotFoundError(2, '/private/top', '/private/file'); writes = []
        with mock.patch.object(lane, 'main', side_effect=original), mock.patch.object(lane.os, 'write', side_effect=lambda fd, data: writes.append(data)):
            self.assertEqual(lane.entry(), 1)
        self.assertIs(lane.TOP_LEVEL_FAILURES[-1], original)
        self.assertNotIn(b'/private', b''.join(writes))

    def test_actual_bootstrap_missing_or_broken_helpers_has_no_traceback_leak(self):
        for variant in ('missing-owner', 'missing-results', 'syntax-owner', 'throwing-results'):
            with self.subTest(variant=variant), tempfile.TemporaryDirectory(prefix='PRIVATE_BOOT_SENTINEL-') as temporary:
                base = Path(temporary); eng = base / 'checkout/eng'; eng.mkdir(parents=True)
                private = base / 'private'; private.mkdir()
                runner = eng / 'run-isolation-boundary-controls.py'
                runner.write_bytes((ROOT / 'eng/run-isolation-boundary-controls.py').read_bytes())
                for name in ('package-smoke-process.py', 'retirement-results.py'):
                    (eng / name).write_bytes((ROOT / 'eng' / name).read_bytes())
                if variant == 'missing-owner': (eng / 'package-smoke-process.py').unlink()
                elif variant == 'missing-results': (eng / 'retirement-results.py').unlink()
                elif variant == 'syntax-owner': (eng / 'package-smoke-process.py').write_text('invalid syntax !!!')
                else:
                    (eng / 'retirement-results.py').write_text(
                        "raise RuntimeError('PRIVATE_BOOT_SENTINEL helper execution failure')\n")
                # The child contains only this guarded Python entry and known
                # failing local helper; subprocess.run kills AND joins on timeout.
                process = subprocess.run([sys.executable, '-I', '-B', str(runner)],
                    env=dict(os.environ, TMPDIR=str(private)), capture_output=True, timeout=10, check=False)
                self.assertEqual(process.returncode, 1)
                self.assertEqual(process.stdout, b'')
                self.assertEqual(process.stderr, b'{"event":"failure","stage":"entry","category":"other"}\n')
                self.assertNotIn(b'PRIVATE_BOOT_SENTINEL', process.stdout + process.stderr)
                self.assertNotIn(b'Traceback', process.stdout + process.stderr)
                roots = list(private.glob('sli9-boot-*')); self.assertEqual(len(roots), 1)
                graph = json.loads((roots[0] / 'failure-graph/failure-graph.json').read_text())
                self.assertEqual(graph['occurrences'][0]['stage'], 'entry')
                self.assertIn('PRIVATE_BOOT_SENTINEL', json.dumps(graph))
                self.assertTrue(graph['nodes'][0]['traceback'])

    def test_console_rejects_untrusted_fields(self):
        for field in ({'stage': '/private/stage'}, {'method': '/private/method'}, {'category': 'arbitrary exception text'}, {'uid': '/private/uid'}):
            output = io.StringIO()
            with contextlib.redirect_stdout(output), self.assertRaises(ValueError): lane.emit('failure', **field)
            self.assertEqual(output.getvalue(), '')


if __name__ == '__main__':
    unittest.main()
