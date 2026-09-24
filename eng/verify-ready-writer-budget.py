#!/usr/bin/env python3
"""Offline, fail-closed verification of the entire predeclared byte-budget matrix."""
import argparse, collections, importlib.util, itertools, json, math, pathlib, statistics

def load(name):
    spec=importlib.util.spec_from_file_location(name,pathlib.Path(__file__).with_name(name+'.py'))
    module=importlib.util.module_from_spec(spec);spec.loader.exec_module(module);return module
base=load('verify-ready-writer')

def expected_plan():
    # Deliberately independent of runner code; additions require explicit review here.
    cases=[]
    for transport in ('sharedmemory','tcp'):
        for pgo in (0,1):
            for size,items,window in ((4096,128,524288),(16,2048,8192)):
                for launch in (0,1):
                    for budget in (0,8192,16384):
                        cases.append([transport,pgo,128,items,size,window,16384,launch,budget])
    return cases

def validate_document(doc, source, case):
    samples=base.validate_report(doc,source,case[:8]);m=doc['metadata'];budget=case[8]
    if m.get('preparedByteBudget')!=budget or m.get('allocationDiagnostic') is not False:raise ValueError('wrong byte cap/diagnostic contamination')
    for s in samples:
        rm=s['ReadyWriterMetrics']; maxpacket=rm['MaximumObservedPacketBytes'];peak=rm['MaximumQueuedBytesPerStream']
        if rm['PreparedByteBudgetPerStream']!=budget or rm['AllocationDiagnostic']!=0:raise ValueError('sample cap/diagnostic mismatch')
        if 'ProducerPreparationAllocatedBytes' in rm:raise ValueError('disabled attribution must not be fabricated as zero')
        if rm['RemainingQueuedBytes']!=0 or maxpacket<=s['ItemBytes'] or peak<maxpacket:raise ValueError('buffer accounting mismatch')
        limit=max(budget,maxpacket) if budget else rm['RingSlotsPerStream']*maxpacket
        if peak>limit or rm['SumOfStreamQueuedBytePeaks']>s['Streams']*limit:raise ValueError('serialized-byte or oversized-ring bound exceeded')
    return samples

