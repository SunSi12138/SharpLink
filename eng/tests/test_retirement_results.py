"""Synthetic classifier checks only; never SharpLink/.NET test evidence."""
import copy
import contextlib
import io
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

    def test_path_diagnostic_never_accepts_an_alternative_prefix(self):
        expected = Path('/_/retirement-unit/test/Example.cs')
        relative = 'test/Example.cs'
        checkout = Path('/private/checkout/test/Example.cs')
        variants = [(str(self.source), 'exact-private-build-source'),
                    (str(checkout), 'exact-invocation-checkout-source'),
                    (relative, 'exact-git-relative-text-unbound'),
                    ('/_/test/Example.cs', 'other-underscore-virtual-root-unbound'),
                    ('/_1/test/Example.cs', 'other-underscore-virtual-root-unbound'),
                    ('/private/unknown/test/Example.cs', 'selected-relative-suffix-with-untrusted-prefix'),
                    ('/private/another/File.cs', 'other-path-unbound')]
        for path, category in variants:
            with self.subTest(category=category):
                row = copy.deepcopy(self.row); row['location']['file'] = path
                details = []
                with self.assertRaisesRegex(ValueError, '^Discovered source path mismatch$'):
                    r.discovery(dict(schemaVersion=1, tests=[row]), self.scope, self.methods, self.source,
                                self.assembly, expected,
                                lambda actual: details.append(r.source_path_diagnostic(actual, self.source,
                                    expected, relative, checkout)))
                self.assertEqual(len(details), 1)
                self.assertEqual(details[0]['actualPathClass'], category)
                self.assertEqual(details[0]['expectedReportedPath'], str(expected))
                self.assertNotIn('/private/', json.dumps(details))
                self.assertNotIn(self.temp.name, json.dumps(details))
        row = copy.deepcopy(self.row); row['location']['file'] = str(expected)
        callbacks = []
        self.assertEqual(set(r.discovery(dict(schemaVersion=1, tests=[row]), self.scope, self.methods,
                                        self.source, self.assembly, expected, callbacks.append)), {'actual-uid'})
        self.assertEqual(callbacks, [])

    def test_path_diagnostic_failure_keeps_original_rejection(self):
        marker = RuntimeError('diagnostic failed')
        def broken(_): raise marker
        with self.assertRaises(BaseExceptionGroup) as caught:
            r.discovery(dict(schemaVersion=1, tests=[self.row]), self.scope, self.methods,
                        self.source, self.assembly, Path('/_/unexpected'), broken)
        self.assertEqual(len(caught.exception.exceptions), 2)
        self.assertIsInstance(caught.exception.exceptions[0], ValueError)
        self.assertEqual(str(caught.exception.exceptions[0]), 'Discovered source path mismatch')
        self.assertIs(caught.exception.exceptions[1], marker)

    def test_path_diagnostic_io_failure_console_keeps_private_filename_private(self):
        # Load the actual renderer only; module import never invokes SDK/main.
        runner_spec = importlib.util.spec_from_file_location('retirement_runner',
            Path(__file__).resolve().parents[1] / 'run-retirement-controls.py')
        runner = importlib.util.module_from_spec(runner_spec); runner_spec.loader.exec_module(runner)
        self.source.unlink()
        expected = Path('/_/retirement-unit/test/Example.cs')
        with self.assertRaises(runner.results.SourcePathDiagnosticFailure) as caught:
            runner.results.discovery(dict(schemaVersion=1, tests=[self.row]), self.scope, self.methods,
                        self.source, self.assembly, expected,
                        lambda row: runner.results.source_path_diagnostic(row, self.source, expected,
                            'test/Example.cs', Path('/private/checkout/test/Example.cs')))
        rejection, diagnostic = caught.exception.exceptions
        self.assertEqual(str(rejection), 'Discovered source path mismatch')
        self.assertIsInstance(diagnostic, FileNotFoundError)
        self.assertEqual(diagnostic.filename, str(self.source))
        envelope = caught.exception
        output = io.StringIO()
        with contextlib.redirect_stdout(output): runner.summarize_failure(envelope)
        self.assertIn('SourcePathDiagnosticFailure', output.getvalue())
        self.assertNotIn(str(self.source), output.getvalue())
        self.assertNotIn(self.temp.name, output.getvalue())
        self.assertIs(envelope.exceptions[0], rejection)
        self.assertIs(envelope.exceptions[1], diagnostic)

    def cache_context(self):
        lane = Path(self.temp.name) / 'lane'; lane.mkdir()
        packages = lane / 'packages'; packages.mkdir()
        source = lane / 'src'; source.mkdir()
        return packages, r.directory_identity(str(packages)), source

    def check_folders(self, folders, restore, packages, identity, source):
        r.package_directories(folders, restore, packages, identity, source,
                              'src/Example/obj/project.assets.json')

    def test_package_directory_single_identity_accepts_separator_variants(self):
        packages, identity, source = self.cache_context()
        for text in (str(packages), str(packages) + '/', str(packages) + '//', str(packages) + '/.'):
            with self.subTest(spelling=text):
                self.check_folders({text: {}}, text, packages, identity, source)

    def test_package_directory_count_is_not_collapsed_by_canonicalization(self):
        packages, identity, source = self.cache_context()
        for folders, count in (({}, 0), ({str(packages): {}, str(packages) + '/': {}}, 2), ([], None)):
            with self.subTest(count=count):
                with self.assertRaises(ValueError) as caught:
                    self.check_folders(folders, str(packages), packages, identity, source)
                self.assertIn('"folderCount": ' + json.dumps(count), str(caught.exception))
                self.assertNotIn(self.temp.name, str(caught.exception))

    def test_package_directory_real_fallback_and_unknown_roots_fail_closed(self):
        packages, identity, source = self.cache_context()
        inside = packages.parent / 'other-cache'; inside.mkdir()
        outside = Path(self.temp.name) / 'private-external-cache'; outside.mkdir()
        for folder, category in ((inside, 'lane-root'), (outside, 'outside-known-roots'), (source, 'source-root')):
            with self.subTest(category=category):
                with self.assertRaises(ValueError) as caught:
                    self.check_folders({str(folder): {}}, str(packages), packages, identity, source)
                self.assertIn(category, str(caught.exception))
                self.assertIn('src/Example/obj/project.assets.json', str(caught.exception))
                self.assertNotIn(self.temp.name, str(caught.exception))
        with self.assertRaises(ValueError) as caught:
            self.check_folders({str(packages): {}, str(outside): {}}, str(packages), packages, identity, source)
        self.assertIn('"folderCount": 2', str(caught.exception))

    def test_package_directory_links_traversal_relative_missing_and_files_rejected(self):
        packages, identity, source = self.cache_context()
        direct_link = packages.parent / 'linked-cache'; direct_link.symlink_to(packages, target_is_directory=True)
        ancestor_link = Path(self.temp.name) / 'linked-lane'; ancestor_link.symlink_to(packages.parent, target_is_directory=True)
        child = packages / 'nested'; child.mkdir()
        file = packages.parent / 'not-directory'; file.write_text('not a folder')
        cases = [(str(direct_link), 'symlink-directory'), (str(ancestor_link / 'packages'), 'symlink-directory'),
                 (str(packages / '..' / 'packages'), 'directory-traversal'), ('packages', 'nonabsolute-directory'),
                 (str(packages.parent / 'missing'), 'unavailable-directory-FileNotFoundError'),
                 (str(file), 'nondirectory-component'), (str(child), 'selected-cache-descendant')]
        for folder, reason in cases:
            with self.subTest(reason=reason):
                with self.assertRaises(ValueError) as caught:
                    self.check_folders({folder: {}}, str(packages), packages, identity, source)
                self.assertIn(reason, str(caught.exception))
                self.assertNotIn(self.temp.name, str(caught.exception))

    def test_package_restore_path_and_pre_restore_inode_both_required(self):
        packages, identity, source = self.cache_context()
        other = packages.parent / 'other'; other.mkdir()
        with self.assertRaises(ValueError) as caught:
            self.check_folders({str(packages): {}}, str(other), packages, identity, source)
        self.assertIn('"restorePathClass": "lane-root"', str(caught.exception))
        packages.rename(packages.parent / 'original-cache')
        packages.mkdir()
        with self.assertRaises(ValueError) as caught:
            self.check_folders({str(packages): {}}, str(packages), packages, identity, source)
        self.assertIn('selected-cache-replaced', str(caught.exception))
        self.assertNotIn(self.temp.name, str(caught.exception))

    def test_json_duplicate_and_nonfinite_rejected(self):
        path = Path(self.temp.name) / 'data.json'
        for data in ('{"a": 1, "a": 2}', '{"a": NaN}', '{"a": Infinity}'):
            path.write_text(data)
            with self.assertRaises(ValueError): r.read_json(path)


if __name__ == '__main__':
    unittest.main()
