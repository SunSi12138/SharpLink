import copy
import importlib.util
import json
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('scan', Path(__file__).with_name('scan.py'))
s = importlib.util.module_from_spec(spec)
spec.loader.exec_module(s)

class Reports(unittest.TestCase):
    def setUp(self):
        self.expected = {'src/A.csproj': ['net10.0'], 'test/B.csproj': ['net10.0']}
        self.data = {'version': 1, 'parameters': '--include-transitive --vulnerable',
                     'sources': ['https://api.nuget.org/v3/index.json'],
                     'projects': [{'path': '/source/' + p} for p in self.expected]}
    def parse(self, data=None, mode='vulnerable'):
        return s.report(self.data if data is None else data, mode, self.expected, Path('/source'))
    def test_clean_filtered_complete(self):
        self.assertEqual(self.parse()['findings'], [])
    def test_missing_project_rejected(self):
        self.data['projects'].pop()
        with self.assertRaises(ValueError): self.parse()
    def test_empty_report_rejected(self):
        self.data['projects'] = []
        with self.assertRaises(ValueError): self.parse()
    def test_duplicate_rejected(self):
        self.data['projects'][1] = self.data['projects'][0]
        with self.assertRaises(ValueError): self.parse()
    def test_no_transitive_rejected(self):
        self.data['parameters'] = '--vulnerable'
        with self.assertRaises(ValueError): self.parse()
    def test_no_sources_rejected(self):
        self.data['sources'] = []
        with self.assertRaises(ValueError): self.parse()
    def test_wrong_mode_rejected(self):
        with self.assertRaises(ValueError): self.parse(mode='deprecated')
    def test_problems_rejected(self):
        self.data['projects'][0]['problems'] = [{'level': 'error'}]
        with self.assertRaises(ValueError): self.parse()
    def test_missing_inventory_framework_rejected(self):
        self.data['parameters'] = '--include-transitive'
        with self.assertRaisesRegex(ValueError, 'inventory missing frameworks'): self.parse(mode='inventory')
    def test_unknown_framework_rejected(self):
        self.data['projects'][0]['frameworks'] = [{'framework': 'net9.0'}]
        with self.assertRaises(ValueError): self.parse()
    def test_advisory_retained_with_scope(self):
        package = {'id': 'Example', 'resolvedVersion': '1.0', 'vulnerabilities': [{'severity': 'High', 'advisoryurl': 'https://example.test/advisory'}]}
        self.data['projects'][0]['frameworks'] = [{'framework': 'net10.0', 'transitivePackages': [package]}]
        item = self.parse()['findings'][0]
        self.assertEqual(item['scope'], 'production')
        self.assertEqual(item['vulnerabilities'], package['vulnerabilities'])
    def test_deprecation_not_converted_to_vulnerability(self):
        self.data['parameters'] = '--include-transitive --deprecated'
        self.data['projects'][1]['frameworks'] = [{'framework': 'net10.0', 'topLevelPackages': [{'id': 'Old', 'resolvedVersion': '1.0', 'deprecationReasons': ['Legacy']}]}]
        item = self.parse(mode='deprecated')['findings'][0]
        self.assertEqual(item['scope'], 'development')
        self.assertNotIn('vulnerabilities', item)
    def test_inventory_cannot_be_empty(self):
        self.data['parameters'] = '--include-transitive'
        for p in self.data['projects']: p['frameworks'] = [{'framework': 'net10.0'}]
        with self.assertRaisesRegex(ValueError, 'empty package inventory'): self.parse(mode='inventory')
    def test_inventory_accepts_complete_frameworks_with_packages(self):
        self.data['parameters'] = '--include-transitive'
        for p in self.data['projects']: p['frameworks'] = [{'framework': 'net10.0', 'transitivePackages': [{'id': 'X', 'resolvedVersion': '1'}]}]
        self.assertEqual(len(self.parse(mode='inventory')['projects']), 2)
    def test_hosted_only_guard(self):
        with patch.dict(s.os.environ, {'GITHUB_ACTIONS': 'false'}):
            with self.assertRaisesRegex(ValueError, 'hosted-only'):
                s.run_scan(Path('/tmp'), Path('/tmp'), Path('/tmp/unused-report'))

    def test_hidden_framework_filter_rejected(self):
        self.data['parameters'] += ' --framework net9.0'
        with self.assertRaises(ValueError): self.parse()
    def test_mode_substring_rejected(self):
        self.data['parameters'] = '--include-transitive --vulnerable-not-really'
        with self.assertRaises(ValueError): self.parse()
    def test_dual_mode_rejected(self):
        self.data['parameters'] += ' --deprecated'
        with self.assertRaises(ValueError): self.parse()
    def test_advisory_cannot_be_structurally_empty(self):
        self.data['projects'][0]['frameworks'] = [{'framework': 'net10.0', 'transitivePackages': [{'id':'X', 'resolvedVersion':'1'}]}]
        with self.assertRaises(ValueError): self.parse()

    def test_sources_must_be_nonempty_list_of_strings(self):
        for sources in ('https://api.nuget.org', {'url':'https://api.nuget.org'}, [None], ['']):
            with self.subTest(sources=sources):
                self.data['sources']=sources
                with self.assertRaisesRegex(ValueError,'malformed data sources'): self.parse()
    def test_inventory_rejects_filtered_mode(self):
        with self.assertRaisesRegex(ValueError,'unexpected scan parameters'): self.parse(mode='inventory')

