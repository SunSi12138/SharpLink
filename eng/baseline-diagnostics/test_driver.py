"""Offline mocks only: never launch dotnet, a product process, or a collector."""
import copy
import hashlib
import io
import json
import tempfile
from pathlib import Path
import unittest
from unittest.mock import patch, Mock
import zipfile
import run, tool, summary
from common import CONTRACT, SOURCE

class DriverTests(unittest.TestCase):
    def test_fixed_two_process_plan(self):
        self.assertEqual(CONTRACT['variants'], ['control','trace'])
        self.assertEqual((CONTRACT['warmupSeconds'],CONTRACT['workSeconds'],CONTRACT['itemCount']), (3,20,100))
        for variant in CONTRACT['variants']:
            c=run.command_for(Path('/private/dotnet'),Path('/prepared'),variant,Path('/output'),[0,2],Path('/collector'))
            self.assertEqual(c.count('PhysicalTcpProbe.dll'),0)
            self.assertIn('/prepared/baseline/PhysicalTcpProbe.dll',c)
            self.assertIn(variant,c); self.assertNotIn('collect-linux',c); self.assertNotIn('perf',c)
            if variant=='trace':
                self.assertIn('collect',c);self.assertIn('Drop',c); self.assertIn('Major',c)
                self.assertEqual(c[c.index('--')+1:c.index('--')+4],['taskset','-c','0,2'])
            else:self.assertNotIn('collect',c)

    def test_execute_one_attempt_and_atomic_go(self):
        for status in ('success','early-exit','bad-ready'):
            with tempfile.TemporaryDirectory() as td:
                slot=Path(td)
                if status!='early-exit':
                    (slot/'ready.json').write_text(json.dumps(dict(schema=1,mode='control',pid=123,
                        sourceRevision=SOURCE if status=='success' else 'bad')))
                proc=Mock(pid=123,returncode=0);proc.poll.return_value=0;proc.wait.return_value=0
                with patch.object(run.subprocess,'Popen',return_value=proc) as launch, \
                     patch.object(run,'process_group_alive',return_value=False), \
                     patch.object(run.os,'getpgid',return_value=123), \
                     patch.object(run.os,'sched_getaffinity',return_value={0,2}):
                    r=run.execute(['never-executed'],'control',slot,{},[0,2])
                self.assertEqual(launch.call_count,1)
                self.assertEqual(r['status'],'success' if status=='success' else 'failed')
                self.assertEqual((slot/'go').exists(),status=='success')
                self.assertFalse((slot/'go.pending').exists())
                self.assertTrue((slot/'host.json').exists())

    def test_final_trace_size_after_exit_is_checked(self):
        for payload in (None,b'',b'123456789',b'1234'):
            with tempfile.TemporaryDirectory() as td:
                slot=Path(td)
                (slot/'ready.json').write_text(json.dumps(dict(schema=1,mode='trace',pid=123,sourceRevision=SOURCE)))
                if payload is not None:(slot/'trace.nettrace').write_bytes(payload)
                proc=Mock(pid=123,returncode=0);proc.poll.return_value=0;proc.wait.return_value=0
                with patch.object(run.subprocess,'Popen',return_value=proc), patch.dict(CONTRACT,traceFileLimitBytes=8), \
                     patch.object(run,'process_group_alive',return_value=False), \
                     patch.object(run.os,'getpgid',return_value=123), patch.object(run.os,'sched_getaffinity',return_value={0,2}):
                    r=run.execute(['never-executed'],'trace',slot,{},[0,2])
                self.assertEqual(r['status'],'success' if payload==b'1234' else 'failed')
                self.assertTrue((slot/'host.json').exists())

    def test_timeout_cleanup_exit_race_retains_failure_without_relaunch(self):
        with tempfile.TemporaryDirectory() as td:
            slot=Path(td)
            (slot/'ready.json').write_text(json.dumps(dict(schema=1,mode='control',pid=123,sourceRevision=SOURCE)))
            proc=Mock(pid=123,returncode=-9);proc.poll.return_value=None;proc.wait.return_value=-9
            with patch.object(run.subprocess,'Popen',return_value=proc) as launch, \
                 patch.object(run,'process_group_alive',side_effect=[True,False,False]), \
                 patch.object(run.os,'getpgid',return_value=123), patch.object(run.os,'sched_getaffinity',return_value={0,2}), \
                 patch.object(run.os,'killpg',side_effect=ProcessLookupError) as kill, \
                 patch.object(run.time,'monotonic',side_effect=[0,181,182,183]):
                r=run.execute(['never-executed'],'control',slot,{},[0,2])
            self.assertEqual(launch.call_count,1);kill.assert_called_once_with(123,run.signal.SIGKILL)
            self.assertEqual(r['status'],'failed');self.assertTrue(r['processGroupQuiescent'])
            self.assertTrue((slot/'host.json').exists())

    def test_unknown_variant_is_not_a_new_slot(self):
        with self.assertRaises(ValueError):
            run.command_for(Path('/r'),Path('/p'),'replacement',Path('/o'),[0,1],Path('/c'))

