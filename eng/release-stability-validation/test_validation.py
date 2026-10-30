#!/usr/bin/env python3
"""Offline fixtures only: no dotnet, MTP, network, or soak execution."""
import copy
import json
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch
import validate as v


class RequestTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name) / 'tools'
        self.source = Path(self.temp.name) / 'source'
        self.root.mkdir()
        self.git('init', '-q')
        self.git('config', 'user.name', 'Offline Fixture')
        self.git('config', 'user.email', 'fixture@example.invalid')
        (self.root / 'global.json').write_text('{}')
        self.commit('candidate')
        self.base = self.git('rev-parse', 'HEAD')
        self.tree = self.git('rev-parse', 'HEAD^{tree}')
        subprocess.run(['git', 'clone', '-q', str(self.root), str(self.source)], check=True)
        for name in v.TOOLS:
            path = self.root / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text('fixture tool')
        self.commit('tools')
        self.tools = self.git('rev-parse', 'HEAD')
        self.request = {'source_sha': self.base, 'source_tree': self.tree, 'tools_sha': self.tools}
        self.request_commit()
        self.patch_source = patch.object(v, 'SOURCE', self.base)
        self.patch_tree = patch.object(v, 'TREE', self.tree)
        self.patch_source.start()
        self.patch_tree.start()
        self.addCleanup(self.patch_source.stop)
        self.addCleanup(self.patch_tree.stop)

    def git(self, *args):
        return v.git(self.root, *args)

    def commit(self, message):
        self.git('add', '.')
        self.git('commit', '-qm', message)

    def request_commit(self):
        (self.root / v.REQUEST).write_text(json.dumps(self.request))
        self.commit('single request')

    def check(self):
        return v.validate(self.root, self.source, self.git('rev-parse', 'HEAD'))

    def amend(self):
        (self.root / v.REQUEST).write_text(json.dumps(self.request))
        self.git('add', '.')
        self.git('commit', '-qm', 'mutated request', '--amend')

    def test_valid_request(self):
        result = self.check()
        self.assertEqual(result['actual_source_sha'], self.base)
        self.assertIn('not covered', result['six_hour_memory_window'])

    def test_wrong_source_sha(self):
        self.request['source_sha'] = '0' * 40
        self.amend()
        with self.assertRaises(ValueError): self.check()

    def test_wrong_source_tree(self):
        self.request['source_tree'] = '0' * 40
        self.amend()
        with self.assertRaises(ValueError): self.check()

    def test_unknown_request_key(self):
        self.request['duration'] = 5
        self.amend()
        with self.assertRaises(ValueError): self.check()

    def test_missing_request_key(self):
        del self.request['tools_sha']
        self.amend()
        with self.assertRaises(ValueError): self.check()

    def test_wrong_reviewed_tools_head(self):
        self.request['tools_sha'] = self.base
        self.amend()
        with self.assertRaises(ValueError): self.check()

    def test_event_identity_mismatch(self):
        with self.assertRaises(ValueError):
            v.validate(self.root, self.source, self.tools)

    def test_request_alters_other_file(self):
        (self.root / 'global.json').write_text('{"changed":true}')
        self.amend()
        with self.assertRaises(ValueError): self.check()

    def test_repeat_request(self):
        self.request['tools_sha'] = self.git('rev-parse', 'HEAD')
        self.request_commit()
        with self.assertRaises(ValueError): self.check()

    def test_dirty_source(self):
        (self.source / 'global.json').write_text('dirty')
        with self.assertRaises(ValueError): self.check()

    def test_wrong_actual_source(self):
        self.source = self.root
        with self.assertRaises(ValueError): self.check()

    def test_unreviewed_tool_file(self):
        self.git('reset', '--hard', self.tools)
        (self.root / 'extra.py').write_text('unreviewed')
        self.commit('extra tool')
        self.request['tools_sha'] = self.git('rev-parse', 'HEAD')
        self.request_commit()
        with self.assertRaises(ValueError): self.check()

    def test_tool_product_modification(self):
        self.git('reset', '--hard', self.tools)
        (self.root / 'global.json').write_text('altered sdk')
        self.commit('altered product')
        self.request['tools_sha'] = self.git('rev-parse', 'HEAD')
        self.request_commit()
        with self.assertRaises(ValueError): self.check()

    def test_inventory_missing_build_is_not_runtime_pass(self):
        with self.assertRaises(ValueError): v.inventory(self.source)

    def test_inventory_hashes_binary_and_runtimeconfig(self):
        base = self.source / 'test/SharpLink.ChaosTests/bin/Release/net10.0'
        base.mkdir(parents=True)
        for name in next(iter(v.required_files('tcp').values())):
            (base / name).write_bytes(b'abc')
        (base / 'ignored.txt').write_text('ignored')
        result = v.inventory(self.source)
        self.assertEqual(len(result), len(next(iter(v.required_files('tcp').values()))))
        self.assertEqual(set(result.values()), {'ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad'})