class Binding(unittest.TestCase):
    def test_request_source_and_diff_negative_controls(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp) / 'tools'; root.mkdir()
            source = Path(tmp) / 'source'
            def git(*args): return s.git(root, *args)
            git('init', '-q'); git('config', 'user.name', 'Test'); git('config', 'user.email', 'test@example.invalid')
            (root/'base').write_text('source')
            git('add', '.'); git('commit', '-qm', 'source')
            sha, tree = git('rev-parse', 'HEAD'), git('rev-parse', 'HEAD^{tree}')
            subprocess.run(['git','clone','-q',str(root),str(source)], check=True)
            for p in s.TOOLS:
                (root/p).parent.mkdir(parents=True, exist_ok=True); (root/p).write_text('tool')
            git('add','.'); git('commit','-qm','tools'); tools = git('rev-parse','HEAD')
            request = {'source_sha':sha, 'source_tree':tree, 'tools_sha':tools}
            s.write(root/s.REQUEST, request); git('add','.'); git('commit','-qm','request'); event=git('rev-parse','HEAD')
            with patch.object(s,'SOURCE',sha), patch.object(s,'TREE',tree):
                self.assertEqual(s.validate(root,source,event)['source_sha'],sha)
                with self.assertRaisesRegex(ValueError,'event/parent'): s.validate(root,source,'0'*40)
                (source/'base').write_text('dirty')
                with self.assertRaisesRegex(ValueError,'dirty'): s.validate(root,source,event)
                (source/'base').write_text('source')
                request['source_sha']='0'*40; s.write(root/s.REQUEST,request)
                with self.assertRaisesRegex(ValueError,'schema/source'): s.validate(root,source,event)
                request['source_sha']=sha; s.write(root/s.REQUEST,request)
                (root/'unauthorized').write_text('x'); git('add','.'); git('commit','-qm','second request')
                with self.assertRaisesRegex(ValueError,'event/parent'): s.validate(root,source,git('rev-parse','HEAD'))


