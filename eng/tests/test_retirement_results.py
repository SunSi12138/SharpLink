"""Synthetic classifier checks only; never SharpLink/.NET test evidence."""
import copy
import importlib.util
import json
from pathlib import Path
import sys
import tempfile
import unittest

sys.dont_write_bytecode = True
spec = importlib.util.spec_from_file_location('retirement_results', Path(__file__).resolve().parents[1] / 'retirement-results.py')
r = importlib.util.module_from_spec(spec); spec.loader.exec_module(r)


class ClassifierTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.source = Path(self.temp.name) / 'Example.cs'
        self.source.write_text('class Example\n{\n    [Test]\n    public void Chosen()\n    {\n    }\n\n    private void Other() {}\n}\n')
        self.scope = dict(namespace='Example.Tests', **{'class': 'Example'})
        self.methods = [dict(name='Chosen', parameters=[], **{'return': 'System.Void'})]
        self.assembly = 'SharpLink.UnitTests, Version=3.0.0.0, Culture=neutral, PublicKeyToken=null'
        self.row = dict(uid='actual-uid', displayName='Chosen', type=dict(assemblyFullName=self.assembly,
            namespace='Example.Tests', typeName='Example', methodName='Chosen', methodArity=0,
            parameterTypeFullNames=[], returnTypeFullName='System.Void'),
            location=dict(file=str(self.source), lineStart=3, lineEnd=6))
        summary = dict(total=1, passed=1, failed=0, skipped=0, cancelled=0, timedOut=0, flaky=0)
        self.native = dict(schemaVersion=1, assemblyName='SharpLink.UnitTests', tunitVersion='1.73.19.0',
            runtimeVersion='.NET 10.0.1', publicationGeneration='a'*32, summary=summary,
            groups=[dict(className='Example', namespace='Example.Tests', summary=copy.deepcopy(summary),
                tests=[dict(id='actual-uid', displayName='Chosen', methodName='Chosen', className='Example',
                    status='passed', durationMs=0.25, filePath=str(self.source), lineNumber=3, endLineNumber=6)])])
        counts = 'name="SharpLink.UnitTests" tests="1" failures="0" errors="0" skipped="0"'
        self.xml = f'<testsuites {counts}><testsuite {counts}><testcase name="Chosen" classname="Example" /></testsuite></testsuites>'

    def verdict(self, native=None, xml=None):
        return r.passed(self.native if native is None else native, self.xml if xml is None else xml,
                        self.row, self.source, '1.73.19.0')

    def test_exact_positive(self):
        self.assertEqual(self.verdict()['status'], 'PASS')
        self.assertEqual(set(r.discovery(dict(schemaVersion=1, tests=[self.row]), self.scope,
                                        self.methods, self.source, self.assembly)), {'actual-uid'})

    def test_no_failure_skip_retry_even_empty_or_null(self):
        for key in ('exception', 'skipReason', 'retryAttempt', 'attempts'):
            for value in (None, {}, [], 0):
                with self.subTest(key=key, value=value):
                    native = copy.deepcopy(self.native); native['groups'][0]['tests'][0][key] = value
                    with self.assertRaises(ValueError): self.verdict(native)

    def test_nonpass_status_is_never_green(self):
        for status in ('failed', 'error', 'skipped', 'cancelled', 'timedOut', 'flaky', '', None):
            with self.subTest(status=status):
                native = copy.deepcopy(self.native); native['groups'][0]['tests'][0]['status'] = status
                with self.assertRaises(ValueError): self.verdict(native)

    def test_counts_and_extra_executed_case(self):
        for summary in (dict(self.native['summary'], total=2), dict(self.native['summary'], passed=True),
                        dict(self.native['summary'], errors=0), {}):
            for group in (False, True):
                with self.subTest(summary=summary, group=group):
                    native = copy.deepcopy(self.native)
                    (native['groups'][0] if group else native)['summary'] = summary
                    with self.assertRaises(ValueError): self.verdict(native)
        native = copy.deepcopy(self.native); native['groups'][0]['tests'] *= 2
        with self.assertRaises(ValueError): self.verdict(native)

    def test_native_identity_and_duration(self):
        for key, value in [('id', 'different-uid'), ('methodName', 'Other'), ('filePath', 'other.cs'),
                           ('endLineNumber', 7), ('durationMs', -1), ('durationMs', True), ('durationMs', float('nan'))]:
            with self.subTest(key=key, value=value):
                native = copy.deepcopy(self.native); native['groups'][0]['tests'][0][key] = value
                with self.assertRaises(ValueError): self.verdict(native)
        for key, value in [('schemaVersion', True), ('tunitVersion', 'other'), ('publicationGeneration', ''),
                           ('runtimeVersion', '.NET 9.0.1')]:
            native = copy.deepcopy(self.native); native[key] = value
            with self.assertRaises(ValueError): self.verdict(native)

    def test_junit_any_child_or_wrong_counts_rejected(self):
        for tag in ('failure', 'error', 'skipped', 'system-out', 'unknown'):
            with self.subTest(tag=tag):
                xml = self.xml.replace('classname="Example" />', f'classname="Example"><{tag}/></testcase>')
                with self.assertRaises(ValueError): self.verdict(xml=xml)
        for xml in (self.xml.replace('errors="0"', 'errors="1"'), self.xml.replace('name="Chosen"', 'name="Other"'),
                    self.xml.replace('</testsuites>', '<testsuite/></testsuites>'),
                    '<!DOCTYPE testsuites>' + self.xml, '<testsuites/>'):
            with self.assertRaises(ValueError): self.verdict(xml=xml)

    def test_discovery_exact_actual_binding(self):
        for member, key, value in [('type', 'parameterTypeFullNames', ['System.Int32']), ('type', 'methodName', 'Other'),
                                   ('type', 'methodArity', True), ('type', 'assemblyFullName', 'other'),
                                   ('location', 'lineEnd', 8), ('location', 'file', 'other.cs')]:
            with self.subTest(member=member, key=key):
                row = copy.deepcopy(self.row); row[member][key] = value
                with self.assertRaises(ValueError):
                    r.discovery(dict(schemaVersion=1, tests=[row]), self.scope, self.methods, self.source, self.assembly)
        for rows in ([], [self.row, self.row]):
            with self.assertRaises(ValueError):
                r.discovery(dict(schemaVersion=1, tests=rows), self.scope, self.methods, self.source, self.assembly)

    def test_next_helper_attribute_is_not_part_of_selected_method(self):
        self.source.write_text('class Example\n{\n    [Test]\n    public void Chosen()\n    {\n    }\n\n    [MethodImpl(MethodImplOptions.NoInlining)]\n    private void Other() {}\n}\n')
        self.assertEqual(r.source_span(self.source, 'Chosen'), (3, 4, 6))
        # Correct actual span passes; the old misclassified helper-attribute end fails.
        self.assertEqual(set(r.discovery(dict(schemaVersion=1, tests=[self.row]), self.scope,
                                        self.methods, self.source, self.assembly)), {'actual-uid'})
        wrong = copy.deepcopy(self.row); wrong['location']['lineEnd'] = 8
        with self.assertRaises(ValueError):
            r.discovery(dict(schemaVersion=1, tests=[wrong]), self.scope,
                        self.methods, self.source, self.assembly)

    def test_optional_filter_is_exact_when_present(self):
        for value in (None, '', 'wrong-selector'):
            native = copy.deepcopy(self.native); native['filter'] = value
            with self.assertRaises(ValueError): self.verdict(native)
        native = copy.deepcopy(self.native); native['filter'] = 'actual-selector'
        self.assertEqual(r.passed(native, self.xml, self.row, self.source, '1.73.19.0', selector='actual-selector')['status'], 'PASS')

    def test_json_duplicate_and_nonfinite_rejected(self):
        path = Path(self.temp.name) / 'data.json'
        for data in ('{"a": 1, "a": 2}', '{"a": NaN}', '{"a": Infinity}'):
            path.write_text(data)
            with self.assertRaises(ValueError): r.read_json(path)


if __name__ == '__main__':
    unittest.main()
