#!/usr/bin/env python3
"""Validate complete, version-pinned ready-writer evidence and expose every control."""
import argparse, collections, importlib.util, itertools, json, math, pathlib, statistics
MODES=('A-ready/q1','B3-ready/q1','A-ready/q16','B3-ready/q16')

def expected_plan():
    cases=[]
    for t,g,w,r in itertools.product(('sharedmemory','tcp'),(0,1),(8192,524288),(0,1)):
        cases.append([t,g,128,2048 if w==8192 else 4096,16,w,16384,r])
    for t,g,c in itertools.product(('sharedmemory','tcp'),(0,1),(1,8,32)):
        cases.append([t,g,c,4096,16,8192,16384,0])
    for t,g,c in itertools.product(('sharedmemory','tcp'),(0,1),(32,128)):
        cases.append([t,g,c,128,4096,524288,16384,0])
    return cases

def validate_provenance(p):
    if p.get('plan')!=expected_plan():raise ValueError('missing or altered experimental population')
    if p.get('rounds')!=4 or p.get('slots')!=16 or p.get('quanta')!=[1,16]:raise ValueError('altered comparison policy')
    for key,length in [('source_tree',40),('host_sha256',64)]:
        if len(p.get(key,''))!=length or any(c not in '0123456789abcdef' for c in p[key]):raise ValueError('invalid digest')
    affinity=p['cpu_affinity']
    if len(affinity)!=4 or len(set(affinity))!=4 or any(type(c) is not int or c<0 for c in affinity):raise ValueError('wrong affinity')


def validate_report(doc, source, expected, check_reads=True):
    transport,pgo,streams,items,bytes_,window,flush,launch=expected
    m=doc['metadata']
    if doc['status']!='completed' or doc.get('error') is not None:raise ValueError('failed or incomplete report')
    if m['Source']!=source or len(source)!=40:raise ValueError('wrong source')
    for key,value in {'transport':transport,'Pgo':str(pgo),'streams':streams,'items':items,'bytes':bytes_,'connection':window,'flush':flush,'slots':16,'rounds':4,'orderOffset':launch,'ProcessorCount':4}.items():
        if m.get(key)!=value:raise ValueError(f'wrong configuration {key}')
    seen=set()
    for s in doc['samples']:
        key=(s['Mode'],s['Round'])
        if key in seen:raise ValueError('duplicate sample')
        seen.add(key); total=streams*items; rm=s['ReadyWriterMetrics']
        if s['Source']!=source or s['Mode'] not in MODES or s['ItemsReceived']!=total or s['BytesReturned']!=total*bytes_:raise ValueError('source/data/credit mismatch')
        for k,v in {'Transport':transport,'Streams':streams,'ItemsPerStream':items,'ItemBytes':bytes_,'StreamWindow':8192,'ConnectionWindow':window}.items():
            if s.get(k)!=v:raise ValueError('sample configuration mismatch: '+k)
        for field in ('ElapsedMs','ItemsPerSecond','CpuMs','AllocatedBytesPerItem'):
            if not isinstance(s[field],(int,float)) or not math.isfinite(s[field]) or s[field]<0:raise ValueError('invalid measured number')
        if s['ElapsedMs']<=0 or not math.isclose(s['ItemsPerSecond'],total*1000/s['ElapsedMs'],rel_tol=1e-9):raise ValueError('throughput inconsistent with actual items/time')
        if len(s['ProducerDurationMs'])!=streams or any(not math.isfinite(x) or x<0 for x in s['ProducerDurationMs']):raise ValueError('producer population/duration mismatch')
        for k,v in rm.items():
            if not isinstance(v,int) or isinstance(v,bool) or v<0:raise ValueError('invalid counter '+k)
        q=16 if s['Mode'].endswith('/q16') else 1
        if rm['SchedulingQuantum']!=q or rm['RingSlotsPerStream']!=16 or rm['MaximumRingDepth']>16:raise ValueError('ring or quantum bound mismatch')
        if rm['FramesReleased']!=total or rm['CreditBytesApplied']!=total*bytes_ or rm['NormalQueueRejections']!=0:raise ValueError('unsettled writer/credit')
        if rm['WireUpdateNotifications']!=s['UpdateFrames'] or not streams<=rm['ReadyNotifications']<=total:raise ValueError('notification count mismatch')
        if check_reads and rm['EventChannelReadCalls']!=rm['ReadyNotifications']+rm['WireUpdateNotifications']:raise ValueError('unbacked or empty event-channel read')
        if rm['ExistingPumpBudgetRmwLowerBound']!=2*total:raise ValueError('cannot hide remaining pump RMWs')
        is_owner=s['Mode'].startswith('B3')
        if rm['PumpOwnedCreditDebits']!=(total if is_owner else 0):raise ValueError('wrong credit authority')
        if rm['ProducerSideCreditGateOperations']!=(0 if is_owner else total):raise ValueError('wrong logical acquire inventory')
        if rm['CreditOwnerQueueCompletions']!=0:raise ValueError('separate per-item credit owner reintroduced')
    if seen!={(mode,r) for mode in MODES for r in range(4)}:raise ValueError('missing case')
    return doc['samples']

