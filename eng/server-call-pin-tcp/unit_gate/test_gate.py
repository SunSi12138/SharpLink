"""Pure Python negative and positive controls; never starts CLR, socket or MTP."""
import copy
import contextlib
import io
import json
from pathlib import Path
import shutil
import sys
import tempfile
import unittest
from types import SimpleNamespace
from unittest.mock import patch
sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
from common import ARMS, CONTRACT
from unit_gate import provenance, record, run
from unit_gate.scope import explicit_workers, ordinary_uids
from unit_gate.validate import GateError, discovery_uids, uid_digest, validate_run

TOOL = Path(__file__).parent
UIDS = ['unit.A', 'unit.B']


def report(uids=UIDS, assembly='SharpLink.UnitTests'):
    summary = dict(total=len(uids), passed=len(uids), failed=0, skipped=0, cancelled=0, timedOut=0, flaky=0)
    return {'schemaVersion': 1, 'publicationGeneration': 'a' * 32, 'assemblyName': assembly,
        'tunitVersion': '1.73.19.0', 'runtimeVersion': '.NET 10.0.14', 'summary': dict(summary),
        'groups': [{'summary': dict(summary), 'tests': [{'id': u, 'status': 'passed'} for u in uids]}]}


def valid_record():
    value = record.expected_record('a' * 40)
    for arm in ARMS:
        value['arms'][arm].update(
            assemblySha256={name: 'b' * 64 for name in record.REQUIRED_ASSEMBLIES},
            integrationAssemblySha256={name: 'b' * 64 for name in record.INTEGRATION_ASSEMBLIES},
            frozenClosure={name: 'b' * 64 for name in record.CLOSURE_NAMES},
            correctnessGraphSha256=record.graph_digest({'test/plugin/bin/Release/net10.0/plugin.dll': 'b' * 64}))
    return value


def children_report():
    tests = []
    for scenario in record.SCENARIOS:
        uid = f'SharpLink.UnitTests.Runtime.ReadOwnershipPipeReaderLifetimeTests.1.1.{scenario}(System.Threading.CancellationToken).1.1.0'
        witness = dict(scenario=scenario, runtime='10.0.14', architecture='X64', executed=1, passed=True)
        tests.append({'id': uid, 'status': 'passed', 'output': 'READ_OWNERSHIP_ALLOCATION_VERIFIED|' + json.dumps(witness)})
    return {'groups': [{'tests': tests}]}


