"""Pure-Python gate controls. Never launch a CLR, build, or product test."""
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
from unit_gate import provenance, record, run
from unit_gate.scope import explicit_workers, ordinary_uids, verify_explicit_sources
from unit_gate.validate import (GateError, discovery_uids, strict_json, uid_digest, validate_run)

TOOL = Path(__file__).parent
UIDS = ['unit.A', 'unit.B']


def report():
    summary = dict(total=2, passed=2, failed=0, skipped=0, cancelled=0, timedOut=0, flaky=0)
    return {'schemaVersion': 1, 'publicationGeneration': 'a' * 32, 'assemblyName': 'SharpLink.UnitTests',
        'tunitVersion': '1.73.19.0', 'runtimeVersion': '.NET 10.0.14', 'summary': dict(summary),
        'groups': [{'summary': dict(summary), 'tests': [{'id': u, 'status': 'passed'} for u in UIDS]}]}


def valid_record():
    value = record.expected_record('a' * 40)
    value['assemblySha256'] = {name: 'b' * 64 for name in record.REQUIRED_ASSEMBLIES}
    return value


def children_report():
    tests = []
    for scenario in record.SCENARIOS:
        uid = f'SharpLink.UnitTests.Runtime.ReadOwnershipPipeReaderLifetimeTests.1.1.{scenario}(System.Threading.CancellationToken).1.1.0'
        witness = dict(scenario=scenario, runtime='10.0.14', architecture='X64', executed=1, passed=True)
        tests.append({'id': uid, 'status': 'passed', 'output': 'READ_OWNERSHIP_ALLOCATION_VERIFIED|' + json.dumps(witness)})
    return {'groups': [{'tests': tests}]}


