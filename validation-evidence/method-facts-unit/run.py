#!/usr/bin/env python3
"""One bounded matched unit confirmation; preserves all failures, no performance rerun."""
import hashlib,json,os,pathlib,shutil,subprocess,sys,time
repo=pathlib.Path(__file__).resolve().parents[2]
out=repo/'artifacts/method-facts-unit';out.mkdir(parents=True,exist_ok=True)
base='ca993a1aa89864755e10d1e35030a612b43d6069';dotnet=shutil.which('dotnet');assert dotnet
os.environ.update(DOTNET_CLI_TELEMETRY_OPTOUT='1',DOTNET_CLI_USE_MSBUILD_SERVER='0',MSBUILDDISABLENODEREUSE='1',DOTNET_PROCESSOR_COUNT='2',TUNIT_MAX_PARALLEL_TESTS='8')
results=[]
def call(cmd,name):
 start=time.monotonic()
 with (out/(name+'.log')).open('w') as f:
  p=subprocess.run(list(map(str,cmd)),stdout=f,stderr=subprocess.STDOUT,timeout=600)
 results.append({'name':name,'exit':p.returncode,'seconds':time.monotonic()-start})
 (out/'results.json').write_text(json.dumps(results,indent=2))
 return p.returncode
call([dotnet,'--info'],'environment')
for arm in ('baseline','candidate'):
 work=out/('work-'+arm)
 subprocess.run(['git','worktree','add','--detach',str(work),base],cwd=repo,check=True)
 if arm=='candidate':
  subprocess.run(['git','apply',str(repo/'eng/perf/method-facts/candidate.patch')],cwd=work,check=True)
  shutil.copyfile(repo/'eng/perf/method-facts/ServerInvocationDescriptorReuseTests.cs',work/'test/SharpLink.UnitTests/Server/ServerInvocationDescriptorReuseTests.cs')
  shutil.copyfile(repo/'validation-evidence/method-facts-unit/ServerInvocationModuleDescriptorTests.cs',work/'test/SharpLink.UnitTests/Server/ServerInvocationModuleDescriptorTests.cs')
 with (out/'source-sha256.txt').open('a') as f:
  for file in ['src/SharpLink.Server/SharpLinkServer.Interceptors.cs','test/SharpLink.UnitTests/Runtime/PooledAsyncStreamDispatcherTests.cs']:
   f.write(f'{arm} {file} {hashlib.sha256((work/file).read_bytes()).hexdigest()}\n')
 code=call([dotnet,'build',work/'test/SharpLink.UnitTests','-c','Release','-m:1','-nr:false','-p:UseSharedCompilation=false'],arm+'-build')
 if code:continue
 binary=work/'test/SharpLink.UnitTests/bin/Release/net10.0/SharpLink.UnitTests.dll'
 call([dotnet,binary],arm+'-full')
 for repeat in range(3):
  call([dotnet,binary,'--treenode-filter','/*/*/PooledAsyncStreamDispatcherTests/EarlyDisposeShouldNotPoolWhileProducerIsDecoding'],arm+'-exact-'+str(repeat))
sys.exit(int(any(r['exit'] for r in results)))
