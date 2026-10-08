import argparse,csv,json
from pathlib import Path
p=argparse.ArgumentParser();p.add_argument('rows',type=Path);p.add_argument('--expected',type=int,required=True);p.add_argument('--scenario');a=p.parse_args()
rows=list(csv.DictReader(a.rows.open(),delimiter='|'))
cases=[a.scenario] if a.scenario else sorted({r['scenario'] for r in rows})
result=[]
for case in cases:
 items=[r for r in rows if r['scenario']==case and r['phase']=='complete-stream' and r['owner']=='entry' and r['operation'] not in ['lock','callback']]
 assert items,case
 actual=sum(int(r['attempts']) for r in items)
 cas=[r for r in items if r['operation']=='CompareExchange']
 result.append(dict(scenario=case,entry_rmw=actual,expected=a.expected,cas_attempts=sum(int(r['attempts']) for r in cas),cas_successes=sum(int(r['cas_successes']) for r in cas),passed=actual==a.expected))
print(json.dumps(result,indent=2))
if any(not r['passed'] for r in result):
 print('EXPECTED_RUNTIME_COUNT_FAILURE: successful real pooled CompleteStream used '+','.join(str(r['entry_rmw']) for r in result if not r['passed'])+' entry RMWs; expected '+str(a.expected))
 raise SystemExit(2)