class UnitGateTests(unittest.TestCase):
    def test_exact_actual_frozen_census(self):
        raw, ordinary, workers = provenance.read_corpus(TOOL)
        self.assertEqual((len(raw), len(ordinary), len(workers)), (2354, 2349, 5))
        self.assertEqual(len({w['type_name'] for w in workers}), 4)
        self.assertEqual(uid_digest(raw), '9677464292100122e2c22f84a6d244ad432656dea1f742ebdefff21d063d5d4c')
        self.assertEqual(uid_digest(ordinary), '8327ccb3f560a78babccdbde4a696b3d12148b30c183c4e45599429cb2af0307')

    def test_result_pass(self):
        self.assertEqual(validate_run(UIDS, report(), 0)['passed'], 2)

    def test_missing_extra_duplicate_wrong_uid(self):
        for mutation in ('missing', 'extra', 'duplicate', 'wrong'):
            value = report()
            tests = value['groups'][0]['tests']
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

    def test_result_summary_and_identity_mutations(self):
        for key, bad in [('schemaVersion', 2), ('assemblyName', 'other'), ('tunitVersion', 'other'),
                         ('publicationGeneration', 'bad')]:
            value = report(); value[key] = bad
            with self.subTest(key=key), self.assertRaises(GateError): validate_run(UIDS, value, 0)
        for key in report()['summary']:
            value = report(); value['summary'][key] += 1
            with self.subTest(key=key), self.assertRaises(GateError): validate_run(UIDS, value, 0)

    def test_discovery_duplicates_missing_schema_and_json_duplicate(self):
        for value in ({}, {'schemaVersion': 1, 'tests': []},
                      {'schemaVersion': 1, 'tests': [{'uid': 'same'}, {'uid': 'same'}]}):
            with self.assertRaises(GateError): discovery_uids(value)
        with self.assertRaises(GateError): strict_json('{"x":1,"x":2}')

    def test_wrong_expected44_count_identity_digest_and_duplicate(self):
        for mutation in ('missing', 'extra', 'wrong', 'digest', 'duplicate'):
            with tempfile.TemporaryDirectory() as temporary:
                root = Path(temporary)
                for name in ('corpus.json', 'expected-base-uids.json', 'new-uids.json', 'explicit-workers.json'):
                    shutil.copyfile(TOOL / name, root / name)
                path = root / 'new-uids.json'; values = json.loads(path.read_text())
                if mutation == 'missing': values.pop()
                if mutation == 'extra': values.append(values[0] + '-extra')
                if mutation == 'wrong': values[0] = 'Some.Other.Class.UID'
                if mutation == 'digest': values[0] += '-wrong'
                if mutation == 'duplicate': values[0] = values[1]
                path.write_text(json.dumps(values))
                with self.subTest(mutation=mutation), self.assertRaises(GateError): provenance.read_corpus(root)

    def test_new_class_distribution_rejected_even_with_matching_uid_digests(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            for name in ('corpus.json', 'expected-base-uids.json', 'new-uids.json', 'explicit-workers.json'):
                shutil.copyfile(TOOL / name, root / name)
            new = json.loads((root / 'new-uids.json').read_text())
            # Keep 44 distinct UIDs, but shift one case to a different allowed class.
            first, second = tuple(provenance.NEW_CLASSES)[:2]
            index = next(i for i, uid in enumerate(new) if uid.startswith(first))
            new[index] = second + 'shifted.synthetic.UID'
            base = json.loads((root / 'expected-base-uids.json').read_text())
            raw = sorted(base + new)
            ordinary = ordinary_uids(raw, explicit_workers(root))
            manifest = json.loads((root / 'corpus.json').read_text())
            manifest.update(new_uid_sha256=uid_digest(new), raw_uid_sha256=uid_digest(raw),
                            ordinary_uid_sha256=uid_digest(ordinary))
            (root / 'corpus.json').write_text(json.dumps(manifest))
            (root / 'new-uids.json').write_text(json.dumps(new))
            with self.assertRaises(GateError): provenance.read_corpus(root)

    def test_explicit_manifest_cannot_be_reduced_or_replaced(self):
        workers = explicit_workers(TOOL)
        with self.assertRaises(GateError): ordinary_uids(['ordinary'], workers)
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            for name in ('corpus.json', 'expected-base-uids.json', 'new-uids.json', 'explicit-workers.json'):
                shutil.copyfile(TOOL / name, root / name)
            manifest = workers[:-1]
            (root / 'explicit-workers.json').write_text(json.dumps(manifest))
            with self.assertRaises(GateError): provenance.read_corpus(root)

    def test_wrong_source_tree_dirty_source(self):
        class Result:
            returncode = 0
            stderr = ''
            def __init__(self, text): self.stdout = text
        for outputs in [('a' * 40,), (provenance.SOURCE, 'b' * 40),
                        (provenance.SOURCE, provenance.TREE, ' M code.cs')]:
            with patch.object(provenance.subprocess, 'run', side_effect=[Result(x) for x in outputs]):
                with self.assertRaises(GateError): provenance.verify_source(Path('/unused'))

    def test_record_identity_count_digest_runtime_and_status(self):
        value = valid_record(); self.assertIs(record.validate_record(value, 'a' * 40), value)
        for key, bad in [('candidateSource', 'b' * 40), ('candidateTree', 'b' * 40),
            ('toolRevision', 'bad'), ('runtime', '10.0.2'), ('sdk', '10.0.400'), ('status', 'failed'),
            ('rawCount', 2353), ('ordinaryCount', 2348), ('executedCount', 2348), ('passedCount', True),
            ('rawUidSha256', 'c' * 64), ('ordinaryUidSha256', 'c' * 64), ('executedUidSha256', 'c' * 64),
            ('skippedCount', 1), ('cancelledCount', 1), ('timedOutCount', 1), ('failedCount', 1), ('flakyCount', 1)]:
            value = valid_record(); value[key] = bad
            with self.subTest(key=key), self.assertRaises(GateError): record.validate_record(value)
        with self.assertRaises(GateError): record.validate_record(valid_record(), 'c' * 40)

    def test_record_closure_missing_or_malformed(self):
        for mutation in ('missing', 'digest', 'path', 'extra_key'):
            value = valid_record()
            if mutation == 'missing': value['assemblySha256'].pop('SharpLink.UnitTests.dll')
            if mutation == 'digest': value['assemblySha256']['SharpLink.UnitTests.dll'] = 'short'
            if mutation == 'path': value['assemblySha256']['../outside.dll'] = 'c' * 64
            if mutation == 'extra_key': value['unexpected'] = True
            with self.subTest(mutation=mutation), self.assertRaises(GateError): record.validate_record(value)

    def test_actual_runtime_and_allocation_child_witnesses(self):
        run.require_runtime(report())
        for runtime in ('.NET 10.0.2', '.NET 10.0.13', None):
            value = report(); value['runtimeVersion'] = runtime
            with self.assertRaises(GateError): run.require_runtime(value)
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

    def test_runtime_configs_fixed_exactly(self):
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / 'runtimeconfig.json'
            path.write_text(json.dumps({'runtimeOptions': {'framework': {'name': 'Microsoft.NETCore.App', 'version': '10.0.0'}}}))
            run.pin_runtime_config(path)
            result = json.loads(path.read_text())['runtimeOptions']
            self.assertEqual(result['framework']['version'], '10.0.14')
            self.assertEqual(result['rollForward'], 'Disable')
            path.write_text(json.dumps({'runtimeOptions': {'framework': {'name': 'Unknown', 'version': '10.0.0'}}}))
            with self.assertRaises(GateError): run.pin_runtime_config(path)

    def test_private_environment_no_github_reporter_or_old_filter(self):
        env = run.private_test_environment({'GITHUB_ACTIONS': 'true', 'TUNIT_FILTER': 'skip',
            'TESTINGPLATFORM_TEST_FILTER': 'skip', 'PATH': '/usr/bin'}, Path('/private/dotnet'), Path('/raw'))
        self.assertNotIn('GITHUB_ACTIONS', env); self.assertNotIn('TUNIT_FILTER', env)
        self.assertNotIn('TESTINGPLATFORM_TEST_FILTER', env)
        self.assertEqual(env['DOTNET_HOST_PATH'], '/private/dotnet')
        self.assertEqual(env['DOTNET_ROLL_FORWARD'], 'Disable')

    def test_host_inventory_success_and_wrong_sdk_runtime(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary); sdk = root / 'sdk/dotnet'; runtime = root / 'runtime/dotnet'
            sdk.parent.mkdir(); runtime.parent.mkdir(); sdk.touch(); runtime.touch()
            lines = ['10.0.102', f'10.0.102 [{sdk.parent / "sdk"}]',
                '\n'.join(f'{n} 10.0.2 [{sdk.parent / "shared" / n}]' for n in ('Microsoft.AspNetCore.App', 'Microsoft.NETCore.App')),
                '', '\n'.join(f'{n} 10.0.14 [{runtime.parent / "shared" / n}]' for n in ('Microsoft.AspNetCore.App', 'Microsoft.NETCore.App'))]
            for wrong_index in (None, 0, 2, 3, 4):
                entries = iter([v if index != wrong_index else 'wrong' for index, v in enumerate(lines)])
                def fake_execute(command, cwd, env, timeout, output):
                    output.write_text(next(entries)); return 0, False
                with patch.object(run, 'execute', side_effect=fake_execute):
                    if wrong_index is None: run.host_inventory(sdk, runtime, root, {}, root)
                    else:
                        with self.assertRaises(GateError): run.host_inventory(sdk, runtime, root, {}, root)

    def test_orchestrator_success_only_marker_and_failure_retention_without_clr(self):
        raw, ordinary, workers = provenance.read_corpus(TOOL)
        reasons = {'missing_uid': 'RESULT_UID', 'skipped': 'RESULT_TEST_STATUS',
            'runtime': 'RESULT_RUNTIME', 'timeout': 'EXECUTION_TIMEOUT', 'build_failure': 'BUILD',
            'missing_report': 'RESULT_REPORT_DISCOVERY', 'malformed_report': 'RESULT_REPORT_PARSE',
            'child': 'RESULT_CHILD', 'schema': 'RESULT_SCHEMA', 'groups': 'RESULT_SCHEMA',
            'counters': 'RESULT_COUNTERS', 'exit': 'RESULT_EXIT'}
        for mutation in ('pass', *reasons):
            with tempfile.TemporaryDirectory() as temporary:
                root = Path(temporary); source = root / 'source'; source.mkdir(); output = root / 'raw'
                assembly = source / run.ASSEMBLY
                discovery = {'schemaVersion': 1, 'tests': [{'uid': uid} for uid in raw]}
                by_uid = {test['uid']: test for test in discovery['tests']}
                for worker in workers:
                    path = source / worker['source_path']; path.parent.mkdir(parents=True, exist_ok=True)
                    path.write_text('[Explicit]\npublic sealed class ' + worker['type_name'] + '\n')
                    by_uid[worker['uid']].update(type={'namespace': worker['namespace'], 'typeName': worker['type_name'],
                        'methodName': worker['method_name']}, location={'file': str(path)})
                value = report(); summary = dict(total=len(ordinary), passed=len(ordinary), failed=0,
                    skipped=0, cancelled=0, timedOut=0, flaky=0)
                tests = [{'id': uid, 'status': 'passed'} for uid in ordinary]
                children = {test['id']: test for test in children_report()['groups'][0]['tests']}
                for test in tests:
                    if test['id'] in children: test.update(children[test['id']])
                value['summary'] = summary
                value['groups'] = [{'summary': dict(summary), 'tests': tests}]
                if mutation == 'schema': value['schemaVersion'] = 'invalid'
                if mutation == 'groups': value['groups'] = [None]
                if mutation == 'counters': value['summary']['total'] += 1
                if mutation == 'missing_uid': tests.pop()
                if mutation == 'skipped': tests[0]['status'] = 'skipped'
                if mutation == 'runtime': value['runtimeVersion'] = '.NET 10.0.2'
                if mutation == 'child':
                    for test in tests: test.pop('output', None)

                def fake_execute(command, cwd, env, timeout, log):
                    log.write_text('RAW_DETAIL_SHOULD_REMAIN_LOCAL')
                    if 'build' in command:
                        if mutation == 'build_failure': return 1, False
                        for name in record.REQUIRED_ASSEMBLIES:
                            path = assembly.parent / name; path.parent.mkdir(parents=True, exist_ok=True)
                            path.write_bytes(b'synthetic fixture, never executable')
                        for path in (assembly.with_suffix('.runtimeconfig.json'), assembly.parent /
                                'allocation-probe/SharpLink.ReadOwnershipAllocationProbe.runtimeconfig.json'):
                            path.write_text(json.dumps({'runtimeOptions': {'framework': {'name': 'Microsoft.NETCore.App', 'version': '10.0.0'}}}))
                    elif '--list-tests' in command:
                        log.write_text(json.dumps(discovery))
                    else:
                        if mutation != 'missing_report':
                            (output / 'results.tunit-report.json').write_text(
                                '{malformed RAW_SECRET' if mutation == 'malformed_report' else json.dumps(value))
                        if mutation == 'timeout': return -9, True
                        if mutation == 'exit': return 2, False
                    return 0, False

                args = SimpleNamespace(source=source, output=output, dotnet=Path('/sdk/dotnet'), runtime_dotnet=Path('/runtime/dotnet'))
                stdout = io.StringIO()
                with patch.object(run, 'execute', side_effect=fake_execute), patch.object(run, 'host_inventory'), \
                     patch.object(run, 'verify_source'), patch.object(run, 'tool_identity', return_value='a' * 40), \
                     patch.object(run, 'create_short_temp', return_value=Path('/tmp/unit-fixture')), \
                     contextlib.redirect_stdout(stdout):
                    result = run.run(args)
                self.assertNotIn('RAW_DETAIL_SHOULD_REMAIN_LOCAL', stdout.getvalue())
                self.assertTrue((output / 'build.log').exists())
                if mutation == 'pass':
                    self.assertEqual(result, 0)
                    record.validate_record(json.loads((output / 'unit-gate.json').read_text()), 'a' * 40)
                else:
                    self.assertEqual(result, 1)
                    self.assertFalse((output / 'unit-gate.json').exists())
                    self.assertTrue((output / 'failure.json').exists())
                    self.assertIn('UNIT_GATE_FAIL reason=' + reasons[mutation], stdout.getvalue())
                    self.assertNotIn('RAW_SECRET', stdout.getvalue())


if __name__ == '__main__': unittest.main()
