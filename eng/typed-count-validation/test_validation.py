#!/usr/bin/env python3
"""Pure count-contract failure controls; no product execution or networking."""
import copy,hashlib,importlib.util,json,tempfile,unittest
from pathlib import Path
from unittest.mock import patch
spec=importlib.util.spec_from_file_location('count_gate',Path(__file__).with_name('run.py'));g=importlib.util.module_from_spec(spec);spec.loader.exec_module(g)

def empty_counts():return dict(Client={k:0 for k in g.SIDES},Server={k:0 for k in g.SIDES},InvalidCredits=0,InvalidRoles=0)
def result(arm='candidate',items=100):
    rpcs=1 if items==1 else 256;count=rpcs if items==1 else 512;credit=rpcs*items*4
    before=empty_counts()
    before['Client'].update(Entered=count,Accepted=count,EnteredCreditArgumentBytes=credit,AcceptedCreditArgumentBytes=credit)
    before['Server'].update(HandlerCompleted=count,ReceivedCreditArgumentBytes=credit)
    owner={k:0 for k in g.OWNERS};owner['Empty']=True
    server_flow=dict(sendStates=0,activeSendStreamCount=0,waiters=0,pendingSendStateWaiterCount=0,sendConnectionCredit=4096,connectionWindow=4096,queuedSendBytes=0,Quiescent=True)
    flow=dict(receiveStates=0,consumedCreditUpdates=0,pendingConnectionConsumed=0,receiveConnectionCredit=4096,connectionWindow=4096,Quiescent=True)
    return dict(schemaVersion=1,status='pass',receiverMeaning='handler_completed_not_credit_granted',diagnosticSchema=g.SCHEMA,arm=arm,itemCount=items,diagnosticInstrumented=True,countersMayAffectScheduling=True,
        sourceTree=g.SOURCE_TREES[arm],diagnosticTree=g.OVERLAY_TREES[arm],diagnosticCommit=g.OVERLAY_LABELS[arm],runtimeVersion='10.0.14',
        workers=1,mode='static-intercepted',transport='tcp-loopback',requestedRpcs=rpcs,completedRpcs=rpcs,completedItems=rpcs*items,checksum=rpcs*items*(items-1)//2,
        connectionPoolMin=1,connectionPoolMax=1,requestTimeout='disabled',heartbeatIntervalSeconds=3600,heartbeatTimeoutSeconds=7200,clientInterceptors=1,serverInterceptors=0,readyConnectionsBeforeDispose=1,
        finalFenceCompleted=True,shutdownCompleted=True,disposeCompleted=True,channelType='SharpLink.Client.SharpLinkClient',streamType='SharpLink.Client.SharpLinkClient+LogicalInvocationAsyncEnumerable`1[System.Int32]',interceptorObserved=True,
        generatedManifest=dict(api=5,protocol=2,generator='3.0.0',abi='sharplink-3.0-api5-owner-bound-unsafe-blit-v1'),scope='instrumented_opportunity_counts_not_uninstrumented_N_or_performance',
        initialCounts=empty_counts(),beforeDisposeCounts=before,afterDisposeCounts=copy.deepcopy(before),beforeDisposeOwnership=owner,afterDisposeOwnership=copy.deepcopy(owner),beforeDisposeFlowState=flow,afterDisposeFlowState=copy.deepcopy(flow),beforeDisposeServerFlowState=server_flow,afterDisposeServerFlowState=copy.deepcopy(server_flow))