import hashlib
import importlib.util
import os
import sys

spec = importlib.util.spec_from_file_location('recording_host', Path(__file__).with_name('dotnet.py'))
host = importlib.util.module_from_spec(spec)
spec.loader.exec_module(host)


def fill_binary_fixture(source, kind):
    for directory, names in v.required_files(kind).items():
        root = source / directory
        root.mkdir(parents=True, exist_ok=True)
        for name in names:
            (root / name).write_bytes(b'abc')


def chaos_fixture():
    return dict(Status='Passed', Phase='Completed', IsFinal=True, ExitCode=0, Commit=v.SOURCE,
        WorkingTreeDirty=False, DurationSeconds=600, CheckpointIntervalSeconds=120,
        RestartIntervalSeconds=60, Concurrency=32, Transport='Tcp', DumpOnFailure=True,
        StopOnUnexpectedFailure=True, StartedUtc='2026-10-30T00:00:00Z', TimestampUtc='2026-10-30T00:10:01Z',
        ActualElapsedSeconds=599.8, UnexpectedFailures=0, UnobservedTaskExceptions=0,
        TerminalFailure=None, ClientErrors=[], ServerErrors=[], Success=5, ExpectedFailures=1, RestartCount=1,
        OperationAttempts={key: 1 for key in ['Unary', 'ServerStreamingEarlyBreak', 'ClientStreaming', 'Cancellation', 'OneWay', 'DuplexStreaming']},
        Drain={'Drained': True, 'Metrics': dict.fromkeys(v.METRICS, 0), 'ActiveCallBreakdown': {}},
        FinalMetrics=dict.fromkeys(v.METRICS, 0), ActiveCallBreakdown={}, Runtime='10.0.2',
        MemorySamples=[{'ElapsedSeconds':599.8}], ServerStops=[{'Reason':'FinalStop','ActiveCallsAfterStop':0}])


def abi_fixture():
    discovery = {'tests': [{'uid': f'{i}-{j}', 'type': {'methodName': method}}
                          for i, method in enumerate(v.METHODS) for j in range(5 if i == 2 else 1)]}
    summary = {'commit': v.SOURCE, 'duration_seconds': '605', 'rounds': '1', 'test_processes': '9',
               'client_registration_replacements': '100', 'route_publication_before_old_drain': '1',
               'api4_stream_exits': '5', 'api4_stream_exit_modes': v.MODES,
               'registration_rejections': '1', 'framework_reference_unloads': '1'}
    records = [{'kind':'test','uid':uid,'exit_code':0,'actual_runtime':'10.0.2'} for uid in v.abi_uids(discovery)]
    return summary, discovery, records


class EvidenceTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.source = Path(self.temp.name)

    def test_dynamic_actual_module_in_inventory(self):
        fill_binary_fixture(self.source, 'abi')
        files = v.inventory(self.source, 'abi')
        for name in ['Contracts', 'Services']:
            self.assertIn(f'test/SharpLink.DynamicServices/bin/Release/net10.0/SharpLink.DynamicPlugin.{name}.dll', files)
        self.assertEqual(len(files), sum(len(names) for names in v.required_files('abi').values()))

    def test_missing_dynamic_services_dll_rejected(self):
        fill_binary_fixture(self.source, 'abi')
        (self.source/'test/SharpLink.DynamicServices/bin/Release/net10.0/SharpLink.DynamicPlugin.Services.dll').unlink()
        with self.assertRaises(ValueError): v.inventory(self.source, 'abi')

    def test_empty_dynamic_dll_rejected(self):
        fill_binary_fixture(self.source, 'abi')
        (self.source/'test/SharpLink.DynamicServices/bin/Release/net10.0/SharpLink.DynamicPlugin.Contracts.dll').write_bytes(b'')
        with self.assertRaises(ValueError): v.inventory(self.source, 'abi')

    def test_runtimeconfig_missing_rejected(self):
        fill_binary_fixture(self.source, 'tcp')
        (self.source/'test/SharpLink.ChaosTests/bin/Release/net10.0/SharpLink.ChaosTests.runtimeconfig.json').unlink()
        with self.assertRaises(ValueError): v.inventory(self.source)

    def test_hash_mutation_rejected(self):
        fill_binary_fixture(self.source, 'abi')
        before = v.inventory(self.source, 'abi')
        (self.source/'test/SharpLink.DynamicServices/bin/Release/net10.0/SharpLink.DynamicPlugin.Services.dll').write_bytes(b'mutated')
        with self.assertRaises(ValueError): v.assert_same(before, v.inventory(self.source, 'abi'))

    def test_empty_hash_sets_rejected(self):
        with self.assertRaises(ValueError): v.assert_same({}, {})

    def test_report_missing_rejected(self):
        with self.assertRaises(ValueError): v.nonempty(self.source/'report.json')

    def test_chaos_complete_original_timer_not_redefined(self):
        result = v.verify_chaos(chaos_fixture(), 'tcp')
        self.assertEqual(result['actual_elapsed_seconds'], 599.8)
        self.assertEqual(result['six_hour_memory_window'], 'not covered')

    def test_chaos_partial_not_final_rejected(self):
        report = chaos_fixture(); report['IsFinal'] = False
        with self.assertRaises(ValueError): v.verify_chaos(report, 'tcp')

    def test_chaos_short_duration_rejected(self):
        report = chaos_fixture(); report['DurationSeconds'] = 120
        with self.assertRaises(ValueError): v.verify_chaos(report, 'tcp')

    def test_chaos_short_actual_timer_rejected(self):
        report = chaos_fixture(); report['TimestampUtc'] = '2026-10-30T00:01:00Z'
        with self.assertRaises(ValueError): v.verify_chaos(report, 'tcp')

    def test_chaos_nonzero_resource_rejected(self):
        report = chaos_fixture(); report['FinalMetrics']['sharplink.calls.active'] = 1
        with self.assertRaises(ValueError): v.verify_chaos(report, 'tcp')

    def test_chaos_missing_runtime_rejected(self):
        report = chaos_fixture(); report['Runtime'] = ''
        with self.assertRaises(ValueError): v.verify_chaos(report, 'tcp')

    def test_abi_complete_last_round(self):
        self.assertEqual(v.verify_abi(*abi_fixture())['test_processes'], 9)

    def test_abi_incomplete_last_round_rejected(self):
        summary, discovery, records = abi_fixture()
        with self.assertRaises(ValueError): v.verify_abi(summary, discovery, records[:-1])

    def test_abi_missing_runtime_rejected(self):
        summary, discovery, records = abi_fixture(); records[-1].pop('actual_runtime')
        with self.assertRaises(ValueError): v.verify_abi(summary, discovery, records)

    def test_abi_short_duration_rejected(self):
        summary, discovery, records = abi_fixture(); summary['duration_seconds'] = '599'
        with self.assertRaises(ValueError): v.verify_abi(summary, discovery, records)

    def test_abi_count_mismatch_rejected(self):
        summary, discovery, records = abi_fixture(); summary['api4_stream_exits'] = '4'
        with self.assertRaises(ValueError): v.verify_abi(summary, discovery, records)

    def test_abi_missing_discovery_case_rejected(self):
        summary, discovery, records = abi_fixture(); discovery['tests'].pop()
        with self.assertRaises(ValueError): v.verify_abi(summary, discovery, records)

    def test_sdk_runtime_not_test_runtime(self):
        trace = self.source/'host.log'
        trace.write_text("Executing as a framework-dependent app as per config file [/sdk/dotnet.runtimeconfig.json]\nCoreCLR path = '/sdk/shared/Microsoft.NETCore.App/10.0.2/libcoreclr.so'\n")
        with self.assertRaises(ValueError): host.actual_test_runtime(trace, self.source)

    def test_actual_application_runtime(self):
        trace = self.source/'host.log'
        marker=self.source/'test/SharpLink.IntegrationTests/bin/Release/net10.0/SharpLink.IntegrationTests.runtimeconfig.json'
        trace.write_text(f"Executing as a framework-dependent app as per config file [{marker}]\nCoreCLR path = '/sdk/shared/Microsoft.NETCore.App/10.0.2/libcoreclr.so'\n")
        self.assertEqual(host.actual_test_runtime(trace, self.source), '10.0.2')

    def test_missing_raw_host_runtime_trace_rejected(self):
        with self.assertRaises(ValueError): host.actual_test_runtime(self.source/'absent.log', self.source)

    def test_chaos_missing_final_stop_rejected(self):
        report = chaos_fixture(); report['ServerStops'][-1]['Reason'] = 'Restart'
        with self.assertRaises(ValueError): v.verify_chaos(report,'tcp')

    def test_complete_valid_chaos_and_missing_report_fail_closed(self):
        fill_binary_fixture(self.source,'tcp')
        evidence = self.source/'artifacts/stability'; evidence.mkdir(parents=True)
        before = {'source_sha':v.SOURCE,'source_tree':v.TREE,'files':v.inventory(self.source,'tcp')}
        (evidence/'built-before.json').write_text(json.dumps(before))
        digest = hashlib.sha256((evidence/'built-before.json').read_bytes()).hexdigest()
        config = {'source':str(self.source),'kind':'tcp','dotnet':'/original/dotnet'}
        (evidence/'host-config.json').write_text(json.dumps(config))
        (evidence/'dotnet-info.txt').write_text('installed inventory')
        args = ['run','-c','Release','--no-build','--project','test/SharpLink.ChaosTests','--','--duration','10m','--transport','tcp','--shm-name','sharplink-release-soak','--concurrency','32','--restart-interval-seconds','60','--checkpoint-interval','2m','--dump-on-failure','true','--stop-on-unexpected','true','--json-output','artifacts/chaos/release-10m-tcp.json']
        rows=[{'kind':'build','argv':['build','test/SharpLink.ChaosTests/SharpLink.ChaosTests.csproj','-c','Release','-v','minimal']},
              {'kind':'chaos','argv':args,'binary_manifest_sha256':digest}]
        for i,row in enumerate(rows): row.update(sequence=i,uid=None,cwd=str(self.source),dotnet='/original/dotnet',exit_code=0)
        (evidence/'commands.jsonl').write_text('\n'.join(json.dumps(row) for row in rows)+'\n')
        report=self.source/'artifacts/chaos/release-10m-tcp.json'; report.parent.mkdir(parents=True)
        report.write_text(json.dumps(chaos_fixture()))
        with patch.object(v,'source_identity',return_value={'source_sha':v.SOURCE,'source_tree':v.TREE}):
            self.assertTrue(v.complete(self.source,'tcp',evidence)['verified'])
            report.unlink()
            with self.assertRaises(ValueError): v.complete(self.source,'tcp',evidence)

    def test_abi_exact_one_success_per_process_log(self):
        v.verify_test_log('total: 1\nsucceeded: 1\nskipped: 0\n' * 9, 9)

    def test_abi_skipped_case_log_rejected(self):
        with self.assertRaises(ValueError): v.verify_test_log('total: 1\nsucceeded: 0\nskipped: 1\n', 1)

    def test_abi_truncated_final_log_rejected(self):
        with self.assertRaises(ValueError): v.verify_test_log('total: 1\nsucceeded: 1\nskipped: 0\n' * 8, 9)

    def test_abi_duplicate_summary_rejected(self):
        with self.assertRaises(ValueError): v.parse_summary('rounds=1\nrounds=2\n')


