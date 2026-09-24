#!/usr/bin/env python3
"""Matched count-only vs byte-bounded preparation controls. Never retry/overwrite samples."""
import argparse, hashlib, itertools, json, os, pathlib, subprocess, time
ROOT = pathlib.Path(__file__).resolve().parents[1]

def plan():
    return [[t, pgo, 128, 128 if size == 4096 else 2048, size,
             524288 if size == 4096 else 8192, 16384, launch, budget]
            for t, pgo, size, launch, budget in itertools.product(
                ('sharedmemory', 'tcp'), (0, 1), (4096, 16), (0, 1), (0, 8192, 16384))]

def name(index, case):
    t, p, c, n, b, w, f, r, budget = case
    return f'{index:02}-{t}-pgo{p}-b{b}-w{w}-budget{budget}-r{r}.json'

def run_case(cmd, target, env):
    """Exactly one launch. Persist failure without preventing independent cases from running."""
    started=time.monotonic(); error=None
    with target.with_suffix('.log').open('w') as log:
        try:code=subprocess.run(cmd,cwd=ROOT,env=env,stdout=log,stderr=subprocess.STDOUT,timeout=180).returncode
        except subprocess.TimeoutExpired:code=124
        except OSError as failure:
            code=127;error=str(failure);log.write(error+'\n')
    result=dict(code=code,seconds=time.monotonic()-started)
    if error is not None:result['launch_error']=error
    target.with_suffix('.exit').write_text(json.dumps(result))
    return code

def run_selected(output, provenance, dll, begin, end):
    selected=[(i,c) for i,c in enumerate(plan()) if begin<=i<end]
    # Preflight the entire selection; never overwrite a failed report or exit record.
    for index,case in selected:
        target=output/name(index,case)
        if any(path.exists() for path in (target,target.with_suffix('.log'),target.with_suffix('.exit'))):
            raise FileExistsError(target)
    failures=[]
    for index,case in selected:
        t,g,c,n,b,w,f,launch,budget=case; target=output/name(index,case)
        env=dict(os.environ,SHARPLINK_SOURCE_TREE=provenance['source_tree'],DOTNET_PROCESSOR_COUNT='4',DOTNET_TieredCompilation='1',
                 DOTNET_TieredPGO=str(g),DOTNET_ReadyToRun='0',SHARPLINK_READY_ORDER=str(launch),
                 SHARPLINK_READY_PREPARED_BYTES=str(budget),SHARPLINK_READY_ALLOCATION_DIAGNOSTIC='0')
        cmd=['taskset','-c',','.join(map(str,provenance['cpu_affinity'])),'dotnet',str(dll),
             '--ready-writer-evidence',t,str(c),str(n),str(b),'4',str(w),'16',str(f),str(target.resolve())]
        print('RUN',index,name(index,case),flush=True)
        code=run_case(cmd,target,env)
        if code:
            failures.append((index,code));print('FAIL',index,code,'retained; no retry',flush=True)
        else:print('PASS',index,flush=True)
    return failures

def main():
    parser=argparse.ArgumentParser(); parser.add_argument('output',type=pathlib.Path)
    parser.add_argument('--source',required=True); parser.add_argument('--begin',type=int,default=0)
    parser.add_argument('--end',type=int,default=48); args=parser.parse_args()
    if len(args.source)!=40 or any(c not in '0123456789abcdef' for c in args.source):raise ValueError('full tree required')
    actual=subprocess.check_output(['git','write-tree'],cwd=ROOT,text=True).strip()
    subprocess.run(['git','diff','--exit-code'],cwd=ROOT,check=True,stdout=subprocess.DEVNULL)
    if actual!=args.source:raise ValueError('index does not match claimed source')
    dll=ROOT/'test/SharpLink.Benchmarks/bin/Release/net10.0/SharpLink.Benchmarks.dll'
    provenance=dict(source_tree=actual,host_sha256=hashlib.sha256(dll.read_bytes()).hexdigest(),
                    cpu_affinity=sorted(os.sched_getaffinity(0))[:4], plan=plan(),rounds=4,slots=16,
                    quanta=[1,16],budgets=[0,8192,16384],allocation_diagnostic=False)
    args.output.mkdir(parents=True,exist_ok=True); path=args.output/'provenance.json'
    if path.exists() and json.loads(path.read_text())!=provenance:raise ValueError('mixed source/binary/configuration')
    path.write_text(json.dumps(provenance,indent=2))
    if not 0<=args.begin<args.end<=len(plan()):raise ValueError('invalid selected range')
    failures=run_selected(args.output,provenance,dll,args.begin,args.end)
    if failures:raise SystemExit(f'{len(failures)} failed processes retained: {failures}; entire selection attempted once.')
if __name__=='__main__':main()
