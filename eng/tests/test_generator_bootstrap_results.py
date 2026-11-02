"""Pure synthetic/source controls; never execute an SDK or a C# test body."""
import copy
import contextlib
import hashlib
import io
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location('generator_results_tests', ROOT / 'eng/generator-bootstrap-results.py')
R = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(R)
CAT = json.loads((ROOT / 'eng/generator-bootstrap-controls.json').read_text())


def row(item, index=0):
    span = CAT['sourceSpans'][item['spanKey']]
    return dict(uid='synthetic-uid-' + str(index), displayName=item['expectedDisplayName'],
                type=dict(namespace=CAT['namespace'], typeName=CAT['class'], methodName=item['method'],
                          assemblyFullName=CAT['assembly'], methodArity=0,
                          parameterTypeFullNames=item['parameterTypes'], returnTypeFullName=CAT['return']),
                location=dict(file=str(R.reported_source_path(item['source'])),
                              lineStart=span['attributeStartLine'], lineEnd=span['methodEndLine']))


def reports(item):
    discovered = row(item)
    summary = dict(total=1, passed=1, failed=0, skipped=0, cancelled=0, timedOut=0, flaky=0)
    test = dict(id=discovered['uid'], displayName=discovered['displayName'], methodName=item['method'],
                className=CAT['class'], filePath=discovered['location']['file'],
                lineNumber=discovered['location']['lineStart'], endLineNumber=discovered['location']['lineEnd'],
                status='passed', durationMs=1)
    native = dict(schemaVersion=1, assemblyName='SharpLink.Generator.Tests', tunitVersion='1.73.19.0',
                  runtimeVersion='.NET 10.0.14', publicationGeneration='a' * 32, summary=summary,
                  groups=[dict(namespace=CAT['namespace'], className=CAT['class'], summary=summary.copy(), tests=[test])])
    counts = dict(name='SharpLink.Generator.Tests', tests='1', failures='0', errors='0', skipped='0')
    junit = ET.Element('testsuites', counts)
    suite = ET.SubElement(junit, 'testsuite', counts)
    ET.SubElement(suite, 'testcase', dict(name=item['expectedDisplayName'], classname=CAT['class']))
    return discovered, native, ET.tostring(junit, encoding='unicode')


def accept(native, junit, discovered, item):
    return R.passed(native, junit, discovered, R.reported_source_path(item['source']), CAT['tunitRuntimeVersion'])