class ForwardTests(unittest.TestCase):
    def setUp(self):
        self.temp=tempfile.TemporaryDirectory(); self.addCleanup(self.temp.cleanup)
        self.source=Path(self.temp.name).resolve()
        self.evidence=self.source/'artifacts/stability'; self.evidence.mkdir(parents=True)
        self.fake=self.source/'fake-dotnet'
        self.fake.write_text('#!/usr/bin/env python3\nimport os,sys,json\nfrom pathlib import Path\nprint("FAKE_STDOUT")\nprint("FAKE_STDERR",file=sys.stderr)\nPath(os.environ["FAKE_CAPTURE"]).write_text(json.dumps({"argv":sys.argv[1:],"cwd":os.getcwd(),"path":os.environ["PATH"],"custom":os.environ["FAKE_CUSTOM"]}))\nif os.environ.get("FAKE_MUTATE"): Path(os.environ["FAKE_MUTATE"]).write_bytes(b"mutated")\nif os.environ.get("FAKE_LEDGER_DIR"): Path(os.environ["FAKE_LEDGER_DIR"]).mkdir()\nsys.exit(int(os.environ.get("FAKE_EXIT","0")))\n')
        self.fake.chmod(0o755)
        self.config={'source':str(self.source),'evidence':str(self.evidence),'kind':'tcp',
                     'dotnet':str(self.fake),'dotnet_sha256':hashlib.sha256(self.fake.read_bytes()).hexdigest(),
                     'original_path':os.environ['PATH']}
        self.configpath=self.evidence/'host-config.json'; self.configpath.write_text(json.dumps(self.config))
        self.capture=self.source/'captured.json'
        self.env={**os.environ,'STABILITY_CONFIG':str(self.configpath),'FAKE_CAPTURE':str(self.capture),'FAKE_CUSTOM':'preserved value'}

    def invoke(self, args, extra=None):
        return subprocess.run([sys.executable,str(Path(__file__).with_name('dotnet.py').resolve()),*args],
                              cwd=self.source,env={**self.env,**(extra or {})},text=True,capture_output=True)

    def test_original_argv_cwd_env_stdio(self):
        args=['--arbitrary','space arg','--literal=$x']
        result=self.invoke(args)
        self.assertEqual(result.returncode,0)
        self.assertEqual(result.stdout,'FAKE_STDOUT\n'); self.assertEqual(result.stderr,'FAKE_STDERR\n')
        self.assertEqual(json.loads(self.capture.read_text()),{'argv':args,'cwd':str(self.source),'path':os.environ['PATH'],'custom':'preserved value'})

    def test_workflow_symlink_entry_resolves_reviewed_imports(self):
        shim=self.source/'shim'; shim.mkdir()
        (shim/'dotnet').symlink_to(Path(__file__).with_name('dotnet.py').resolve())
        result=subprocess.run([str(shim/'dotnet'),'--info'],cwd=self.source,env=self.env,text=True,capture_output=True)
        self.assertEqual(result.returncode,0,result.stderr)
        self.assertEqual(result.stdout,'FAKE_STDOUT\n')

    def test_original_nonzero_exit_preserved(self):
        result=self.invoke(['--info'],{'FAKE_EXIT':'7'})
        self.assertEqual(result.returncode,7)
        self.assertIn('FAKE_STDERR',result.stderr)

    def test_recording_failure_is_tool_failure(self):
        (self.evidence/'commands.jsonl').mkdir()
        result=self.invoke(['--info'])
        # Unwritable ledger may prevent recording before launch, but never pass.
        self.assertEqual(result.returncode,86)

    def test_original_failure_survives_recording_failure(self):
        result=self.invoke(['--info'],{'FAKE_EXIT':'7','FAKE_LEDGER_DIR':str(self.evidence/'commands.jsonl')})
        self.assertEqual(result.returncode,7)
        self.assertIn('Cannot persist command ledger',result.stderr)

    def test_original_success_with_recording_failure_is_not_pass(self):
        result=self.invoke(['--info'],{'FAKE_LEDGER_DIR':str(self.evidence/'commands.jsonl')})
        self.assertEqual(result.returncode,86)
        self.assertTrue(self.capture.exists())

    def test_invalid_config_is_tool_failure_without_execution(self):
        self.configpath.write_text('bad json')
        result=self.invoke(['--info'])
        self.assertEqual(result.returncode,86); self.assertFalse(self.capture.exists())

    def test_original_dotnet_missing_is_tool_failure(self):
        self.fake.unlink()
        result=self.invoke(['--info'])
        self.assertEqual(result.returncode,86); self.assertFalse(self.capture.exists())

    def test_exact_original_chaos_argv_classified(self):
        args=['run','-c','Release','--no-build','--project','test/SharpLink.ChaosTests','--','--duration','10m','--transport','tcp','--shm-name','sharplink-release-soak','--concurrency','32','--restart-interval-seconds','60','--checkpoint-interval','2m','--dump-on-failure','true','--stop-on-unexpected','true','--json-output','artifacts/chaos/release-10m-tcp.json']
        self.assertEqual(host.classify(args,self.config),('chaos',None))
        args[args.index('10m')]='2m'
        self.assertEqual(host.classify(args,self.config),('other',None))

    def test_recognized_run_missing_binaries_does_not_execute(self):
        config={**self.config,'kind':'abi'}
        args=['run','-c','Release','--no-build','--no-restore','--project',str(self.source/'test/SharpLink.IntegrationTests/SharpLink.IntegrationTests.csproj'),'--','--list-tests','json']
        with patch.dict(os.environ,self.env), patch.object(host.v,'source_identity',return_value={}), patch('os.getcwd',return_value=str(self.source)), patch.object(Path,'cwd',return_value=self.source):
            self.assertEqual(host.forward(args,config),86)
        self.assertFalse(self.capture.exists())

    def test_recognized_discovery_records_actual_pre_and_post_bytes(self):
        fill_binary_fixture(self.source,'abi')
        config={**self.config,'kind':'abi'}
        args=['run','-c','Release','--no-build','--no-restore','--project',str(self.source/'test/SharpLink.IntegrationTests/SharpLink.IntegrationTests.csproj'),'--','--list-tests','json']
        with patch.dict(os.environ,self.env), patch.object(host.v,'source_identity',return_value={'source_sha':v.SOURCE,'source_tree':v.TREE}), patch('os.getcwd',return_value=str(self.source)), patch.object(Path,'cwd',return_value=self.source):
            self.assertEqual(host.forward(args,config),0)
        self.assertEqual(json.loads((self.evidence/'built-before.json').read_text()),json.loads((self.evidence/'built-after.json').read_text()))
        row=json.loads((self.evidence/'commands.jsonl').read_text())
        self.assertEqual(row['argv'],args); self.assertEqual(row['kind'],'discovery')
        self.assertEqual(row['exit_code'],0); self.assertNotIn('tool_error',row)

    def test_recognized_run_hash_mutation_is_tool_failure(self):
        fill_binary_fixture(self.source,'abi')
        config={**self.config,'kind':'abi'}
        args=['run','-c','Release','--no-build','--no-restore','--project',str(self.source/'test/SharpLink.IntegrationTests/SharpLink.IntegrationTests.csproj'),'--','--list-tests','json']
        target=self.source/'test/SharpLink.DynamicServices/bin/Release/net10.0/SharpLink.DynamicPlugin.Services.dll'
        with patch.dict(os.environ,{**self.env,'FAKE_MUTATE':str(target)}), patch.object(host.v,'source_identity',return_value={}), patch('os.getcwd',return_value=str(self.source)), patch.object(Path,'cwd',return_value=self.source):
            self.assertEqual(host.forward(args,config),86)
        self.assertTrue(self.capture.exists())
        self.assertIn('changed',json.loads((self.evidence/'commands.jsonl').read_text())['tool_error'])


if __name__ == '__main__':
    unittest.main(verbosity=2)
