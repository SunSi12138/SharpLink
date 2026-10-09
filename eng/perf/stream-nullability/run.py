#!/usr/bin/env python3
"""Bounded actual-path validation for #731. Does not modify the input checkout."""
import argparse,io,json,os,pathlib,random,shutil,subprocess,tarfile,time
BASE='ca993a1aa89864755e10d1e35030a612b43d6069'
PROJECT='test/SharpLink.NullabilityEvidence'
p=argparse.ArgumentParser();p.add_argument('--out',type=pathlib.Path,required=True);p.add_argument('--dotnet',default='dotnet');p.add_argument('--rounds',type=int,default=3);p.add_argument('--counts',default='1000,10000,100000');p.add_argument('--offline-source');p.add_argument('--aot-tasks');p.add_argument('--aot',action='store_true');p.add_argument('--full',action='store_true');p.add_argument('--ceiling',action='store_true');p.add_argument('--cpus');a=p.parse_args()
root=pathlib.Path(__file__).resolve().parents[3];out=a.out.resolve();out.mkdir(parents=True,exist_ok=True)
env=os.environ.copy();env.update(DOTNET_PROCESSOR_COUNT='2',DOTNET_CLI_TELEMETRY_OPTOUT='1',DOTNET_CLI_USE_MSBUILD_SERVER='0',MSBUILDDISABLENODEREUSE='1',DOTNET_ReadyToRun='0')
cpus=a.cpus or ','.join(str(c) for c in sorted(os.sched_getaffinity(0))[:2])
manifest={'baseline':BASE,'validation_head':subprocess.check_output(['git','rev-parse','HEAD'],cwd=root,text=True).strip(),'cpus':cpus,'rounds':a.rounds,'counts':a.counts,'arms':{},'note':'Unchecked ceiling estimate removes semantic validation and must never be used in production. Timing is not a mathematical upper bound.'}
(out/'environment.txt').write_text(subprocess.check_output([a.dotnet,'--info'],env=env,text=True))
def command(cmd,log,cwd=None,environment=env):
 start=time.perf_counter();result=subprocess.run(cmd,cwd=cwd,env=environment,text=True,stdout=subprocess.PIPE,stderr=subprocess.STDOUT)
 (out/log).write_text(result.stdout)
 if result.returncode:raise RuntimeError(f'{cmd} failed; see {out/log}')
 return time.perf_counter()-start
arms=['baseline','candidate']+(['ceiling'] if a.ceiling else [])
archive=subprocess.check_output(['git','archive',BASE],cwd=root)
for arm in arms:
 work=out/f'work-{arm}';work.mkdir(exist_ok=False)
 with tarfile.open(fileobj=io.BytesIO(archive)) as tar:tar.extractall(work,filter='data')
 shutil.copytree(root/PROJECT,work/PROJECT,ignore=shutil.ignore_patterns('bin','obj'))
 shutil.copy(root/'eng/perf/stream-nullability/StreamNullabilityGuardTests.cs',work/'test/SharpLink.UnitTests/Runtime/StreamNullabilityGuardTests.cs')
 if arm!='baseline':
  for name,var in [('PooledAsyncStreamDispatcher.cs','_payloadNullable'),('RpcSession.GeneratedServerBridge.cs','payloadNullable')]:
   file=work/'src/SharpLink.Runtime'/name;s=file.read_text();old=f'!{var} && default(T) is null && item is null';assert s.count(old)==1
   if arm=='candidate':s=s.replace(old,f'default(T) is null && item is null && !{var}')
   else:
    lines=s.splitlines(True);start=next(i for i,line in enumerate(lines) if old in line);end=start+1
    while lines[end].strip()!='}':end+=1
    s=''.join(lines[:start]+lines[end+1:])
   file.write_text(s)
 common=['-c','Release','-m:1','-nr:false','-p:UseSharedCompilation=false']
 if a.offline_source:common+=['-p:RestoreSources='+a.offline_source,'-p:NuGetAudit=false']
 elapsed=command([a.dotnet,'build',str(work/PROJECT)]+common,f'build-{arm}.log')
 shutil.copytree(work/PROJECT/'bin/Release/net10.0',out/arm)
 manifest['arms'][arm]={'managed_build_seconds':elapsed}
 if a.aot:
  native=common+['-r','linux-x64','-p:PublishAot=true','-p:StripSymbols=true','-p:CppCompilerAndLinker=gcc','-o',str(out/f'aot-{arm}')]
  if a.aot_tasks:native+=['-p:CustomAfterMicrosoftCommonTargets='+a.aot_tasks]
  elapsed=command([a.dotnet,'publish',str(work/PROJECT)]+native,f'aot-{arm}.log')
  manifest['arms'][arm].update(aot_build_seconds=elapsed,aot_image_bytes=(out/f'aot-{arm}'/'SharpLink.Benchmarks').stat().st_size)
 if arm=='candidate':
  command([a.dotnet,'build',str(work/'test/SharpLink.UnitTests')]+common,'unit-build.log')
  command([a.dotnet,str(work/'test/SharpLink.UnitTests/bin/Release/net10.0/SharpLink.UnitTests.dll'),'--treenode-filter','/*/SharpLink.UnitTests.Runtime/*Stream*/*','--no-ansi','--progress','off','--maximum-parallel-tests','1'],'unit-stream-tests.log')