class TimeoutSafety(unittest.TestCase):
    def test_timeout_kills_group_and_records_confirmed_absence(self):
        from unittest.mock import Mock
        process = Mock(pid=12345, returncode=-9)
        process.wait.side_effect = [subprocess.TimeoutExpired(['dotnet'], 600), -9]
        with tempfile.TemporaryDirectory() as tmp, patch.object(s.subprocess, 'Popen', return_value=process) as popen, patch.object(s.os, 'killpg', side_effect=[None, ProcessLookupError()]) as kill:
            record = s.execute('restore', ['dotnet'], Path(tmp), Path(tmp))
            self.assertTrue(popen.call_args.kwargs['start_new_session'])
            self.assertEqual(kill.call_args_list[0].args, (12345, s.signal.SIGKILL))
            self.assertEqual(kill.call_args_list[1].args, (12345, 0))
            self.assertTrue(record['timed_out'])
            self.assertIsNone(record['exit_code'])
            self.assertEqual(record['process_group_cleanup'], 'confirmed_absent')
    def test_timeout_uncertain_group_remains_explicit(self):
        from unittest.mock import Mock
        process = Mock(pid=12345, returncode=-9)
        process.wait.side_effect = [subprocess.TimeoutExpired(['dotnet'], 600), -9]
        with tempfile.TemporaryDirectory() as tmp, patch.object(s.subprocess, 'Popen', return_value=process), patch.object(s.os, 'killpg'):
            record = s.execute('restore', ['dotnet'], Path(tmp), Path(tmp))
            self.assertEqual(record['process_group_cleanup'], 'uncertain_group_still_exists')
    def test_timeout_never_continues_scan_for_any_cleanup_result(self):
        for cleanup in ('confirmed_absent', 'uncertain_group_still_exists', 'uncertain: PermissionError'):
            with self.subTest(cleanup=cleanup), tempfile.TemporaryDirectory() as tmp:
                root = Path(tmp); source = root/'source'; source.mkdir(); out=root/'out'
                for file in ('global.json', 'Directory.Build.props', 'Sharplink.slnx'): (source/file).write_text('fixture')
                record = {'name':'dotnet-info','exit_code':None,'timed_out':True,'process_group_cleanup':cleanup}
                env={'GITHUB_ACTIONS':'true','GITHUB_SHA':'fixture','NUGET_PACKAGES':str(root/'packages'),'NUGET_HTTP_CACHE_PATH':str(root/'http')}
                with patch.dict(s.os.environ,env), patch.object(s,'validate',return_value={}), patch.object(s,'census',return_value={'solution_projects':[]}), patch.object(s,'execute',return_value=record) as execute:
                    self.assertEqual(s.run_scan(root,source,out),1)
                    self.assertEqual(execute.call_count,1)
                    summary=json.loads((out/'summary.json').read_text())
                    self.assertEqual(summary['status'],'INCOMPLETE')
                    self.assertIn('no further restore/scan',summary['errors'][0])

class RestoreDiagnostics(unittest.TestCase):
    def test_normal_restore_error_keeps_all_three_diagnostics_incomplete(self):
        with tempfile.TemporaryDirectory() as tmp:
            root=Path(tmp); source=root/'source'; source.mkdir(); out=root/'out'
            for file in ('global.json','Directory.Build.props','Sharplink.slnx'): (source/file).write_text('fixture')
            project='src/A/A.csproj'; assets=source/'src/A/obj/project.assets.json'
            seen=[]
            def execute(name,args,cwd,out):
                seen.append(name)
                stdout='fixture'
                if name=='package-list-help': stdout='--project --no-restore --include-transitive --vulnerable --deprecated --format --output-version'
                if name=='restore':
                    s.write(assets,{'project':{'restore':{'originalTargetFrameworks':['net10.0']}},'libraries':{'Package/1':{'type':'package'}}})
                if name in ('inventory','vulnerable','deprecated'):
                    data={'version':1,'parameters':'--include-transitive'+('' if name=='inventory' else ' --'+name),'sources':['https://api.nuget.org/v3/index.json'],'projects':[{'path':str(source/project)}]}
                    if name=='inventory': data['projects'][0]['frameworks']=[{'framework':'net10.0','topLevelPackages':[{'id':'Package','resolvedVersion':'1'}]}]
                    stdout=json.dumps(data)
                (out/(name+'.stdout')).write_text(stdout);(out/(name+'.stderr')).write_text('')
                return {'name':name,'exit_code':1 if name=='restore' else 0,'timed_out':False}
            env={'GITHUB_ACTIONS':'true','GITHUB_SHA':'fixture','NUGET_PACKAGES':str(root/'packages'),'NUGET_HTTP_CACHE_PATH':str(root/'http')}
            with patch.dict(s.os.environ,env),patch.object(s,'validate',return_value={}),patch.object(s,'census',return_value={'solution_projects':[project]}),patch.object(s,'execute',side_effect=execute),patch.object(s,'git',return_value=''):
                self.assertEqual(s.run_scan(root,source,out),1)
            self.assertEqual(seen[-3:],['inventory','vulnerable','deprecated'])
            summary=json.loads((out/'summary.json').read_text())
            self.assertEqual(summary['status'],'INCOMPLETE')
            self.assertEqual(summary['errors'],['restore exit 1'])

if __name__ == '__main__': unittest.main()
