"""Synthetic Python protocol checks ONLY; never SharpLink/.NET runtime evidence."""
import copy
import contextlib
import importlib.util
import io
import json
import os
import re
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


def fixture_configuration_sources():
    return {path.name: path.read_text() for path in
            (ROOT / 'test/SharpLink.UnitTests/Client').glob('ContractScopedRoute*.cs')}


def verify_fixture_configuration_sources(sources):
    """Narrow source-shape guard, not C# parsing, compilation or runtime proof."""
    compact = {name: re.sub(r'\s+', '', source) for name, source in sources.items()}
    common = 'privatestaticvoidConfigure(SharpClientBuilderbuilder){builder.UseCluster('
    for name in ('SelectionFixture', 'LoadFixture'):
        source = compact['ContractScopedRoute' + name + '.cs']
        r.require(common in source, 'Fixture common configuration must not select a policy')
        r.require('if(selectorisnull)builder.UseLoadBalancing(strategy);elsebuilder.UseEndpointSelector(selector);' in source,
                  'Fixture must choose exactly one selection configuration')
        r.require(source.count('builder.UseLoadBalancing(') == source.count('builder.UseEndpointSelector(') == 1,
                  'Unexpected extra fixture selection configuration')
    protocol = compact['ContractScopedRouteProtocolControls.cs']
    r.require(common in protocol and
              'if(callbacksisnull)builder.UseLoadBalancing(SharpLinkLoadBalancingStrategy.RoundRobin);else{builder.UseEndpointSelector(callbacks);builder.UseEndpointAdmission(callbacks);}' in protocol,
              'Protocol fixture must preserve explicit RR or custom callbacks exclusively')
    r.require(protocol.count('builder.UseLoadBalancing(') == protocol.count('builder.UseEndpointSelector(') == 1,
              'Unexpected extra protocol selection configuration')
    handoff = compact['ContractScopedRouteHandoffFixture.cs']
    r.require('privatestaticvoidConfigure(SharpClientBuilderbuilder,booluseBuiltIn=true){if(useBuiltIn)builder.UseLoadBalancing(SharpLinkLoadBalancingStrategy.RoundRobin);builder.UseCluster(' in handoff,
              'Handoff common must preserve original RR by default')
    r.require(handoff.count('builder.UseLoadBalancing(') == 1 and 'builder.UseEndpointSelector(' not in handoff,
              'Unexpected extra handoff selection configuration')
    retry = compact['ContractScopedRouteRetryFixture.cs']
    r.require(retry.count('builder.UseEndpointSelector(_retrySelector);') == 1 and 'builder.UseLoadBalancing(' not in retry,
              'Retry fixture must keep its original custom selector only')
    expected = {'ContractScopedRouteHandoffFixture.cs': ['Configure(builder);'],
                'ContractScopedRouteGenerationControls.cs': ['Configure(builder);'],
                'ContractScopedRouteRetryFixture.cs': ['Configure(builder,useBuiltIn:false);']}
    actual = {name: re.findall(r'(?<![A-Za-z])Configure\(builder[^)]*\);', source)
              for name, source in compact.items()
              if 'partialclassContractScopedRouteHandoffFixture' in source}
    r.require({name: calls for name, calls in actual.items() if calls} == expected,
              'Handoff partial caller graph changed or retry inherited builtin policy')


def fixture_method_source(source, signature):
    # Exact checked fixture layout only; not a general C# method parser.
    start = source.index(signature)
    return re.sub(r'\s+', '', source[start:source.index('\n    }', start) + 6])