(out/'manifest.json').write_text(json.dumps(manifest,indent=2))
def measure(mode,shapes,counts,rounds,runtimes,eligible):
 for r in range(rounds):
  cases=[(rt,shape,count) for rt in runtimes for shape in shapes for count in counts];random.Random(731+r).shuffle(cases)
  for rt,shape,count in cases:
   for arm in (eligible if r%2==0 else eligible[::-1]):
    ev=env.copy();ev.update(DOTNET_TieredCompilation='1' if rt=='tiered' else '0',DOTNET_TieredPGO='1' if rt=='tiered' else '0')
    prefix=[str(out/f'aot-{arm}'/'SharpLink.Benchmarks')] if rt=='aot' else [a.dotnet,str(out/arm/'SharpLink.Benchmarks.dll')]
    n=max(1,(2000000 if mode=='dispatch' else 1000000 if mode=='pump' else 200000)//count)
    cmd=['taskset','-c',cpus]+prefix+[mode,shape,str(count),str(n)];start=time.time();result=subprocess.run(cmd,env=ev,text=True,capture_output=True,timeout=180)
    if result.returncode:raise RuntimeError(f'{cmd}: {result.stdout}\n{result.stderr}')
    row=json.loads(result.stdout.strip().splitlines()[-1]);row.update(Arm=arm,Runtime=rt,Round=r,Seconds=time.time()-start)
    with (out/'rows.jsonl').open('a') as f:f.write(json.dumps(row)+'\n')
    print(json.dumps(row),flush=True)
runtimes=['fullopt','tiered']+(['aot'] if a.aot else [])
measure('dispatch',['required','nullable','value','nullable-value','half-null','all-null','realistic'],[int(n) for n in a.counts.split(',')],a.rounds,runtimes,['baseline','candidate'])
measure('pump',['required','nullable','value','nullable-value','half-null','all-null','realistic'],[int(n) for n in a.counts.split(',')],a.rounds,runtimes,['baseline','candidate'])
if a.ceiling:
 for mode in ['dispatch','pump']:measure(mode,['required','nullable','value'],[10000],a.rounds,runtimes,['baseline','ceiling'])
if a.full:measure('rpc',[f'{d}-{s}' for d in ['in','out'] for s in ['required','nullable','value','half-null','all-null','realistic']],[1000,10000,100000],1,['tiered']+(['aot'] if a.aot else []),['baseline','candidate'])
