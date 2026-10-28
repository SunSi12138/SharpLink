import importlib.util
import json
from pathlib import Path
import unittest
from unittest.mock import patch, MagicMock
import tempfile
import time
from run import environment, bounded
from contract import SOURCE,TREE,CASES,CAP,TIMEOUT,PROVIDERS,record_command,script_command
from verify_request import validate_request,BRANCH,MESSAGE,REQUEST

HERE=Path(__file__).resolve().parent
class ContractTests(unittest.TestCase):
    def request(self):
        return dict(state='authorized',campaign='baseline-user-cpu-three-cases-v1',reviewedToolCommit='a'*40,reviewedToolTree='b'*40,sources={'dev':SOURCE},trees={'dev':TREE})
    def validate(self,data=None,**changes):
        args=dict(environment={'GITHUB_EVENT_NAME':'push','GITHUB_REF':BRANCH,'GITHUB_RUN_ATTEMPT':'1','GITHUB_REPOSITORY':'SunSi12138/SharpLink','GITHUB_SHA':'c'*40},head='c'*40,parent='a'*40,parent_tree='b'*40,changed=[REQUEST],message=MESSAGE,clean=True)
        args.update(changes); validate_request(data or self.request(),**args)
    def test_request_valid(self): self.validate()
    def test_request_preparation_never_runs(self):
        with self.assertRaises(ValueError): self.validate(json.loads((HERE/'REQUEST.json').read_text()))
    def test_no_extra_changes(self):
        with self.assertRaises(ValueError): self.validate(changed=[REQUEST,'run.py'])
    def test_no_retry(self):
        env={'GITHUB_EVENT_NAME':'push','GITHUB_REF':BRANCH,'GITHUB_RUN_ATTEMPT':'2'}
        with self.assertRaises(ValueError): self.validate(environment=env)
    def test_no_unreviewed_parent(self):
        with self.assertRaises(ValueError): self.validate(parent='d'*40)
    def test_no_dirty_tool(self):
        with self.assertRaises(ValueError): self.validate(clean=False)
    def test_environment_rejects_profiling_overrides(self):
        for key in ('DOTNET_EnableEventPipe','DOTNET_PerfMapEnabled','COMPlus_TieredPGO','CORECLR_ENABLE_PROFILING'):
            with patch.dict('os.environ',{key:'1'},clear=True):
                with self.assertRaises(ValueError): environment(Path('/runtime/dotnet'))
    def test_environment_accepts_isolated_cli_home(self):
        with patch.dict('os.environ',{'DOTNET_CLI_HOME':'/safe/private'},clear=True):
            env=environment(Path('/runtime/dotnet'))
            self.assertEqual(env['DEBUGINFOD_URLS'],'')
            self.assertEqual(env['DOTNET_ROOT'],'/runtime')
    def test_bounded_success_is_synthetic(self):
        with tempfile.TemporaryDirectory() as d:
            process=MagicMock(); process.poll.return_value=0; process.wait.return_value=0; process.returncode=0
            with patch('run.subprocess.Popen',return_value=process):
                bounded(['not-executed'],Path(d),'mock',{},time.monotonic()+1)
            self.assertEqual(json.loads((Path(d)/'mock-exit.json').read_text())['exitCode'],0)
    def test_bounded_timeout_kills_own_group_and_fails(self):
        with tempfile.TemporaryDirectory() as d:
            process=MagicMock(); process.pid=123; process.poll.return_value=None; process.wait.return_value=-15; process.returncode=-15
            with patch('run.subprocess.Popen',return_value=process),patch('run.os.killpg') as kill:
                with self.assertRaises(RuntimeError): bounded(['not-executed'],Path(d),'mock',{},0)
                self.assertEqual(kill.call_count,2)
                self.assertEqual([call.args[0] for call in kill.call_args_list],[123,123])
            self.assertIn('deadline',json.loads((Path(d)/'mock-exit.json').read_text())['reason'])
    def test_bounded_postexit_deadline_is_failure(self):
        with tempfile.TemporaryDirectory() as d:
            process=MagicMock(); process.pid=123; process.poll.return_value=0; process.wait.return_value=0; process.returncode=0
            with patch('run.subprocess.Popen',return_value=process),patch('run.os.killpg'):
                with self.assertRaises(RuntimeError): bounded(['not-executed'],Path(d),'mock',{},0)
            self.assertIn('deadline',json.loads((Path(d)/'mock-exit.json').read_text())['reason'])
    def test_bounded_cap_kills_and_fails(self):
        with tempfile.TemporaryDirectory() as d:
            process=MagicMock(); process.pid=123; process.poll.return_value=None; process.wait.return_value=-15; process.returncode=-15
            with patch('run.subprocess.Popen',return_value=process),patch('run.os.killpg') as kill,patch('run.bytes_in',return_value=CAP+1):
                with self.assertRaises(RuntimeError): bounded(['not-executed'],Path(d),'mock',{},time.monotonic()+1)
                self.assertEqual(kill.call_count,2)
                self.assertEqual([call.args[0] for call in kill.call_args_list],[123,123])
    def test_exact_schedule(self):
        self.assertEqual(CASES,(('static-intercepted',1),('static-intercepted',100),('dynamic-intercepted',1)))
        self.assertEqual((CAP,TIMEOUT),(268435456,180))
    def test_exact_user_only_record(self):
        command=record_command('/usr/bin/perf','x',['dotnet','probe.dll'])
        self.assertEqual(command,['/usr/bin/perf','record','-e','task-clock:u','-F','199','--strict-freq','--call-graph','fp,64','--user-callchains','--clockid','mono','-T','--no-buildid','--no-buildid-cache','-o','x','--','dotnet','probe.dll'])
        self.assertNotIn('-p',command); self.assertNotIn('-a',command)
    def test_minimal_provider(self):
        self.assertEqual(PROVIDERS,'Microsoft-Windows-DotNETRuntime:0x19:4,SharpLink-RpcCpu-Phase:0xffffffffffffffff:4')
        self.assertNotIn('SampleProfiler',PROVIDERS)
    def test_no_hotpath_timestamps_or_gc(self):
        s=(HERE/'probe/Program.cs').read_text(); operation=s.split('async ValueTask Operation()')[1].split('var firstStream')[0]
        for forbidden in ('Stopwatch','Clock.Now','GC.Collect','TotalAllocatedBytes','ticks['): self.assertNotIn(forbidden,operation)
        self.assertIn('for(var i=0;i<batch;i++)',s); self.assertIn('var cpu=Environment.CpuUsage',s)
    def test_gate_is_single_software_event(self):
        s=(HERE/'gate.py').read_text(); self.assertIn("[perf,'stat','-e','task-clock:u','--','/usr/bin/true']",s)
        for forbidden in ('sudo','setcap','cycles:u','instructions:u','apt-get'): self.assertNotIn(forbidden,s)
    def test_no_allocation_profiler(self):
        self.assertNotIn('SampleProfiler',(HERE/'run.py').read_text())
if __name__=='__main__': unittest.main()
