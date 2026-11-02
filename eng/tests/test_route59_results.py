"""Synthetic Python protocol checks ONLY; never SharpLink/.NET runtime evidence."""
import copy
import contextlib
import importlib.util
import io
import json
import os
from pathlib import Path
import sys
import tempfile
import unittest
from unittest import mock

sys.dont_write_bytecode = True
ROOT = Path(__file__).resolve().parents[2]


def module(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    result = importlib.util.module_from_spec(spec); spec.loader.exec_module(result)
    return result


r = module('route59_results_test', ROOT / 'eng/route59-results.py')
runner = module('route59_runner_test', ROOT / 'eng/run-route59-controls.py')
accepted = module('accepted_retirement_tests', ROOT / 'eng/tests/test_retirement_results.py')
CATALOG = r.read_json(ROOT / 'eng/route59-controls.json')


class Route59Tests(unittest.TestCase):
    def setUp(self):
        self.base = accepted.ClassifierTests()
        self.base.setUp()
        self.addCleanup(self.base.doCleanups)
        self.addCleanup(self.close_private_capability)
        self.native = copy.deepcopy(self.base.native)
        self.row = copy.deepcopy(self.base.row)
        self.source = self.base.source
        self.xml = self.base.xml

    def close_private_capability(self):
        fd = getattr(runner.main, 'private_directory_fd', None)
        if fd is not None:
            os.close(fd)
            runner.main.private_directory_fd = None

    def verdict(self, native=None, row=None, xml=None, generations=()):
        return r.passed(self.native if native is None else native, self.xml if xml is None else xml,
                        self.row if row is None else row, self.source, '1.73.19.0', generations=generations)

    def cases_and_rows(self):
        cases = [dict(c, sourceSHA256=CATALOG['sourcePins'][c['source']]) for c in CATALOG['cases']]
        rows = []
        for i, c in enumerate(cases):
            rows.append(dict(uid='synthetic-uid-' + str(i), displayName=c['displayName'],
                type=dict(assemblyFullName=CATALOG['assemblyFullName'], namespace=c['namespace'], typeName=c['className'],
                          methodName=c['methodName'], methodArity=0, parameterTypeFullNames=c['parameterTypes'],
                          returnTypeFullName=c['returnType']),
                location=dict(file=str(r.reported_source_path(c['source'])), lineStart=c['span']['signatureLine'],
                              lineEnd=c['span']['endLine'])))
        return cases, rows

    def discover(self, cases, rows):
        return r.discovery(dict(schemaVersion=1, tests=rows), cases, ROOT, CATALOG['assemblyFullName'])

    def test_actual_catalog_source_spans_graph_budget_and_pins(self):
        r.verify_catalog(ROOT, CATALOG)
        self.assertEqual(len(CATALOG['cases']), 59)
        self.assertTrue(all(c['actualUID'] is None for c in CATALOG['cases']))

    def test_59_parameter_cases_biject_even_when_discovery_reordered(self):
        cases, rows = self.cases_and_rows()
        matches = []
        for klass in sorted({c['className'] for c in cases}):
            selected = [c for c in cases if c['className'] == klass]
            found = [row for row in rows if row['type']['typeName'] == klass]
            matches.extend(self.discover(selected, list(reversed(found))))
        ledger = r.freeze_ledger(list(reversed(matches)), CATALOG['cases'])
        self.assertEqual([e['discovery']['uid'] for e in ledger], [row['uid'] for row in rows])
        self.assertEqual(len({r.case_key(e['case'])[:3] for e in ledger}), 40)
        self.assertEqual(len({e['discovery']['uid'] for e in ledger}), 59)

    def test_discovery_rejects_missing_duplicate_extra_or_guessed_argument(self):
        cases, rows = self.cases_and_rows()
        variants = [rows[:-1], rows + [rows[0]], [rows[0]]*59]
        for changed in variants:
            with self.assertRaises(ValueError): self.discover(cases, changed)
        for field, value in (('uid', rows[1]['uid']), ('uid', ''), ('uid', '--evil'), ('uid', 'x\n'),
                             ('displayName', 'wrong argument')):
            changed = copy.deepcopy(rows); changed[0][field] = value
            with self.subTest(field=field, value=value):
                with self.assertRaises(ValueError): self.discover(cases, changed)
        for field, value in (('namespace','Other'), ('typeName','Other'), ('methodName','Other'),
                             ('assemblyFullName','Other'), ('methodArity',True), ('parameterTypeFullNames',['System.Int64']),
                             ('returnTypeFullName','System.Void')):
            changed = copy.deepcopy(rows); changed[0]['type'][field] = value
            with self.subTest(field=field):
                with self.assertRaises(ValueError): self.discover(cases, changed)

    def test_unknown_path_span_or_source_hash_never_gets_learned(self):
        cases, rows = self.cases_and_rows()
        for field, value in (('file','/tmp/other/'+cases[0]['source']), ('file','/_/retirement-unit/'+cases[0]['source']),
                             ('lineStart',0), ('lineStart',True), ('lineEnd',rows[0]['location']['lineEnd']+1)):
            changed = copy.deepcopy(rows); changed[0]['location'][field] = value
            with self.subTest(field=field):
                with self.assertRaises(ValueError): self.discover(cases, changed)
        changed = copy.deepcopy(cases); changed[0]['sourceSHA256'] = '0'*64
        with self.assertRaises(ValueError): self.discover(changed, rows)

    def test_global_duplicate_uid_across_classes_rejected(self):
        cases, rows = self.cases_and_rows(); matches = self.discover(cases, rows)
        matches[-1]['discovery']['uid'] = matches[0]['discovery']['uid']
        with self.assertRaises(ValueError): r.freeze_ledger(matches, CATALOG['cases'])
        with self.assertRaises(ValueError): r.freeze_ledger(matches[:-1], CATALOG['cases'])

    def test_filter_presence_type_or_value_is_diagnostic_only(self):
        self.assertEqual(self.verdict()['filterDiagnostic'], {'present':False})
        for value in ('wrong selector', 'SECRET\nraw', None, [], {'nested': True}, 12, False):
            native = copy.deepcopy(self.native); native['filter'] = value; before = copy.deepcopy(native)
            result = self.verdict(native)
            self.assertEqual(result['status'],'PASS')
            self.assertEqual(native,before)
            diagnostic = result['filterDiagnostic']
            self.assertTrue(diagnostic['present']); self.assertEqual(len(diagnostic['sha256']),64)
            self.assertNotIn('SECRET',json.dumps(diagnostic))

    def test_diagnostic_filter_cannot_rescue_any_strict_failure(self):
        for filter_value in (None, 'correct-looking-uid', {'raw':'unexpected'}):
            for mutation in ('uid','extra','skip','retry','exception','runtime','source','generation'):
                native = copy.deepcopy(self.native); native['filter'] = filter_value
                test = native['groups'][0]['tests'][0]
                if mutation == 'uid': test['id'] = 'wrong'
                elif mutation == 'extra': native['groups'][0]['tests'].append(copy.deepcopy(test))
                elif mutation == 'skip': test['status'] = 'skipped'
                elif mutation == 'retry': test['attempts'] = []
                elif mutation == 'exception': test['exception'] = None
                elif mutation == 'runtime': native['runtimeVersion'] = '.NET 9.0.0'
                elif mutation == 'source': test['filePath'] = '/unrelated'
                else: native['publicationGeneration'] = 'nothex'
                with self.subTest(filter_value=filter_value, mutation=mutation):
                    with self.assertRaises(ValueError): self.verdict(native)
            with self.assertRaises(ValueError): self.verdict(generations={'a'*32})

    def test_filter_projection_removes_only_one_top_level_key(self):
        native = copy.deepcopy(self.native); native['filter'] = 'not oracle'
        with mock.patch.object(r.common, 'passed', wraps=r.common.passed) as checked:
            self.verdict(native)
            projected = checked.call_args.args[0]
            self.assertEqual(set(projected),set(native)-{'filter'})
            for key in projected: self.assertIs(projected[key],native[key])

    def test_junit_independent_mismatch_extra_entity_fail(self):
        for value in (self.xml.replace('Chosen','Wrong'), self.xml.replace('tests="1"','tests="2"'),
                      self.xml.replace('/></testsuite>', '><skipped/></testcase></testsuite>'),
                      '<!DOCTYPE x>'+self.xml):
            with self.subTest(value=value[:35]):
                with self.assertRaises((ValueError, Exception)): self.verdict(xml=value)

    def receipt(self, argv):
        return dict(stage='case-0',argv=argv,timeoutSeconds=120,exitCode=0,timedOut=False,
                    errors=[],cleanupErrors=[],reportingErrors=[],remainingOwned={},elapsedSeconds=0.5)

    def test_exact_single_uid_argv_and_actual_receipt(self):
        argv = r.case_argv(Path('/dotnet'),Path('/unit.dll'),'actual opaque uid',Path('/r'), '90s')
        self.assertEqual(argv.count('--filter-uid'),1)
        self.assertEqual(argv[argv.index('--filter-uid')+1],'actual opaque uid')
        self.assertNotIn('--treenode-filter',argv)
        r.verify_receipt(self.receipt(argv),'case-0',argv,120)
        for key,value in (('argv',argv+['extra']),('stage','wrong'),('exitCode',True),('exitCode',1),
                          ('timedOut',True),('timedOut',None),('errors',['x']),('cleanupErrors',['x']),
                          ('reportingErrors',['x']),('remainingOwned',[]),('remainingOwned',{'123':[1]}),
                          ('timeoutSeconds',119),('elapsedSeconds',float('nan'))):
            receipt = self.receipt(argv); receipt[key] = value
            with self.subTest(key=key):
                with self.assertRaises(ValueError): r.verify_receipt(receipt,'case-0',argv,120)

    def test_fresh_report_regular_identity_size_and_symlink_guards(self):
        folder = Path(self.base.temp.name)/'r'; folder.mkdir(); identity=(folder.stat().st_dev,folder.stat().st_ino)
        report=folder/'report.json'; report.write_text('{}')
        self.assertEqual(r.regular_report(folder,identity,'report.json'),report)
        with self.assertRaises(ValueError):r.regular_report(folder,(0,0),'report.json')
        report.unlink(); report.symlink_to(self.source)
        with self.assertRaises(ValueError):r.regular_report(folder,identity,'report.json')
        report.unlink(); report.write_bytes(b' '*(16*1024*1024+1))
        with self.assertRaises(ValueError):r.regular_report(folder,identity,'report.json')

    def test_budget_does_not_reuse_30_minute_or_inflate_body(self):
        for key,value in (('commandBudgetSeconds',1500),('workflowMinutes',30),('caseSeconds',121),
                          ('engineTimeout','120s'),('ownedCleanupAllowanceSeconds',0),('ownedCommandCount',82)):
            catalog=copy.deepcopy(CATALOG);catalog['limits'][key]=value
            with self.subTest(key=key):
                with self.assertRaises(ValueError):r.validate_budget(catalog)

    def test_failure_rendering_never_stringifies_raw_objects(self):
        class SecretFailure(Exception):
            def __str__(self):raise AssertionError('must not stringify')
        error=SecretFailure();out=io.StringIO()
        with contextlib.redirect_stdout(out):runner.summarize_failure(error)
        self.assertIn('SecretFailure',out.getvalue())
        self.assertNotIn('must not',out.getvalue())
        out=io.StringIO()
        with contextlib.redirect_stdout(out):runner.summarize_failure(BaseExceptionGroup('raw secret',[error,error]))
        self.assertIn('"independentMembers": 2',out.getvalue());self.assertNotIn('raw secret',out.getvalue())

    def test_duplicate_json_keys_and_nonfinite_values_rejected(self):
        path=Path(self.base.temp.name)/'report.json'
        for raw in ('{"uid":"a","uid":"b"}','{"value":NaN}','{"value":Infinity}'):
            path.write_text(raw)
            with self.assertRaises(ValueError):r.read_json(path)

    def test_immutable_source_binary_inventory_and_deadline_guards(self):
        folder=Path(self.base.temp.name); output=folder/'out'; output.mkdir()
        source=folder/'source.cs'; source.write_text('pinned source')
        binary=output/'unit.dll'; binary.write_bytes(b'synthetic')
        original={source:(runner.sha(source),source.stat().st_mode & 0o777)}
        built={binary:runner.sha(binary)}
        def check(now=0):r.verify_inputs(original,built,output,{binary},now,10)
        check()
        with self.assertRaises(ValueError):check(11)
        source.write_text('changed')
        with self.assertRaises(ValueError):check()
        source.write_text('pinned source'); binary.write_bytes(b'changed')
        with self.assertRaises(ValueError):check()
        binary.write_bytes(b'synthetic'); extra=output/'extra.dll'; extra.write_bytes(b'extra')
        with self.assertRaises(ValueError):check()
        extra.unlink(); source.chmod(0o700)
        with self.assertRaises(ValueError):check()

    def test_original_owner_failure_and_reporting_failure_remain_independent(self):
        primary=RuntimeError('private body')
        diagnostic=RuntimeError('private diagnostic')
        work=Path(self.base.temp.name)/'work'; work.mkdir()
        with mock.patch.object(runner.tempfile,'mkdtemp',return_value=str(work)), \
             mock.patch.object(runner.owner,'enable_subreaper'), \
             mock.patch.object(runner.owner,'run_owned',side_effect=primary), \
             mock.patch.object(runner,'summarize_failure',side_effect=diagnostic), \
             contextlib.redirect_stdout(io.StringIO()):
            self.assertEqual(runner.main(),1)
        graph=runner.main.last_failure
        self.assertIsInstance(graph,BaseExceptionGroup)
        self.assertIs(graph.exceptions[0].exceptions[0],primary)
        self.assertIs(graph.exceptions[-1],diagnostic)
        self.assertIsNot(primary,diagnostic)

    def test_original_owner_failure_survives_independent_final_guard_failure(self):
        primary=RuntimeError('private body'); guard=RuntimeError('private guard')
        work=Path(self.base.temp.name)/'work'; work.mkdir()
        with mock.patch.object(runner.tempfile,'mkdtemp',return_value=str(work)), \
             mock.patch.object(runner.owner,'enable_subreaper'), \
             mock.patch.object(runner.owner,'run_owned',side_effect=primary), \
             mock.patch.object(runner.results,'verify_inputs',side_effect=guard), \
             contextlib.redirect_stdout(io.StringIO()):
            self.assertEqual(runner.main(),1)
        graph=runner.main.last_failure
        self.assertIs(graph.exceptions[0].exceptions[0],primary)
        self.assertIs(graph.exceptions[1].exceptions[0],guard)


    def test_pipe_same_inode_symlink_replacement_rejected(self):
        folder=Path(self.base.temp.name)/'pipe'; folder.mkdir()
        identity=r.directory_identity_strict(folder)
        renamed=folder.with_name('renamed');folder.rename(renamed);folder.symlink_to(renamed,target_is_directory=True)
        self.assertEqual((folder.stat().st_dev,folder.stat().st_ino),identity)
        with self.assertRaises(ValueError):r.verify_directory(folder,identity,empty=True)

    def test_special_runtime_nodes_fail_initial_and_final_inventory(self):
        folder=Path(self.base.temp.name)/'out';folder.mkdir();os.mkfifo(folder/'fifo')
        with self.assertRaises(ValueError):r.ordinary_inventory(folder)
        with self.assertRaises(ValueError):r.verify_inputs({}, {},folder,set(),0,10)

    def test_private_graph_preserves_occurrences_causes_context_traceback_and_owner(self):
        cause=ValueError('private cause')
        try:
            raise RuntimeError('private body') from cause
        except RuntimeError as primary:
            error=primary
        owner=runner.owner.CommandFailure(self.receipt(['actual','argv']),[error,cause])
        group=BaseExceptionGroup('private group',[owner,error,error])
        errors=[group,error,error]
        graph=r.exception_graph(errors)
        roots=graph['orderedRootOccurrences']; nodes=graph['nodes']
        self.assertEqual(len(roots),3);self.assertEqual(roots[1],roots[2])
        members=nodes[roots[0]]['members'];self.assertEqual(members[1],members[2])
        self.assertTrue(nodes[roots[1]]['traceback']);self.assertEqual(nodes[nodes[roots[1]]['cause']]['message'],'private cause')
        self.assertEqual(nodes[members[0]]['ownerOutcome']['argv'],['actual','argv'])
        self.assertEqual(len(nodes[members[0]]['ownerCauses']),2)

    def test_formatter_failure_added_and_private_fd_survives_root_replacement(self):
        class Unformattable(Exception):
            def __str__(self):raise RuntimeError('independent formatter failure')
        folder=Path(self.base.temp.name)/'private';folder.mkdir()
        fd=os.open(folder,os.O_RDONLY|os.O_DIRECTORY|os.O_NOFOLLOW|os.O_CLOEXEC);self.addCleanup(os.close,fd)
        moved=folder.with_name('moved');folder.rename(moved);folder.mkdir()
        errors=[Unformattable()];name=r.persist_failures(fd,errors)
        self.assertEqual(name,'failure-graph.json');self.assertFalse((folder/name).exists())
        graph=r.read_json(moved/name);self.assertEqual(len(errors),2)
        self.assertEqual(len(graph['orderedRootOccurrences']),2)
        self.assertIn('messageFailure',graph['nodes'][graph['orderedRootOccurrences'][0]])

    def test_receipt_failure_adds_original_then_writes_distinct_fallback(self):
        folder=Path(self.base.temp.name)/'private';folder.mkdir();(folder/'failure-graph.json').write_text('occupied')
        fd=os.open(folder,os.O_RDONLY|os.O_DIRECTORY|os.O_NOFOLLOW|os.O_CLOEXEC);self.addCleanup(os.close,fd)
        primary=RuntimeError('body');errors=[primary]
        self.assertEqual(r.persist_failures(fd,errors),'failure-graph-fallback.json')
        self.assertIs(errors[0],primary);self.assertIsInstance(errors[1],FileExistsError)
        graph=r.read_json(folder/'failure-graph-fallback.json')
        self.assertEqual(len(graph['orderedRootOccurrences']),2)
        self.assertEqual((folder/'failure-graph.json').read_text(),'occupied')

    def test_two_receipt_failures_are_not_reported_durable_or_green(self):
        folder=Path(self.base.temp.name)/'private';folder.mkdir()
        for name in ('failure-graph.json','failure-graph-fallback.json'):(folder/name).write_text('occupied')
        fd=os.open(folder,os.O_RDONLY|os.O_DIRECTORY|os.O_NOFOLLOW|os.O_CLOEXEC);self.addCleanup(os.close,fd)
        errors=[RuntimeError('body')]
        self.assertIsNone(r.persist_failures(fd,errors));self.assertEqual(len(errors),3)


    def test_prior_report_modification_deletion_and_extra_file_fail(self):
        folder=Path(self.base.temp.name)/'report';folder.mkdir()
        for name in ('session.html','session.tunit-report.json','junit.xml'):(folder/name).write_text('original '+name)
        sealed=r.seal_directory(folder);r.verify_seal(folder,sealed)
        target=folder/'junit.xml';target.write_text('changed')
        with self.assertRaises(ValueError):r.verify_seal(folder,sealed)
        target.write_text('original junit.xml');target.unlink()
        with self.assertRaises(ValueError):r.verify_seal(folder,sealed)
        target.write_text('original junit.xml');(folder/'extra').write_text('new')
        with self.assertRaises(ValueError):r.verify_seal(folder,sealed)

    def test_prior_logs_cannot_be_resealed_after_mutation_or_unselected_addition(self):
        folder=Path(self.base.temp.name)/'logs';folder.mkdir();sealed=r.seal_directory(folder)
        names=['case-0.stdout','case-0.stderr','case-0.json']
        for name in names:(folder/name).write_text('original')
        sealed=r.verify_seal(folder,sealed,expected_new=names)
        next_names=['case-1.stdout','case-1.stderr','case-1.json']
        for name in next_names:(folder/name).write_text('next')
        with self.assertRaises(ValueError):r.verify_seal(folder,sealed)
        r.verify_seal(folder,sealed,expected_new=next_names)
        (folder/names[0]).write_text('tampered')
        with self.assertRaises(ValueError):r.verify_seal(folder,sealed,expected_new=next_names)
        with self.assertRaises(ValueError):r.verify_seal(folder,sealed,expected_new=names)


    def test_multiline_expression_method_end_is_64_not_next_helper_77(self):
        # Independent literal source slice, not a row synthesized from catalog endLine.
        source=ROOT/'test/SharpLink.UnitTests/Client/ContractScopedRouteCandidateFailureControls.cs'
        lines=source.read_text().splitlines()
        self.assertEqual(lines[16].strip(),'public Task SiteBuildAndCandidateCleanupFailuresKeepOriginalTaskGraphs(bool observerHandoffFails)')
        self.assertEqual(lines[17].strip(),'=> WithCandidateFaultFixture(async fixture =>')
        self.assertEqual(lines[63],'        });')
        self.assertTrue(lines[65].startswith('    private static async Task WithCandidateFaultFixture('))
        self.assertEqual(lines[76],'    }')
        snippet='\n'.join(lines[13:64])+'\n'
        self.assertEqual(r.hashlib.sha256(snippet.encode()).hexdigest(),
                         'f0c00e205ac544c4b232bb7123da08b6e99526d799ac2ca3af0955fbafdbe74b')
        cases,rows=self.cases_and_rows()
        selected=[c for c in cases if c['methodName']=='SiteBuildAndCandidateCleanupFailuresKeepOriginalTaskGraphs']
        selected_rows=[row for row in rows if row['type']['methodName']==selected[0]['methodName']]
        self.assertEqual(len(selected),2)
        for row in selected_rows:row['location']['lineEnd']=64
        self.assertEqual(len(self.discover(selected,selected_rows)),2)
        wrong=copy.deepcopy(selected_rows);wrong[0]['location']['lineEnd']=77
        with self.assertRaises(r.DiscoveryRejected) as caught:self.discover(selected,wrong)
        self.assertEqual(caught.exception.reason_code,'RD_LOCATION')
        self.assertEqual(caught.exception.safe_details['expectedEnd'],64)
        self.assertEqual(caught.exception.safe_details['lineEnd']['integer'],77)

    def test_safe_discovery_reason_redacts_unknown_path_and_identity(self):
        cases,rows=self.cases_and_rows();rows[0]['location']['file']='/private/SECRET-token/raw-path'
        with self.assertRaises(runner.results.DiscoveryRejected) as caught:
            runner.results.discovery(dict(schemaVersion=1,tests=rows),cases,ROOT,CATALOG['assemblyFullName'])
        output=io.StringIO()
        with contextlib.redirect_stdout(output):runner.summarize_failure(caught.exception)
        text=output.getvalue();self.assertIn('RD_LOCATION',text);self.assertIn('expectedEnd',text)
        self.assertNotIn('SECRET',text);self.assertNotIn('/private/',text)
        self.assertEqual(caught.exception.safe_details['path']['jsonType'],'str')
        self.assertEqual(len(caught.exception.safe_details['path']['sha256']),64)
        rows[0]['displayName']='SECRET arbitrary display'
        with self.assertRaises(runner.results.DiscoveryRejected) as caught:
            runner.results.discovery(dict(schemaVersion=1,tests=rows),cases,ROOT,CATALOG['assemblyFullName'])
        output=io.StringIO()
        with contextlib.redirect_stdout(output):runner.summarize_failure(caught.exception)
        self.assertIn('RD_CASE',output.getvalue());self.assertNotIn('SECRET',output.getvalue())

    def test_safe_reason_code_count_and_signature_do_not_relax_acceptance(self):
        cases,rows=self.cases_and_rows()
        with self.assertRaises(r.DiscoveryRejected) as caught:self.discover(cases,rows[:-1])
        self.assertEqual(caught.exception.reason_code,'RD_COUNT')
        self.assertEqual(caught.exception.safe_details['actualRows']['count'],58)
        rows[0]['type']['parameterTypeFullNames']=['SECRET unknown type']
        with self.assertRaises(r.DiscoveryRejected) as caught:self.discover(cases,rows)
        self.assertEqual(caught.exception.reason_code,'RD_SIGNATURE')
        self.assertNotIn('SECRET',json.dumps(caught.exception.safe_details))
        self.assertFalse(caught.exception.safe_details['parameterTypesEqual'])
        huge=r.diagnostic_value(10**100)
        self.assertNotIn('integer',huge);self.assertEqual(len(huge['sha256']),64)

    def test_safe_diagnostic_failure_keeps_original_rejection_first(self):
        independent=RuntimeError('private diagnostic failure')
        with self.assertRaises(BaseExceptionGroup) as caught:
            r.discovery_expect(False,'RD_LOCATION',{'selectionClass':'Known'},
                               lambda: (_ for _ in ()).throw(independent))
        self.assertIsInstance(caught.exception.exceptions[0],r.DiscoveryRejected)
        self.assertEqual(caught.exception.exceptions[0].reason_code,'RD_LOCATION')
        self.assertIs(caught.exception.exceptions[1],independent)


    def test_invalid_integer_uid_or_identity_is_hash_only_not_raw_value(self):
        cases,rows=self.cases_and_rows();rows[0]['uid']=123456
        with self.assertRaises(r.DiscoveryRejected) as caught:self.discover(cases,rows)
        self.assertEqual(caught.exception.reason_code,'RD_UID')
        self.assertNotIn('integer',caught.exception.safe_details['uid'])
        self.assertEqual(caught.exception.safe_details['uid']['jsonType'],'int')
        cases,rows=self.cases_and_rows();rows[0]['displayName']=987654
        with self.assertRaises(r.DiscoveryRejected) as caught:self.discover(cases,rows)
        self.assertEqual(caught.exception.reason_code,'RD_IDENTITY')
        self.assertNotIn('integer',caught.exception.safe_details['displayName'])
        cases,rows=self.cases_and_rows();rows[0]['location']['file']=456789
        with self.assertRaises(r.DiscoveryRejected) as caught:self.discover(cases,rows)
        self.assertEqual(caught.exception.reason_code,'RD_LOCATION')
        self.assertNotIn('integer',caught.exception.safe_details['path'])
        self.assertNotIn('integer',r.diagnostic_value(77))
        self.assertEqual(r.diagnostic_value(77,source_line=True)['integer'],77)



if __name__ == '__main__':
    suite=unittest.TestSuite((unittest.defaultTestLoader.loadTestsFromModule(accepted),
                             unittest.defaultTestLoader.loadTestsFromTestCase(Route59Tests)))
    result=unittest.TextTestRunner(verbosity=2).run(suite)
    sys.exit(not result.wasSuccessful())
