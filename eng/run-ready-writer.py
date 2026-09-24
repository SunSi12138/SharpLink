#!/usr/bin/env python3
"""Run fixed same-host A-ready/B3-ready experiments. No retry or dropping failures."""
import argparse, hashlib, itertools, json, os, pathlib, subprocess, time
ROOT=pathlib.Path(__file__).resolve().parents[1]

def plan():
    cases=[]
    for transport,pgo,window,launch in itertools.product(('sharedmemory','tcp'),(0,1),(8192,524288),(0,1)):
        cases.append((transport,pgo,128,2048 if window==8192 else 4096,16,window,16384,launch))
    # Secondary controls explicitly have one launch, not replicated primary evidence.
    for transport,pgo,streams in itertools.product(('sharedmemory','tcp'),(0,1),(1,8,32)):
        cases.append((transport,pgo,streams,4096,16,8192,16384,0))
    for transport,pgo,streams in itertools.product(('sharedmemory','tcp'),(0,1),(32,128)):
        cases.append((transport,pgo,streams,128,4096,524288,16384,0))
    return cases

def main():
    a=argparse.ArgumentParser();a.add_argument('output',type=pathlib.Path);a.add_argument('--source',required=True);a.add_argument('--begin',type=int,default=0);a.add_argument('--end',type=int,default=36);args=a.parse_args()
    if len(args.source)!=40 or any(c not in '0123456789abcdef' for c in args.source):raise ValueError('exact tree required')
    args.output.mkdir(parents=True,exist_ok=True)
    dll=ROOT/'test/SharpLink.Benchmarks/bin/Release/net10.0/SharpLink.Benchmarks.dll'
    provenance={'source_tree':args.source,'host_sha256':hashlib.sha256(dll.read_bytes()).hexdigest(),'cpu_affinity':sorted(os.sched_getaffinity(0))[:4], 'plan':plan(), 'rounds':4, 'slots':16,'quanta':[1,16],'secondary_controls_have_one_launch':True}
    prov=args.output/'provenance.json'
    if prov.exists() and json.loads(prov.read_text())!=json.loads(json.dumps(provenance)):raise ValueError('mixed source/binary/configuration')
    prov.write_text(json.dumps(provenance,indent=2))
    for i,(transport,pgo,streams,items,bytes_,window,flush,launch) in enumerate(plan()):
        if not args.begin<=i<args.end:continue
        path=args.output/f'{i:02}-{transport}-pgo{pgo}-c{streams}-b{bytes_}-w{window}-f{flush}-r{launch}.json'
        if path.exists():raise FileExistsError('Refusing to overwrite an earlier measurement: '+str(path))
        env=dict(os.environ,SHARPLINK_SOURCE_TREE=args.source,DOTNET_PROCESSOR_COUNT='4',DOTNET_TieredCompilation='1',DOTNET_TieredPGO=str(pgo),DOTNET_ReadyToRun='0',SHARPLINK_READY_ORDER=str(launch))
        cmd=['taskset','-c',','.join(map(str,provenance['cpu_affinity'])),'dotnet',str(dll),'--ready-writer-evidence',transport,str(streams),str(items),str(bytes_),'4',str(window),'16',str(flush),str(path.resolve())]
        print(f'RUN {i+1}/36 '+path.name,flush=True);started=time.monotonic()
        with path.with_suffix('.log').open('w') as log:
            try: code=subprocess.run(cmd,cwd=ROOT,env=env,stdout=log,stderr=subprocess.STDOUT,timeout=240).returncode
            except subprocess.TimeoutExpired:code=124
        path.with_suffix('.exit').write_text(json.dumps({'code':code,'seconds':time.monotonic()-started}))
        if code:raise SystemExit(f'Failed case {path.name}, exit={code}; retained, not retried.')
        print(f'PASS {path.name}',flush=True)
if __name__=='__main__':main()