def verify_refresh_registration_sources(sources):
    for name, signature in (
            ('HandoffFixture', 'internal async Task RefreshWhileReplacementUnboundAsync()'),
            ('RetryFixture', 'private async Task RefreshAForRetryAsync()')):
        body = fixture_method_source(sources['ContractScopedRoute' + name + '.cs'], signature)
        ordered = ('_refreshStarted=true;', 'RequireNoPriorRefreshRollout();', 'InjectFrameAsync(',
                   'await_factory!.ReplacementEntered.Task.WaitAsync(Bound);',
                   '_rollout=awaitCapturePendingRefreshRolloutAsync();',
                   '_factory.ReleaseReplacement.TrySetResult();', 'awaitcut.Task.WaitAsync(Bound);',
                   'awaitObserveAsync(_rollout);')
        positions = [body.index(part) for part in ordered]
        r.require(positions == sorted(positions) and '.Single(' not in body,
                  'Refresh owner must be captured after entry but before release, never by immediate Single')
    source = sources['ContractScopedRouteHandoffFixture.cs']
    prior = fixture_method_source(source, 'private void RequireNoPriorRefreshRollout()')
    r.require('CaptureFrameworkOwners("StaticClusterSessionRefreshRollout","originalsessionrefreshrollout");' in prior and
              'Ensure(_rolloutisnull&&!SnapshotOperations().Any(operation=>operation.Stage=="originalsessionrefreshrollout"),' in prior,
              'Prior original rollouts cannot be borrowed')
    capture = fixture_method_source(source, 'private async Task<OwnedOperation> CapturePendingRefreshRolloutAsync()')
    r.require('awaitAwaitAsync(()=>{' in capture and 'Ensure(!_factory!.ReleaseReplacement.Task.IsCompleted,' in capture and
              'CaptureFrameworkOwners("StaticClusterSessionRefreshRollout","originalsessionrefreshrollout");' in capture and
              'varmatches=SnapshotOperations().Where(operation=>operation.Stage=="originalsessionrefreshrollout").ToArray();' in capture and
              'Ensure(matches.Length<=1,' in capture and 'if(matches.Length==0)returnfalse;' in capture and
              'captured=matches[0];' in capture and 'Ensure(!captured.Task.IsCompleted,' in capture and
              'returncaptured!;' in capture and 'TrySetResult' not in capture,
              'Only one registered pending original is acceptable while factory remains held')
    audit = fixture_method_source(source, 'private static async Task<Exception?> CaptureTerminalFailureAsync(Task source)')
    r.require('returnsource.Exceptionis{}graph?graph:original;' in audit,
              'Terminal audit must retain all original Task.Exception roots')


