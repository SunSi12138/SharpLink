import subprocess,os,json,time,sys
from pathlib import Path
root=Path(__file__).resolve().parent
if os.environ.get('AFFINITY'): os.sched_setaffinity(0,{int(os.environ['AFFINITY'])})
profiles={'jit-pgo':{'DOTNET_TieredPGO':'1','DOTNET_TieredCompilation':'1','DOTNET_TC_QuickJitForLoops':'1'},'jit-fullopt':{'DOTNET_TieredCompilation':'0'},'aot':{}}
shapes=['plain','plain-context','intercept','intercept-context','dynamic-plain-context','dynamic-intercept-context']
if os.environ.get('SHAPES'): shapes=os.environ['SHAPES'].split(',')
iterations=int(os.environ.get('ITERATIONS','2000000'));pairs=int(os.environ.get('PAIRS','9'))
only=os.environ.get('PROFILES');profiles={p:v for p,v in profiles.items() if not only or p in only.split(',')}
out=root/os.environ.get('ROWS','rows.jsonl')
for profile,extra in profiles.items():
 for pair in range(pairs):
  for shape in shapes:
   order=['baseline','candidate'] if pair%2==0 else ['candidate','baseline']
   for arm in order:
    env=os.environ.copy();env.update(extra)
    actual='baseline' if os.environ.get('AA')=='1' else (os.environ.get('CANDIDATE','candidate') if arm=='candidate' else arm)
    file=root/('aot-'+actual if profile=='aot' else actual)/'SharpLink.Benchmarks'
    cmd=[str(file)] if profile=='aot' else ['/tmp/dotnet730/dotnet',str(file)+'.dll']
    text=subprocess.check_output(cmd+['local',shape,str(iterations)],env=env,text=True,timeout=60)
    row=json.loads(text.strip());row.update(profile=profile,pair=pair,arm=arm,actual=actual,affinity=os.environ.get('AFFINITY'),calibration=os.environ.get('AA')=='1',variant=os.environ.get('VARIANT','A'))
    with out.open('a') as f:f.write(json.dumps(row)+'\n')
    print(profile,pair,shape,arm,round(row['ns'],2),flush=True)
