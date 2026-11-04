#!/usr/bin/env python3
"""Local-only parser regression checks. Takes dotnet, parser dll, synthetic trace directory."""
import csv,json,pathlib,subprocess,sys
runtime,parser,root=sys.argv[1:];root=pathlib.Path(root)
trace=root/'synthetic.nettrace';sidecar=root/'phases.jsonl'
def run(name,args,want):
    p=subprocess.run([runtime,parser,*map(str,args)],capture_output=True,text=True)
    assert p.returncode==want,(name,p.returncode,p.stderr)
    print(name+': PASS')
run('usage',[],2)
run('complete synthetic trace',[trace,root/'valid',sidecar],0)
s=json.loads((root/'valid/summary.json').read_text())
assert s['sidecarMatched'] and s['phaseCount']==54 and s['lostEvents']==0
assert s['targetCoverage']['facts-proxy']['liveLoads']>0
assert s['targetCoverage']['facts-proxy']['tiers']
assert s['targetCoverage']['facts-stub']['status']=='not-observed-not-proof-of-absence'
assert s['targetCoverage']['facts-proxy']['loadsMissingRejitId']>0
assert any(k.startswith('live:ThreadPool') for k in s['runtimeEventCounts'])
assert any(k.startswith('live:TieredCompilation') for k in s['runtimeEventCounts'])
assert (root/'valid/summary.json').stat().st_size<25000
spans=list(csv.DictReader((root/'valid/gc-pauses.csv').open()))
assert sum(x['kind']=='gc-collection-not-pause' for x in spans)==20
assert sum(x['kind']=='runtime-suspension' for x in spans)==20
assert all(x['complete']=='True' for x in spans)
for x in spans:
    if x['kind']=='runtime-suspension':
        assert float(x['start_ms'])<=float(x['fully_suspended_ms'])<=float(x['restart_start_ms'])<=float(x['end_ms'])
phases=list(csv.DictReader((root/'valid/phase-runtime-summary.csv').open()))
for kind,column in [('gc-collection-not-pause','gc_collection_overlap_ms_not_pause'),('runtime-suspension','runtime_suspension_overlap_ms')]:
    assert abs(sum(float(x[column]) for x in phases)-sum(float(x['observed_span_ms']) for x in spans if x['kind']==kind))<1e-6
print('method/tier/GC/suspension/ThreadPool/phase-overlap assertions: PASS')
run('missing sidecar',[trace,root/'missing-sidecar',root/'does-not-exist.jsonl'],3)
lines=sidecar.read_text().splitlines();(root/'short.jsonl').write_text('\n'.join(lines[:-1])+'\n')
run('short sidecar',[trace,root/'short-sidecar',root/'short.jsonl'],3)
a=json.loads(lines[0]);a['timestamp']+=1;(root/'tampered.jsonl').write_text(json.dumps(a)+'\n'+'\n'.join(lines[1:])+'\n')
run('payload mismatch',[trace,root/'mismatch',root/'tampered.jsonl'],3)
(root/'bad.nettrace').write_bytes(b'not a trace')
run('malformed trace',[root/'bad.nettrace',root/'malformed',sidecar],1)
assert json.loads((root/'malformed/summary.json').read_text())['parseStatus']=='incomplete'
run('missing trace',[root/'missing.nettrace',root/'missing-trace',sidecar],1)
print('All parser regression checks passed. No RPC workload ran.')
