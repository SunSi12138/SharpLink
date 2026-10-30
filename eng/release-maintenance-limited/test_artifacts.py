import copy, json, tempfile, unittest
from pathlib import Path
from validate import ORDERS, validate_dataset, runtime_environment, expected_partial, read_partial, validate_partial
from prepare import CANDIDATE,TREE
from test_protocol import sample

def dataset():
    digest='a'*64;runtime='/runtime/dotnet';build='/build';output='/output'
    fixture={'probe/Program.cs':digest};products={'src/product.cs':digest}
    runtime_names=['dotnet','shared/Microsoft.NETCore.App/10.0.14/System.Private.CoreLib.dll','shared/Microsoft.NETCore.App/10.0.14/libcoreclr.so','shared/Microsoft.NETCore.App/10.0.14/libclrjit.so','shared/Microsoft.NETCore.App/10.0.14/libhostpolicy.so','host/fxr/10.0.14/libhostfxr.so']
    runtime_closure={k:digest for k in runtime_names};runtime_files={'/runtime/'+k:digest for k in runtime_names}
    names=['SharpLink.UnitTests','SharpLink.Client','SharpLink.Server','SharpLink.Runtime','SharpLink.Abstractions','SharpLink.Sdk','HotCostDynamic.Contracts']
    binaries={f'HotCostProbe/release/{name}.dll':digest for name in names}
    binaries.update({'HotCostDynamic.Contracts/release/HotCostDynamic.Contracts.dll':digest,'HotCostDynamic.Services/release/HotCostDynamic.Services.dll':digest})
    inputs=dict(candidate=CANDIDATE,candidateTree=TREE,sdk='10.0.102',runtime='10.0.14',fixtureSources=fixture,productSources=products,runtimeFiles=runtime_files,runtimeClosure=runtime_closure)
    protocol=dict(environment=runtime_environment(runtime),orders=ORDERS,warmupCalls=1250,rounds=5,callsPerRound=5000,processCount=18,processTimeoutSeconds=90,inputIdentity=copy.deepcopy(inputs),binaryIdentity=copy.deepcopy(binaries),runtimeDotnet=runtime,buildRoot=build,outputRoot=output)
    rows=[];hosts=[];receipts=[]
    for i,state in enumerate(s for order in ORDERS for s in order):
        name=f'{i:02d}-{state}';command=[runtime,'exec','--fx-version','10.0.14','--roll-forward','Disable','/build/build/bin/HotCostProbe/release/SharpLink.UnitTests.dll',state,output+'/'+name+'.json'];start=i*100+1
        first=dict(index=i,block=i//3,position=i%3,state=state,output=name+'.json',command=command,startedUnixNs=start,event='started');last=dict(first,event='finished',endedUnixNs=start+50,returncode=0,status='success',processGroupQuiescent=True);receipts.extend([first,last])
        hosts.append(dict(command=command,status='success',exitCode=0,pid=i+100,processGroupQuiescent=True,timedOut=False,descendantLeak=False,startedUnixNs=start+1,endedUnixNs=start+50,wallSeconds=.1))
        row=sample(state);row.update(HostPath=runtime,CoreLibPath='/runtime/shared/Microsoft.NETCore.App/10.0.14/System.Private.CoreLib.dll',CoreLibSha256=digest)
        closure=[dict(Name=name,Path='/build/build/bin/HotCostProbe/release/'+name+'.dll',Sha256=digest,Collectible=False) for name in names]
        closure.append(dict(Name='System.Private.CoreLib',Path=row['CoreLibPath'],Sha256=digest,Collectible=False))
        closure.extend([dict(Name='HotCostDynamic.Services',Path='/build/build/bin/HotCostDynamic.Services/release/HotCostDynamic.Services.dll',Sha256=digest,Collectible=True) for _ in range(2)])
        row['Network']['LoadedAssemblies']=closure;rows.append(row)
    return [protocol,inputs,binaries,receipts,hosts,rows,fixture,products,[expected_partial(row) for row in rows]]

class ArtifactTests(unittest.TestCase):
    def test_valid_recompute(self):self.assertEqual(validate_dataset(*dataset())['states']['off']['pooled']['calls'],150000)
    def reject(self,mutate):
        a=dataset();mutate(a)
        with self.assertRaises((ValueError,KeyError)):validate_dataset(*a)
    def test_missing_or_extra_receipts_hosts_rows(self):
        for index in (3,4,5):
            self.reject(lambda a,index=index:a[index].pop())
            self.reject(lambda a,index=index:a[index].append(a[index][0]))
    def test_wrong_command_order_and_exit(self):
        for key,value in [('command',['other']),('index',1),('block',1),('position',1),('state','post-update'),('output','foreign.json'),('event','finished'),('startedUnixNs',False)]:
            self.reject(lambda a,key=key,value=value:a[3][0].update({key:value}))
        for key,value in [('returncode',1),('status','failed'),('processGroupQuiescent',False),('endedUnixNs',0)]:
            self.reject(lambda a,key=key,value=value:a[3][1].update({key:value}))
    def test_failed_or_uncertain_host(self):
        for key,value in [('command',['other']),('exitCode',False),('pid',0),('status','failed'),('timedOut',True),('descendantLeak',True),('processGroupQuiescent',False),('cleanupError','uncertain'),('wallSeconds',float('nan')),('startedUnixNs',999)]:
            self.reject(lambda a,key=key,value=value:a[4][0].update({key:value}))
    def test_runtime_fixture_product_identity(self):
        self.reject(lambda a:a[6].update({'new.cs':'a'*64}))
        self.reject(lambda a:a[7].clear())
        for key in ('runtimeFiles','runtimeClosure'):
            def clear(a,key=key):a[1][key]={};a[0]['inputIdentity']=copy.deepcopy(a[1])
            self.reject(clear)
        self.reject(lambda a:a[0].update({'binaryIdentity':{}}))
        self.reject(lambda a:a[1].update({'runtime':'10.0.2'}))
        for key,value in [('HostPath','/other/dotnet'),('CoreLibPath','/other/corelib'),('CoreLibSha256','b'*64)]:
            self.reject(lambda a,key=key,value=value:a[5][0].update({key:value}))
    def test_loaded_closure_negative(self):
        self.reject(lambda a:a[5][0]['Network'].update({'LoadedAssemblies':[]}))
        for key,value in [('Path','/foreign/evil.dll'),('Sha256','b'*64),('Collectible',1)]:
            self.reject(lambda a,key=key,value=value:a[5][0]['Network']['LoadedAssemblies'][0].update({key:value}))
        self.reject(lambda a:a[5][0]['Network']['LoadedAssemblies'].pop())
        self.reject(lambda a:a[5][0]['Network']['LoadedAssemblies'][0].update({'Name':'Missing'}))
    def test_missing_truncated_mismatched_partial_rejected(self):
        self.reject(lambda a:a[8].pop())
        self.reject(lambda a:a[8][0].pop())
        self.reject(lambda a:a[8][0].clear())
        self.reject(lambda a:a[8][0][0]['Update'].update({'UpdateCount':99}))
        self.reject(lambda a:a[8][0][1]['Measurement'].update({'ElapsedTicks':99}))
        self.reject(lambda a:a[8][0].reverse())
    def test_partial_file_truncation_and_missing_rejected(self):
        row=sample('off');records=expected_partial(row)
        with tempfile.TemporaryDirectory() as temp:
            path=Path(temp)/'partial.jsonl'
            with self.assertRaises(FileNotFoundError):read_partial(path)
            complete=''.join(json.dumps(record)+'\n' for record in records)
            path.write_text(complete);validate_partial(row,read_partial(path))
            for text in (complete[:-1],complete[:-4]+'\n',''.join(json.dumps(record)+'\n' for record in records[:-1])):
                path.write_text(text)
                with self.assertRaises(ValueError):validate_partial(row,read_partial(path))
    def test_sdk_facade_need_not_be_loaded(self):
        a=dataset()
        for row in a[5]:row['Network']['LoadedAssemblies']=[x for x in row['Network']['LoadedAssemblies'] if x['Name']!='SharpLink.Sdk']
        validate_dataset(*a)
    def test_protocol_and_overlap(self):
        self.reject(lambda a:a[0].update({'processCount':19}))
        self.reject(lambda a:a[0].update({'environment':{}}))
        self.reject(lambda a:a[3][2].update({'startedUnixNs':1}))