class UnitGateTests(unittest.TestCase):
    def test_both_frozen_corpora(self):
        for arm, raw_count, ordinary_count in [('baseline', 2316, 2311), ('candidate', 2331, 2326)]:
            raw, ordinary, workers = provenance.read_corpus(TOOL, arm)
            self.assertEqual((len(raw), len(ordinary), len(workers)), (raw_count, ordinary_count, 5))
            self.assertEqual(len({w['type_name'] for w in workers}), 4)
            self.assertEqual(len([u for u in raw if u.startswith(provenance.PIN_CLASS)]), 15 if arm == 'candidate' else 0)

    def test_result_pass(self):
        self.assertEqual(validate_run(UIDS, report(), 0)['passed'], 2)

    def test_missing_extra_duplicate_wrong_uid(self):
        for mutation in ('missing', 'extra', 'duplicate', 'wrong'):
            value = report(); tests = value['groups'][0]['tests']
            if mutation == 'missing': tests.pop()
            if mutation == 'extra': tests.append({'id': 'unit.C', 'status': 'passed'})
            if mutation == 'duplicate': tests.append(dict(tests[0]))
            if mutation == 'wrong': tests[0]['id'] = 'unit.D'
            with self.subTest(mutation=mutation), self.assertRaises(GateError): validate_run(UIDS, value, 0)

    def test_skip_fail_cancel_timeout_retry_rejected(self):
        for status in ('skipped', 'failed', 'cancelled', 'timedOut', 'flaky', 'unknown'):
            value = report(); value['groups'][0]['tests'][0]['status'] = status
            with self.subTest(status=status), self.assertRaises(GateError): validate_run(UIDS, value, 0)
        for code in (1, -9):
            with self.assertRaises(GateError): validate_run(UIDS, report(), code)
        with self.assertRaises(GateError): validate_run(UIDS, report(), 0, True)

    def test_result_identity_and_summary(self):
        for key, bad in [('schemaVersion', 2), ('assemblyName', 'other'), ('tunitVersion', 'other'),
                         ('publicationGeneration', 'bad')]:
            value = report(); value[key] = bad
            with self.subTest(key=key), self.assertRaises(GateError): validate_run(UIDS, value, 0)
        for key in report()['summary']:
            value = report(); value['summary'][key] += 1
            with self.subTest(key=key), self.assertRaises(GateError): validate_run(UIDS, value, 0)

    def test_corrupt_corpus_and_exclusions_rejected(self):
        for file, mutation in [('new-uids.json', 'drop'), ('expected-base-uids.json', 'duplicate'),
                               ('explicit-workers.json', 'drop')]:
            with tempfile.TemporaryDirectory() as temporary:
                root = Path(temporary)
                for name in ('corpus.json', 'expected-base-uids.json', 'new-uids.json', 'explicit-workers.json'):
                    shutil.copyfile(TOOL / name, root / name)
                path = root / file; values = json.loads(path.read_text())
                if mutation == 'drop': values.pop()
                else: values[0] = values[1]
                path.write_text(json.dumps(values))
                with self.assertRaises(GateError): provenance.read_corpus(root)
        with self.assertRaises(GateError): ordinary_uids(['ordinary'], explicit_workers(TOOL))

    def test_record_positive_and_per_arm_identity_mutations(self):
        self.assertIsInstance(record.validate_record(valid_record(), 'a' * 40), dict)
        for arm in ARMS:
            for key, bad in [('source', 'c' * 40), ('tree', 'c' * 40), ('runtime', '10.0.2'),
                ('sdk', '10.0.400'), ('correctnessGraphSha256', 'short'), ('ordinaryCount', 2325), ('passedCount', True), ('failedCount', 1),
                ('skippedCount', 1), ('flakyCount', 1), ('rawUidSha256', 'c' * 64)]:
                value = valid_record(); value['arms'][arm][key] = bad
                with self.subTest(arm=arm, key=key), self.assertRaises(GateError): record.validate_record(value)
        value = valid_record(); value['arms'].pop('baseline')
        with self.assertRaises(GateError): record.validate_record(value)

    def test_closure_missing_malformed_and_cross_build_rejected(self):
        for mutation in ('missing', 'malformed', 'unknown', 'changed', 'outside'):
            value = valid_record(); arm = value['arms']['candidate']
            if mutation == 'missing': arm['assemblySha256'].pop('SharpLink.Client.dll')
            if mutation == 'malformed': arm['frozenClosure']['SharpLink.Client.dll'] = 'short'
            if mutation == 'unknown': arm['frozenClosure']['Unknown.dll'] = 'b' * 64
            if mutation == 'changed': arm['integrationAssemblySha256']['SharpLink.Client.dll'] = 'c' * 64
            if mutation == 'outside': arm['assemblySha256']['../unknown.dll'] = 'b' * 64
            with self.subTest(mutation=mutation), self.assertRaises(GateError): record.validate_record(value)

    def test_record_binds_prepared_snapshots(self):
        value = valid_record()
        manifest = {'arms': {arm: {'closure': value['arms'][arm]['frozenClosure'],
            'source': {'commit': CONTRACT[arm + 'Source'], 'tree': CONTRACT[arm + 'Tree']},
            'correctness': {'graphSnapshot': {'test/plugin/bin/Release/net10.0/plugin.dll': 'b' * 64},
                           'unitSnapshot': value['arms'][arm]['assemblySha256'],
                           'integrationSnapshot': value['arms'][arm]['integrationAssemblySha256']}} for arm in ARMS}}
        record.validate_record(value, 'a' * 40, manifest)
        manifest = copy.deepcopy(manifest); manifest['arms']['baseline']['correctness']['unitSnapshot']['extra'] = 'b' * 64
        with self.assertRaises(GateError): record.validate_record(value, 'a' * 40, manifest)

    def test_actual_dynamic_gate_exact_uid_all_counters_runtime(self):
        good = report([record.DYNAMIC_UID], 'SharpLink.IntegrationTests')
        run.validate_integration(good, 0, False)
        for mutation in ('uid', 'runtime', 'status', 'summary', 'duplicate', 'assembly', 'generation'):
            value = copy.deepcopy(good)
            if mutation == 'uid': value['groups'][0]['tests'][0]['id'] = 'module-configured-not-dynamic'
            if mutation == 'runtime': value['runtimeVersion'] = '.NET 10.0.2'
            if mutation == 'status': value['groups'][0]['tests'][0]['status'] = 'skipped'
            if mutation == 'summary': value['summary']['flaky'] = 1
            if mutation == 'duplicate': value['groups'][0]['tests'] *= 2
            if mutation == 'assembly': value['assemblyName'] = 'SharpLink.UnitTests'
            if mutation == 'generation': value['publicationGeneration'] = 'bad'
            with self.subTest(mutation=mutation), self.assertRaises(GateError): run.validate_integration(value, 0, False)
        with self.assertRaises(GateError): run.validate_integration(good, 1, False)
        with self.assertRaises(GateError): run.validate_integration(good, 0, True)

    def test_allocation_child_exact_witnesses(self):
        self.assertEqual(len(run.allocation_children(children_report())), 2)
        for mutation in ('missing', 'duplicate', 'runtime', 'status', 'wrong_uid', 'bool_count'):
            value = children_report(); tests = value['groups'][0]['tests']
            if mutation == 'missing': tests.pop()
            if mutation == 'duplicate': tests.append(dict(tests[0]))
            if mutation == 'runtime': tests[0]['output'] = tests[0]['output'].replace('10.0.14', '10.0.2')
            if mutation == 'status': tests[0]['status'] = 'skipped'
            if mutation == 'wrong_uid': tests[0]['id'] = 'wrong'
            if mutation == 'bool_count': tests[0]['output'] = tests[0]['output'].replace('"executed": 1', '"executed": true')
            with self.subTest(mutation=mutation), self.assertRaises(GateError): run.allocation_children(value)

    def test_runtime_config_is_read_only_exact_core14(self):
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / 'runtimeconfig.json'
            document = {'runtimeOptions': {'rollForward': 'Disable',
                'framework': {'name': 'Microsoft.NETCore.App', 'version': '10.0.14'}}}
            path.write_text(json.dumps(document)); before = path.read_bytes()
            run.validate_runtime_config(path); self.assertEqual(path.read_bytes(), before)
            for name in ('Microsoft.AspNetCore.App', 'Unknown'):
                document['runtimeOptions']['framework']['name'] = name; path.write_text(json.dumps(document))
                with self.assertRaises(GateError): run.validate_runtime_config(path)

    def test_environment_sanitized_no_filter_rollforward_injection(self):
        env = run.private_test_environment({'DOTNET_STARTUP_HOOKS': 'bad', 'COMPlus_ReadyToRun': '0',
            'GITHUB_ACTIONS': 'true', 'TUNIT_FILTER': 'skip', 'MSBUILD_PATH': 'bad',
            'TESTINGPLATFORM_TEST_FILTER': 'skip', 'PATH': '/usr/bin'}, Path('/private/dotnet'), Path('/raw'))
        for key in ('GITHUB_ACTIONS', 'TUNIT_FILTER', 'DOTNET_STARTUP_HOOKS', 'COMPlus_ReadyToRun',
                    'TESTINGPLATFORM_TEST_FILTER', 'MSBUILD_PATH'): self.assertNotIn(key, env)
        self.assertEqual(env['DOTNET_HOST_PATH'], '/private/dotnet')
        self.assertEqual(env['DOTNET_ROLL_FORWARD'], 'Disable')

    def test_host_inventory_rejects_dualframework_and_sdk(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary); host = root / 'dotnet'; host.touch()
            good = ['', f'Microsoft.NETCore.App 10.0.14 [{root / "shared/Microsoft.NETCore.App"}]']
            for bad in (None, 0, 1):
                lines = iter([line if index != bad else 'unexpected' for index, line in enumerate(good)])
                def execute(command, cwd, env, timeout, output):
                    output.write_text(next(lines)); return 0, False
                with patch.object(run, 'execute', side_effect=execute):
                    if bad is None: run.host_inventory(host, root, {}, root)
                    else:
                        with self.assertRaises(GateError): run.host_inventory(host, root, {}, root)

    def test_commands_never_build_and_only_dynamic_filters(self):
        ordinary = run.test_command('/runtime/dotnet', '/unit.dll', Path('/raw'), 2326)
        dynamic = run.test_command('/runtime/dotnet', '/integration.dll', Path('/raw'), 1, record.DYNAMIC_UID)
        self.assertNotIn('build', ordinary + dynamic)
        self.assertNotIn('--filter-uid', ordinary)
        self.assertEqual(dynamic[dynamic.index('--filter-uid') + 1], record.DYNAMIC_UID)

    def test_prepared_requires_full_same_bytes_and_pinned_configs(self):
        for mutation in (None, 'changed_unit', 'changed_integration', 'runtime', 'closure', 'source', 'symlink', 'sibling_plugin'):
            with tempfile.TemporaryDirectory() as temporary:
                root = Path(temporary)
                manifest = {'contract': CONTRACT, 'toolRevision': 'a' * 40,
                    'toolSourceHashes': {}, 'metadataPreflight': 'passed', 'arms': {}}
                for arm in ARMS:
                    source = root / arm; source.mkdir()
                    snapshots = {}
                    for key, rel, snap in [('unitOutput', run.ASSEMBLY, 'unitSnapshot'),
                                           ('integrationOutput', run.INTEGRATION, 'integrationSnapshot')]:
                        assembly = source / rel; assembly.parent.mkdir(parents=True)
                        assembly.write_bytes(b'assembly')
                        for name in record.CLOSURE_NAMES: (assembly.parent / name).write_bytes(b'closure')
                        config = {'runtimeOptions': {'rollForward': 'Disable',
                            'framework': {'name': 'Microsoft.NETCore.App', 'version': '10.0.14'}}}
                        assembly.with_suffix('.runtimeconfig.json').write_text(json.dumps(config))
                        if key == 'unitOutput':
                            allocation = assembly.parent / 'allocation-probe/SharpLink.ReadOwnershipAllocationProbe.runtimeconfig.json'
                            allocation.parent.mkdir(); allocation.write_text(json.dumps(config))
                        snapshots[key] = str(assembly.parent)
                        snapshots[snap] = run.inventory(assembly.parent)
                    snapshots['graphSnapshot'] = run.correctness_graph_inventory(source)
                    manifest['arms'][arm] = {'source': {'path': str(source), 'commit': CONTRACT[arm + 'Source'],
                        'tree': CONTRACT[arm + 'Tree']}, 'correctness': snapshots,
                        'closure': {name: snapshots['unitSnapshot'][name] for name in record.CLOSURE_NAMES}}
                data = manifest['arms']['candidate']; unit = Path(data['correctness']['unitOutput'])
                if mutation == 'sibling_plugin':
                    plugin = Path(data['source']['path']) / 'test/DynamicServices/bin/Release/net10.0/plugin.dll'
                    plugin.parent.mkdir(parents=True); plugin.write_bytes(b'unrecorded plugin')
                if mutation == 'changed_unit': (unit / 'SharpLink.Client.dll').write_bytes(b'changed')
                if mutation == 'changed_integration': (Path(data['correctness']['integrationOutput']) / 'SharpLink.Client.dll').write_bytes(b'changed')
                if mutation == 'runtime':
                    path = unit / 'SharpLink.UnitTests.runtimeconfig.json'
                    path.write_text(path.read_text().replace('10.0.14', '10.0.2'))
                    data['correctness']['unitSnapshot'] = run.inventory(unit)
                if mutation == 'closure': data['closure']['SharpLink.Client.dll'] = 'a' * 64
                if mutation == 'source': data['source']['commit'] = 'a' * 40
                if mutation == 'symlink':
                    (unit / 'linked.dll').symlink_to(unit / 'SharpLink.Client.dll')
                    data['correctness']['unitSnapshot'] = run.inventory(unit)
                with patch.object(run, 'source_inventory', return_value={}):
                    if mutation is None: run.check_prepared(manifest, 'a' * 40)
                    else:
                        with self.subTest(mutation=mutation), self.assertRaises(ValueError): run.check_prepared(manifest, 'a' * 40)

    def test_arm_orchestration_executes_full_corpus_and_one_dynamic_no_build(self):
        for arm in ARMS:
            with tempfile.TemporaryDirectory() as temporary:
                root = Path(temporary); source = root / 'source'; source.mkdir(); output = root / 'output'; output.mkdir()
                raw, ordinary, workers = provenance.read_corpus(TOOL, arm)
                for relative in (run.ASSEMBLY, run.INTEGRATION):
                    path = source / relative; path.parent.mkdir(parents=True); path.write_bytes(b'never-executable-synthetic')
                data = {'source': {'path': str(source)}, 'correctness': {
                    'graphSnapshot': run.correctness_graph_inventory(source),
                    'unitSnapshot': run.inventory((source / run.ASSEMBLY).parent),
                    'integrationSnapshot': run.inventory((source / run.INTEGRATION).parent)}, 'closure': {}}
                discovery = {'schemaVersion': 1, 'tests': [{'uid': uid} for uid in raw]}
                ordinary_report = report(ordinary)
                child_tests = {test['id']: test for test in children_report()['groups'][0]['tests']}
                for test in ordinary_report['groups'][0]['tests']:
                    if test['id'] in child_tests: test.update(child_tests[test['id']])
                commands = []
                def fake_execute(command, cwd, env, timeout, log):
                    command = list(map(str, command)); commands.append(command); log.write_text('retained raw data')
                    dynamic = 'SharpLink.IntegrationTests.dll' in command[1]
                    if '--list-tests' in command:
                        document = {'schemaVersion': 1, 'tests': [{'uid': record.DYNAMIC_UID,
                            'location': {'file': str(source / 'test/SharpLink.IntegrationTests/RuntimeAssemblyIntegrationTests.PhysicalOwnership.cs')}}]} if dynamic else discovery
                        log.write_text(json.dumps(document))
                    else:
                        result = report([record.DYNAMIC_UID], 'SharpLink.IntegrationTests') if dynamic else ordinary_report
                        (log.parent / 'results.tunit-report.json').write_text(json.dumps(result))
                    return 0, False
                value = record.expected_arm(arm)
                with patch.object(run, 'verify_source'), patch.object(run, 'verify_explicit_sources'), \
                     patch.object(run, 'create_short_temp', return_value=Path('/tmp/mock')), \
                     patch.object(run, 'execute', side_effect=fake_execute), contextlib.redirect_stdout(io.StringIO()):
                    run.run_arm(arm, data, Path('/core/dotnet'), output, {}, value)
                self.assertEqual(len(commands), 4)
                self.assertTrue(all('build' not in c for c in commands))
                self.assertEqual(sum('--filter-uid' in c for c in commands), 1)
                self.assertEqual(value['assemblySha256'], data['correctness']['unitSnapshot'])
                self.assertEqual(value['integrationAssemblySha256'], data['correctness']['integrationSnapshot'])

    def test_orchestrator_both_arms_marker_only_after_pass(self):
        for failing_arm in (None, 'baseline', 'candidate'):
            with tempfile.TemporaryDirectory() as temporary:
                root = Path(temporary); prepared = root / 'prepared'; prepared.mkdir(); runtime = root / 'dotnet'; runtime.touch()
                value = valid_record()
                manifest = {'arms': {arm: {'source': {'path': str(root / arm)}} for arm in ARMS}}
                (prepared / 'prepared.json').write_text(json.dumps(manifest))
                attempted = []
                def arm_runner(arm, data, host, output, env, arm_record):
                    attempted.append(arm)
                    if arm == failing_arm: raise GateError('RAW_PRIVATE_ERROR')
                    arm_record.update(value['arms'][arm])
                with patch.object(run, 'tool_identity', return_value='a' * 40), patch.object(run, 'check_prepared'), \
                     patch.object(run, 'host_inventory'), patch.object(run, 'create_short_temp', return_value=Path('/tmp/mock')), \
                     patch.object(run, 'run_arm', side_effect=arm_runner), patch.object(run, 'validate_record'), \
                     contextlib.redirect_stdout(io.StringIO()) as output_text:
                    status = run.run(SimpleNamespace(prepared=prepared, output=root / 'output', runtime_dotnet=runtime))
                self.assertEqual(attempted, list(ARMS)); self.assertNotIn('RAW_PRIVATE_ERROR', output_text.getvalue())
                self.assertEqual(status, 1 if failing_arm else 0)
                self.assertEqual((root / 'output/unit-gate.json').exists(), failing_arm is None)
                if failing_arm: self.assertTrue((root / ('output/' + failing_arm + '/failure.json')).exists())


if __name__ == '__main__': unittest.main()