class Counts(unittest.TestCase):
    def test_four_fixed_cells_and_one_rpc_control(self):
        self.assertEqual(g.schedule(),[('baseline',1),('candidate',1),('baseline',100),('candidate',100)])
        rows=[g.validate_result(result(a,n),a,n) for a,n in g.schedule()];g.validate_cross_arm(rows)
        self.assertEqual(len(rows),4);self.assertEqual(rows[0]['accepted'],1)
        self.assertEqual(rows[2]['rpcs'],256);self.assertEqual(rows[2]['payload_items'],25600)

    def test_empty_and_missing_hook_counters_never_pass(self):
        for mutation in ('missing-schema','empty','missing-snapshot','entered','accepted','handler-completed','entry-bytes','received-argument-bytes','invalid-credit','reverse'):
            d=result();before=d['beforeDisposeCounts']
            if mutation=='missing-schema':d['diagnosticSchema']=None
            if mutation=='empty':d['beforeDisposeCounts']=empty_counts();d['afterDisposeCounts']=empty_counts()
            if mutation=='missing-snapshot':d.pop('initialCounts')
            if mutation=='entered':before['Client']['Entered']=0
            if mutation=='accepted':before['Client']['Accepted']=0
            if mutation=='handler-completed':before['Server']['HandlerCompleted']=0
            if mutation=='entry-bytes':before['Client']['EnteredCreditArgumentBytes']=0
            if mutation=='received-argument-bytes':before['Server']['ReceivedCreditArgumentBytes']=0
            if mutation=='invalid-credit':before['InvalidCredits']=1
            if mutation=='reverse':before['Server']['Entered']=1
            if mutation not in ('empty','missing-snapshot','missing-schema'):d['afterDisposeCounts']=copy.deepcopy(before)
            with self.subTest(mutation=mutation),self.assertRaises(g.GateError):g.validate_result(d,'candidate',100)

    def test_n1_exact_one_n100_bounded_and_all_samples_retained(self):
        for items,count in ((1,0),(1,2),(100,255),(100,25601)):
            d=result(items=items)
            for key in ('beforeDisposeCounts','afterDisposeCounts'):
                d[key]['Client']['Entered']=d[key]['Client']['Accepted']=d[key]['Server']['HandlerCompleted']=count
            with self.subTest(items=items,count=count),self.assertRaises(g.GateError):g.validate_result(d,'candidate',items)
        rows=[g.validate_result(result(a,n),a,n) for a,n in g.schedule()]
        with self.assertRaises(g.GateError):g.validate_cross_arm(rows[:-1])
        bad=copy.deepcopy(rows);bad[3]['accepted_credit_argument_bytes']+=1
        with self.assertRaises(g.GateError):g.validate_cross_arm(bad)

    def test_parameters_provenance_and_ownership_fail_closed(self):
        mutations=[('schemaVersion',True),('arm','baseline'),('itemCount',1),('diagnosticInstrumented',False),('countersMayAffectScheduling',False),
            ('sourceTree','x'),('diagnosticTree','x'),('diagnosticCommit','x'),('runtimeVersion','10.0.2'),('workers',2),('mode','dynamic'),('transport','memory'),
            ('requestedRpcs',1),('completedRpcs',255),('completedItems',25599),('checksum',0),('connectionPoolMax',2),('requestTimeout','enabled'),('clientInterceptors',0),('serverInterceptors',1),
            ('finalFenceCompleted',False),('shutdownCompleted',False),('disposeCompleted',False),('generatedManifest',{}),('scope','performance'),('receiverMeaning','credit_granted'),('elapsedTicks',1)]
        for key,value in mutations:
            d=result();d[key]=value
            with self.subTest(key=key),self.assertRaises(g.GateError):g.validate_result(d,'candidate',100)
        for key in ('beforeDisposeOwnership','afterDisposeOwnership'):
            d=result();d[key]['queuedSendBytes']=21
            with self.assertRaises(g.GateError):g.validate_result(d,'candidate',100)
        d=result();d['afterDisposeCounts']['Server']['HandlerCompleted']+=1
        with self.assertRaises(g.GateError):g.validate_result(d,'candidate',100)
        d=result();d['initialCounts']['Client']['Entered']=1
        with self.assertRaises(g.GateError):g.validate_result(d,'candidate',100)

    def test_pending_credit_or_receive_state_is_not_quiescent(self):
        for field,value in [('receiveStates',1),('consumedCreditUpdates',1),('pendingConnectionConsumed',4),('receiveConnectionCredit',4095),('connectionWindow',0),('Quiescent',False)]:
            d=result();d['beforeDisposeFlowState'][field]=value;d['afterDisposeFlowState']=copy.deepcopy(d['beforeDisposeFlowState'])
            with self.subTest(field=field),self.assertRaises(g.GateError):g.validate_result(d,'candidate',100)
        d=result();d['afterDisposeFlowState']['connectionWindow']=8192;d['afterDisposeFlowState']['receiveConnectionCredit']=8192
        with self.assertRaises(g.GateError):g.validate_result(d,'candidate',100)

    def test_server_debt_and_handler_completion_not_grant_claim(self):
        for field,value in [('sendStates',1),('activeSendStreamCount',1),('waiters',1),('pendingSendStateWaiterCount',1),('queuedSendBytes',21),('sendConnectionCredit',4095),('connectionWindow',0),('Quiescent',False)]:
            d=result();d['beforeDisposeServerFlowState'][field]=value;d['afterDisposeServerFlowState']=copy.deepcopy(d['beforeDisposeServerFlowState'])
            with self.subTest(field=field),self.assertRaises(g.GateError):g.validate_result(d,'candidate',100)
        d=result();d['afterDisposeServerFlowState']['connectionWindow']=8192;d['afterDisposeServerFlowState']['sendConnectionCredit']=8192
        with self.assertRaises(g.GateError):g.validate_result(d,'candidate',100)
        row=g.validate_result(result(),'candidate',100)
        self.assertEqual(row['receiver_meaning'],'handler_completed_not_credit_granted')
        self.assertIn('peer_handler_completed',row);self.assertIn('received_credit_argument_bytes',row)
        self.assertNotIn('peer_applied',row);self.assertNotIn('granted_credit_bytes',row)

    def test_bounded_four_scalar_records_no_timing_or_raw_paths(self):
        records=[]
        for arm,n in g.schedule():
            d=result(arm,n);d['rawAssertion']='do not export';d['loadedAssemblies']=[dict(location='/private/raw')]
            records.append(g.validate_result(d,arm,n))
        text=json.dumps(records,allow_nan=False)
        self.assertNotIn('do not export',text);self.assertNotIn('/private',text)
        self.assertLess(len(text),6000)
        self.assertTrue(all(r['instrumented_diagnostic_not_production_N'] for r in records))

