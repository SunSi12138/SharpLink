"""Pure Python path-budget and bounded-report controls; no CLR, sockets, or CI."""
import contextlib
import io
import json
import os
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
from unit_gate import diagnostics, run, temp_paths
from unit_gate.provenance import read_corpus
from unit_gate.validate import GateError

TOOL = Path(__file__).parent
UID = 'SharpLink.UnitTests.Runtime.UnixSocketPermissionTests.1.1.FilesystemUdsListenerShouldPublishOwnerOnlyPermissions.1.1.0'


def failed_report(message='Expected socket path to fit', exception_type='System.ArgumentOutOfRangeException'):
    return {'summary': {'total': 1, 'passed': 0, 'failed': 1, 'skipped': 0, 'cancelled': 0, 'timedOut': 0, 'flaky': 0},
        'groups': [{'tests': [{'id': UID, 'status': 'failed',
            'exception': {'type': exception_type, 'message': message, 'stackTrace': 'STACK_SECRET',
                'innerException': {'message': 'INNER_SECRET'}}, 'output': 'OUTPUT_SECRET',
            'errorOutput': 'STDERR_SECRET', 'spans': [{'payload': 'TRACE_SECRET'}],
            'displayName': 'DISPLAY_SECRET'}]}]}


class TempPathTests(unittest.TestCase):
    def test_old_pathname110_and116_rejected_including_nul(self):
        old = Path('/' + 'x' * 63)
        self.assertEqual(len(str(old).encode()), 64)
        flat = old / ('sl-perm-' + '0' * 32 + '.sock')
        nested = old / ('sl-perm-' + '0' * 32 + '/probe.sock')
        self.assertEqual((len(str(flat).encode()), len(str(nested).encode())), (110, 116))
        self.assertEqual((len(str(flat).encode()) + 1, len(str(nested).encode()) + 1), (111, 117))
        with self.assertRaises(GateError): temp_paths.socket_budget(old)

    def test_new_short_path_and_utf8_exact_boundary(self):
        values = temp_paths.socket_budget(Path('/tmp/slu-12345678'))
        self.assertLessEqual(max(values.values()), 108)
        self.assertEqual(max(temp_paths.socket_budget(Path('/' + 'x' * 54)).values()), 108)
        with self.assertRaises(GateError): temp_paths.socket_budget(Path('/' + 'x' * 55))
        self.assertEqual(max(temp_paths.socket_budget(Path('/' + 'é' * 27)).values()), 108)
        with self.assertRaises(GateError): temp_paths.socket_budget(Path('/' + 'é' * 28))

    def test_source_templates_are_actual_pinned_fixture_forms(self):
        # Read only: independently check the literal suffix formulas against this checkout.
        root = TOOL.parents[2]
        permission = (root / 'test/SharpLink.UnitTests/Runtime/UnixSocketPermissionTests.cs').read_text()
        transport = (root / 'test/SharpLink.UnitTests/Runtime/TransportValidationTests.cs').read_text()
        for text in ('sl-perm-{Guid.NewGuid():N}.sock', 'sl-perm-{Guid.NewGuid():N}', '"probe.sock"'):
            self.assertIn(text, permission)
        for text in ('sl-{Guid.NewGuid():N}', '"directory.sock"', '"file.sock"', 'sharplink-filesystem.sock'):
            self.assertIn(text, transport)

    def test_mkdtemp_private_writable_retained_and_associated_ignores_long_tmpdir(self):
        with tempfile.TemporaryDirectory(dir='/tmp') as temporary:
            root = Path(temporary); short = root / 's'; short.mkdir(mode=0o700); output = root / 'out'; output.mkdir()
            with patch.dict(os.environ, {'TMPDIR': '/very/long/inherited/diagnostic/tmp'}), \
                 patch.object(temp_paths.tempfile, 'mkdtemp', return_value=str(short)) as create:
                self.assertEqual(temp_paths.create_short_temp(output), short)
            create.assert_called_once_with(prefix='slu-', dir='/tmp')
            association = json.loads((output / 'short-temp.json').read_text())
            self.assertEqual(association['path'], str(short)); self.assertTrue(association['retained'])
            self.assertEqual(association['mode'], '0700')
            self.assertTrue((short / '.gate-write-check').exists())
            self.assertLessEqual(max(association['socket_bytes_including_nul'].values()), 108)

    def test_nonprivate_temp_failure_still_leaves_association(self):
        with tempfile.TemporaryDirectory(dir='/tmp') as temporary:
            root = Path(temporary); short = root / 's'; short.mkdir(mode=0o755); output = root / 'out'; output.mkdir()
            with patch.object(temp_paths.tempfile, 'mkdtemp', return_value=str(short)):
                with self.assertRaises(GateError): temp_paths.create_short_temp(output)
            self.assertTrue(short.exists()); self.assertTrue((output / 'short-temp.json').exists())

    def test_env_diff_preserves_inherited_tmp_temp_and_unrelated_keys(self):
        before = {'TMPDIR': '/long/old', 'TMP': '/original-tmp', 'TEMP': '/original-temp',
                  'PATH': '/usr/bin', 'UNRELATED': 'keep', 'CI': 'false'}
        after = run.private_test_environment(before, Path('/runtime/dotnet'), Path('/tmp/slu-12345678'))
        self.assertEqual(after['TMPDIR'], '/tmp/slu-12345678')
        self.assertEqual(after['TMP'], before['TMP']); self.assertEqual(after['TEMP'], before['TEMP'])
        self.assertEqual(after['UNRELATED'], before['UNRELATED'])
        self.assertEqual({key for key in before if before[key] != after.get(key)}, {'TMPDIR', 'PATH', 'CI'})
        self.assertEqual(set(after) - set(before), {'DOTNET_ROOT', 'DOTNET_ROOT_X64', 'DOTNET_HOST_PATH',
            'DOTNET_ROLL_FORWARD', 'TESTINGPLATFORM_TELEMETRY_OPTOUT', 'DOTNET_MULTILEVEL_LOOKUP',
            'DOTNET_CLI_TELEMETRY_OPTOUT', 'DOTNET_PROCESSOR_COUNT', 'PYTHONDONTWRITEBYTECODE'})