class CollectorPackageTests(unittest.TestCase):
    def package(self, path, extra=None):
        xml=f'<package xmlns="http://schemas.microsoft.com/packaging/2012/06/nuspec.xsd"><metadata><id>{tool.PIN["id"]}</id><version>{tool.PIN["version"]}</version><repository commit="{tool.PIN["source"]}" /></metadata></package>'
        with zipfile.ZipFile(path,'w') as z:
            z.writestr('dotnet-trace.nuspec',xml);z.writestr(tool.PIN['payloadPrefix']+'dotnet-trace.dll',b'fixture')
            if extra:z.writestr(tool.PIN['payloadPrefix']+extra,b'bad')
        return hashlib.sha512(path.read_bytes()).hexdigest()

    def test_hash_source_payload_and_extracted_closure(self):
        with tempfile.TemporaryDirectory() as td:
            root=Path(td); package=root/'tool.nupkg';sha=self.package(package)
            with self.assertRaises(ValueError):tool.payload(package)
            with patch.dict(tool.PIN,sha512=sha,payloadFiles=1):
                values=tool.payload(package);self.assertEqual(set(values),{'dotnet-trace.dll'})
                target=root/'payload';target.mkdir();(target/'dotnet-trace.dll').write_bytes(b'fixture')
                tool.verify(package,target)
                (target/'extra.dll').write_bytes(b'bad')
                with self.assertRaises(ValueError):tool.verify(package,target)
            sha=self.package(package,'../escape')
            with patch.dict(tool.PIN,sha512=sha,payloadFiles=2):
                with self.assertRaises(ValueError):tool.payload(package)

class SummaryTests(unittest.TestCase):
    def sample(self):
        v=dict(schema=1,status='Complete',coverage='Classified',failures=[],sourceRevision=SOURCE,
            scenario='baseline-static-n100-work20s',managedThreadTimeSamples={k:0 for k in summary.CATEGORIES},
            censoredIntervals={k:0 for k in ('gc','pause','contention','wait')},
            gcStartsByGeneration={k:0 for k in ('gen0','gen1','gen2')})
        v['markerCensus']=dict(__import__('marker_gate').EXPECTED_CENSUS)
        v['normalizedEventCounts']={k:0 for k in summary.NORMALIZED_KINDS}
        v['normalizedEventCounts'].update({'work-start':1,'work-stop':1})
        for k in ('lostEvents','unknownStacks','unmatchedEvents','outsideWindowEvents','otherProcessEvents',
            'unknownEvents','externalThreadSamples','sampleErrors','allocationTickSamples','allocationTickReportedBytes'):v[k]=0
        for k in ('gcPauses','contention','waitHandle'):v[k]=dict(count=0,durationMilliseconds=0)
        v['methodSamples']=dict(windowValid=True,leafMethods=[],frameworkLeafWithSharpLinkAncestorSamples=0,**{k:0 for k in summary.METHOD_EXCLUDED})
        v['unmatchedBreakdown']={key:0 for key in summary.UNMATCHED_KEYS}
        return v

    def test_bounded_categories_only_no_raw_fields(self):
        v=self.sample();v.update(stack='SECRET',exception='SECRET',path='/private/SECRET')
        text='\n'.join(summary.render(v));self.assertNotIn('SECRET',text);self.assertLess(len(text),3500)
        self.assertIn('MANAGED_THREAD_TIME_SAMPLES',text);self.assertNotIn('cpuPercent',text)

    def test_bad_scalar_schema_and_unknown_reasons_rejected(self):
        for key,value in [('schema',2),('status','PASS'),('coverage','invented'),('sourceRevision','wrong'),
                          ('lostEvents',float('nan')),('lostEvents',1),('unknownStacks',True),('unknownStacks',1),
                          ('unknownEvents',1),('unmatchedEvents',1),('sampleErrors',1),('lostEvents',2**64),('failures',['SECRET'])]:
            v=self.sample();v[key]=value
            with self.assertRaises(ValueError):summary.render(v)
        v=self.sample();v['managedThreadTimeSamples']['SECRET']=1
        with self.assertRaises(ValueError):summary.render(v)

    def test_parse_failure_no_fake_metrics(self):
        self.assertEqual(summary.render(dict(schema=1,status='Incomplete',failures=['parse-failure'])),
                         ['DIAGNOSTIC_ANALYSIS status=Incomplete reasons=parse-failure'])

    def test_real_shaped_parse_failure_never_prints_zero_metrics(self):
        v=self.sample();v.update(status='Incomplete',failures=['parse-failure'])
        self.assertEqual(summary.render(v),['DIAGNOSTIC_ANALYSIS status=Incomplete reasons=parse-failure'])
        v['status']='Complete'
        with self.assertRaises(ValueError):summary.render(v)

    def test_invalid_window_withholds_aggregates_unknown_coverage_preserves_observations(self):
        v=self.sample();v.update(status='Incomplete',failures=['marker-cardinality'])
        v['managedThreadTimeSamples']['sharplink-stream']=123
        v['methodSamples']['windowValid']=False
        text='\n'.join(summary.render(v))
        self.assertIn('DIAGNOSTIC_WINDOW unavailable',text)
        self.assertNotIn('MANAGED_THREAD_TIME_SAMPLES',text);self.assertNotIn('GC_PAUSE',text)
        v.update(failures=['unknown-coverage'],coverage='Partial',unknownStacks=1)
        v['methodSamples'].update(windowValid=True,unavailableLeafSamples=124)
        v['managedThreadTimeSamples']['unknown']=1
        text='\n'.join(summary.render(v))
        self.assertIn('sharplink-stream=123',text);self.assertIn('unknown=1',text)
        self.assertIn('status=Incomplete',text)

if __name__=='__main__':unittest.main()