class GeneratorBootstrapResultTests(unittest.TestCase):
    def test_five_explicit_spans_and_all_eight_discovery_bindings(self):
        for span in CAT['sourceSpans'].values():
            R.verify_span(ROOT / span['source'], span)
        all_uids = set()
        for method in dict.fromkeys(item['method'] for item in CAT['cases']):
            cases = [item for item in CAT['cases'] if item['method'] == method]
            rows = [row(item, CAT['cases'].index(item)) for item in cases]
            found = R.discovery(dict(schemaVersion=1, tests=rows), CAT, cases, ROOT)
            self.assertFalse(all_uids.intersection(found))
            all_uids.update(found)
        self.assertEqual(len(all_uids), 8)

    def test_raw_string_and_return_line_truncations_rejected(self):
        for key, ends in ((CAT['cases'][2]['method'], (22, 44)), (CAT['cases'][3]['method'], (57, 79))):
            item = next(item for item in CAT['cases'] if item['method'] == key)
            for end in ends:
                with self.subTest(method=key, end=end):
                    discovered = row(item)
                    discovered['location']['lineEnd'] = end
                    with self.assertRaises(ValueError):
                        R.discovery(dict(schemaVersion=1, tests=[discovered]), CAT, [item], ROOT)

    def test_source_bytes_and_boundary_ledger_mutants_rejected(self):
        span = next(iter(CAT['sourceSpans'].values()))
        raw = (ROOT / span['source']).read_bytes()
        with tempfile.TemporaryDirectory(prefix='slgen-span-') as directory:
            path = Path(directory) / 'source.cs'
            path.write_bytes(raw + b'\n')
            with self.assertRaises(ValueError):
                R.verify_span(path, span)
        for field, value in (('methodEndLine', 25), ('signatureText', 'wrong'), ('methodBytesSha256', '0' * 64),
                             ('nextNonemptyText', 'wrong'), ('sourceSha256', '0' * 64)):
            with self.subTest(field=field):
                changed = dict(span, **{field: value})
                with self.assertRaises(ValueError):
                    R.verify_span(ROOT / span['source'], changed)

    def test_missing_duplicate_extra_or_wrong_negative_argument_rows_rejected(self):
        cases = CAT['cases'][4:]
        originals = [row(item, index) for index, item in enumerate(cases)]
        changes = [originals[:-1], originals + [row(CAT['cases'][0], 10)], [originals[0]] * 4]
        for field, value in (('displayName', cases[0]['expectedDisplayName']), ('uid', originals[0]['uid'])):
            mutated = copy.deepcopy(originals)
            mutated[1][field] = value
            changes.append(mutated)
        mutated = copy.deepcopy(originals)
        mutated[-1]['displayName'] = mutated[-1]['displayName'].replace('SHARPLINK', 'sharplink')
        changes.append(mutated)
        for changed in changes:
            with self.assertRaises(ValueError):
                R.discovery(dict(schemaVersion=1, tests=changed), CAT, cases, ROOT)

    def test_discovery_type_path_and_span_identity_mutants_rejected(self):
        item = CAT['cases'][0]
        for section, field, value in (('type', 'assemblyFullName', 'wrong'), ('type', 'methodName', 'wrong'),
                                     ('type', 'parameterTypeFullNames', ['System.Int32']), ('type', 'methodArity', True),
                                     ('type', 'returnTypeFullName', 'System.Void'), ('location', 'file', '/tmp/source.cs'),
                                     ('location', 'lineStart', 1), ('location', 'lineEnd', True)):
            changed = row(item)
            changed[section][field] = value
            with self.subTest(field=field), self.assertRaises(ValueError):
                R.discovery(dict(schemaVersion=1, tests=[changed]), CAT, [item], ROOT)

    def test_all_eight_synthetic_singleton_reports_pass(self):
        for item in CAT['cases']:
            discovered, native, junit = reports(item)
            self.assertEqual(accept(native, junit, discovered, item)['status'], 'PASS')

    def test_native_identity_counts_and_nonpass_fields_rejected(self):
        item = CAT['cases'][0]
        discovered, native, junit = reports(item)
        changes = []
        for field, value in (('schemaVersion', True), ('assemblyName', 'SharpLink.UnitTests'),
                             ('tunitVersion', '1.78.0.0'), ('runtimeVersion', '.NET 9.0.1'), ('publicationGeneration', '')):
            changed = copy.deepcopy(native); changed[field] = value; changes.append(changed)
        for field, value in (('id', 'wrong'), ('displayName', 'wrong'), ('methodName', 'wrong'), ('className', 'wrong'),
                             ('filePath', '/tmp/wrong.cs'), ('lineNumber', 1), ('endLineNumber', 1), ('status', 'skipped'),
                             ('exception', None), ('exception', {}), ('skipReason', ''), ('retryAttempt', 0),
                             ('attempts', []), ('durationMs', float('nan'))):
            changed = copy.deepcopy(native); changed['groups'][0]['tests'][0][field] = value; changes.append(changed)
        changed = copy.deepcopy(native); changed['summary']['total'] = 0; changes.append(changed)
        changed = copy.deepcopy(native); changed['groups'][0]['tests'].append(copy.deepcopy(changed['groups'][0]['tests'][0])); changes.append(changed)
        for changed in changes:
            with self.assertRaises(ValueError):
                accept(changed, junit, discovered, item)

    def test_junit_mismatch_and_nonpass_children_rejected(self):
        item = CAT['cases'][0]
        discovered, native, junit = reports(item)
        changes = [junit.replace('tests="1"', 'tests="2"'), junit.replace(item['expectedDisplayName'], 'wrong'),
                   '<!DOCTYPE testsuites>' + junit]
        for tag in ('failure', 'error', 'skipped', 'system-out', 'unknown'):
            changed = ET.fromstring(junit)
            ET.SubElement(changed[0][0], tag)
            changes.append(ET.tostring(changed, encoding='unicode'))
        for changed in changes:
            with self.assertRaises(ValueError):
                accept(native, changed, discovered, item)

    def test_filter_is_diagnostic_only_but_cannot_rescue_wrong_uid(self):
        item = CAT['cases'][0]
        discovered, native, junit = reports(item)
        for sentinel in ('absent', None, 'different-filter', {'untrusted': 'metadata'}):
            changed = copy.deepcopy(native)
            if sentinel != 'absent': changed['filter'] = sentinel
            self.assertEqual(accept(changed, junit, discovered, item)['status'], 'PASS')
            changed['groups'][0]['tests'][0]['id'] = 'wrong'
            with self.assertRaises(ValueError): accept(changed, junit, discovered, item)

    def test_exact_uid_argv_and_unique_publication_required(self):
        argv = ['dotnet', '/tmp/a.dll', '--filter-uid', 'actual-uid', '--timeout', '90s']
        R.verify_uid_command(argv, '/tmp/a.dll', 'actual-uid')
        for changed in (argv + ['--treenode-filter', '*'], argv + ['--filter-uid', 'other'],
                        ['dotnet', '/tmp/a.dll', '--filter-uid', 'wrong'],
                        ['dotnet', '/tmp/a.dll', '--filter-uid', 'actual-uid', 'other-uid']):
            with self.assertRaises(ValueError): R.verify_uid_command(changed, '/tmp/a.dll', 'actual-uid')
        generations = set(); verdict = dict(publicationGeneration='a' * 32)
        R.unique_generation(verdict, generations)
        with self.assertRaises(ValueError): R.unique_generation(verdict, generations)

    def test_remaining_total_budget_caps_each_process(self):
        spec = importlib.util.spec_from_file_location('generator_budget_tests', ROOT / 'eng/run-generator-bootstrap-controls.py')
        runner = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(runner)
        self.assertEqual(runner.command_seconds(20, 120, now=15), 5)
        self.assertEqual(runner.command_seconds(200, 120, now=15), 120)
        for now in (20, 21):
            with self.assertRaises(ValueError): runner.command_seconds(20, 120, now=now)

    def test_failure_console_contains_only_safe_identities(self):
        spec = importlib.util.spec_from_file_location('generator_safe_console', ROOT / 'eng/run-generator-bootstrap-controls.py')
        runner = importlib.util.module_from_spec(spec); spec.loader.exec_module(runner)
        secret = 'SECRET-source-token /private/customer.cs <xml attr="secret">\nerror failed exception'
        malicious = type('SECRET_exception_name', (ValueError,), {})(secret)
        row = dict(stage='case-2', exitCode=7, timedOut=False, errors=[secret],
                   cleanupErrors=[secret], reportingErrors=[], remainingOwned={})
        command = runner.owner.CommandFailure(row, [malicious])
        group = ExceptionGroup(secret, [command, malicious, ValueError(secret)])
        output = io.StringIO()
        with tempfile.TemporaryDirectory(prefix='slgen-safe-') as directory:
            logs = Path(directory)
            for stream in ('stdout', 'stderr'):
                (logs / ('case-2.' + stream)).write_text(secret)
            with contextlib.redirect_stdout(output):
                runner.summarize_logs(logs, 'case-2')
                runner.summarize_failure(group)
            self.assertEqual((logs / 'case-2.stderr').read_text(), secret)
        public = output.getvalue()
        for forbidden in ('SECRET', '/private/', '<xml', 'attr=', secret):
            self.assertNotIn(forbidden, public)
        self.assertIn(hashlib.sha256(secret.encode()).hexdigest(), public)
        command_summary = json.loads(next(line.split(': ', 1)[1] for line in public.splitlines()
                                         if line.startswith('command-failure: ')))
        self.assertEqual(command_summary, dict(phase='case-2', exitCode=7, timedOut=False,
                         executeErrors=1, cleanupErrors=1, reportingErrors=0, remainingOwned=0))
        private = runner.private_errors([group, malicious])
        self.assertEqual(private['nodes'][private['roots'][0]]['message'], str(group))
        command_node = private['nodes'][private['nodes'][private['roots'][0]]['members'][0]]
        self.assertEqual(command_node['commandCauses'], [private['roots'][1]])
        self.assertEqual(private['nodes'][private['roots'][1]]['message'], secret)
        row['stage'] = secret
        with contextlib.redirect_stdout(io.StringIO()) as unknown:
            runner.summarize_failure(runner.owner.CommandFailure(row, [malicious]))
        self.assertNotIn(secret, unknown.getvalue())
        self.assertIn('"phase": "unknown"', unknown.getvalue())

    def test_reporter_and_private_write_failures_remain_nonzero_without_traceback(self):
        spec = importlib.util.spec_from_file_location('generator_failed_console', ROOT / 'eng/run-generator-bootstrap-controls.py')
        runner = importlib.util.module_from_spec(spec); spec.loader.exec_module(runner)
        secret = 'PRIVATE-exception-message/customer/path'
        output = io.StringIO()
        with tempfile.TemporaryDirectory(prefix='slgen-safe-final-') as directory:
            with patch.object(runner, 'main', side_effect=ValueError(secret)), \
                 patch.object(runner.tempfile, 'mkdtemp', return_value=directory), \
                 contextlib.redirect_stdout(output):
                self.assertEqual(runner.guarded_main(), 1)
            graph = json.loads((Path(directory) / 'failure.json').read_text())
            self.assertEqual(graph['nodes'][graph['roots'][0]]['message'], secret)
        self.assertNotIn(secret, output.getvalue())
        for write_fails in (False, True):
            with patch.object(runner, 'main', side_effect=ValueError(secret)), \
                 patch.object(runner.tempfile, 'mkdtemp', side_effect=OSError(secret)), \
                 patch.object(runner, 'summarize_failure', side_effect=OSError(secret)), \
                 patch.object(runner.os, 'write', side_effect=OSError(secret) if write_fails else None) as fallback:
                self.assertEqual(runner.guarded_main(), 1)
                self.assertEqual(fallback.call_args.args, (2, b'GENERATOR_BOOTSTRAP_FAIL safe-reporter-unavailable\n'))

    def test_duplicate_json_keys_and_missing_report_rejected(self):
        with tempfile.TemporaryDirectory(prefix='slgen-json-') as directory:
            path = Path(directory) / 'report.json'
            with self.assertRaises(FileNotFoundError): R.read_json(path)
            path.write_text('{"schemaVersion":1,"schemaVersion":1}')
            with self.assertRaises(ValueError): R.read_json(path)


if __name__ == '__main__':
    unittest.main()