class DiagnosticsTests(unittest.TestCase):
    def test_known_uid_public_name_counts_and_verified_exception_schema(self):
        self.assertIn(UID, read_corpus(TOOL)[1])
        value = diagnostics.report_diagnostics(failed_report(), [UID], 2, False)
        self.assertEqual(value['exit'], 2); self.assertEqual(value['reportedSummary']['failed'], 1)
        self.assertEqual(value['observedStatuses'], {'failed': 1})
        failure = value['failures'][0]
        self.assertEqual(failure['uid'], UID)
        self.assertEqual(failure['name'], 'FilesystemUdsListenerShouldPublishOwnerOnlyPermissions')
        self.assertEqual(failure['exceptionType'], 'System.ArgumentOutOfRangeException')
        self.assertEqual(failure['message'], 'Expected socket path to fit')

    def test_raw_output_trace_inner_fields_and_guessed_exception_fields_never_read(self):
        value = failed_report(); test = value['groups'][0]['tests'][0]
        del test['exception']; test.update(exceptionMessage='TOP_SECRET', exceptionType='TOP_SECRET',
            error={'message': 'ERROR_SECRET'}, attempts=[{'stackTrace': 'ATTEMPT_SECRET'}])
        result = diagnostics.report_diagnostics(value, [UID], 2, False)
        self.assertEqual(result['failures'][0]['message'], 'missing-message')
        self.assertNotIn('SECRET', json.dumps(result))

    def test_embedded_child_logs_sensitive_fields_stack_and_multiline_removed(self):
        for text in ('assert failed stdout: RAW_SECRET', 'assert failed stderr= RAW_SECRET',
            'assert failed stackTrace: RAW_SECRET', 'assert failed raw output: RAW_SECRET',
            'assert failed child results: RAW_SECRET', 'assert failed\n  at RAW_SECRET',
            'assert failed std\x00out: RAW_SECRET', 'assert failed api_key=RAW_SECRET',
            'assert failed Authorization: Bearer RAW_SECRET', 'assert failed https://user:RAW_SECRET@example.com'):
            with self.subTest(text=text):
                value = diagnostics.report_diagnostics(failed_report(text), [UID], 2, False)
                self.assertNotIn('RAW_SECRET', json.dumps(value)); self.assertIn('assert failed', value['failures'][0]['message'])

    def test_ordinary_ticks_and_telemetry_assertions_remain_useful(self):
        text = 'Expected elapsed ticks to be positive and traceId/spanId to remain stable'
        value = diagnostics.report_diagnostics(failed_report(text), [UID], 2, False)
        self.assertEqual(value['failures'][0]['message'], text)

    def test_limits_controls_ansi_and_workflow_annotations(self):
        message = '\x1b[31m::error:: assertion\x1b[0m\x00\u202e ' + 'a' * 100000 + '\n::notice::INJECTED'
        value = failed_report(message); value['groups'][0]['tests'] *= 20
        output = io.StringIO()
        with contextlib.redirect_stdout(output): summary = diagnostics.emit_diagnostics(value, [UID], 2, False)
        lines = output.getvalue().splitlines()
        self.assertEqual(len(lines), 13)
        self.assertTrue(lines[0].startswith('UNIT_TEST_STATUS '))
        self.assertTrue(all(line.startswith('UNIT_TEST_FAILURE ') for line in lines[1:]))
        self.assertEqual(summary['omittedFailures'], 8)
        self.assertLessEqual(max(len(item['message']) for item in summary['failures']), 240)
        self.assertNotIn('::error::', output.getvalue()); self.assertNotIn('INJECTED', output.getvalue())
        self.assertNotIn('\x1b', output.getvalue()); self.assertNotIn('SECRET', output.getvalue())
        self.assertLess(len(output.getvalue()), 12000)

    def test_uid_census_missing_unexpected_duplicates_without_echo(self):
        value = failed_report(); tests = value['groups'][0]['tests']
        tests.extend([dict(tests[0]), {'id': 'SECRET', 'status': 'passed'}])
        result = diagnostics.report_diagnostics(value, [UID, 'missing'], 2, False)
        self.assertEqual({key: result[key] for key in ('expectedUIDCount', 'observedUniqueUIDCount',
            'missingUIDCount', 'unexpectedUIDCount', 'duplicateUIDCount')},
            dict(expectedUIDCount=2, observedUniqueUIDCount=2, missingUIDCount=1,
                 unexpectedUIDCount=1, duplicateUIDCount=1))
        self.assertNotIn('SECRET', json.dumps(result))

    def test_unknown_uid_and_malformed_shapes_fail_closed_without_exception(self):
        value = failed_report('SECRET'); value['groups'][0]['tests'][0]['id'] = 'secret-token=SECRET'
        self.assertNotIn('SECRET', json.dumps(diagnostics.report_diagnostics(value, [UID], 2, False)))
        for report in (None, [], {}, {'groups': 'bad'}, {'groups': [None, {'tests': [None, {'status': []}]}]}):
            with self.subTest(report=report): diagnostics.report_diagnostics(report, [UID], -9, True)


if __name__ == '__main__': unittest.main()
