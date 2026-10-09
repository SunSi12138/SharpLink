#!/usr/bin/env python3
"""Bounded #732 evidence; isolated frozen baseline and candidate; never edits dev."""
import argparse,hashlib,json,os,pathlib,shutil,subprocess,time
p=argparse.ArgumentParser();p.add_argument('--out',default='artifacts/method-facts');p.add_argument('--rounds',type=int,default=7);p.add_argument('--iterations',type=int,default=2000000);a=p.parse_args()
repo=pathlib.Path(__file__).resolve().parents[3];out=(repo/a.out).resolve();out.mkdir(parents=True,exist_ok=True)
base='ca993a1aa89864755e10d1e35030a612b43d6069';dotnet=shutil.which('dotnet');assert dotnet
os.environ.update(DOTNET_CLI_TELEMETRY_OPTOUT='1',DOTNET_CLI_USE_MSBUILD_SERVER='0',MSBUILDDISABLENODEREUSE='1',DOTNET_PROCESSOR_COUNT='2',TUNIT_MAX_PARALLEL_TESTS='8')

def call(cmd,log,cwd=None,env=None):
 start=time.monotonic()
 with (out/log).open('w') as f:subprocess.run(list(map(str,cmd)),cwd=cwd,stdout=f,stderr=subprocess.STDOUT,check=True,env=env)
 with (out/'build-times.txt').open('a') as f:f.write(f'{log}: {time.monotonic()-start:.3f} seconds\n')

def row(cmd,path,**metadata):
 raw=subprocess.check_output(list(map(str,cmd)),text=True,timeout=120)
 for line in raw.splitlines():
  x=json.loads(line);x.update(metadata)
  with (out/path).open('a') as f:f.write(json.dumps(x)+'\n')

call([dotnet,'--info'],'environment.txt')
(out/'baseline.txt').write_text(base+'\n')
(out/'candidate-sha256.txt').write_text(hashlib.sha256((repo/'eng/perf/method-facts/candidate.patch').read_bytes()).hexdigest()+'\n')
for arm in ('baseline','candidate'):
 work=out/('work-'+arm)
 subprocess.run(['git','worktree','add','--detach',str(work),base],cwd=repo,check=True)
 for name in ('SharpLink.MethodFactsEvidence','SharpLink.MethodFactsRpcEvidence'):
  shutil.copytree(repo/'test'/name,work/'test'/name,ignore=shutil.ignore_patterns('bin','obj'))
 if arm=='candidate':
  subprocess.run(['git','apply',str(repo/'eng/perf/method-facts/candidate.patch')],cwd=work,check=True)
  shutil.copyfile(repo/'eng/perf/method-facts/ServerInvocationDescriptorReuseTests.cs',work/'test/SharpLink.UnitTests/Server/ServerInvocationDescriptorReuseTests.cs')
 for project,prefix in [('SharpLink.MethodFactsEvidence',''),('SharpLink.MethodFactsRpcEvidence','rpc-')]:
  path=work/'test'/project
  common=['-c','Release','-m:1','-nr:false','-p:UseSharedCompilation=false']
  call([dotnet,'build',path,*common],f'{prefix}build-{arm}.log')
  shutil.copytree(path/'bin/Release/net10.0',out/(prefix+arm),dirs_exist_ok=True)
  call([dotnet,'publish',path,*common,'-r','linux-x64','-p:PublishAot=true','-p:StripSymbols=false','-o',out/(prefix+'aot-'+arm)],f'{prefix}aot-{arm}.log')
 if arm=='candidate':
  call([dotnet,'build',work/'test/SharpLink.UnitTests','-c','Release','-m:1','-nr:false','-p:UseSharedCompilation=false'],'tests-build.log')
  call([dotnet,work/'test/SharpLink.UnitTests/bin/Release/net10.0/SharpLink.UnitTests.dll'],'tests.log')
 with (out/'source-sha256.txt').open('a') as f:
  source=work/'src/SharpLink.Server/SharpLinkServer.Interceptors.cs'
  f.write(f'{arm} {hashlib.sha256(source.read_bytes()).hexdigest()}\n')
 for prefix in ('','aot-','rpc-','rpc-aot-'):
  path=out/(prefix+arm)/('SharpLink.Benchmarks' if 'aot' in prefix else 'SharpLink.Benchmarks.dll')
  with (out/'binary-sha256.txt').open('a') as f:f.write(f'{prefix}{arm} {hashlib.sha256(path.read_bytes()).hexdigest()}\n')
 with (out/'binary-sha256.txt').open('a') as f:
  f.write(f'{arm} SharpLink.Server.dll {hashlib.sha256((out/arm/"SharpLink.Server.dll").read_bytes()).hexdigest()}\n')
 for profile in ('jit','aot'):
  binary=out/('aot-'+arm if profile=='aot' else arm)/'SharpLink.Benchmarks'
  cmd=[binary] if profile=='aot' else [dotnet,str(binary)+'.dll']
  row(cmd+['counts'],'counts.jsonl',arm=arm,profile=profile)
  row(cmd+['edges'],'edges.jsonl',arm=arm,profile=profile)

call(['python3',repo/'eng/perf/method-facts/verify-counts.py',out/'counts.jsonl',out/'edges.jsonl'],'counts-verification.txt')
profiles={'jit-pgo':{'DOTNET_TieredPGO':'1','DOTNET_TieredCompilation':'1','DOTNET_TC_QuickJitForLoops':'1'},'jit-fullopt':{'DOTNET_TieredCompilation':'0'},'aot':{}}
shapes=['plain-context','intercept-context','dynamic-plain-context','dynamic-intercept-context']
for profile,env in profiles.items():
 os.environ.update(env)
 for pair in range(a.rounds):
  for shape in shapes:
   for arm in (('baseline','candidate') if pair%2==0 else ('candidate','baseline')):
    binary=out/('aot-'+arm if profile=='aot' else arm)/'SharpLink.Benchmarks'
    cmd=[binary] if profile=='aot' else [dotnet,str(binary)+'.dll']
    row(cmd+['local',shape,str(a.iterations)],'rows.jsonl',arm=arm,profile=profile,pair=pair)
for profile in ('jit-pgo','aot'):
 os.environ.update(profiles['jit-pgo'])
 for arm in ('baseline','candidate'):
  binary=out/('rpc-aot-'+arm if profile=='aot' else 'rpc-'+arm)/'SharpLink.Benchmarks'
  cmd=[binary] if profile=='aot' else [dotnet,str(binary)+'.dll']
  for mode in ('plain','intercept','intercept-admission-telemetry'):
   for shape in ('unary','oneway','cancellable','upload','download','duplex'):
    row(cmd+[mode,shape,'256'],'rpc.jsonl',arm=arm,profile=profile)
call(['python3',repo/'eng/perf/method-facts/summarize.py',out/'rows.jsonl'],'summary.json')
for arm in ('baseline','candidate'):
 subprocess.run(['nm','-S','--size-sort',str(out/('aot-'+arm)/'SharpLink.Benchmarks')],stdout=(out/f'symbols-{arm}.txt').open('w'),check=True)
 with (out/'image-sizes.txt').open('a') as f:f.write(f'{arm} {(out/("aot-"+arm)/"SharpLink.Benchmarks").stat().st_size}\n')
