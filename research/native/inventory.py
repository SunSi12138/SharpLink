#!/usr/bin/env python3
"""Native dump inventory, deliberately NOT a correctness/fence/tier acceptance oracle."""
import argparse,json,re
from pathlib import Path

def inventory(text):
    rows=[]
    for block in re.split(r'(?=; Assembly listing for method )',text):
        if not block.startswith('; Assembly listing for method '):continue
        header=block.splitlines()[0]
        if not any(name in block for name in ('WakeupSignal','SendPump','SingleConsumerUnboundedChannel','SingleProducerSingleConsumerQueue')):continue
        rows.append({'header':header,'tierLabels':sorted(set(re.findall(r'\b(?:Instrumented Tier0|Tier0|Instrumented Tier1|Tier1|Tier1-OSR|FullOpts)\b',block))),
                     'instructionHints':[line.strip() for line in block.splitlines() if re.search(r'\b(?:lock|mfence|dmb|casal|ldaxr|stlxr)\b',line,re.I)],
                     'reviewRequired':'Map producer publication/fence/reread and consumer clear/recheck to actual addresses; hints do not prove placement or execution.'})
    return {'accepted':False,'status':'MANUAL_REVIEW_REQUIRED' if rows else 'INCOMPLETE_NO_MATCHING_NATIVE_BODY',
            'missingCalleePolicy':'Inspect actual Tier1 callers and inlining/load events; absent callee is NOT zero cost.', 'bodies':rows}
if __name__=='__main__':
    p=argparse.ArgumentParser();p.add_argument('dump',type=Path);args=p.parse_args();print(json.dumps(inventory(args.dump.read_text(errors='replace')),indent=2))
