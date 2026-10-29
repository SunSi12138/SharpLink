#!/usr/bin/env python3
"""Pure controls only: no product execution or networking."""
import copy, importlib.util, json, subprocess, tempfile, unittest
from pathlib import Path
from unittest.mock import patch, MagicMock
spec=importlib.util.spec_from_file_location('typed_gate',Path(__file__).with_name('run.py'))
g=importlib.util.module_from_spec(spec);spec.loader.exec_module(g)

class Scope(unittest.TestCase):
    def test_exact_census_and_fixed_schedule(self):
        self.assertEqual((len(g.FOCUSED),len(g.TYPED),len(g.CLEANUP)),(138,12,6))
        self.assertEqual(len(set(g.FOCUSED)),138)
        self.assertTrue(set(g.TYPED+g.CLEANUP).issubset(g.FOCUSED))
        self.assertEqual(g.schedule(),[(p,a) for p in range(1,7) for a in (('baseline','candidate') if p%2 else ('candidate','baseline'))])
        self.assertEqual(len(g.schedule()),12)
        self.assertEqual(g.CASES,['wu1','wu100','burst513','cold128','shared4','normal21','normal1024'])

    def test_source_freeze_scope(self):
        for arm in ('baseline','candidate'):
            root=Path('/source');request=dict(baseline_sha='a'*40,candidate_sha='b'*40)
            expected=request[arm+'_sha'];parent=g.BASE_PARENT if arm=='baseline' else request['baseline_sha']
            answers={('rev-parse','HEAD'):expected,('rev-parse','HEAD^{tree}'):g.BASE_TREE if arm=='baseline' else g.SOURCE_TREE,
                ('show','-s','--format=%P','HEAD'):parent,('status','--porcelain','--untracked-files=all'):''}
            with patch.object(g,'git',side_effect=lambda _,*a:answers[a]):
                g.verify_source(root,request,arm)
                for k in answers:
                    original=answers[k];answers[k]='wrong'
                    with self.subTest(arm=arm,key=k),self.assertRaises(g.GateError):g.verify_source(root,request,arm)
                    answers[k]=original

    def test_request_only_and_frozen_ancestry(self):
        with tempfile.TemporaryDirectory() as temp:
            root=Path(temp);path=root/g.REQUEST;path.parent.mkdir(parents=True)
            request=dict(schema_version=1,baseline_sha='a'*40,candidate_sha='b'*40,tools_sha='c'*40,tools_tree='d'*40)
            path.write_text(json.dumps(request))
            files=json.loads((g.HERE/'tool-files.json').read_text())
            answers={('rev-parse','HEAD'):'e'*40,('status','--porcelain','--untracked-files=all'):'',
                ('show','-s','--format=%P','HEAD'):'c'*40,
                ('diff','--name-status','--no-renames','c'*40,'HEAD'):'A\t'+g.REQUEST,
                ('rev-parse','c'*40+'^{tree}'):'d'*40,
                ('show','-s','--format=%P','c'*40):'b'*40,
                ('show','-s','--format=%P','b'*40):'a'*40,
                ('show','-s','--format=%P','a'*40):g.BASE_PARENT,
                ('rev-parse','b'*40+'^{tree}'):g.SOURCE_TREE,
                ('rev-parse','a'*40+'^{tree}'):g.BASE_TREE,
                ('diff','--name-status','--no-renames','a'*40,'b'*40):'\n'.join(g.SOURCE_DIFF),
                ('diff','--name-status','--no-renames','b'*40,'c'*40):'\n'.join(sorted('A\t'+f for f in files))}
            with patch.object(g,'git',side_effect=lambda _,*a:answers[a]):
                self.assertEqual(g.verify_release(root,'e'*40,g.BRANCH),request)
                for k in answers:
                    original=answers[k];answers[k]='wrong'
                    with self.subTest(key=k),self.assertRaises(g.GateError):g.verify_release(root,'e'*40,g.BRANCH)
                    answers[k]=original
                with self.assertRaises(g.GateError):g.verify_release(root,'e'*40,'refs/heads/main')

    def test_workflow_no_upload_cache_or_other_campaign(self):
        root=Path(__file__).resolve().parents[2];w=(root/'.github/workflows/typed-window-mechanism-no-upload.yml').read_text()
        for token in ('actions/upload-artifact@','actions/cache@','sudo ','run-performance-matrix','normal-measure','workflow_dispatch:'):
            self.assertNotIn(token,w)
        self.assertIn('contents: read',w);self.assertIn('persist-credentials: false',w)
        self.assertIn('validation/typed-window-mechanism-20261029',w)
        self.assertEqual(w.count('run: python3 release/eng/typed-mechanism-validation/run.py'),2)

    def test_watchdog_kills_entire_process_group(self):
        process=MagicMock(pid=5432,returncode=-9);process.wait.side_effect=[subprocess.TimeoutExpired(['probe'],120),-9]
        with tempfile.TemporaryDirectory() as temp,patch.object(g.unit.subprocess,'Popen',return_value=process) as start,patch.object(g.unit.os,'killpg') as kill:
            self.assertEqual(g.unit.execute(['host','probe.dll'],Path(temp),{},120,Path(temp)/'log'),(-9,True))
        self.assertTrue(start.call_args.kwargs['start_new_session']);kill.assert_called_once_with(5432,g.unit.signal.SIGKILL)

