"""Pure offline controls. Never opens IPC or launches collector/emitter/product."""
import copy
import json
import tempfile
import unittest
from pathlib import Path
from unittest.mock import Mock,patch
import marker_gate as gate
import run
from common import SOURCE

def emitter():
    return dict(schemaVersion=1,purpose='MarkerCompatibilityOnly',listenerPassed=True,
        runtimeVersion='10.0.14',pid=123,sourceRevision=SOURCE,variant='trace',mode='synthetic-marker',
        itemCount=100,workSeconds=1,window='marker-smoke',eventCount=2,
        constructionExceptionNull=True,enabled=True,eventIds=[1,2],eventNames=['WorkStart','WorkStop'],
        payloadNames=gate.FIELDS,startTick=1000,stopTick=2000,frequency=1000)

def analysis():
    return dict(schema=1,kind='synthetic-marker-check',status='Complete',failures=[],
        markerCensus=dict(gate.EXPECTED_CENSUS),observedSampleCount=0,lostEvents=0,
        windowMilliseconds=1000,canonicalStartRecognized=True,canonicalStopRecognized=True)

class MarkerGateTests(unittest.TestCase):
    def test_strict_emitter_fields_and_values(self):
        gate.validate_emitter(emitter(),123)
        for key in emitter():
            value=emitter();value[key]=None
            with self.subTest(key=key),self.assertRaises(ValueError):gate.validate_emitter(value,123)
        for key,value in [('runtimeVersion','10.0.2'),('pid',124),('startTick',True),('stopTick',1200),
            ('stopTick',7000),('eventIds',[1,1]),('eventIds',[True,2]),('eventNames',['Work/Start','Work/Stop']),('listenerPassed',1)]:
            v=emitter();v[key]=value
            with self.assertRaises(ValueError):gate.validate_emitter(v,123)

    def test_strict_actual_parser_result(self):
        gate.validate_analysis(analysis())
        for k in gate.CENSUS:
            v=analysis();v['markerCensus'][k]+=1
            with self.assertRaises(ValueError):gate.validate_analysis(v)
        for k,x in [('status','Incomplete'),('lostEvents',1),('canonicalStartRecognized',False),
            ('canonicalStopRecognized',1),('windowMilliseconds',499),('windowMilliseconds',float('nan')),
            ('failures',['marker-cardinality']),('observedSampleCount',True)]:
            v=analysis();v[k]=x
            with self.assertRaises(ValueError):gate.validate_analysis(v)

    def test_safe_census_even_failed_no_payload_dump(self):
        v=analysis();v.update(status='Incomplete',failures=['marker-cardinality'],secret='RAW')
        v['markerCensus']['startIdCount']=0
        text='\n'.join(gate.render_marker(v))
        self.assertIn('startIdCount=0',text);self.assertNotIn('RAW',text)
        v['failures']=['parse-failure'];v['markerCensus']={}
        self.assertEqual(len(gate.render_marker(v)),1)
        v['status']='Complete'
        with self.assertRaises(ValueError):gate.render_marker(v)

    def test_same_collector_command_for_synthetic_and_product(self):
        base=run.command_for(Path('/r'),Path('/p'),'trace',Path('/s'),[0,2],Path('/c'))
        synthetic=run.collector_command(Path('/r'),Path('/c'),Path('/s'),['synthetic-only'])
        self.assertEqual(base[:base.index('--')+1],synthetic[:synthetic.index('--')+1])
        self.assertIn('SharpLink-DiagnosticPhase:0x1:4',base[base.index('--providers')+1])

    def test_one_shot_marker_gate_failures_never_write_success(self):
        for state in ('success','host','listener','parser-exit','parser-census'):
            with tempfile.TemporaryDirectory() as td:
                slot=Path(td)/'marker'
                def execute(*args):
                    v=emitter()
                    if state=='listener':v['listenerPassed']=False
                    (slot/'result.json').write_text(json.dumps(v))
                    return dict(status='failed' if state=='host' else 'success',processGroupQuiescent=True,targetPid=123)
                def decode(*args,**kwargs):
                    v=analysis()
                    if state=='parser-census':v['markerCensus']['startIdCount']=2
                    (slot/'analysis.json').write_text(json.dumps(v))
                    return Mock(returncode=2 if state=='parser-exit' else 0)
                with patch.object(run,'execute',side_effect=execute) as start,patch.object(run.subprocess,'run',side_effect=decode) as parse:
                    if state=='success':run.run_marker_gate(Path('/r'),Path('/a/bin'),Path('/c'),slot,{},[0,2],'a'*40)
                    else:
                        with self.assertRaises(ValueError):run.run_marker_gate(Path('/r'),Path('/a/bin'),Path('/c'),slot,{},[0,2],'a'*40)
                self.assertEqual(start.call_count,1)
                self.assertEqual(parse.call_count,0 if state in ('host','listener') else 1)
                self.assertEqual((slot/'validated.json').exists(),state=='success')
                self.assertNotIn('PhysicalTcpProbe',str(start.call_args))

    def test_main_marker_failure_stops_before_any_product_launch(self):
        from common import CONTRACT,TREE
        with tempfile.TemporaryDirectory() as td:
            root=Path(td);prepared=root/'prepared';prepared.mkdir();runtime=root/'runtime/dotnet'
            runtime.parent.mkdir();runtime.touch();archives=root/'archives';archives.mkdir()
            analyzer=root/'analyzer/bin';analyzer.mkdir(parents=True)
            (prepared/'prepared.json').write_text(json.dumps(dict(toolRevision='a'*40,toolSourceHashes={},
                contract=CONTRACT,sourceRevision=SOURCE,sourceTree=TREE,arms={'baseline':{'snapshot':{}}})))
            (analyzer.parent/'analyzer-manifest.json').write_text(json.dumps(dict(toolRevision='a'*40,
                toolSourceHashes={},snapshot={},markerSnapshot={})))
            quiet=root/'quiet.json';quiet.write_text(json.dumps(dict(noOtherClrBuildTest=True,
                reviewedToolCommit='a'*40,campaign=CONTRACT['campaign'])))
            args=['run.py','--prepared',str(prepared),'--runtime-dotnet',str(runtime),
                '--runtime-archives',str(archives),'--collector',str(root/'collector'),
                '--collector-package',str(root/'collector.nupkg'),'--analyzer',str(analyzer),
                '--quiet-window-record',str(quiet),'--output',str(root/'output'),'--reviewed-tool-sha','a'*40]
            def check(command,**kwargs):
                if command[:3]==['git','rev-parse','HEAD']:return 'a'*40
                if '--list-runtimes' in command:return '\n'.join(f'{n} 10.0.14 [{runtime.parent/"shared"/n}]' for n in ('Microsoft.AspNetCore.App','Microsoft.NETCore.App'))
                return ''
            original_read=Path.read_text
            def read(path,*args,**kwargs):
                if str(path)=='/etc/os-release':return 'ID=ubuntu\nVERSION_ID="24.04"\n'
                return original_read(path,*args,**kwargs)
            with patch.object(run.sys,'argv',args),patch.object(run.subprocess,'check_output',side_effect=check), \
                 patch.object(run,'source_inventory',return_value={}),patch.object(run,'inventory',return_value={}), \
                 patch.object(run.old_identity,'framework_policy',return_value={}), \
                 patch.object(run.old_identity,'archive_framework_policy',return_value={}), \
                 patch.object(run,'verify_collector'),patch.object(run,'select_cpus',return_value=[0,2]), \
                 patch.object(run.os,'sched_getaffinity',return_value={0,2}),patch.dict(run.os.environ,GITHUB_ACTIONS='true'), \
                 patch.object(Path,'read_text',read),patch.object(run,'run_marker_gate',side_effect=ValueError('fixed-negative')) as gate_call, \
                 patch.object(run,'execute') as product:
                self.assertEqual(run.main(),1)
            self.assertEqual(gate_call.call_count,1);product.assert_not_called()
            self.assertEqual(json.loads((root/'output/failure.json').read_text())['phase'],'MARKER')
            self.assertFalse((root/'output/control').exists());self.assertFalse((root/'output/trace').exists())

    def test_shared_source_and_no_product_reference(self):
        root=Path(__file__).parent
        project=(root/'marker_smoke/MarkerSmoke.csproj').read_text()
        self.assertIn('../fixture/probe/DiagnosticPhase.cs',project)
        self.assertNotIn('PackageReference',project);self.assertNotIn('ProjectReference',project)
        program=(root/'fixture/probe/Program.cs').read_text()
        self.assertNotIn('class DiagnosticPhase',program)
        # Hard control-flow gate occurs before the sole product loop.
        driver=(root/'run.py').read_text()
        self.assertLess(driver.index("run_marker_gate(runtime, a.analyzer"),driver.index("for variant in CONTRACT['variants']"))

if __name__=='__main__':unittest.main()
