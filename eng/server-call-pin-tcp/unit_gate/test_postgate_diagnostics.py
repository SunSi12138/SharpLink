"""Pure controls for bounded postgate observation and unchanged gate semantics."""
import ast
import contextlib
import copy
import hashlib
import io
import json
from pathlib import Path
import unittest
from unittest.mock import patch
from unit_gate import run
from unit_gate.postgate_diagnostics import PostGateDiagnostics, emit_stage_failure, LAST_STAGE
from unit_gate.test_gate import report, children_report, UIDS
from unit_gate import test_gate as gate_tests
from unit_gate.validate import GateError, validate_run


class RemoveObservation(ast.NodeTransformer):
    """Remove ONLY the named added observers; every acceptance AST node remains."""
    def visit_ImportFrom(self, node):
        return None if node.module == 'unit_gate.postgate_diagnostics' else node

    def visit_Assign(self, node):
        if (len(node.targets) == 1 and isinstance(node.targets[0], ast.Name) and node.targets[0].id == 'postgate'
                and isinstance(node.value, ast.Call) and isinstance(node.value.func, ast.Name)
                and node.value.func.id == 'PostGateDiagnostics'):
            return None
        return self.generic_visit(node)

    def visit_Expr(self, node):
        if isinstance(node.value, ast.Call):
            function = node.value.func
            if (isinstance(function, ast.Attribute) and isinstance(function.value, ast.Name)
                    and function.value.id == 'postgate') or (isinstance(function, ast.Name) and function.id == 'emit_stage_failure'):
                return None
        return self.generic_visit(node)

    def visit_Call(self, node):
        if isinstance(node.func, ast.Name) and node.func.id == 'validate_run':
            node.keywords = [key for key in node.keywords if key.arg != 'on_stage']
        return self.generic_visit(node)


def capture(action):
    with contextlib.redirect_stdout(io.StringIO()) as output:
        action()
    text = output.getvalue()
    return text, [json.loads(line.split(' ', 1)[1]) for line in text.splitlines()]