class Provenance(unittest.TestCase):
    def test_two_exact_overlay_hooks_and_parent(self):
        for arm in ('baseline','candidate'):
            request={arm+'_overlay_sha':'a'*40,arm+'_source_sha':'b'*40}
            answers={('rev-parse','HEAD'):'a'*40,('rev-parse','HEAD^{tree}'):g.OVERLAY_TREES[arm],('show','-s','--format=%P','HEAD'):'b'*40,
                ('rev-parse','b'*40+'^{tree}'):g.SOURCE_TREES[arm],('diff','--name-status','--no-renames','b'*40,'HEAD'):'M\tsrc/SharpLink.Runtime/RpcSession.cs\nA\tsrc/SharpLink.Runtime/WindowUpdateCountDiagnostic.cs',('status','--porcelain','--untracked-files=all'):''}
            for path,blob in g.HOOK_BLOBS.items():answers['rev-parse','HEAD:'+path]=blob
            with patch.object(g,'git',side_effect=lambda _,*a:answers[a]):
                g.verify_overlay(Path('/overlay'),request,arm)
                for key in answers:
                    old=answers[key];answers[key]='wrong'
                    with self.subTest(arm=arm,key=key),self.assertRaises(g.GateError):g.verify_overlay(Path('/overlay'),request,arm)
                    answers[key]=old

    def test_request_only_tool_and_product_ancestry(self):
        with tempfile.TemporaryDirectory() as temp:
            root=Path(temp);path=root/g.REQUEST;path.parent.mkdir(parents=True)
            request=dict(schema_version=1,baseline_source_sha='a'*40,candidate_source_sha='b'*40,baseline_overlay_sha='1'*40,candidate_overlay_sha='2'*40,tools_sha='c'*40,tools_tree='d'*40);path.write_text(json.dumps(request))
            files=json.loads((g.HERE/'tool-files.json').read_text())
            answers={('rev-parse','HEAD'):'e'*40,('status','--porcelain','--untracked-files=all'):'',('show','-s','--format=%P','HEAD'):'c'*40,
                ('diff','--name-status','--no-renames','c'*40,'HEAD'):'A\t'+g.REQUEST,('rev-parse','c'*40+'^{tree}'):'d'*40,('show','-s','--format=%P','c'*40):'b'*40,
                ('show','-s','--format=%P','b'*40):'a'*40,('show','-s','--format=%P','a'*40):g.BASE_PARENT,('rev-parse','a'*40+'^{tree}'):g.SOURCE_TREES['baseline'],('rev-parse','b'*40+'^{tree}'):g.SOURCE_TREES['candidate'],
                ('diff','--name-status','--no-renames','a'*40,'b'*40):'\n'.join(g.SOURCE_DIFF),('diff','--name-status','--no-renames','b'*40,'c'*40):'\n'.join(sorted('A\t'+p for p in files))}
            with patch.object(g,'git',side_effect=lambda _,*a:answers[a]):
                g.verify_release(root,'e'*40,g.BRANCH)
                for key in answers:
                    old=answers[key];answers[key]='wrong'
                    with self.subTest(key=key),self.assertRaises(g.GateError):g.verify_release(root,'e'*40,g.BRANCH)
                    answers[key]=old
                with self.assertRaises(g.GateError):g.verify_release(root,'e'*40,'refs/heads/main')

    def test_loaded_binary_runtime_closure_rejects_mixed_arm_and_missing_identities(self):
        with tempfile.TemporaryDirectory() as temp:
            root=Path(temp);binary=root/'bin';binary.mkdir();host=root/'private/dotnet';host.parent.mkdir()
            core=host.parent/'shared/Microsoft.NETCore.App/10.0.14';core.mkdir(parents=True)
            asp=host.parent/'shared/Microsoft.AspNetCore.App/10.0.14';asp.mkdir(parents=True)
            names=['SharpLink.Runtime','SharpLink.Client','SharpLink.Server','SharpLink.Abstractions','ScalarStreamModule','SharpLink.Benchmarks']
            rows=[]
            for name in names:
                path=binary/(name+'.dll');path.write_bytes(name.encode())
                rows.append(dict(name=name,location=str(path),mvid='12345678-1234-1234-1234-123456789abc',sha256=hashlib.sha256(path.read_bytes()).hexdigest()))
            expected=g.hashes(binary)
            d=dict(binaryHashes=expected,frameworkLocations=[str(core/'System.Private.CoreLib.dll'),str(asp/'Microsoft.AspNetCore.dll')],loadedAssemblies=rows,loadedAssemblyCount=len(rows))
            g.identity(d,binary,expected,str(host))
            for kind in ('inventory','framework','mixed','duplicate','missing'):
                bad=copy.deepcopy(d)
                if kind=='inventory':bad['binaryHashes']={}
                if kind=='framework':bad['frameworkLocations'][0]='/wrong/runtime.dll'
                if kind=='mixed':
                    path=core/'SharpLink.Runtime.dll';path.write_bytes(names[0].encode());bad['loadedAssemblies'][0]['location']=str(path)
                if kind=='duplicate':bad['loadedAssemblies'].append(copy.deepcopy(rows[0]));bad['loadedAssemblyCount']+=1
                if kind=='missing':bad['loadedAssemblies'].pop();bad['loadedAssemblyCount']-=1
                with self.subTest(kind=kind),self.assertRaises(g.GateError):g.identity(bad,binary,expected,str(host))

    def test_worker_and_workflow_no_timing_or_per_rpc_drain(self):
        root=Path(__file__).resolve().parents[2];workflow=(root/'.github/workflows/typed-window-counts-no-upload.yml').read_text();program=(g.HERE/'fixture/worker/Program.cs').read_text()
        for token in ('actions/upload-artifact@','actions/cache@','sudo ','normal-measure','workflow_dispatch:'):self.assertNotIn(token,workflow)
        for token in ('Stopwatch','TotalProcessorTime','GetAllocatedBytes','GetTotalAllocatedBytes','normal-measure'):self.assertNotIn(token,program)
        self.assertIn('invoke(itemCount, CancellationToken.None)',program)
        self.assertEqual(program.count('FlushSendQueueAsync('),1)
        self.assertIn('System.Threading.Lock gate',program)
        self.assertIn('itemCount == 1 ? 1 : 256',program)
        self.assertIn('contents: read',workflow);self.assertIn('persist-credentials: false',workflow)

if __name__=='__main__':unittest.main(verbosity=2)
