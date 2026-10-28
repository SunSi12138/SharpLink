#!/usr/bin/env python3
import argparse,json,statistics as st
from pathlib import Path
p=argparse.ArgumentParser();p.add_argument('--group',required=True);a=p.parse_args();r=Path(__file__).resolve().parent/'evidence'/a.group;prov=json.load(open(r/'provenance.json'));assert len(prov['Launches'])==12 and all(x['ExitCode']==0 for x in prov['Launches']);groups=[];samples=[]
for mode in ['fullopts','pgo']:
 for arm in ['published-prerequisites','candidate']:
  rows=[];launches=[]
  for rep in range(3):
   j=json.load(open(r/arm/f'{mode}-{rep}.json'));n=j['Network'];assert n['Calls']==7 and n['ItemsPerCall']==32768 and n['ConfiguredServerStreamReceiveWindowBytes']==4096;assert n['MaintenanceEnabled']==(arm=='candidate') and not n['CancellationCallbackOwnerTableCreated']
   for stage in ['Before','After']:
    c=n[stage];assert c['ClientReady']==c['ServerPhysical']==c['AuthenticationCalls']==2;assert all(c[k] for k in ['SameSessionObjects','SameConnectionObjects','AllReady','AuthenticatedTls'])
   measured=[]
   for x in n['Samples']:
    assert x['Received']==32768 and x['FiniteUnexpiredDeadlineAtStart'] and x['CooperativeCancellationTokenAtStart'] and x['ActiveOwningCallStatesAtStart']==1
    row={'Arm':arm,'Mode':mode,'Repetition':rep,**x};samples.append(row)
    if not x['Warmup']:rows.append(row);measured.append(row)
   launches.append({'Repetition':rep,'MedianMilliseconds':st.median(x['ElapsedMilliseconds'] for x in measured),'MedianProcessManagedBytes':st.median(x['ProcessManagedAllocatedBytes'] for x in measured)})
  q={'Arm':arm,'Mode':mode,'ObservedSamples':len(rows),'LaunchMedians':launches}
  for key in ['ElapsedMilliseconds','ProcessManagedAllocatedBytes']:
   vals=[x[key] for x in rows];q[key]={'Min':min(vals),'Median':st.median(vals),'Max':max(vals)}
  groups.append(q)
(r/'compact-metrics.json').write_text(json.dumps({'Runtime':prov['ExpectedRuntime'],'Scope':prov['Scope'],'Groups':groups,'AllSamples':samples,'NoPerformanceThreshold':True},indent=2)+'\n')
