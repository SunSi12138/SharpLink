"""Pure Python synthetic controls only; NEVER execute CodeQL, CLR or a build."""
import copy
import json
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

import compilation_evidence as ce


class CompilationEvidenceTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name).resolve()
        self.source = self.root/'source'; self.source.mkdir()
        self.tables = {name: [] for name in ce.SCHEMA}
        self.build = {'projects': []}
        self.archive = {'csharpFiles': {}, 'externalSourcePaths': {}}
        self.add_project('First', '1')

    def file(self, path, data=b'fixed synthetic bytes'):
        path.parent.mkdir(parents=True, exist_ok=True); path.write_bytes(data)
        return path

    def add_project(self, name, entity, assembly='SameName'):
        project = 'test/'+name+'/'+name+'.csproj'
        directory = (self.source/project).parent
        self.file(self.source/project, b'<Project/>')
        binfile = self.file(directory/'bin/Release/net10.0'/(assembly+'.dll'))
        objfile = self.file(directory/'obj/Release/net10.0'/(assembly+'.dll'))
        sourcefile = self.file(directory/'A.cs', b'class A {}')
        reference = self.file(self.root/'sdk/ref/System.Runtime.dll', b'synthetic reference')
        self.build['projects'].append({'project': project, 'scope': 'direct',
            'observedProjectConfiguration': 'Release', 'targetFramework': 'net10.0', 'assemblyName': assembly,
            'dll': {'path': str(binfile.relative_to(self.source)), 'sha256': ce.sha(binfile)}})
        self.tables['contexts'].append([entity])
        self.tables['directories'].append([entity, str(directory)])
        self.tables['outputs'].append([entity, str(objfile)])
        self.tables['inputs'].append([entity, 0, str(sourcefile)])
        self.tables['references'].append([entity, 0, str(reference)])
        self.tables['finished'].append([entity, 1.25, 2.5])
        self.archive['csharpFiles'][str(sourcefile.relative_to(self.source))] = {
            'workspaceSha256': ce.sha(sourcefile), 'archiveSha256': ce.sha(sourcefile), 'kind': 'tracked'}

    def result(self):
        return ce.reconcile(self.source, self.tables, self.build, self.archive)

    def good(self):
        result = self.result()
        self.assertEqual(result['problems'], [])
        self.assertEqual(result['status'], 'STRUCTURAL_COMPILATION_COVERAGE_OBSERVED_NOT_SEMANTIC_COMPLETENESS')
        return result

    def bad(self, contains):
        result = self.result()
        self.assertEqual(result['status'], 'INCOMPLETE')
        self.assertTrue(any(contains in p for p in result['problems']), result['problems'])
        return result

    def encoded(self):
        doc = {}
        for name, schema in ce.SCHEMA.items():
            rows = []
            for row in self.tables[name]:
                rows.append([{'id': int(value)} if kind == 'Entity' else value for value, (_, kind) in zip(row, schema)])
            doc[name] = {'columns': [{'name': n, 'kind': k} for n, k in schema], 'tuples': rows}
        return doc

    def test_full_path_actual_bytes_positive(self):
        result = self.good()
        self.assertEqual(result['contexts'][0]['outputBinding']['relation'], 'actual-obj-and-bin-byte-sha256-equality')
        self.assertFalse(result['finishedMeansZeroErrors'])
        self.assertFalse(result['opaqueEntityIdsArePids'])
        self.assertEqual(result['contexts'][0]['inputsEvidence'][0]['provenanceKind'], 'tracked')

    def test_exact_bin_output_positive(self):
        self.tables['outputs'][0][1] = str(self.source/self.build['projects'][0]['dll']['path'])
        self.assertEqual(self.good()['contexts'][0]['outputBinding']['relation'], 'same-full-path')

    def test_same_basename_two_projects_map_separately(self):
        self.add_project('Second', '2')
        result = self.good()
        self.assertEqual([c['project'] for c in result['contexts']], ['test/First/First.csproj', 'test/Second/Second.csproj'])

    def test_same_basename_wrong_cwd_does_not_match(self):
        self.add_project('Second', '2')
        self.tables['outputs'][0][1] = self.tables['outputs'][1][1]
        self.bad('Unmapped/ambiguous full-path')

    def test_obj_bin_different_actual_bytes_fail(self):
        Path(self.tables['outputs'][0][1]).write_bytes(b'changed')
        self.bad('compiler obj/bin output bytes differ')

    def test_bin_changed_after_snapshot_fails(self):
        (self.source/self.build['projects'][0]['dll']['path']).write_bytes(b'changed')
        self.bad('bin DLL differs from build snapshot')

    def test_missing_context_preserves_project_gap(self):
        for name in ('contexts', 'directories', 'outputs', 'inputs', 'references', 'finished'):
            self.tables[name] = []
        result = self.bad('No structural context')
        self.assertEqual(len(result['projectMappings']), 1)

    def test_context_missing_relations_never_filtered(self):
        for name in ('directories', 'outputs', 'inputs', 'finished'):
            with self.subTest(name=name):
                old = self.tables[name]; self.tables[name] = []
                result = self.bad('Context 1')
                self.assertEqual(len(result['contexts']), 1)
                self.assertEqual(result['contexts'][0]['id'], '1')
                self.tables[name] = old

    def test_extra_unknown_context_preserved_and_blocked(self):
        self.tables['contexts'].append(['2'])
        result = self.bad('Context 2')
        self.assertEqual(result['extraContextIds'], ['2'])

    def test_more_contexts_than_projects_not_rejected_by_count(self):
        for name in ('contexts', 'directories', 'outputs', 'inputs', 'references', 'finished'):
            extra = copy.deepcopy(self.tables[name][0]); extra[0] = '2'; self.tables[name].append(extra)
        result = self.good()
        self.assertEqual(result['expectedProjectCount'], 1)
        self.assertEqual(result['actualContextCount'], 2)
        self.assertEqual(result['projectMappings'][0]['contextIds'], ['1', '2'])
        self.assertEqual(result['extraContextIds'], ['2'])

    def test_duplicate_context_rejected(self):
        self.tables['contexts'].append(['1'])
        self.bad('Duplicate context ID')

    def test_two_outputs_for_context_blocked(self):
        self.tables['outputs'].append(['1', '/unknown.dll'])
        self.bad('Missing/ambiguous cwd, output or finished')

    def test_duplicate_input_index_rejected(self):
        self.tables['inputs'].append(self.tables['inputs'][0][:])
        self.bad('duplicate inputs index')

    def test_orphan_relationship_rejected(self):
        self.tables['outputs'].append(['999', '/unknown.dll'])
        self.bad('Orphan outputs relationship')

    def test_unknown_source_preserved_blocked(self):
        value = str(self.file(self.root/'external/Unknown.cs'))
        self.tables['inputs'].append(['1', 1, value])
        result = self.bad('lacks verified native archive')
        self.assertEqual(result['contexts'][0]['inputsEvidence'][-1]['path'], value)

    def test_changed_source_rejected(self):
        Path(self.tables['inputs'][0][2]).write_bytes(b'changed')
        self.bad('input changed')

    def test_copied_content_not_accepted_as_compile_proof(self):
        next(iter(self.archive['csharpFiles'].values()))['kind'] = 'copied-tracked-content'
        self.bad('lacks verified native archive')

    def test_symlink_input_rejected(self):
        original = Path(self.tables['inputs'][0][2]); target = self.file(self.root/'other.cs')
        original.unlink(); original.symlink_to(target)
        self.bad('symlinked path')

    def test_traversal_path_rejected(self):
        self.tables['inputs'][0][2] = str(self.source)+'/test/First/../First/A.cs'
        self.bad('Noncanonical')

    def test_missing_reference_rejected(self):
        Path(self.tables['references'][0][2]).unlink()
        self.bad('Missing regular file')

    def test_finished_does_not_replace_compiler_error(self):
        self.tables['diagnostics'] = [['9', 3, 'CS0001']]
        self.tables['diagnosticContexts'] = [['9', '1']]
        result = self.bad('Compiler error')
        self.assertEqual(len(result['contexts'][0]['finished']), 1)
        self.assertEqual(result['diagnostics'][0]['contextIds'], ['1'])

    def test_finished_does_not_replace_global_extractor_error(self):
        self.tables['extractorMessages'] = [['9', 5, 'C# extractor']]
        result = self.bad('Global extractor error')
        self.assertEqual(len(result['extractorMessages']), 1)

    def test_unknown_severity_rejected(self):
        self.tables['extractorMessages'] = [['9', 0, 'C# extractor']]
        self.bad('unknown severity')

    def test_warning_not_error(self):
        self.tables['diagnostics'] = [['9', 2, 'CS0001']]
        self.tables['diagnosticContexts'] = [['9', '1']]
        self.tables['extractorMessages'] = [['10', 4, 'C# extractor']]
        self.good()

    def test_diagnostic_multiple_known_contexts_preserved(self):
        self.add_project('Second', '2')
        self.tables['diagnostics'] = [['9', 2, 'CS0001']]
        self.tables['diagnosticContexts'] = [['9', '1'], ['9', '2']]
        self.assertEqual(self.good()['diagnostics'][0]['contextIds'], ['1', '2'])

    def test_global_compiler_diagnostic_retained_without_join(self):
        self.tables['diagnostics'] = [['9', 2, 'CS0001']]
        result = self.bad('Missing compiler diagnostic context')
        self.assertEqual(len(result['diagnostics']), 1)

    def test_decode_round_trip(self):
        self.assertEqual(ce.decode_tables(self.encoded()), self.tables)

    def test_decode_missing_set_or_extra_set_rejected(self):
        for mutation in ('missing', 'extra'):
            doc = self.encoded()
            if mutation == 'missing': del doc['outputs']
            else: doc['unknown'] = {}
            with self.assertRaisesRegex(ValueError, 'result sets'): ce.decode_tables(doc)

    def test_decode_duplicate_tuple_rejected(self):
        doc = self.encoded(); doc['contexts']['tuples'] *= 2
        with self.assertRaisesRegex(ValueError, 'Duplicate'): ce.decode_tables(doc)

    def test_decode_entity_label_cannot_be_key(self):
        doc = self.encoded(); doc['contexts']['tuples'][0][0] = {'label': 'compilation'}
        with self.assertRaisesRegex(ValueError, 'opaque entity'): ce.decode_tables(doc)

    def test_decode_pagination_rejected(self):
        doc = self.encoded(); doc['inputs']['next'] = 10
        with self.assertRaisesRegex(ValueError, 'paginated'): ce.decode_tables(doc)

    def test_decode_schema_mismatch_rejected(self):
        doc = self.encoded(); doc['inputs']['columns'][1]['kind'] = 'String'
        with self.assertRaisesRegex(ValueError, 'column schema'): ce.decode_tables(doc)

    def test_decode_nan_timing_rejected(self):
        doc = self.encoded(); doc['finished']['tuples'][0][1] = float('nan')
        with self.assertRaisesRegex(ValueError, 'timing'): ce.decode_tables(doc)

    def test_decode_boolean_integer_rejected(self):
        doc = self.encoded(); doc['inputs']['tuples'][0][1] = True
        with self.assertRaisesRegex(ValueError, 'Noninteger'): ce.decode_tables(doc)

    def test_library_fallback_rejected(self):
        fallback = self.root/'cache/csharp-all'; fallback.mkdir(parents=True)
        with self.assertRaisesRegex(ValueError, 'outside frozen bundle'):
            ce.library_snapshot(self.root/'bundle', [str(fallback)], self.root/'query')

    def test_no_local_codeql_execution_even_with_valid_executable(self):
        out = self.root/'out'; out.mkdir()
        with patch.dict(os.environ, {'GITHUB_ACTIONS': 'false'}, clear=True), patch.object(ce.process_host, 'execute') as execute:
            result = ce.run_query(self.root/'db', Path('/bin/true'), out)
            execute.assert_not_called()
        self.assertEqual(result['status'], 'INCOMPLETE')
        self.assertTrue((out/'compilation-query.json').is_file())
        self.assertIn('hosted CI', result['problems'][0])

    def mocked_hosted_run(self, fail_phase=None, unsafe=False):
        out = self.root/'out'; out.mkdir()
        codeql = self.file(self.root/'bundle/codeql', b'not executable, mock only')
        lib = codeql.parent/'qlpacks/codeql/csharp-queries/1.10.2/.codeql/libraries'
        manifest = self.file(lib/'codeql/csharp-all/7.4.1/qlpack.yml', b'name: codeql/csharp-all\nversion: 7.4.1\n')
        schema = self.file(manifest.parent/'semmlecode.csharp.dbscheme', b'synthetic schema')
        ce.write(out/'toolchain.json', {'codeqlPath': str(codeql), 'codeqlBinarySha256': ce.sha(codeql),
            'bundleSha256': ce.BUNDLE_SHA, 'actualVersion': {'version': '2.27.3'}})
        calls = []

        def execute(argv, cwd, timeout, receipt, capture=False):
            phase = receipt.stem.removeprefix('process-compilation-'); calls.append(phase)
            self.assertGreater(timeout, 0); self.assertLessEqual(timeout, 300)
            self.assertFalse(cwd.is_relative_to(self.root/'db'))
            result = {'completed': True, 'processGroupQuiescent': not (unsafe and phase == fail_phase),
                      'exitCode': 1 if phase == fail_phase else 0, 'argv': argv, 'cwd': str(cwd)}
            if phase == 'resolve': result['capturedOutput'] = json.dumps({'libraryPath': [str(manifest.parent)], 'dbscheme': str(schema)})
            if phase == 'schema': result['capturedOutput'] = str(schema)
            if phase == 'query':
                target = Path(next(a.split('=', 1)[1] for a in argv if a.startswith('--output=')))
                target.write_bytes(b'synthetic BQRS, never evaluated')
            if phase == 'decode':
                target = Path(next(a.split('=', 1)[1] for a in argv if a.startswith('--output=')))
                ce.write(target, self.encoded())
            ce.write(receipt, result)
            return result

        environment = {'GITHUB_ACTIONS': 'true', 'RUNNER_ENVIRONMENT': 'github-hosted',
                       'RUNNER_TEMP': str(self.root), 'GITHUB_OUTPUT': str(self.root/'github-output')}
        with patch.dict(os.environ, environment, clear=True), patch.object(ce.process_host, 'execute', side_effect=execute), \
             patch.object(ce, 'library_snapshot', side_effect=lambda *a: {'synthetic': 'mock-only'}):
            result = ce.run_query(self.root/'db', codeql, out)
        return result, calls, out

    def test_mocked_owned_pipeline_no_real_codeql(self):
        result, calls, out = self.mocked_hosted_run()
        self.assertEqual(calls, ['resolve', 'schema', 'query', 'decode'])
        self.assertEqual(result['status'], 'QUERY_ROWS_OBSERVED_NOT_COVERAGE_COMPLETION')
        self.assertTrue(result['safeToContinue'])
        self.assertFalse(list(out.rglob('*.bqrs')))
        self.assertEqual((self.root/'github-output').read_text(), 'safe=true\n')

    def test_mocked_nonzero_query_stops_without_retry_keeps_partial(self):
        result, calls, out = self.mocked_hosted_run('query')
        self.assertEqual(calls, ['resolve', 'schema', 'query'])
        self.assertEqual(result['status'], 'INCOMPLETE')
        self.assertTrue(result['safeToContinue'])
        self.assertTrue((out/'compilation-query.json').is_file())
        self.assertFalse((out/'compilation-rows.json').exists())

    def test_mocked_unsafe_query_marks_safe_false_and_stops(self):
        result, calls, out = self.mocked_hosted_run('query', unsafe=True)
        self.assertEqual(calls, ['resolve', 'schema', 'query'])
        self.assertFalse(result['safeToContinue'])
        self.assertEqual((self.root/'github-output').read_text(), 'safe=false\n')
        self.assertEqual(result['status'], 'INCOMPLETE')

    def test_input_index_gap_fails(self):
        self.tables['inputs'][0][1] = 2
        self.bad('Noncontiguous inputs')

    def test_empty_references_fail(self):
        self.tables['references'] = []
        self.bad('Missing actual assembly reference')

    def test_diagnostic_known_location_preserved(self):
        self.tables['diagnostics'] = [['9', 2, 'CS0001']]
        self.tables['diagnosticContexts'] = [['9', '1']]
        self.tables['diagnosticLocations'] = [['9', self.tables['inputs'][0][2], 1, 1, 1, 2]]
        self.assertEqual(len(self.good()['diagnostics'][0]['locations']), 1)

    def test_diagnostic_unknown_location_not_dropped(self):
        self.tables['extractorMessages'] = [['9', 4, 'C# extractor']]
        self.tables['extractorLocations'] = [['9', '/unobserved/Unknown.cs', 1, 1, 1, 2]]
        result = self.bad('Diagnostic location not bound')
        self.assertEqual(result['extractorMessages'][0]['locations'][0][0], '/unobserved/Unknown.cs')

    def test_global_empty_location_preserved(self):
        self.tables['extractorMessages'] = [['9', 3, 'C# extractor']]
        self.tables['extractorLocations'] = [['9', '', 0, 0, 0, 0]]
        self.good()

    def test_final_failure_receipt_survives_missing_query(self):
        out = self.root/'out'; out.mkdir()
        with self.assertRaisesRegex(ValueError, 'incomplete'):
            ce.validate(self.source, out)
        self.assertEqual(json.loads((out/'compilation-evidence.json').read_text())['status'], 'INCOMPLETE')


if __name__ == '__main__':
    unittest.main()