def summarize(root):
    root=pathlib.Path(root);p=json.loads((root/'provenance.json').read_text())
    if p.get('plan')!=expected_plan() or p.get('budgets')!=[0,8192,16384] or p.get('allocation_diagnostic') is not False:raise ValueError('altered population')
    for k,size in [('source_tree',40),('host_sha256',64)]:
        if len(p.get(k,''))!=size or any(c not in '0123456789abcdef' for c in p[k]):raise ValueError('invalid revision/digest')
    if p.get('rounds')!=4 or p.get('slots')!=16 or p.get('quanta')!=[1,16]:raise ValueError('changed sample policy')
    affinity=p['cpu_affinity']
    if len(affinity)!=4 or len(set(affinity))!=4 or any(type(x) is not int or x<0 for x in affinity):raise ValueError('wrong affinity')
    rows=[];names=set()
    for i,case in enumerate(expected_plan()):
        t,g,c,n,b,w,f,r,budget=case
        name=f'{i:02}-{t}-pgo{g}-b{b}-w{w}-budget{budget}-r{r}.json';names.add(name);path=root/name
        exit=json.loads(path.with_suffix('.exit').read_text())
        if exit['code']!=0:raise ValueError('nonzero exit')
        rows.extend(dict(x,Pgo=g,Launch=r,Budget=budget,Report=name) for x in validate_document(json.loads(path.read_text()),p['source_tree'],case))
    if {x.name for x in root.glob('[0-9]*.json')}!=names:raise ValueError('extra or missing reports')
    groups=collections.defaultdict(list)
    for x in rows:groups[(x['Transport'],x['Pgo'],x['ItemBytes'],x['Budget'])].append(x)
    derived=[]
    median=lambda rr,key:statistics.median(x[key] for x in rr)
    for (transport,pgo,size,budget),ss in sorted(groups.items()):
        for q in (1,16):
            a=[x for x in ss if x['Mode']==f'A-ready/q{q}'];b=[x for x in ss if x['Mode']==f'B3-ready/q{q}']
            processes=[]
            for launch in (0,1):
                aa=[x for x in a if x['Launch']==launch];bb=[x for x in b if x['Launch']==launch]
                paired=[next(x['ItemsPerSecond'] for x in bb if x['Round']==r)/next(x['ItemsPerSecond'] for x in aa if x['Round']==r) for r in range(4)]
                processes.append(dict(launch=launch,throughput_pct=100*(median(bb,'ItemsPerSecond')/median(aa,'ItemsPerSecond')-1),paired_geomean_pct=100*(math.exp(statistics.mean(math.log(x) for x in paired))-1)))
            derived.append(dict(transport=transport,pgo=pgo,item_bytes=size,budget=budget,quantum=q,samples_per_mode=len(a),
                a_item_s=median(a,'ItemsPerSecond'),b_item_s=median(b,'ItemsPerSecond'),throughput_pct=100*(median(b,'ItemsPerSecond')/median(a,'ItemsPerSecond')-1),
                cpu_pct=100*(median(b,'CpuMs')/median(a,'CpuMs')-1),a_B_item=median(a,'AllocatedBytesPerItem'),b_B_item=median(b,'AllocatedBytesPerItem'),
                allocation_delta=median(b,'AllocatedBytesPerItem')-median(a,'AllocatedBytesPerItem'),processes=processes,
                b_events_item=statistics.median((x['ReadyWriterMetrics']['ReadyNotifications']+x['UpdateFrames'])/x['ItemsReceived'] for x in b),
                a_peak_queued=max(x['ReadyWriterMetrics']['MaximumQueuedBytesPerStream'] for x in a),b_peak_queued=max(x['ReadyWriterMetrics']['MaximumQueuedBytesPerStream'] for x in b)))
    lines=['# Prepared-byte budget controls','',f"Exact tree: `{p['source_tree']}`. 48 processes / 768 samples. Byte budgets do not change credit windows or pool settings.",'',
           'Each A/B pair uses the same prepared-byte cap, count cap, quantum, flush and transport. Report all budgets; do not choose a winning mode per cell. Non-instrumented timing; positive throughput is faster, positive CPU or allocation is worse.','',
           '| Transport | PGO | Item B | Prepared cap B | Quantum | A item/s | B3 item/s | Throughput | CPU | A B/item | B3 B/item | B3 events/item | Process throughput |',
           '|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|']
    for x in derived:
        proc=', '.join(f"{r['throughput_pct']:+.2f}%" for r in x['processes'])
        lines.append(f"|{x['transport']}|{x['pgo']}|{x['item_bytes']}|{x['budget']}|{x['quantum']}|{x['a_item_s']:,.0f}|{x['b_item_s']:,.0f}|{x['throughput_pct']:+.2f}%|{x['cpu_pct']:+.2f}%|{x['a_B_item']:.3f}|{x['b_B_item']:.3f}|{x['b_events_item']:.6f}|{proc}|")
    lines+=['','Cap 0 is the former count-only control, not an unbounded queue. A single oversized prepared frame can occupy an otherwise empty ring; subsequent preparation still waits. The caller may also hold one not-yet-enqueued packet per stream. This is NOT a total connection-memory or SendPump-admission budget.','',
            'Producer frame preparation after the start gate is included in actual measured allocation. Context/ring/task/transport setup before the gate and lifecycle teardown after measurement are excluded; this is not a complete cold-stream allocation measurement. Per-stream peak sums are upper bounds, not simultaneous retained-memory measurements. This remains fixed-lifecycle balanced-wire research, not full production RPC or NativeAOT performance.']
    return '\n'.join(lines)+'\n',derived,rows

def main():
    p=argparse.ArgumentParser();p.add_argument('root',type=pathlib.Path);args=p.parse_args();text,groups,rows=summarize(args.root)
    (args.root/'summary.md').write_text(text);(args.root/'derived.json').write_text(json.dumps(groups,indent=2));print(f'PASS {len(rows)} rows; all 48 process exits/configurations/source/credit/buffers verified')
if __name__=='__main__':main()
