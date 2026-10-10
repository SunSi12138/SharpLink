#!/usr/bin/env python3
"""Fresh-process validation only. Preserves every sample and fails closed on schema/source errors."""
import argparse, hashlib, json, os, pathlib, platform, statistics, subprocess, time
from validate import validate
p=argparse.ArgumentParser()
p.add_argument('--dotnet',default='dotnet');p.add_argument('--output',required=True)
p.add_argument('--samples',type=int,default=5);p.add_argument('--operations',type=int,default=131072)
p.add_argument('--bounded-oneway',action='store_true')
p.add_argument('--warmup',type=int,default=32768);p.add_argument('--smoke',action='store_true')
a=p.parse_args(); root=pathlib.Path(__file__).resolve().parents[3]; out=pathlib.Path(a.output).resolve();out.mkdir(parents=True,exist_ok=True)
sha=subprocess.check_output(['git','rev-parse','HEAD'],cwd=root,text=True).strip()
source=os.environ.get('ISSUE739_SOURCE_SHA','eb99fe887cf2129d9b88441245ca0a4a6406b6c2')
if subprocess.check_output(['git','diff',source,'--','src','Directory.Build.props','Directory.Packages.props','global.json'],cwd=root): raise SystemExit('Production sources or build inputs differ from pinned source')
exe=root/'eng/validation/issue739/bin/Release/net10.0/SharpLink.Benchmarks.dll'
def hash_file(p):return hashlib.sha256(p.read_bytes()).hexdigest()
env=dict(os.environ,DOTNET_TieredPGO='1',DOTNET_TieredCompilation='1',DOTNET_gcServer='0',DOTNET_CLI_TELEMETRY_OPTOUT='1',ISSUE739_SOURCE_SHA=source)
manifest={'schemaVersion':2,'sourceSha':source,'sourceBranch':'dev','sourceRole':'primary','runtimePin':os.environ.get('ISSUE739_RUNTIME_VERSION'),'harnessCommit':sha,'harnessDirty':bool(subprocess.check_output(['git','status','--porcelain'],cwd=root)),
'dotnetInfo':subprocess.check_output([a.dotnet,'--info'],text=True),'cpuInfo':pathlib.Path('/proc/cpuinfo').read_text() if pathlib.Path('/proc/cpuinfo').exists() else platform.processor(),
'os':platform.platform(),'environment':{k:v for k,v in env.items() if k.startswith(('DOTNET_','COMPlus_','ISSUE739_'))},
'assemblySha256':{f.name:hash_file(f) for f in exe.parent.glob('*.dll')},'runtimeConfigSha256':{f.name:hash_file(f) for f in exe.parent.glob('*.json')},'harnessSha256':{str(f.relative_to(root)):hash_file(f) for f in pathlib.Path(__file__).parent.glob('*') if f.is_file()},
'policy':{'samples':a.samples,'operations':a.operations,'warmup':a.warmup,'bytesCvMaximum':.05,'qpsCvMaximum':.10,'outlierRemoval':'none','freshProcess':True,'oneConnection':True,'concurrencyMeaning':'receiver-handler credits<=c for bounded variant; otherwise client workers only','sourceComparison':False,'boundedOneWay':a.bounded_oneway,'scope':'Phase1 baseline; sampled owner coverage remains separate'}}
(out/'manifest.json').write_text(json.dumps(manifest,indent=2))
cells=[(t,r,c) for t in ['tcp','shm'] for r in ['add','oneway'] for c in [1,8,32,128]]
controls=[(t,'control',c) for t in ['tcp','shm'] for c in [1,8,32,128]]
if a.bounded_oneway:
    cells=[(t,'oneway-bounded',c) for t in ['tcp','shm'] for c in [1,8,32,128]]
    controls=[(t,'credit-control',c) for t in ['tcp','shm'] for c in [1,8,32,128]]
if a.smoke: cells=[('tcp','add',1),('shm','oneway',8)];controls=[]
results=[]
for sample in range(a.samples):
    order=(cells+controls) if sample%2==0 else list(reversed(cells+controls))
    for t,r,c in order:
        key=f'{t}-{r}-c{c}-s{sample:02}'
        cmd=[a.dotnet,str(exe),t,r,str(c),str(a.operations),str(a.warmup),key,str(out/(key+'.json'))]
        start=time.time()
        result=subprocess.run(cmd,env=env,cwd=root,capture_output=True,text=True,timeout=180)
        (out/(key+'.log')).write_text(result.stdout+result.stderr)
        if result.returncode:raise SystemExit(f'{key}: exit {result.returncode}; see raw log')
        data=json.loads((out/(key+'.json')).read_text())
        validate(data,source)
        results.append(data);print(key,round(data['bytesPerOperation'],3),'B/op',round(time.time()-start,2),'s',flush=True)
summary=[]
for t,r,c in cells+controls:
    rows=[x for x in results if (x['transport'],x['rpc'],x['concurrency'])==(t,r,c)]
    def cv(field):
        values=[x[field] for x in rows];mean=statistics.mean(values)
        return statistics.stdev(values)/mean if len(values)>1 and mean else None
    bcv,qcv=cv('bytesPerOperation'),cv('qps')
    summary.append({'transport':t,'rpc':r,'concurrency':c,'sampleCount':len(rows),'bytesCv':bcv,'qpsCv':qcv,'stable':len(rows)>=5 and bcv is not None and bcv<=.05 and qcv<=.10,'median':{f:statistics.median(x[f] for x in rows) for f in ['bytesPerOperation','gen0','cpuNanosecondsPerOperation','qps','p50Nanoseconds','p99Nanoseconds']}})
(out/'summary.json').write_text(json.dumps({'schemaVersion':2,'cells':summary,'allPrimaryStable':all(x['stable'] for x in summary if x['rpc'] not in ['control','credit-control']),'ownerCoverageStatus':'unproven; requires trace ledger'},indent=2))