def verify_refresh_capability_sources(sources):
    # Existing ordering guard proves both wire injection paths call the shared
    # precondition before injection; this is source-shape only, not runtime proof.
    verify_refresh_registration_sources(sources)
    source = sources['ContractScopedRouteHandoffFixture.cs']
    compact = re.sub(r'\s+', '', source)
    r.require('privatestaticTestClientTransportFactoryManualTransport()=>new(negotiatedCapabilities:ProtocolV2Capabilities.SessionRefresh,contractManifest:' in compact,
              'Refresh fixture must negotiate exactly the needed optional capability')
    prior = fixture_method_source(source, 'private void RequireNoPriorRefreshRollout()')
    actual = 'Ensure((Source.Session.NegotiatedCapabilities&ProtocolV2Capabilities.SessionRefresh)!=0,'
    r.require(actual in prior and prior.index(actual) < prior.index('CaptureFrameworkOwners('),
              'Refresh injection must check actual negotiated source session capability')


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

    def test_fixture_configuration_exclusive_source_shape(self):
        verify_fixture_configuration_sources(fixture_configuration_sources())

    def test_fixture_configuration_rejects_common_builtin_reintroduction(self):
        sources = fixture_configuration_sources()
        for name in ('SelectionFixture', 'LoadFixture', 'ProtocolControls'):
            with self.subTest(fixture=name):
                changed = dict(sources); key = 'ContractScopedRoute' + name + '.cs'
                changed[key] = changed[key].replace('builder.UseCluster(options =>',
                    'builder.UseLoadBalancing(SharpLinkLoadBalancingStrategy.RoundRobin); builder.UseCluster(options =>', 1)
                with self.assertRaises(ValueError): verify_fixture_configuration_sources(changed)

    def test_fixture_configuration_rejects_nonexclusive_strategy(self):
        for name in ('SelectionFixture', 'LoadFixture'):
            with self.subTest(fixture=name):
                changed = fixture_configuration_sources(); key = 'ContractScopedRoute' + name + '.cs'
                changed[key] = changed[key].replace('if (selector is null) builder.UseLoadBalancing(strategy);',
                    'builder.UseLoadBalancing(strategy);')
                with self.assertRaises(ValueError): verify_fixture_configuration_sources(changed)

    def test_fixture_configuration_preserves_all_partial_default_callers(self):
        mutations = (
            ('RetryFixture', 'Configure(builder, useBuiltIn: false);', 'Configure(builder);'),
            ('HandoffFixture', 'bool useBuiltIn = true', 'bool useBuiltIn = false'),
            ('GenerationControls', 'Configure(builder);', 'Configure(builder, useBuiltIn: false);'))
        for name, old, new in mutations:
            with self.subTest(fixture=name):
                changed = fixture_configuration_sources(); key = 'ContractScopedRoute' + name + '.cs'
                self.assertIn(old, changed[key]); changed[key] = changed[key].replace(old, new, 1)
                with self.assertRaises(ValueError): verify_fixture_configuration_sources(changed)

    def test_refresh_registration_waits_for_actual_pending_original_source_shape(self):
        verify_refresh_registration_sources(fixture_configuration_sources())

    def test_refresh_registration_rejects_skipped_capture_or_premature_release(self):
        for name in ('HandoffFixture', 'RetryFixture'):
            for old, new in (
                    ('RequireNoPriorRefreshRollout();', ''),
                    ('_rollout = await CapturePendingRefreshRolloutAsync();',
                     '_factory.ReleaseReplacement.TrySetResult(); _rollout = await CapturePendingRefreshRolloutAsync();')):
                with self.subTest(fixture=name, mutation=old):
                    changed = fixture_configuration_sources(); key = 'ContractScopedRoute' + name + '.cs'
                    self.assertIn(old, changed[key]); changed[key] = changed[key].replace(old, new, 1)
                    with self.assertRaises(ValueError): verify_refresh_registration_sources(changed)

    def test_refresh_registration_rejects_absent_multiple_terminal_or_old_owner(self):
        for old, new in (
                ('_rollout is null && !SnapshotOperations()', 'true && !SnapshotOperations()'),
                ('Ensure(matches.Length <= 1,', 'Ensure(matches.Length <= 2,'),
                ('if (matches.Length == 0) return false;', 'if (matches.Length == 0) return true;'),
                ('Ensure(!captured.Task.IsCompleted,', 'Ensure(captured.Task.IsCompleted,')):
            with self.subTest(mutation=old):
                changed = fixture_configuration_sources(); key = 'ContractScopedRouteHandoffFixture.cs'
                self.assertIn(old, changed[key]); changed[key] = changed[key].replace(old, new, 1)
                with self.assertRaises(ValueError): verify_refresh_registration_sources(changed)

    def test_refresh_registration_preserves_complete_original_task_error_graph(self):
        changed = fixture_configuration_sources(); key = 'ContractScopedRouteHandoffFixture.cs'
        old = 'return source.Exception is { } graph ? graph : original;'
        self.assertIn(old, changed[key]); changed[key] = changed[key].replace(old, 'return original;', 1)
        with self.assertRaises(ValueError): verify_refresh_registration_sources(changed)

    def test_refresh_capability_is_negotiated_and_checked_before_wire_injection(self):
        verify_refresh_capability_sources(fixture_configuration_sources())

    def test_refresh_capability_rejects_omitted_or_unrelated_optional_bits(self):
        old = 'negotiatedCapabilities: ProtocolV2Capabilities.SessionRefresh,'
        for new in ('', 'negotiatedCapabilities: ProtocolV2Capabilities.None,',
                    'negotiatedCapabilities: ProtocolV2Capabilities.SessionRefresh | ProtocolV2Capabilities.Metadata,'):
            with self.subTest(replacement=new):
                changed = fixture_configuration_sources(); key = 'ContractScopedRouteHandoffFixture.cs'
                self.assertIn(old, changed[key]); changed[key] = changed[key].replace(old, new, 1)
                with self.assertRaises(ValueError): verify_refresh_capability_sources(changed)

    def test_refresh_capability_rejects_synthetic_or_missing_session_precondition(self):
        old = '(Source.Session.NegotiatedCapabilities & ProtocolV2Capabilities.SessionRefresh) != 0'
        for new in ('true', '(ProtocolV2Capabilities.SessionRefresh & ProtocolV2Capabilities.SessionRefresh) != 0'):
            with self.subTest(replacement=new):
                changed = fixture_configuration_sources(); key = 'ContractScopedRouteHandoffFixture.cs'
                self.assertIn(old, changed[key]); changed[key] = changed[key].replace(old, new, 1)
                with self.assertRaises(ValueError): verify_refresh_capability_sources(changed)

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


    def body_context(self):
        case=next(c for c in CATALOG['cases'] if c['designId']=='R05')
        return {'index':4,'entry':{'case':case,'discovery':self.row},'phase':'owned-command',
                'argv':['dotnet','SECRET-argv','--filter-uid','SECRET-uid'],'report':None}

    def test_original_failed_owner_classification_has_exit_timeout_counts_not_raw(self):
        context=self.body_context();receipt=self.receipt(context['argv'])
        receipt.update(stage='case-4',exitCode=2,timedOut=False,errors=['SECRET-error'],cleanupErrors=[],
                       reportingErrors=[],remainingOwned={},elapsedSeconds=10.321)
        detail,terminal=r.owner_case_diagnostic(context,{'phase':'owner-call','provenance':'original-owner-failure','receipt':receipt})
        self.assertTrue(terminal);self.assertEqual(detail['exitCode'],2);self.assertFalse(detail['timedOut'])
        self.assertEqual(detail['errorsCount'],1);self.assertEqual(detail['remainingOwnedCount'],0)
        self.assertNotIn('SECRET',json.dumps(detail));self.assertEqual(detail['phase'],'owned-command')
        self.assertEqual(receipt['errors'],['SECRET-error'])
        receipt['remainingOwned']={'12345':['SECRET-owner']}
        detail,terminal=r.owner_case_diagnostic(context,{'phase':'owner-call','provenance':'original-owner-failure','receipt':receipt})
        self.assertFalse(terminal);self.assertEqual(detail['remainingOwnedCount'],1)
        self.assertNotIn('12345',json.dumps(detail));self.assertNotIn('SECRET',json.dumps(detail))

    def test_unknown_owner_never_reads_reports_or_turns_waiter_timeout_into_exit(self):
        context=self.body_context()
        detail,terminal=r.owner_case_diagnostic(context,None)
        self.assertFalse(terminal)
        with mock.patch.object(r,'regular_report',side_effect=AssertionError('must not read')):
            reports,errors=r.failure_report_diagnostic(context,terminal,CATALOG,ROOT)
        self.assertEqual(reports['reports'],'not-read-owner-exit-unknown');self.assertEqual(errors,[])
        receipt=self.receipt(context['argv']);receipt.update(stage='case-4',exitCode=None,timedOut=True)
        _,terminal=r.owner_case_diagnostic(context,{'phase':'owner-call','provenance':'original-owner-failure','receipt':receipt})
        self.assertFalse(terminal)
        receipt.update(exitCode=-15,remainingOwned={})
        detail,terminal=r.owner_case_diagnostic(context,{'phase':'owner-call','provenance':'original-owner-failure','receipt':receipt})
        self.assertTrue(terminal);self.assertTrue(detail['timedOut']) # terminal is NOT success
        with self.assertRaises(ValueError):r.verify_receipt(receipt,'case-4',context['argv'],120)

    def test_failed_native_classification_exposes_only_whitelisted_source_frames(self):
        native=copy.deepcopy(self.native);test=native['groups'][0]['tests'][0]
        test['status']='failed'
        relative='test/SharpLink.UnitTests/Client/ContractScopedRouteLoadFixture.cs'
        test['exception']={'type':'System.AggregateException','message':'SECRET-message',
            'stackTrace':'SECRET-stack at Unknown in /private/SECRET.cs:line 7',
            'innerException':{'type':'System.TimeoutException','message':'SECRET-timeout',
               'stackTrace':'at Hidden in /_/'+relative+':line 214'}}
        before=copy.deepcopy(native)
        classified=r.native_failure_classification(native,self.row,CATALOG,ROOT)
        self.assertEqual(native,before);self.assertEqual(classified['cases'][0]['status'],'failed')
        chain=classified['cases'][0]['exception']['serializedChain']
        self.assertEqual(chain[0]['typeClass'],'aggregate');self.assertEqual(chain[1]['typeClass'],'timeout')
        self.assertEqual(chain[0]['reportedSourceFrames'],[])
        self.assertEqual(chain[1]['reportedSourceFrames'],[{'source':relative,'line':214}])
        self.assertNotIn('SECRET',json.dumps(classified));self.assertNotIn('/private/',json.dumps(classified))
        with self.assertRaises(ValueError):self.verdict(native)

    def test_unknown_native_type_status_and_junit_text_remain_redacted(self):
        native=copy.deepcopy(self.native);test=native['groups'][0]['tests'][0]
        test.update(id='SECRET-id',displayName='SECRET-display',status='SECRET-status',
                    exception={'type':'SECRET.Type','message':'SECRET-message','stackTrace':'SECRET-path'})
        classified=r.native_failure_classification(native,self.row,CATALOG,ROOT)
        self.assertFalse(classified['cases'][0]['uidMatches']);self.assertEqual(classified['cases'][0]['status'],'unknown')
        self.assertEqual(classified['cases'][0]['exception']['serializedChain'][0]['typeClass'],'other')
        self.assertNotIn('SECRET',json.dumps(classified))
        xml='<testsuites><testsuite><testcase name="SECRET-name"><failure>SECRET-body</failure><SECRET-tag/></testcase></testsuite></testsuites>'
        classified=r.junit_failure_classification(xml,self.row)
        self.assertEqual(classified['childCounts']['failure'],1);self.assertEqual(classified['unknownChildCount'],1)
        self.assertNotIn('SECRET',json.dumps(classified))

    def test_failure_report_read_is_readonly_and_independent_parse_error_is_retained(self):
        context=self.body_context();folder=Path(self.base.temp.name)/'failed-report';folder.mkdir()
        context.update(report=folder,reportIdentity=r.directory_identity_strict(folder))
        native=copy.deepcopy(self.native);native['groups'][0]['tests'][0]['status']='failed'
        data=json.dumps(native);(folder/'session.tunit-report.json').write_text(data)
        (folder/'junit.xml').write_text('SECRET-invalid-xml')
        summary,failures=r.failure_report_diagnostic(context,True,CATALOG,ROOT)
        self.assertEqual(summary['native']['cases'][0]['status'],'failed')
        self.assertEqual(summary['junit']['classification'],'diagnostic-rejected');self.assertEqual(len(failures),1)
        self.assertEqual((folder/'session.tunit-report.json').read_text(),data)
        self.assertNotIn('SECRET',json.dumps(summary))
        (folder/'junit.xml').unlink();(folder/'junit.xml').symlink_to(folder/'missing')
        summary,failures=r.failure_report_diagnostic(context,True,CATALOG,ROOT)
        self.assertEqual(summary['junit']['classification'],'diagnostic-rejected');self.assertEqual(len(failures),1)

    def test_report_exception_chain_is_bounded_diagnostic_not_full_graph_claim(self):
        value=None
        for i in range(12):value={'type':'System.TimeoutException','message':'private','innerException':value}
        detail=r.serialized_exception_diagnostic(value,CATALOG,ROOT)
        self.assertEqual(len(detail['serializedChain']),8);self.assertTrue(detail['tailUnclassified'])
        self.assertIn('not a complete',detail['qualification'])

    def test_owner_phase_and_untrusted_receipt_provenance_are_not_accepted(self):
        context=self.body_context();context['phase']='SECRET-phase'
        detail,terminal=r.owner_case_diagnostic(context,{'phase':'SECRET-command','provenance':'disk-unverified','receipt':self.receipt(context['argv'])})
        self.assertFalse(terminal);self.assertEqual(detail['phase'],'unknown');self.assertEqual(detail['commandPhase'],'unknown')
        self.assertNotIn('SECRET',json.dumps(detail))
        receipt=self.receipt(['wrong-argv']);receipt['stage']='case-4'
        detail,terminal=r.owner_case_diagnostic(self.body_context(),{'phase':'owner-call','provenance':'original-owner-failure','receipt':receipt})
        self.assertFalse(terminal);self.assertFalse(detail['argvMatches'])



if __name__ == '__main__':
    suite=unittest.TestSuite((unittest.defaultTestLoader.loadTestsFromModule(accepted),
                             unittest.defaultTestLoader.loadTestsFromTestCase(Route59Tests)))
    result=unittest.TextTestRunner(verbosity=2).run(suite)
    sys.exit(not result.wasSuccessful())
