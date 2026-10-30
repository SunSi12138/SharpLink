import copy, unittest
from validate import validate_process, summarize, ORDERS
from verify_request import validate_request, strict_json, BRANCH, MESSAGE, CAMPAIGN, REPOSITORY, DEV, DEV_TREE, REQUEST, DORMANT

def capture(enabled,g):
    return dict(CatalogGeneration=g,MaintenanceConfigured=enabled,EnrollmentPresent=enabled,EnrollmentAbsent=not enabled,ClientReady=2,ServerPhysical=2,SameConnectionObjects=True,SameSessionObjects=True,AllReady=True,AuthenticatedTls=True,AuthenticationCalls=2,TlsProtocols=['Tls13']*2,TlsCipherSuites=['TLS_AES_256_GCM_SHA384']*2,Capabilities=['CooperativeMaintenance' if enabled else 'None']*2)

def sample(state):
    enabled=state!='off';post=state=='post-update';g=4+int(post)
    n=dict(UpdateStartedTicks=100 if post else 0,UpdateEndedTicks=110 if post else 0,UpdateAllocatedBefore=100 if post else 0,UpdateAllocatedAfter=105 if post else 0,State=state,MaintenanceEnabled=enabled,WarmupCalls=1250,RoundCount=5,CallsPerRound=5000,MeasuredRpcCalls=25000,BeforeResult=30,Result=31 if post else 30,HeldProxyAcrossPhases=True,OriginalGeneration=4,SteadyGeneration=g,GenerationAdvanced=post,UpdateCount=int(post),UpdateStage='Succeeded' if post else 'NotRequested',UpdateElapsedTicks=10 if post else 0,UpdateAllocatedBytes=5 if post else 0,OldCreated=1,OldDisposed=int(post),ReplacementCreated=int(post),Before=capture(enabled,4),PreUpdate=capture(enabled,4),Resumed=capture(enabled,g),After=capture(enabled,g),Frequency=1000000,Rounds=[])
    for i in range(5):n['Rounds'].append(dict(StartedTicks=1000+i*70000,EndedTicks=61000+i*70000,AllocatedBefore=1000+i*60000,AllocatedAfter=51000+i*60000,Round=i,Calls=5000,Frequency=1000000,ElapsedTicks=60000,AllocatedBytes=50000,LatencyTicks=[10]*5000,Before=capture(enabled,g),After=capture(enabled,g)))
    return dict(Protocol='maintenance-tls-unary-limited-v1',RuntimeVersion='10.0.14',Architecture='X64',Settings={'DOTNET_TieredCompilation':'1','DOTNET_TieredPGO':'1','DOTNET_TC_QuickJit':'1','DOTNET_TC_QuickJitForLoops':'1','DOTNET_ReadyToRun':'1','DOTNET_gcServer':'0'},Network=n)