def measurement(arm='candidate',pair=1):
    rows=[]
    for name in g.CASES:
        cold=name=='cold128';batch=100 if name=='wu100' else 513 if name=='burst513' else 1
        sessions=4 if name=='shared4' else 1;size=1024 if name=='normal1024' else 21
        ops=128 if cold else batch*sessions*100
        retained=None if arm=='baseline' or name.startswith('normal') else {'wu1':1,'wu100':100,'burst513':512,'cold128':1,'shared4':4}[name]
        row=dict(case=name,passed=True,ops=ops,bytes=ops*size,allocatedBytes=10000,cpuTicks=100,elapsedTicks=1000,
            finalQueuedBytes=0,poolDisposedAndEmpty=True,framesFromConservedFixedSize=ops,warmupSeconds=0 if cold else 1,
            measureSeconds=0 if cold else 1,retainedNodes=retained)
        if cold:row.update(contexts=128,fixedOperationCount=True,validatedFrames=128,construction=dict(Operations=128,AllocatedBytes=1000,ElapsedTicks=100,CpuTicks=10))
        else:row.update(batchSize=batch,sessions=sessions,ordinaryBytes=size if name.startswith('normal') else 0,
            forcedHeldFlushStress=name=='burst513',validation=dict(Frames=batch*sessions,Bytes=batch*sessions*size,Flushes=1),flushes=100)
        rows.append(row)
    return dict(schemaVersion=1,arm=arm,pair=pair,status='pass',failure=None,runtimeVersion='10.0.14',runtime='.NET 10.0.14',
        sourceTree=g.BASE_TREE if arm=='baseline' else g.SOURCE_TREE,
        reviewedSourceCommit='0403d6a0a4eb2b1bc9dafc9c87aa876c36f0d158' if arm=='baseline' else 'e62f7a335f16595bad37bc3a8edeb7b945c5b686',
        architecture='X64',processorCount=4,profile='Balanced_unmodified',stopwatchFrequency=1000,cpuTicksPerSecond=10000000,
        boundary='zero_queued_bytes_then_one_zero_byte_marker_per_batch',scope='mechanism_only_not_TCP_or_QPS_or_p99',cases=rows)

