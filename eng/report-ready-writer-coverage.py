#!/usr/bin/env python3
"""Inventory every planned case even on failure. Never derive speedups from partial cases."""
import argparse
import importlib.util
import json
from pathlib import Path

SPEC=importlib.util.spec_from_file_location('budget',Path(__file__).with_name('verify-ready-writer-budget.py'))
BUDGET=importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(BUDGET)


def inventory(root):
    root=Path(root)
    provenance=json.loads((root/'provenance.json').read_text())
    plan=BUDGET.expected_plan()
    BUDGET.validate_provenance(provenance)
    source=provenance['source_tree']
    results=[]
    for index,case in enumerate(plan):
        t,g,c,n,b,w,f,launch,budget=case
        name=f'{index:02}-{t}-pgo{g}-b{b}-w{w}-budget{budget}-r{launch}.json'
        path=root/name
        entry=dict(index=index,report=name,transport=t,pgo=g,item_bytes=b,budget=budget,
                   launch=launch,outcome='missing',exit_code=None,retained_samples=0,error=None)
        try:
            exit_record=json.loads(path.with_suffix('.exit').read_text())
            entry['exit_code']=exit_record['code']
            if type(entry['exit_code']) is not int:
                raise ValueError('invalid exit code')
            if not path.exists():
                entry['outcome']='failed' if entry['exit_code'] else 'invalid'
                entry['error']='process emitted no report'
            else:
                document=json.loads(path.read_text())
                entry['retained_samples']=len(document.get('samples',[]))
                if entry['exit_code']:
                    entry['outcome']='failed'
                    entry['error']=document.get('error') or f"exit {entry['exit_code']}"
                else:
                    BUDGET.validate_document(document,source,case)
                    entry['outcome']='complete'
        except FileNotFoundError:
            entry['error']='missing exit record; not counted as success'
        except (KeyError,TypeError,ValueError) as error:
            entry['outcome']='invalid';entry['error']=str(error)
        results.append(entry)
    complete=sum(row['outcome']=='complete' for row in results)
    lines=['# Ready-writer matrix coverage','',f'Exact source `{source}`. {complete}/48 validated complete processes.',
           '**This is coverage, not a performance acceptance report. Failed/partial cases are not pooled into speedups.**','',
           '| Case | Transport | PGO | Item B | Prepared cap | Launch | Outcome | Exit | Retained rows |',
           '|---|---|---|---:|---:|---:|---|---:|---:|']
    for row in results:
        lines.append(f"| {row['index']} | {row['transport']} | {row['pgo']} | {row['item_bytes']} | {row['budget']} | {row['launch']} | {row['outcome']} | {row['exit_code']} | {row['retained_samples']} |")
    lines+=['','A complete matrix still requires the unchanged full verifier: 48 successful exits, 768 rows, all identities/configuration and credit/buffer checks. The collection continues after failures; its final exit and CI remain failing. No retries, relaxed timeout, removed cases or per-cell winner selection.']
    return dict(source_tree=source,complete=complete,planned=48,cases=results),'\n'.join(lines)+'\n'


def main():
    parser=argparse.ArgumentParser();parser.add_argument('root',type=Path);args=parser.parse_args()
    data,text=inventory(args.root)
    (args.root/'coverage.json').write_text(json.dumps(data,indent=2))
    (args.root/'coverage.md').write_text(text)
    print(text)
    if data['complete']!=48:raise SystemExit('Incomplete/failed population retained; NOT accepted.')


if __name__=='__main__':main()