class ProtocolTests(unittest.TestCase):
    def test_valid_all_states_and_metrics(self):
        rows=[sample(s) for order in ORDERS for s in order];result=summarize(rows)
        self.assertEqual(result['states']['off']['pooled']['calls'],150000)
        self.assertEqual(result['states']['off']['pooled']['p99Microseconds'],10)
    def test_negative_invariants(self):
        changes=[('State','off'),('MaintenanceEnabled',False),('WarmupCalls',0),('RoundCount',4),('CallsPerRound',4999),('MeasuredRpcCalls',1),('BeforeResult',31),('Result',30),('HeldProxyAcrossPhases',False),('OriginalGeneration',True),('SteadyGeneration',4),('GenerationAdvanced',False),('UpdateCount',2),('UpdateStage','Failed'),('UpdateElapsedTicks',0),('UpdateAllocatedBytes',-1),('OldCreated',0),('OldDisposed',0),('ReplacementCreated',0),('Frequency',0),('UpdateStartedTicks',99),('UpdateEndedTicks',111),('UpdateAllocatedBefore',99),('UpdateAllocatedAfter',106)]
        for key,value in changes:
            with self.subTest(key=key):
                d=sample('post-update');d['Network'][key]=value
                with self.assertRaises(ValueError):validate_process(d,'post-update')
    def test_overlapping_round_intervals_rejected(self):
        d=sample('off')
        for r in d['Network']['Rounds']:r.update(StartedTicks=1000,EndedTicks=61000)
        with self.assertRaises(ValueError):validate_process(d,'off')
    def test_post_update_after_measurement_rejected(self):
        d=sample('post-update');d['Network'].update(UpdateStartedTicks=1000000,UpdateEndedTicks=1000010)
        with self.assertRaises(ValueError):validate_process(d,'post-update')
    def test_later_cumulative_allocation_regression_rejected(self):
        d=sample('off');d['Network']['Rounds'][2].update(AllocatedBefore=1,AllocatedAfter=50001)
        with self.assertRaises(ValueError):validate_process(d,'off')
    def test_update_allocation_after_first_round_rejected(self):
        d=sample('post-update');d['Network'].update(UpdateAllocatedBefore=1000000,UpdateAllocatedAfter=1000005)
        with self.assertRaises(ValueError):validate_process(d,'post-update')
    def test_raw_latency_rejections(self):
        for value in ([],[10]*4999,[True]*5000,[0]*5000,[-1]*5000,[float('nan')]*5000,[float('inf')]*5000,[20]*5000):
            d=sample('off');d['Network']['Rounds'][0]['LatencyTicks']=value
            with self.assertRaises(ValueError):validate_process(d,'off')
    def test_round_and_capture_rejections(self):
        for key,value in [('Round',1),('Calls',4999),('Frequency',1),('ElapsedTicks',0),('AllocatedBytes',-1),('StartedTicks',0),('EndedTicks',60002),('AllocatedBefore',99),('AllocatedAfter',50101)]:
            d=sample('off');d['Network']['Rounds'][0][key]=value
            with self.assertRaises(ValueError):validate_process(d,'off')
        for capture_name in ('Before','PreUpdate','Resumed','After'):
            for key,value in [('CatalogGeneration',99),('MaintenanceConfigured',True),('EnrollmentPresent',True),('EnrollmentAbsent',False),('ClientReady',1),('ServerPhysical',3),('AuthenticationCalls',3),('SameConnectionObjects',False),('SameSessionObjects',False),('AllReady',False),('AuthenticatedTls',False),('TlsProtocols',['None']*2),('TlsCipherSuites',[]),('Capabilities',['CooperativeMaintenance']*2)]:
                d=sample('off');d['Network'][capture_name][key]=value
                with self.subTest(capture=capture_name,key=key),self.assertRaises(ValueError):validate_process(d,'off')
    def test_runtime_and_order(self):
        for key,value in [('RuntimeVersion','10.0.2'),('Architecture','Arm64'),('Settings',{}),('Protocol','historical')]:
            d=sample('off');d[key]=value
            with self.assertRaises(ValueError):validate_process(d,'off')
        rows=[sample(s) for order in ORDERS for s in order]
        with self.assertRaises(ValueError):summarize(rows[:-1])
        rows[0],rows[1]=rows[1],rows[0]
        with self.assertRaises(ValueError):summarize(rows)
    def test_off_no_update_cost_or_replacement(self):
        for key,value in [('UpdateElapsedTicks',1),('UpdateAllocatedBytes',1),('OldDisposed',1),('ReplacementCreated',1),('UpdateCount',1)]:
            d=sample('on-no-update');d['Network'][key]=value
            with self.assertRaises(ValueError):validate_process(d,'on-no-update')

class RequestTests(unittest.TestCase):
    def args(self):
        head='1'*40;parent='2'*40;tree='3'*40
        data=dict(state='authorized',campaign=CAMPAIGN,reviewedToolCommit=parent,reviewedToolTree=tree,reviewStatus='PASS',rootRelease='authorized',devSource=DEV,devTree=DEV_TREE)
        env=dict(GITHUB_EVENT_NAME='push',GITHUB_REF=BRANCH,GITHUB_RUN_ATTEMPT='1',GITHUB_REPOSITORY=REPOSITORY,GITHUB_SHA=head)
        event=dict(deleted=False,forced=False,before=parent,after=head,ref=BRANCH,commits=[{'id':head}],head_commit={'id':head,'message':MESSAGE},repository={'full_name':REPOSITORY})
        return [data,env,head,parent,tree,[REQUEST],MESSAGE,True,DORMANT,event]
    def test_positive(self):validate_request(*self.args())
    def test_request_changes(self):
        for key,value in [('state','review-required'),('campaign','other'),('reviewedToolCommit','0'*40),('reviewedToolTree','0'*40),('reviewStatus','pending'),('rootRelease','pending'),('devSource','0'*40),('devTree','0'*40),('unexpected',1)]:
            a=self.args();a[0][key]=value
            with self.subTest(key=key),self.assertRaises(ValueError):validate_request(*a)
    def test_environment_and_push_changes(self):
        for key,value in [('GITHUB_EVENT_NAME','workflow_dispatch'),('GITHUB_REF','refs/heads/main'),('GITHUB_RUN_ATTEMPT','2'),('GITHUB_REPOSITORY','fork/repo'),('GITHUB_SHA','0'*40)]:
            a=self.args();a[1][key]=value
            with self.assertRaises(ValueError):validate_request(*a)
        for key,value in [('deleted',True),('forced',True),('before','0'*40),('after','0'*40),('ref','main'),('commits',[]),('head_commit',{})]:
            a=self.args();a[9][key]=value
            with self.assertRaises(ValueError):validate_request(*a)
    def test_commit_changes(self):
        for index,value in [(5,[REQUEST,'src/evil.cs']),(6,'other'),(7,False),(8,{'state':'authorized'})]:
            a=self.args();a[index]=value
            with self.assertRaises(ValueError):validate_request(*a)
    def test_duplicate_nonfinite_json(self):
        for value in ('{"a":1,"a":2}','{"a":NaN}','{"a":Infinity}'):
            with self.assertRaises(ValueError):strict_json(value)

if __name__=='__main__':unittest.main()