def summarize(root, check_reads=True):
    root=pathlib.Path(root);p=json.loads((root/'provenance.json').read_text());validate_provenance(p);rows=[]
    names=set()
    for i,case in enumerate(p['plan']):
        t,g,c,n,b,w,f,launch=case
        name=f'{i:02}-{t}-pgo{g}-c{c}-b{b}-w{w}-f{f}-r{launch}.json';names.add(name)
        path=root/name
        if json.loads(path.with_suffix('.exit').read_text())['code']!=0:raise ValueError('nonzero process exit')
        samples=validate_report(json.loads(path.read_text()),p['source_tree'],case,check_reads)
        rows.extend(dict(x,Pgo=g,Launch=launch,Report=name) for x in samples)
    if {x.name for x in root.glob('[0-9]*.json')}!=names:raise ValueError('unexpected/missing report')
    grouped=collections.defaultdict(list)
    for x in rows:grouped[(x['Transport'],x['Pgo'],x['Streams'],x['ItemBytes'],x['ConnectionWindow'])].append(x)
    result=[]
    for group,ss in sorted(grouped.items()):
        t,g,c,b,w=group
        for quantum in (1,16):
            a=[x for x in ss if x['Mode']==f'A-ready/q{quantum}'];after=[x for x in ss if x['Mode']==f'B3-ready/q{quantum}']
            med=lambda samples,field:statistics.median(x[field] for x in samples)
            base=med(a,'ItemsPerSecond'); candidate=med(after,'ItemsPerSecond')
            proc=[]
            for launch in sorted({x['Launch'] for x in a}):
                aa=[x for x in a if x['Launch']==launch];bb=[x for x in after if x['Launch']==launch]
                proc.append(100*(med(bb,'ItemsPerSecond')/med(aa,'ItemsPerSecond')-1))
            result.append(dict(transport=t,pgo=g,streams=c,item_bytes=b,connection_window=w,quantum=quantum,samples_per_mode=len(a),
                baseline_items_per_second=base,owner_items_per_second=candidate,throughput_delta_pct=100*(candidate/base-1),
                cpu_delta_pct=100*(med(after,'CpuMs')/med(a,'CpuMs')-1),allocation_delta_B_per_item=med(after,'AllocatedBytesPerItem')-med(a,'AllocatedBytesPerItem'),
                launch_throughput_deltas_pct=proc,
                owner_events_per_item=statistics.median((x['ReadyWriterMetrics']['ReadyNotifications']+x['ReadyWriterMetrics']['WireUpdateNotifications'])/x['ItemsReceived'] for x in after),
                owner_update_frames_per_item=statistics.median(x['UpdateFrames']/x['ItemsReceived'] for x in after)))
    lines=['# Matched ready-writer controls','',f"Source: `{p['source_tree']}`. {len(names)} reports / {len(rows)} rows. Positive throughput changes are faster; positive CPU/allocation deltas are regressions.",'','Both sides use identical rings, quantum and patched SendPump. These are not comparisons with the earlier one-unsettled-emission baseline.','',
    '| Transport | PGO | Streams | Bytes/item | Connection bytes | Quantum | A item/s | B3 item/s | Throughput Δ | CPU Δ | B/item Δ | Per-process throughput Δ | B3 events/item |',
    '|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|---:|']
    for x in result:
        proc=', '.join(f'{v:+.2f}%' for v in x['launch_throughput_deltas_pct'])
        lines.append(f"|{x['transport']}|{x['pgo']}|{x['streams']}|{x['item_bytes']}|{x['connection_window']}|{x['quantum']}|{x['baseline_items_per_second']:,.0f}|{x['owner_items_per_second']:,.0f}|{x['throughput_delta_pct']:+.2f}%|{x['cpu_delta_pct']:+.2f}%|{x['allocation_delta_B_per_item']:+.3f}|{proc}|{x['owner_events_per_item']:.6f}|")
    lines+=['','B3 events count ready notifications plus wire-update notifications, not hardware synchronization. Existing pump byte-budget operations still require at least two authored RMWs per frame. Runtime-internal atomic attempts are not measured. The legacy `ProducerSideCreditGateOperations` field is a logical acquire-call inventory/lower bound, not an exact lock-entry counter.','',
    'Scheduling quantum bounds a turn when competitors are runnable. It does not establish the original global per-item waiter FIFO under all dynamic lifecycle/cancellation interleavings. Secondary controls have one process; c128 primary groups have two.']
    return '\n'.join(lines)+'\n',result,rows

def main():
    a=argparse.ArgumentParser();a.add_argument('root',type=pathlib.Path);a.add_argument('--prior-polling-control',action='store_true');args=a.parse_args()
    text,groups,rows=summarize(args.root,not args.prior_polling_control)
    (args.root/'summary.md').write_text(text);(args.root/'derived.json').write_text(json.dumps(groups,indent=2))
    print(f'PASS {len(rows)} rows; all settings, exits, exact source, credits, events and controls verified')
if __name__=='__main__':main()