class Measurement(unittest.TestCase):
    def test_both_arms_strict_seven_cases(self):
        for arm in ('baseline','candidate'):self.assertEqual(len(g.validate_measurement(measurement(arm),arm,1)),7)

    def test_wrong_identity_runtime_boundary_and_incomplete_cases_rejected(self):
        for key,value in [('schemaVersion',True),('arm','baseline'),('pair',True),('status','fail'),('failure','timeout'),
            ('runtimeVersion','10.0.2'),('runtime','.NET 10.0.2'),('sourceTree',g.BASE_TREE),('reviewedSourceCommit','wrong'),
            ('architecture','Arm64'),('processorCount',2),('profile','Throughput'),('stopwatchFrequency',0),('cpuTicksPerSecond',1),
            ('boundary','marker_before_zero'),('scope','QPS'),('cases',[])]:
            d=measurement();d[key]=value
            with self.subTest(key=key),self.assertRaises(g.GateError):g.validate_measurement(d,'candidate',1)
        d=measurement();d['cases'].append(copy.deepcopy(d['cases'][0]))
        with self.assertRaises(g.GateError):g.validate_measurement(d,'candidate',1)

    def test_cost_conservation_retention_and_cold_contract_rejected(self):
        for index,key,value in [(0,'passed',False),(0,'ops',0),(0,'ops',True),(0,'bytes',0),(0,'allocatedBytes',-1),
            (0,'cpuTicks',0),(0,'elapsedTicks',999),(0,'finalQueuedBytes',21),(0,'poolDisposedAndEmpty',False),
            (0,'framesFromConservedFixedSize',0),(0,'warmupSeconds',0),(0,'measureSeconds',2),(0,'retainedNodes',2),
            (1,'batchSize',1),(4,'sessions',1),(5,'ordinaryBytes',0),(5,'retainedNodes',1),(2,'retainedNodes',511),
            (0,'forcedHeldFlushStress',True),(0,'validation',{}),(0,'flushes',0),(3,'ops',127),(3,'contexts',1),
            (3,'fixedOperationCount',False),(3,'validatedFrames',0),(3,'construction',{})]:
            d=measurement();d['cases'][index][key]=value
            with self.subTest(index=index,key=key),self.assertRaises(g.GateError):g.validate_measurement(d,'candidate',1)

    def test_loaded_product_identity_own_arm(self):
        identity={'SharpLink.Runtime.dll':'a'*64,'SharpLink.Benchmarks.dll':'b'*64}
        d={field:dict(path='/bin/'+name,sha256=identity[name],mvid='0'*36) for field,name in [('productAssembly','SharpLink.Runtime.dll'),('consoleAssembly','SharpLink.Benchmarks.dll')]}
        g.validate_loaded_identity(d,Path('/bin'),identity)
        for field in d:
            for key,value in [('path','/other/SharpLink.Runtime.dll'),('sha256','wrong'),('mvid','')]:
                bad=copy.deepcopy(d);bad[field][key]=value
                with self.assertRaises(g.GateError):g.validate_loaded_identity(bad,Path('/bin'),identity)

    def test_all_arm_case_scalars_and_cold_construction_visible_without_raw_data(self):
        all_rows=[]
        for pair,arm in g.schedule():
            data=measurement(arm,pair);data['arbitraryRawText']='NEVER PRINT THIS'
            data['productAssembly']=dict(path='/private/raw/path')
            rows=g.scalar_rows(data);self.assertEqual(len(rows),7);all_rows+=rows
            for row in rows:
                self.assertEqual(row['pair'],pair);self.assertEqual(row['arm'],arm)
                for field in ('ops','bytes','cpuTicks','elapsedTicks','allocatedBytes','retainedNodes','finalQueuedBytes','stopwatchFrequency','cpuTicksPerSecond'):
                    self.assertIn(field,row)
                self.assertEqual('constructionOps' in row,row['case']=='cold128')
                self.assertEqual(row['retainedNodesMeaning'],'maximum_per_context_across_128' if row['case']=='cold128' else 'single_shared_context_idle_nodes')
        self.assertEqual(len(all_rows),84)
        self.assertEqual(len({(r['pair'],r['arm'],r['case']) for r in all_rows}),84)
        output=json.dumps(all_rows,allow_nan=False)
        self.assertNotIn('NEVER PRINT',output);self.assertNotIn('/private',output)
        self.assertLess(len(output),40000)
        self.assertFalse(g.integer(2**63));self.assertFalse(g.integer(float('nan')))

    def test_all_six_pairs_required_no_cold_cpu_subset_median(self):
        rows=[measurement(a,p) for p,a in g.schedule()]
        summaries=g.summarize(rows);self.assertEqual(len(summaries),7)
        self.assertTrue(all(s['paired_cpu_ratio_median']==1 for s in summaries))
        self.assertTrue(all([p['pair'] for p in s['pairs']]==list(range(1,7)) for s in summaries))
        self.assertEqual(sum(len(s['pairs']) for s in summaries),42)
        with self.assertRaises(g.GateError):g.summarize(rows[:-1])
        rows[0]['cases'][3]['cpuTicks']=0
        summary=g.summarize(rows)[3]
        self.assertIsNone(summary['paired_cpu_ratio_median']);self.assertFalse(summary['cpu_ratio_all_six_available'])
        self.assertEqual(len(summary['pairs']),6);self.assertIsNone(summary['pairs'][0]['cpu_ratio'])
        rows[0]['cases'][0]['cpuTicks']=0
        with self.assertRaises(g.GateError):g.summarize(rows)

if __name__=='__main__':unittest.main(verbosity=2)
