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

    def synthetic_observations(self):
        # Semantic unit tests inject explicit TEST-ONLY observed bytes. Admission
        # policy itself is exercised separately through collect_file_observations.
        result = {'files': {}}
        candidates = []
        for entity, value in self.tables['outputs']: candidates.append((entity, 'compiler-output', value))
        for kind in ('inputs', 'references'):
            for entity, _, value in self.tables[kind]: candidates.append((entity, kind, value))
        for entity, directory in self.tables['directories']:
            for project in self.build['projects']:
                if str((self.source/project['project']).parent) == directory:
                    candidates.append((entity, 'bin-output', str(self.source/project['dll']['path'])))
        for entity, role, value in candidates:
            path = Path(value)
            if not path.is_relative_to(self.root): continue
            try: ce.regular(path)
            except ValueError: continue
            row = result['files'].setdefault(value, {'sha256': ce.sha(path), 'proofs': []})
            row['proofs'].append({'contextId': entity, 'role': role})
        return result

    def result(self):
        return ce.reconcile(self.source, self.tables, self.build, self.archive, self.synthetic_observations())

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
        self.bad('No admitted file-byte observation')

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

    def mocked_hosted_run(self, fail_phase=None, unsafe=False, between=None):
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
                      'exitCode': 1 if phase == fail_phase else 0, 'argv': argv, 'cwd': str(cwd), 'timeoutSeconds': timeout}
            if phase == 'resolve': result['capturedOutput'] = json.dumps({'libraryPath': [str(cwd/'query'), str(manifest.parent)], 'dbscheme': str(schema),
                'qlPackName': 'sharplink/compilation-forensics', 'relativeName': 'sharplink/compilation-forensics/CompilationEvidence.ql', 'compilationCache': []})
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
            prior = ce.preflight(self.root/'db', codeql, out)
            if prior['status'] == 'QUERY_CHECKED_NOT_EVALUATED':
                if between: between(out)
                result = ce.run_query(self.root/'db', codeql, out)
            else: result = prior
        return result, calls, out

    def test_mocked_owned_pipeline_no_real_codeql(self):
        result, calls, out = self.mocked_hosted_run()
        self.assertEqual(calls, ['resolve', 'schema', 'compile', 'query', 'decode'])
        self.assertEqual(result['status'], 'QUERY_ROWS_OBSERVED_NOT_COVERAGE_COMPLETION')
        self.assertTrue(result['safeToContinue'])
        self.assertFalse(list(out.rglob('*.bqrs')))
        self.assertEqual((self.root/'github-output').read_text(), 'safe=true\nsafe=true\n')

    def test_mocked_nonzero_query_stops_without_retry_keeps_partial(self):
        result, calls, out = self.mocked_hosted_run('query')
        self.assertEqual(calls, ['resolve', 'schema', 'compile', 'query'])
        self.assertEqual(result['status'], 'INCOMPLETE')
        self.assertTrue(result['safeToContinue'])
        self.assertTrue((out/'compilation-query.json').is_file())
        self.assertFalse((out/'compilation-rows.json').exists())

    def test_mocked_unsafe_query_marks_safe_false_and_stops(self):
        result, calls, out = self.mocked_hosted_run('query', unsafe=True)
        self.assertEqual(calls, ['resolve', 'schema', 'compile', 'query'])
        self.assertFalse(result['safeToContinue'])
        self.assertEqual((self.root/'github-output').read_text(), 'safe=true\nsafe=false\n')
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

    def actual_resolution_fixture(self):
        path = ce.ROOT/'fixtures/CQL5-process-compilation-resolve.json'
        self.assertEqual(ce.sha(path), '5c33393f52b9e6b81ed34c294572e68219ce481b08afb6223acc444443e7d3ca')
        process = ce.load(path)
        self.assertTrue(ce.process_host.safe(process)); self.assertEqual(process['exitCode'], 0)
        # capturedOutput is the original process_host merged stdout/stderr, NOT a
        # reconstructed stdout-only sample or evidence from any subsequent run.
        text = process['capturedOutput']; doc = ce.strict_json(text)
        query_dir = Path(doc['libraryPath'][0]); library_root = Path(doc['dbscheme']).parents[3]
        return text, doc, library_root, query_dir

    def test_actual_cql5_merged_resolution_fixture_exact_contract(self):
        text, doc, library, query = self.actual_resolution_fixture()
        self.assertEqual(ce.parse_resolution(text, library, query), doc)
        self.assertEqual(doc['compilationCache'], [])
        old_allowed = {'libraryPath', 'dbscheme', 'relativeName', 'compilationCache'}
        self.assertEqual(set(doc) - old_allowed, {'qlPackName'})

    def test_resolution_unknown_or_missing_key_still_rejected(self):
        text, original, library, query = self.actual_resolution_fixture()
        for key in original:
            doc = copy.deepcopy(original); del doc[key]
            with self.subTest(key=key), self.assertRaisesRegex(ValueError, 'schema'):
                ce.parse_resolution(json.dumps(doc), library, query)
        doc = dict(original, unknownFutureField=[])
        with self.assertRaisesRegex(ValueError, 'schema'):
            ce.parse_resolution(json.dumps(doc), library, query)

    def test_resolution_pack_and_relative_identity_are_exact(self):
        text, original, library, query = self.actual_resolution_fixture()
        for key in ('qlPackName', 'relativeName'):
            for value in (None, 1, '', 'other/forensics'):
                doc = copy.deepcopy(original); doc[key] = value
                with self.subTest(key=key, value=value), self.assertRaisesRegex(ValueError, 'Unexpected resolved'):
                    ce.parse_resolution(json.dumps(doc), library, query)

    def test_resolution_cache_is_only_observed_empty_list(self):
        text, original, library, query = self.actual_resolution_fixture()
        for value in (None, '', {}, ['/unknown/cache'], 0, False):
            doc = copy.deepcopy(original); doc['compilationCache'] = value
            with self.subTest(value=value), self.assertRaisesRegex(ValueError, 'cache'):
                ce.parse_resolution(json.dumps(doc), library, query)

    def test_resolution_library_paths_and_schema_reject_unknowns(self):
        text, original, library, query = self.actual_resolution_fixture()
        for paths in ([], original['libraryPath'][1:], original['libraryPath']*2,
                      [str(query), '/unknown/fallback'], [str(query), 2]):
            doc = copy.deepcopy(original); doc['libraryPath'] = paths
            with self.subTest(paths=paths), self.assertRaises(ValueError):
                ce.parse_resolution(json.dumps(doc), library, query)
        doc = copy.deepcopy(original); doc['dbscheme'] = '/unknown/schema'
        with self.assertRaisesRegex(ValueError, 'schema'):
            ce.parse_resolution(json.dumps(doc), library, query)

    def test_resolution_merged_non_json_output_is_not_silently_stripped(self):
        text, doc, library, query = self.actual_resolution_fixture()
        with self.assertRaises(ValueError):
            ce.parse_resolution('warning: synthetic non-JSON prefix\n'+text, library, query)

    def test_mocked_preflight_compile_failure_stops_before_query(self):
        result, calls, out = self.mocked_hosted_run('compile')
        self.assertEqual(calls, ['resolve', 'schema', 'compile'])
        self.assertEqual(result['status'], 'INCOMPLETE')
        self.assertTrue(result['safeToContinue'])
        self.assertTrue((out/'compilation-preflight.json').is_file())
        self.assertFalse((out/'compilation-query.json').exists())

    def test_mocked_preflight_compile_unsafe_stops_before_query(self):
        result, calls, out = self.mocked_hosted_run('compile', unsafe=True)
        self.assertEqual(calls, ['resolve', 'schema', 'compile'])
        self.assertFalse(result['safeToContinue'])
        self.assertEqual((self.root/'github-output').read_text(), 'safe=false\n')

    def test_mocked_mutated_query_between_phases_blocks_execution(self):
        def mutate(out):
            runtime = ce.load(out/'compilation-preflight.json')['runtime']
            Path(runtime['queryPath']).write_text('changed query')
        result, calls, out = self.mocked_hosted_run(between=mutate)
        self.assertEqual(calls, ['resolve', 'schema', 'compile'])
        self.assertEqual(result['status'], 'INCOMPLETE')
        self.assertIn('differs from exact staged tool', result['problems'][0])

    def test_mocked_added_precompiled_plan_blocks_execution(self):
        def mutate(out):
            query = Path(ce.load(out/'compilation-preflight.json')['runtime']['queryPath'])
            query.with_suffix('.qlx').write_bytes(b'not a real compiled plan')
        result, calls, out = self.mocked_hosted_run(between=mutate)
        self.assertEqual(calls, ['resolve', 'schema', 'compile'])
        self.assertEqual(result['status'], 'INCOMPLETE')
        self.assertIn('cached plan', result['problems'][0])

    def test_mocked_mutated_preflight_process_receipt_blocks_execution(self):
        def mutate(out):
            path = out/'process-compilation-compile.json'
            path.write_text(path.read_text()+' ')
        result, calls, out = self.mocked_hosted_run(between=mutate)
        self.assertEqual(calls, ['resolve', 'schema', 'compile'])
        self.assertEqual(result['status'], 'INCOMPLETE')
        self.assertIn('receipt changed', result['problems'][0])

    def test_mocked_mutated_library_bytes_between_phases_blocks_execution(self):
        def mutate(out):
            schema = Path(ce.load(out/'compilation-library.json')['resolvedSchema'])
            schema.write_text('changed schema')
        result, calls, out = self.mocked_hosted_run(between=mutate)
        self.assertEqual(calls, ['resolve', 'schema', 'compile'])
        self.assertEqual(result['status'], 'INCOMPLETE')
        self.assertIn('library bytes changed', result['problems'][0])

    def test_mocked_exhausted_preflight_budget_cannot_reset(self):
        def mutate(out):
            path = out/'compilation-preflight.json'; doc = ce.load(path)
            doc['elapsedActiveSeconds'] = doc['elapsedWallSeconds'] = 300
            path.write_text(json.dumps(doc))
        result, calls, out = self.mocked_hosted_run(between=mutate)
        self.assertEqual(calls, ['resolve', 'schema', 'compile'])
        self.assertEqual(result['status'], 'INCOMPLETE')
        self.assertIn('preflight', result['problems'][0])

    def test_mocked_shared_budget_and_check_only_command(self):
        result, calls, out = self.mocked_hosted_run()
        preflight = ce.load(out/'compilation-preflight.json')
        compile_receipt = ce.load(out/'process-compilation-compile.json')
        self.assertIn('--check-only', compile_receipt['argv'])
        self.assertNotIn('--precompile', compile_receipt['argv'])
        self.assertFalse(any(arg.startswith('--database=') for arg in compile_receipt['argv']))
        query_receipt = ce.load(out/'process-compilation-query.json')
        self.assertLessEqual(query_receipt['timeoutSeconds'], 300-preflight['elapsedActiveSeconds'])
        self.assertEqual(result['elapsedActiveSeconds'], preflight['elapsedActiveSeconds']+result['elapsedWallSeconds'])
        self.assertEqual(result['preflightReceiptSha256'], ce.sha(out/'compilation-preflight.json'))

    def test_no_local_preflight_execution(self):
        out = self.root/'out'; out.mkdir()
        with patch.dict(os.environ, {'GITHUB_ACTIONS': 'false'}, clear=True), patch.object(ce.process_host, 'execute') as execute:
            result = ce.preflight(self.root/'db', Path('/bin/true'), out)
            execute.assert_not_called()
        self.assertEqual(result['status'], 'INCOMPLETE')
        self.assertIn('hosted CI', result['problems'][0])

    def file_proof_setup(self):
        self.sdk = self.root/'dotnet/sdk/10.0.114'
        self.sdk_ref = self.file(self.root/'dotnet/packs/Microsoft.NETCore.App.Ref/10.0.14/ref/net10.0/System.Runtime.dll', b'synthetic sdk reference')
        self.file(self.sdk/'Microsoft.NETCoreSdk.BundledVersions.props', b'<Project><ItemGroup><KnownFrameworkReference Include="Microsoft.NETCore.App" TargetFramework="net10.0" TargetingPackName="Microsoft.NETCore.App.Ref" TargetingPackVersion="10.0.14" /></ItemGroup></Project>')
        self.sdk_list = self.file(self.sdk_ref.parents[2]/'data/FrameworkList.xml', b'<FileList><File Type="Managed" AssemblyName="System.Runtime" Path="ref/net10.0/System.Runtime.dll" /></FileList>')
        self.package_dir = self.root/'nuget/example.package/1.2.3'
        self.package_ref = self.file(self.package_dir/'lib/net10.0/Example.dll', b'synthetic package reference')
        self.identity = {'sourceFiles': {str(Path(row[2]).relative_to(self.source)): ce.sha(Path(row[2])) for row in self.tables['inputs']}}
        self.package = {'externalPackageSources': {}}
        self.assets_docs = {}
        for project in self.build['projects']:
            directory = (self.source/project['project']).parent
            doc = {'project': {'restore': {'projectPath': str(self.source/project['project']), 'outputPath': str(directory/'obj')+'/', 'originalTargetFrameworks': ['net10.0']},
                               'frameworks': {'net10.0': {'frameworkReferences': {'Microsoft.NETCore.App': {'privateAssets': 'all'}}}}},
                   'packageFolders': {str(self.root/'nuget')+'/': {}},
                   'libraries': {'Example.Package/1.2.3': {'type': 'package', 'path': 'example.package/1.2.3', 'files': ['lib/net10.0/Example.dll']}},
                   'targets': {'net10.0': {'Example.Package/1.2.3': {'type': 'package', 'compile': {'lib/net10.0/Example.dll': {}}}}}}
            self.assets_docs[project['project']] = doc
            self.write_assets(project, doc)
        self.tables['references'] = [[entity, 0, str(self.sdk_ref)] for [entity] in self.tables['contexts']]
        self.build['workspaceCSharp'] = {}

    def write_assets(self, project, doc):
        path = self.file((self.source/project['project']).parent/'obj/project.assets.json', json.dumps(doc).encode())
        project['assets'] = {'path': str(path.relative_to(self.source)), 'sha256': ce.sha(path), 'frameworks': [project['targetFramework']]}

    def file_observations(self):
        return ce.collect_file_observations(self.source, {name: self.tables[name] for name in ('contexts', 'directories', 'outputs', 'inputs', 'references')},
                                            self.build, self.archive, self.identity, self.package, self.sdk)

    def assert_never_hashed(self, path):
        real_sha = ce.sha
        def guarded(candidate):
            self.assertNotEqual(str(candidate), str(path), 'unproven path was opened for hashing')
            return real_sha(candidate)
        with patch.object(ce, 'sha', side_effect=guarded):
            result = self.file_observations()
        self.assertNotIn(str(path), result['files'])
        self.assertTrue(result['unread'])
        self.assertEqual(result['status'], 'INCOMPLETE')
        return result

    def test_wrong_project_tfm_never_reads_bin_obj_or_reference(self):
        self.file_proof_setup(); project = self.build['projects'][0]
        directory = (self.source/project['project']).parent
        wrong = [self.file(directory/'bin/Release/netstandard2.0/SameName.dll'),
                 self.file(directory/'obj/Release/netstandard2.0/SameName.dll'),
                 self.file(directory/'obj/Release/netstandard2.0/ref/SameName.dll')]
        project['targetFramework'] = 'netstandard2.0'
        project['dll']['path'] = str(wrong[0].relative_to(self.source))
        self.tables['outputs'][0][1] = str(wrong[1]); self.tables['references'] = [['1', 0, str(wrong[2])]]
        original = ce.regular
        def guarded(path):
            self.assertNotIn(path, wrong, 'wrong-TFM file admitted before proof')
            return original(path)
        with patch.object(ce, 'regular', side_effect=guarded): result = self.file_observations()
        self.assertEqual(result['status'], 'INCOMPLETE')
        self.assertTrue(all(str(path) not in result['files'] for path in wrong))
        self.assertTrue(result['unread'])

    def test_generator_framework_is_exact_not_either_supported_tfm(self):
        project = {'project': 'src/SharpLink.Generator/SharpLink.Generator.csproj',
                   'observedProjectConfiguration': 'Release', 'targetFramework': 'netstandard2.0',
                   'assemblyName': 'SharpLink.Generator', 'dll': {'path': 'src/SharpLink.Generator/bin/Release/netstandard2.0/SharpLink.Generator.dll'}}
        self.assertEqual(ce.project_paths(self.source, project)['bin'], self.source/project['dll']['path'])
        project['targetFramework'] = 'net10.0'; project['dll']['path'] = project['dll']['path'].replace('netstandard2.0','net10.0')
        with self.assertRaisesRegex(ValueError, 'output identity'): ce.project_paths(self.source, project)

    def test_assets_framework_cannot_disagree_even_if_old_snapshot_matches(self):
        self.file_proof_setup(); project = self.build['projects'][0]
        project['assets']['frameworks'] = ['netstandard2.0']; result = {'metadataHashes': {}, 'problems': []}
        import manual
        with patch.object(manual, 'project_assets', return_value=project['assets']), patch.object(ce, 'load') as read:
            documents = ce.project_asset_documents(self.source, [project], result)
        read.assert_not_called(); self.assertEqual(documents, {}); self.assertTrue(result['problems'])

    def test_signed_ids_complete_eleven_table_schema(self):
        for name in ('contexts', 'directories', 'outputs', 'inputs', 'references', 'finished'):
            self.tables[name][0][0] = '-1'
        path = self.tables['inputs'][0][2]
        self.tables['diagnostics'] = [['-9', 0, 'CS8933']]
        self.tables['diagnosticContexts'] = [['-9', '-1']]
        self.tables['diagnosticLocations'] = [['-9', path, 1, 1, 1, 2]]
        self.tables['extractorMessages'] = [['-10', 3, 'C# extractor']]
        self.tables['extractorLocations'] = [['-10', path, 1, 1, 1, 2]]
        self.assertEqual(ce.decode_tables(self.encoded()), self.tables)
        self.assertEqual(self.good()['diagnostics'][0]['id'], '-9')

    def test_signed_id_never_matches_positive_absolute_value(self):
        self.tables['diagnostics'] = [['-9', 0, 'CS8933']]
        self.tables['diagnosticContexts'] = [['9', '1']]
        self.bad('Orphan compiler diagnostic')

    def test_signed_id_error_severity_remains_blocking(self):
        self.tables['diagnostics'] = [['-9', 3, 'CS0001']]
        self.tables['diagnosticContexts'] = [['-9', '1']]
        self.bad('Compiler error')

    def test_signed_ids_reject_non_integer_and_boolean(self):
        for value in (True, False, -1.0, '-1', None):
            doc = self.encoded(); doc['contexts']['tuples'][0][0] = {'id': value}
            with self.subTest(value=value), self.assertRaisesRegex(ValueError, 'opaque entity'):
                ce.decode_tables(doc)

    def test_signed_duplicate_tuple_still_rejected(self):
        doc = self.encoded(); doc['contexts']['tuples'] = [[{'id': -1}], [{'id': -1}]]
        with self.assertRaisesRegex(ValueError, 'Duplicate'):
            ce.decode_tables(doc)

    def test_decoder_schema_failure_keeps_raw_rows_hash_after_safe_decode(self):
        self.tables['diagnostics'] = [['-9', 'not-an-integer', 'CS0001']]
        result, calls, out = self.mocked_hosted_run()
        self.assertEqual(calls, ['resolve', 'schema', 'compile', 'query', 'decode'])
        self.assertEqual(result['status'], 'INCOMPLETE')
        self.assertEqual(result['decodedSha256'], ce.sha(out/'compilation-rows.json'))
        self.assertTrue(result['safeToContinue'])
        self.assertEqual(ce.load(out/'process-compilation-decode.json')['exitCode'], 0)

    def test_file_hashes_survive_unrelated_diagnostic_schema_failure(self):
        self.file_proof_setup()
        doc = self.encoded(); doc['diagnostics']['columns'] = []
        tables = ce.decode_tables(doc, paths_only=True)
        result = ce.collect_file_observations(self.source, tables, self.build, self.archive, self.identity, self.package, self.sdk)
        self.assertEqual(result['problems'], [])
        self.assertIn(self.tables['outputs'][0][1], result['files'])
        with self.assertRaisesRegex(ValueError, 'column schema'):
            ce.decode_tables(doc)

    def test_missing_late_receipt_hash_preserves_admitted_file_observations(self):
        self.file_proof_setup(); out = self.root/'out'; out.mkdir()
        docs = {'manual-build.json': self.build, 'manual-archive.json': self.archive,
                'source-identity.json': self.identity, 'package-evidence.json': self.package}
        inputs = (self.tables, docs, self.sdk, None, [])
        with patch.object(ce, 'file_observation_inputs', return_value=inputs):
            result = ce.make_file_observations(self.source, out)
        self.assertEqual(result['status'], 'INCOMPLETE')
        self.assertIn(str(self.sdk_ref), result['files'])
        self.assertEqual(result['missingInputs'], list(ce.FILE_INPUTS))

    def reference_preflight_setup(self):
        import package_evidence
        self.file_proof_setup()
        self.file(self.sdk.parent.parent/'dotnet', b'not executable; synthetic identity only')
        for name in package_evidence.SDK_ASSEMBLIES:
            self.file(self.sdk/(name+'.dll'), b'synthetic identity only')
        self.reference_out = self.root/'reference-out'; self.reference_out.mkdir()
        self.helper_dir = self.root/'helper'; self.helper_dir.mkdir()
        self.helper = {'status': 'HELPER_BUILT', 'sdk': package_evidence.sdk_identity(self.sdk),
                       'process': {'completed': True, 'processGroupQuiescent': True, 'exitCode': 0}}
        ce.write(self.helper_dir/'helper-build.json', self.helper)
        ce.write(self.reference_out/'helper-build.json', self.helper)
        self.addCleanup(patch.stopall)
        patch.dict(os.environ, {'DOTNET_ROOT': str(self.sdk.parent.parent)}).start()

    def test_reference_preflight_positive_pure_files_and_final_recheck(self):
        self.reference_preflight_setup()
        with patch.object(ce.process_host, 'execute') as execute:
            result = ce.reference_preflight(self.helper_dir, self.reference_out)
            self.assertEqual(ce.verify_reference_preflight(self.reference_out), self.sdk)
            execute.assert_not_called()
        self.assertEqual(result['status'], 'SDK_REFERENCE_METADATA_OBSERVED_NOT_COMPILATION_COMPLETION')
        self.assertEqual(set(result['members']), {str(self.sdk_ref)})
        self.assertEqual(len(result['metadataHashes']), 2)
        self.assertNotIn('xml', result)

    def test_reference_preflight_unknown_xml_preserves_metadata_hash(self):
        self.reference_preflight_setup()
        props = self.sdk/'Microsoft.NETCoreSdk.BundledVersions.props'; props.write_text('<bad')
        result = ce.reference_preflight(self.helper_dir, self.reference_out)
        self.assertEqual(result['status'], 'INCOMPLETE')
        self.assertEqual(result['metadataHashes'][str(props)], ce.sha(props))
        self.assertEqual(ce.load(self.reference_out/'sdk-reference-preflight.json'), result)

    def test_reference_preflight_unknown_manifest_preserves_both_hashes(self):
        self.reference_preflight_setup(); self.sdk_list.write_text('<bad')
        result = ce.reference_preflight(self.helper_dir, self.reference_out)
        self.assertEqual(result['status'], 'INCOMPLETE')
        self.assertEqual(len(result['metadataHashes']), 2)
        self.assertEqual(result['metadataHashes'][str(self.sdk_list)], ce.sha(self.sdk_list))

    def test_reference_preflight_missing_exact_member_fails(self):
        self.reference_preflight_setup(); self.sdk_ref.unlink()
        result = ce.reference_preflight(self.helper_dir, self.reference_out)
        self.assertEqual(result['status'], 'INCOMPLETE')
        self.assertIn(str(self.sdk_ref), result['members'])

    def test_reference_preflight_changed_sdk_identity_fails_before_metadata(self):
        self.reference_preflight_setup(); (self.sdk/'NuGet.Common.dll').write_bytes(b'changed')
        result = ce.reference_preflight(self.helper_dir, self.reference_out)
        self.assertEqual(result['status'], 'INCOMPLETE')
        self.assertEqual(result['metadataHashes'], {})

    def test_reference_preflight_unsafe_helper_fails(self):
        self.reference_preflight_setup(); self.helper['process']['processGroupQuiescent'] = False
        (self.helper_dir/'helper-build.json').write_text(json.dumps(self.helper))
        result = ce.reference_preflight(self.helper_dir, self.reference_out)
        self.assertEqual(result['status'], 'INCOMPLETE')
        self.assertEqual(result['metadataHashes'], {})

    def test_reference_preflight_final_recheck_rejects_changed_metadata(self):
        self.reference_preflight_setup(); ce.reference_preflight(self.helper_dir, self.reference_out)
        self.sdk_list.write_text(self.sdk_list.read_text()+'\n')
        with self.assertRaisesRegex(ValueError, 'metadataHashes'):
            ce.verify_reference_preflight(self.reference_out)

    def test_reference_preflight_final_recheck_rejects_changed_helper_receipt(self):
        self.reference_preflight_setup(); ce.reference_preflight(self.helper_dir, self.reference_out)
        self.helper['extra'] = 'changed'; (self.reference_out/'helper-build.json').write_text(json.dumps(self.helper))
        with self.assertRaisesRegex(ValueError, 'helperBuildSha256'):
            ce.verify_reference_preflight(self.reference_out)

    def test_file_hashes_observe_exact_sdk_metadata_member(self):
        self.file_proof_setup(); result = self.file_observations()
        self.assertEqual(result['problems'], [])
        self.assertEqual(result['files'][str(self.sdk_ref)]['proofs'][0]['kind'], 'sdk-framework-reference')
        self.assertIn(str(self.sdk/'Microsoft.NETCoreSdk.BundledVersions.props'), result['metadataHashes'])
        self.assertIn(str(self.sdk_list), result['metadataHashes'])
        self.assertNotIn('finished', result)  # This phase does not reinterpret semantic success.

    def test_file_hashes_exact_nuget_member_and_no_compile_requirement(self):
        self.file_proof_setup(); self.tables['references'][0][2] = str(self.package_ref)
        project = self.build['projects'][0]; doc = self.assets_docs[project['project']]
        doc['targets']['net10.0']['Example.Package/1.2.3']['compile'] = {}
        self.write_assets(project, doc)
        result = self.file_observations(); self.assertEqual(result['problems'], [])
        proof = result['files'][str(self.package_ref)]['proofs'][0]
        self.assertEqual(proof['member'], 'lib/net10.0/Example.dll')
        self.assertFalse(proof['selectedCompileMember'])

    def test_netstandard_long_target_name_is_used(self):
        self.file_proof_setup(); project = dict(self.build['projects'][0], targetFramework='netstandard2.0')
        assets = copy.deepcopy(self.assets_docs[self.build['projects'][0]['project']])
        assets['targets']['.NETStandard,Version=v2.0'] = assets['targets'].pop('net10.0')
        proof = ce.nuget_member_proof(self.package_ref, assets, project)
        self.assertEqual(proof['assetsTarget'], '.NETStandard,Version=v2.0')
        assets['targets']['netstandard2.0'] = assets['targets'].pop('.NETStandard,Version=v2.0')
        with self.assertRaisesRegex(ValueError, 'target framework'):
            ce.nuget_member_proof(self.package_ref, assets, project)

    def test_unknown_reference_not_read_but_other_hashes_survive(self):
        self.file_proof_setup(); unknown = self.file(self.root/'private/Unknown.dll')
        self.tables['references'].append(['1', 1, str(unknown)])
        result = self.assert_never_hashed(unknown)
        self.assertIn(str(self.sdk_ref), result['files'])
        self.assertIn(self.tables['outputs'][0][1], result['files'])

    def test_nuget_root_alone_does_not_authorize_member(self):
        self.file_proof_setup(); unknown = self.file(self.package_dir/'lib/net10.0/Unlisted.dll')
        self.tables['references'][0][2] = str(unknown)
        self.assert_never_hashed(unknown)

    def test_nuget_member_for_another_project_not_authorized(self):
        self.add_project('Second', '2'); self.file_proof_setup()
        self.tables['references'] = [['1', 0, str(self.package_ref)], ['2', 0, str(self.package_ref)]]
        second = self.build['projects'][1]; doc = self.assets_docs[second['project']]
        doc['libraries'] = {}; doc['targets']['net10.0'] = {}; self.write_assets(second, doc)
        result = self.file_observations()
        self.assertIn(str(self.package_ref), result['files'])
        proofs = result['files'][str(self.package_ref)]['proofs']
        self.assertEqual({proof['contextId'] for proof in proofs}, {'1'})
        self.assertTrue(any(row['contextId'] == '2' and row['path'] == str(self.package_ref) for row in result['unread']))

    def test_nuget_library_path_traversal_not_read(self):
        self.file_proof_setup(); self.tables['references'][0][2] = str(self.package_ref)
        project = self.build['projects'][0]; doc = self.assets_docs[project['project']]
        doc['libraries']['Example.Package/1.2.3']['path'] = '../example.package/1.2.3'; self.write_assets(project, doc)
        self.assert_never_hashed(self.package_ref)

    def test_changed_assets_cannot_authorize_reference(self):
        self.file_proof_setup(); self.tables['references'][0][2] = str(self.package_ref)
        path = self.source/self.build['projects'][0]['assets']['path']; path.write_text(path.read_text()+' ')
        result = self.assert_never_hashed(self.package_ref)
        self.assertIn(self.tables['outputs'][0][1], result['files'])

    def test_assets_from_another_project_cannot_authorize_reference(self):
        self.file_proof_setup(); self.tables['references'][0][2] = str(self.package_ref)
        project = self.build['projects'][0]; doc = self.assets_docs[project['project']]
        doc['project']['restore']['projectPath'] = str(self.source/'test/Other/Other.csproj'); self.write_assets(project, doc)
        self.assert_never_hashed(self.package_ref)

    def test_sdk_wrong_version_framework_or_unlisted_member_not_read(self):
        self.file_proof_setup()
        paths = [self.root/'dotnet/packs/Microsoft.NETCore.App.Ref/10.0.99/ref/net10.0/System.Runtime.dll',
                 self.root/'dotnet/packs/Microsoft.NETCore.App.Ref/10.0.14/ref/net9.0/System.Runtime.dll',
                 self.root/'dotnet/packs/Microsoft.NETCore.App.Ref/10.0.14/ref/net10.0/Unknown.dll']
        for path in paths:
            self.file(path); self.tables['references'][0][2] = str(path); self.assert_never_hashed(path)

    def test_sdk_nonliteral_version_cannot_authorize_reference(self):
        self.file_proof_setup(); props = self.sdk/'Microsoft.NETCoreSdk.BundledVersions.props'
        props.write_text(props.read_text().replace('10.0.14', '$(UnknownVersion)'))
        self.assert_never_hashed(self.sdk_ref)

    def test_sdk_unselected_framework_cannot_authorize_reference(self):
        self.file_proof_setup(); project = self.build['projects'][0]; doc = self.assets_docs[project['project']]
        doc['project']['frameworks']['net10.0']['frameworkReferences'] = {}; self.write_assets(project, doc)
        self.assert_never_hashed(self.sdk_ref)

    def test_sdk_analyzer_or_explicitly_not_default_not_read(self):
        self.file_proof_setup()
        for attributes in ('Type="Analyzer"', 'Type="Native"', 'Type="Unknown"', 'Type="Managed" ReferencedByDefault="false"', 'Type="Managed" Profile="Other"'):
            self.sdk_list.write_text('<FileList><File '+attributes+' AssemblyName="System.Runtime" /></FileList>')
            self.assert_never_hashed(self.sdk_ref)

    def test_sdk_manifest_path_optional_but_if_present_must_match(self):
        self.file_proof_setup()
        self.sdk_list.write_text('<FileList><File Type="Managed" AssemblyName="System.Runtime" /></FileList>')
        self.assertEqual(self.file_observations()['problems'], [])
        self.sdk_list.write_text('<FileList><File Type="Managed" AssemblyName="System.Runtime" Path="ref/net10.0/Other.dll" /></FileList>')
        self.assert_never_hashed(self.sdk_ref)

    def test_source_reference_exact_bin_ref_refint_not_arbitrary_obj(self):
        self.file_proof_setup(); project = self.build['projects'][0]; paths = ce.project_paths(self.source, project)
        for role in ('bin', 'ref', 'refint'):
            self.file(paths[role]); self.tables['references'][0][2] = str(paths[role])
            result = self.file_observations()
            self.assertTrue(any(p['kind'] == 'project-'+role+'-reference' for p in result['files'][str(paths[role])]['proofs']))
        unknown = self.file(paths['directory']/'obj/Release/net10.0/Other.dll')
        self.tables['references'][0][2] = str(unknown); self.assert_never_hashed(unknown)

    def test_untracked_source_not_read(self):
        self.file_proof_setup(); unknown = self.file(self.source/'test/First/Unknown.cs')
        self.tables['inputs'].append(['1', 1, str(unknown)]); self.assert_never_hashed(unknown)

    def test_unmapped_context_does_not_read_input_even_if_file_exists(self):
        self.file_proof_setup(); self.tables['directories'][0][1] = str(self.root/'unowned')
        self.assert_never_hashed(Path(self.tables['inputs'][0][2]))

    def test_known_hash_mismatch_preserves_actual_observation(self):
        self.file_proof_setup(); binpath = self.source/self.build['projects'][0]['dll']['path']; binpath.write_bytes(b'changed bin')
        result = self.file_observations()
        self.assertEqual(result['status'], 'INCOMPLETE')
        self.assertEqual(result['files'][str(binpath)]['sha256'], ce.sha(binpath))
        self.assertIn(self.tables['outputs'][0][1], result['files'])

    def test_file_hashes_survive_semantic_error_without_weakening_it(self):
        self.file_proof_setup(); self.tables['diagnostics'] = [['-9', 3, 'CS0001']]; self.tables['diagnosticContexts'] = [['-9', '1']]
        observation = self.file_observations(); self.assertEqual(observation['problems'], [])
        result = ce.reconcile(self.source, self.tables, self.build, self.archive, observation)
        self.assertEqual(result['status'], 'INCOMPLETE')
        self.assertTrue(any('Compiler error' in p for p in result['problems']))

    def test_conditioned_sdk_metadata_not_evaluated(self):
        self.file_proof_setup(); props = self.sdk/'Microsoft.NETCoreSdk.BundledVersions.props'
        props.write_text(props.read_text().replace('<ItemGroup>', '<ItemGroup Condition="true">'))
        self.assert_never_hashed(self.sdk_ref)

    def test_failed_sdk_identity_still_preserves_owned_outputs_and_sources(self):
        self.file_proof_setup()
        result = ce.collect_file_observations(self.source, {k: self.tables[k] for k in ('contexts', 'directories', 'outputs', 'inputs', 'references')},
                                              self.build, self.archive, self.identity, self.package, None)
        self.assertEqual(result['status'], 'INCOMPLETE')
        self.assertIn(self.tables['outputs'][0][1], result['files'])
        self.assertIn(self.tables['inputs'][0][2], result['files'])
        self.assertNotIn(str(self.sdk_ref), result['files'])

    def test_disallowed_project_does_not_erase_other_project_hashes(self):
        self.add_project('Second', '2'); self.file_proof_setup()
        result = ce.collect_file_observations(self.source, {k: self.tables[k] for k in ('contexts', 'directories', 'outputs', 'inputs', 'references')},
                                              self.build, self.archive, self.identity, self.package, self.sdk, {'test/First/First.csproj'})
        self.assertIn(self.tables['outputs'][0][1], result['files'])
        self.assertNotIn(self.tables['outputs'][1][1], result['files'])
        self.assertTrue(any(x['contextId'] == '2' for x in result['unread']))

    def test_shared_reference_hashed_once_but_proofs_remain_per_context(self):
        self.add_project('Second', '2'); self.file_proof_setup(); real_sha = ce.sha; count = []
        def counted(path):
            if path == self.sdk_ref: count.append(path)
            return real_sha(path)
        with patch.object(ce, 'sha', side_effect=counted): result = self.file_observations()
        self.assertEqual(len(count), 1)
        self.assertEqual({p['contextId'] for p in result['files'][str(self.sdk_ref)]['proofs']}, {'1', '2'})

    def test_admitted_reference_symlink_not_read(self):
        self.file_proof_setup(); target = self.file(self.root/'private/Target.dll')
        self.sdk_ref.unlink(); self.sdk_ref.symlink_to(target)
        self.assert_never_hashed(target)

    def test_obj_mismatch_hash_retained_but_reconcile_blocks(self):
        self.file_proof_setup(); path = Path(self.tables['outputs'][0][1]); path.write_bytes(b'changed obj')
        observation = self.file_observations()
        self.assertEqual(observation['files'][str(path)]['sha256'], ce.sha(path))
        result = ce.reconcile(self.source, self.tables, self.build, self.archive, observation)
        self.assertEqual(result['status'], 'INCOMPLETE')
        self.assertTrue(any('obj/bin output bytes differ' in p for p in result['problems']))

    def test_observe_files_preserves_partial_receipt_before_raising(self):
        out = self.root/'out'; out.mkdir(); partial = {'schema': 1, 'status': 'INCOMPLETE', 'files': {'/synthetic/admitted.dll': {'sha256': 'a'*64}}, 'unread': [], 'problems': ['synthetic unknown path']}
        with patch.object(ce, 'make_file_observations', return_value=partial), self.assertRaisesRegex(ValueError, 'partial receipt'):
            ce.observe_files(self.source, out)
        self.assertEqual(ce.load(out/'compilation-file-hashes.json'), partial)

    def test_final_failure_receipt_survives_missing_query(self):
        out = self.root/'out'; out.mkdir()
        with self.assertRaisesRegex(ValueError, 'incomplete'):
            ce.validate(self.source, out)
        self.assertEqual(json.loads((out/'compilation-evidence.json').read_text())['status'], 'INCOMPLETE')


if __name__ == '__main__':
    unittest.main()