class PostGateDiagnosticTests(unittest.TestCase):
    def test_acceptance_ast_equals_reviewed_a03_original(self):
        # sha256(ast.dump(ast.parse(original unit_gate/run.py), include_attributes=False))
        # Original commit a03ee20cd3b80bbe026e713666d5e50bd3b1535c.
        original_ast_sha256 = 'f7950cd7407d0e86a1262f8f594c536cff37d07ac623603f90225e6e7caa0b18'
        current = ast.parse(Path(run.__file__).read_text())
        stripped = RemoveObservation().visit(current)
        self.assertEqual(hashlib.sha256(ast.dump(stripped, include_attributes=False).encode()).hexdigest(), original_ast_sha256)

    def test_runtime_schema_generation_and_groups_are_bounded(self):
        value = report()
        value.update(runtimeVersion='RAW_SECRET https://user:pass@host/?sig=SECRET', tunitVersion='TOKEN=SECRET',
                     publicationGeneration='RAW_SECRET', assemblyName='RAW_SECRET')
        value['groups'][0]['summary']['passed'] = 1
        text, rows = capture(lambda: PostGateDiagnostics('baseline').report('ORDINARY_RUNTIME', value, 2, 0, False))
        self.assertNotIn('RAW_SECRET', text); self.assertNotIn('TOKEN=', text); self.assertNotIn('https:', text)
        values = {row['field']: row['actual'] for row in rows}
        self.assertEqual(values['runtime'], 'invalid'); self.assertFalse(values['generationValid'])
        self.assertEqual(values['groupInconsistentCounters'], 1); self.assertEqual(values['group.passed'], 1)

    def test_bool_counter_and_invalid_group_shapes_visible(self):
        value = report(); value['summary']['passed'] = True
        value['groups'] += [None, {'tests': [], 'summary': {'password': 'SECRET'}}]
        text, rows = capture(lambda: PostGateDiagnostics('candidate').report('ORDINARY_RUNTIME', value, 2, 0, False))
        self.assertNotIn('SECRET', text)
        values = {r['field']: r['actual'] for r in rows}
        self.assertIsNone(values['summary.passed']); self.assertEqual(values['groupInvalidShape'], 1)
        self.assertEqual(values['groupInvalidKeys'], 1)

    def test_validation_stages_do_not_change_pass_or_failure(self):
        for mutation in (None, 'schema', 'uid', 'status', 'counters', 'exit'):
            value = report(); code = 0
            if mutation == 'schema': value['schemaVersion'] = 2
            if mutation == 'uid': value['groups'][0]['tests'][0]['id'] = 'wrong'
            if mutation == 'status': value['groups'][0]['tests'][0]['status'] = 'failed'
            if mutation == 'counters': value['summary']['passed'] = 1
            if mutation == 'exit': code = 1
            results = []
            for observed in (False, True):
                with contextlib.redirect_stdout(io.StringIO()):
                    try:
                        d = PostGateDiagnostics('baseline')
                        results.append(('pass', validate_run(UIDS, value, code,
                            on_stage=(lambda stage: d.validation(stage, value, 2, code, False)) if observed else None)))
                    except GateError as error:
                        results.append(('fail', str(error)))
            self.assertEqual(results[0], results[1])

    def test_child_markers_fields_duplicate_and_no_output_leak(self):
        value = children_report()
        value['groups'][0]['tests'][0]['output'] += '\nRAW_SECRET token=SECRET'
        value['groups'][0]['tests'].append(copy.deepcopy(value['groups'][0]['tests'][0]))
        text, rows = capture(lambda: PostGateDiagnostics('baseline').children(value))
        self.assertNotIn('RAW_SECRET', text); self.assertNotIn('token=', text)
        self.assertEqual(next(r['actual'] for r in rows if r['field'] == 'markerCount'), 3)
        self.assertEqual(sorted(r['actual'] for r in rows if r['field'] == 'child.markerCount'), [1, 2])
        self.assertTrue(all(r['actual'] is True for r in rows if r['field'] == 'child.scenarioMatches'))

    def test_child_malformed_record_and_nonstring_output(self):
        value = children_report()
        value['groups'][0]['tests'][0]['output'] = 'READ_OWNERSHIP_ALLOCATION_VERIFIED|{"token":"SECRET","token":"bad"}'
        value['groups'][0]['tests'][1]['output'] = None
        text, rows = capture(lambda: PostGateDiagnostics('baseline').children(value))
        self.assertNotIn('SECRET', text)
        self.assertEqual(next(r['actual'] for r in rows if r['field'] == 'outputNonStringCount'), 1)
        self.assertFalse(next(r['actual'] for r in rows if r['field'] == 'child.recordObject'))

    def test_child_bool_executed_rejected_observationally(self):
        value = children_report()
        value['groups'][0]['tests'][0]['output'] = value['groups'][0]['tests'][0]['output'].replace('"executed": 1', '"executed": true')
        _, rows = capture(lambda: PostGateDiagnostics('baseline').children(value))
        self.assertIn(False, [r['actual'] for r in rows if r['field'] == 'child.executedIsInteger'])
        with self.assertRaises(GateError): run.allocation_children(value)

    def test_snapshot_counts_hashes_no_path_leak_and_bound(self):
        expected = {'secret-path-token=' + str(i): 'a' * 64 for i in range(12)}
        actual = dict(expected); actual.pop('secret-path-token=0'); actual['credential=RAW_SECRET'] = 'b' * 64
        for i in range(1, 12): actual['secret-path-token=' + str(i)] = 'c' * 64
        text, rows = capture(lambda: PostGateDiagnostics('candidate').snapshot('ORDINARY_SNAPSHOT', expected, actual))
        self.assertNotIn('secret-path', text); self.assertNotIn('RAW_SECRET', text)
        values = {r['field']: r['actual'] for r in rows}
        self.assertEqual((values['snapshot.added'], values['snapshot.removed'], values['snapshot.changed']), (1, 1, 11))
        hashes = [r for r in rows if r['field'] == 'snapshot.fileSHA256']
        self.assertEqual(len(hashes), 8); self.assertTrue(all(len(r['pathSHA256']) == 64 for r in hashes))
        self.assertEqual(values['snapshot.omittedDifferences'], 5)

    def test_dynamic_discovery_mapping_and_no_raw_ids(self):
        value = {'schemaVersion': 1, 'tests': [{'uid': 'RAW_SECRET'}, {'uid': 'RAW_SECRET'}, {'uid': None}]}
        text, rows = capture(lambda: PostGateDiagnostics('baseline').discovery(value, 'expected'))
        self.assertNotIn('RAW_SECRET', text)
        values = {r['field']: r['actual'] for r in rows}
        self.assertEqual(values['discoveryDuplicateUIDs'], 1); self.assertEqual(values['dynamicUIDCount'], 0)
        text, rows = capture(lambda: PostGateDiagnostics('baseline').mapping([{'location': {'file': 'https://user:pass@host/?sig=SECRET'}}]))
        self.assertNotIn('SECRET', text); self.assertFalse(rows[-1]['actual'])

    def test_diagnostic_error_is_contained_and_failure_type_is_allowlisted(self):
        text, rows = capture(lambda: PostGateDiagnostics('baseline').snapshot('ORDINARY_GRAPH', None, None))
        self.assertFalse(rows[-1]['actual'])
        text, rows = capture(lambda: emit_stage_failure('baseline', 'RAW_SECRET'))
        self.assertNotIn('RAW_SECRET', text); self.assertEqual(rows[-1]['errorType'], 'other')

    def test_read_failures_record_stage_before_argument_evaluation(self):
        # Reuse the full four-process synthetic orchestration (its execute is mocked).
        # No CLR, product, TCP or real process is executed. Inject at the first read
        # belonging to each stage, not after snapshot() has already been entered.
        cases = (
            ('ORDINARY_SNAPSHOT', 'CHILD_WITNESSES', 'inventory'),
            ('ORDINARY_GRAPH', 'ORDINARY_SNAPSHOT', 'correctness_graph_inventory'),
            ('DYNAMIC_SNAPSHOT', 'DYNAMIC_RESULT', 'inventory'),
            ('DYNAMIC_GRAPH', 'DYNAMIC_SNAPSHOT', 'correctness_graph_inventory'))
        for stage, previous, function in cases:
            LAST_STAGE.clear()
            calls = []
            original = getattr(run, function)
            def read(path):
                # Inject at the FIRST I/O after the preceding phase, including when
                # the missing stage-before-evaluation defect is reintroduced.
                current = LAST_STAGE.get('baseline')
                if current in (previous, stage):
                    calls.append(current)
                    raise OSError('RAW_PRIVATE_FILE_READ_ERROR')
                return original(path)
            with patch.object(run, function, side_effect=read):
                with self.subTest(stage=stage), self.assertRaises(OSError):
                    gate_tests.UnitGateTests('test_arm_orchestration_executes_full_corpus_and_one_dynamic_no_build').test_arm_orchestration_executes_full_corpus_and_one_dynamic_no_build()
            self.assertEqual(calls, [stage])
            self.assertEqual(LAST_STAGE.get('baseline'), stage)
            text, rows = capture(lambda: emit_stage_failure('baseline', 'OSError'))
            self.assertNotIn('RAW_PRIVATE', text)
            self.assertEqual(rows[0]['stage'], stage)

    def test_no_original_input_mutation(self):
        value = report(); original = copy.deepcopy(value)
        capture(lambda: PostGateDiagnostics('baseline').report('ORDINARY_RUNTIME', value, 2, 0, False))
        self.assertEqual(value, original)
        value = children_report(); original = copy.deepcopy(value)
        capture(lambda: PostGateDiagnostics('baseline').children(value))
        self.assertEqual(value, original)


if __name__ == '__main__': unittest.main()
