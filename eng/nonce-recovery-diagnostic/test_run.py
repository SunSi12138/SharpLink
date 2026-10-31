#!/usr/bin/env python3
"""Offline checks only: no CLR, sockets, git writes or network."""
from pathlib import Path
import json
import subprocess
from unittest.mock import patch
import tempfile
import unittest
import run


TOOLS = Path(__file__).resolve().parent
ROOT = TOOLS.parents[1]


class DiagnosticToolTests(unittest.TestCase):
    def test_exact_counts_and_all_declared_negative_results(self):
        for total, failed in ((2, 0), (1, 0), (1, 1)):
            text = f'total: {total}\nfailed: {failed}\nsucceeded: {total-failed}\nskipped: 0\n'
            self.assertEqual(run.inspect(text, total, failed)['total'], total)

    def test_missing_extra_failed_skipped_and_inconsistent_counts_fail(self):
        for text in ('total: 1\nsucceeded: 1\nskipped: 0',
                     'total: 3\nsucceeded: 3\nskipped: 0',
                     'total: 2\nfailed: 1\nsucceeded: 1\nskipped: 0',
                     'total: 2\nsucceeded: 1\nskipped: 1',
                     'total: 2\nsucceeded: 1\nskipped: 0', ''):
            with self.subTest(text=text), self.assertRaises(ValueError):
                run.inspect(text, 2, 0)

    def test_zero_or_duplicate_anchor_fails(self):
        for text in ('none', 'anchor anchor'):
            with self.assertRaises(ValueError):
                run.replace_once(text, 'anchor', 'replacement')

    def test_each_overlay_only_touches_declared_runtime_and_diagnostic_files(self):
        original = (ROOT / run.RUNTIME).read_text()
        for mode in run.MODES:
            with self.subTest(mode=mode), tempfile.TemporaryDirectory() as temporary:
                work = Path(temporary)
                (work / run.RUNTIME).parent.mkdir(parents=True)
                (work / run.DIAGNOSTIC).parent.mkdir(parents=True)
                (work / run.RUNTIME).write_text(original)
                run.apply_overlay(work, TOOLS, mode)
                changed = (work / run.RUNTIME).read_text()
                self.assertEqual(changed.count('internal Func<Task>? PipeConnectedForDiagnostics'), 1)
                self.assertEqual(changed.count('internal Func<Task>? RejectedMappingDisposedForDiagnostics'), 1)
                self.assertEqual('acknowledgement nonce did not match' in changed, mode != 'remove-nonce-check')
                self.assertIn('Shared-memory server response failed validation.', changed)
                self.assertEqual('Task.Delay(Timeout.InfiniteTimeSpan' in changed, mode == 'block-rearm')
                self.assertEqual((work / run.DIAGNOSTIC).read_bytes(),
                                 (TOOLS / 'SharedMemoryHandshakeBacklogDiagnosticTests.cs').read_bytes())
                self.assertEqual(sorted(str(p.relative_to(work)) for p in work.rglob('*') if p.is_file()),
                                 sorted((run.RUNTIME, run.DIAGNOSTIC)))
                with self.assertRaises(ValueError):
                    run.apply_overlay(work, TOOLS, mode)
        self.assertEqual((ROOT / run.RUNTIME).read_text(), original)

    def test_unexpected_exit_retains_new_reports_and_isolates_stale_reports(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            out, reports = root / 'out', root / 'TestResults'
            out.mkdir()
            reports.mkdir()
            (reports / 'stale.json').write_text('old')
            calls = []

            def child(*args, **kwargs):
                calls.append(args)
                self.assertFalse(reports.exists())
                reports.mkdir()
                (reports / 'actual.json').write_text('unexpected failure report')
                return subprocess.CompletedProcess(args[0], 17, 'raw stdout', 'raw stderr')

            result = dict(status='incomplete', rows=[])
            with self.assertRaisesRegex(ValueError, 'exit 17, expected 0'):
                run.run_command(out, result, 'bad', ['fake'], root, reports=reports, runner=child)
            self.assertEqual(len(calls), 1)
            self.assertEqual((out / 'bad-TestResults/actual.json').read_text(), 'unexpected failure report')
            self.assertFalse((out / 'bad-TestResults/stale.json').exists())
            self.assertEqual((out / 'bad-preexisting-TestResults/stale.json').read_text(), 'old')
            self.assertEqual((out / 'bad.stdout.log').read_text(), 'raw stdout')
            saved = json.loads((out / 'result.json').read_text())
            self.assertNotEqual(saved['status'], 'passed')
            self.assertEqual(saved['rows'][0]['status'], 'unexpected-exit')
            self.assertEqual(saved['rows'][0]['exitCode'], 17)
            self.assertEqual(saved['rows'][0]['expectedExitCode'], 0)
            self.assertEqual(saved['rows'][0]['reportsStatus'], 'retained')

    def test_timeout_retains_partial_report_and_original_exception_without_retry(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            out, reports = root / 'out', root / 'TestResults'
            out.mkdir()
            calls = []
            failure = subprocess.TimeoutExpired(['fake'], 600, output=b'partial stdout', stderr=b'partial stderr')

            def child(*args, **kwargs):
                calls.append(args)
                reports.mkdir()
                (reports / 'partial.json').write_text('partial native report')
                raise failure

            result = dict(status='incomplete', rows=[])
            with self.assertRaises(subprocess.TimeoutExpired) as raised:
                run.run_command(out, result, 'timeout', ['fake'], root, reports=reports, runner=child)
            self.assertIs(raised.exception, failure)
            self.assertEqual(len(calls), 1)
            self.assertEqual((out / 'timeout-TestResults/partial.json').read_text(), 'partial native report')
            self.assertEqual((out / 'timeout.stdout.log').read_text(), 'partial stdout')
            row = json.loads((out / 'result.json').read_text())['rows'][0]
            self.assertEqual(row['status'], 'timeout')
            self.assertIsNone(row['exitCode'])
            self.assertEqual(row['error']['type'], 'TimeoutExpired')
            self.assertEqual(row['reportsStatus'], 'retained')
            self.assertNotEqual(result['status'], 'passed')

    def test_report_copy_failure_does_not_replace_original_exit_error(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            out, reports = root / 'out', root / 'TestResults'
            out.mkdir()

            def child(*args, **kwargs):
                reports.mkdir()
                return subprocess.CompletedProcess(args[0], 19, '', '')

            result = dict(status='incomplete', rows=[])
            with patch.object(run.shutil, 'copytree', side_effect=OSError('archive broke')):
                with self.assertRaisesRegex(ValueError, 'exit 19, expected 0'):
                    run.run_command(out, result, 'bad', ['fake'], root, reports=reports, runner=child)
            self.assertEqual(result['rows'][0]['reportsStatus'], 'archive-failed')
            self.assertIn('archive broke', result['rows'][0]['reportArchiveError'])

    def test_runtime_identity_requires_exact_version_and_hashes_separate_binaries(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            runtime = root / '10.0.14'
            runtime.mkdir()
            for name in ('System.IO.Pipes.dll', 'System.Private.CoreLib.dll'):
                (runtime / name).write_bytes(name.encode())
            text = f'Microsoft.NETCore.App 10.0.14 [{root}]\n'
            identity = run.capture_runtime_identity(text)
            self.assertEqual(identity['expectedReportRuntime'], '.NET 10.0.14')
            self.assertEqual(len(identity['binaries']), 2)
            self.assertEqual(identity['officialSource']['gitBlob'], '7a0b6dc8e37055dc1670bfce307336e1c36f65cc')
            for wrong in ('', text + text, text.replace('10.0.14', '10.0.15')):
                with self.assertRaises(ValueError):
                    run.capture_runtime_identity(wrong)

    def test_unknown_mode_fails_before_writing(self):
        with tempfile.TemporaryDirectory() as temporary:
            work = Path(temporary)
            with self.assertRaises(ValueError):
                run.apply_overlay(work, TOOLS, 'accept-any-error')
            self.assertEqual(list(work.iterdir()), [])


if __name__ == '__main__':
    unittest.main()
