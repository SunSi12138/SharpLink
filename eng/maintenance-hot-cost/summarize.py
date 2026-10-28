#!/usr/bin/env python3
"""Compact functional/native evidence, without timing scores or performance thresholds."""
import argparse,json
from pathlib import Path
from native_evidence import validate_native_evidence
p=argparse.ArgumentParser(description=__doc__);p.add_argument('--group',required=True);p.add_argument('--expected-runtime');a=p.parse_args()
root=Path(__file__).resolve().parent;folder=root/'evidence'/a.group
provenance=json.loads((folder/'provenance.json').read_text());native=json.loads((folder/'native-summary.json').read_text())
rows=[]
for arm,entry in provenance['Arms'].items():
 for mode in ['fullopts','tier0','tier1','pgo']:
  assert entry['Exits'][mode]==0,(arm,mode,entry['Exits'][mode])
  listing=folder/arm/(mode+'.asm')
  assert listing.is_file() and listing.stat().st_size>0,(arm,mode,'missing native listing')
  native_rows=[r for r in native[arm] if r['Mode']==mode]
  observed_tiers=validate_native_evidence(mode,native_rows)
  j=json.loads((folder/arm/(mode+'.json')).read_text());n=j['Network']
  if a.expected_runtime:assert j['RuntimeBinding']['RuntimeVersion']==a.expected_runtime
  assert j['RuntimeBinding']['Architecture']=='X64'
  for phase in ['Before','PreUpdate','Resumed','After']:
   c=n[phase]
   assert c['ClientReady']==c['ServerPhysical']==c['AuthenticationCalls']==2
   assert all(c[k] for k in ['SameConnectionObjects','SameSessionObjects','AllReady','AuthenticatedTls'])
  assert n['HeldProxyAcrossPhases'] and n['BeforeResult']==30
  if arm=='candidate':assert n['MaintenanceEnabled'] and n['UpdateStage']=='Succeeded' and n['GenerationAdvanced'] and n['Result']==31
  else:assert not n['MaintenanceEnabled'] and n['Result']==30
  rows.append({'Arm':arm,'Mode':mode,'Runtime':j['Runtime'],'RuntimeBinding':j['RuntimeBinding'],'Network':n,
   'Native':native_rows,'ObservedTiers':observed_tiers,
   'ParserMicroAllocations':j['Micro']})
comparison=json.loads((root/('comparison-'+a.group)/'summary.json').read_text())
structural=comparison['published-prerequisites']
assert len(structural['Layouts'])==8
assert all(x['BeforeBytes']==x['AfterBytes'] and not x['FieldDeltas'] and x['CompleteFieldSetEqual'] for x in structural['Layouts'])
selection=[x for x in structural['IL'] if any(n in x['Key'] for n in ['GetReadyConnection(', 'EnsureExpansion(', 'EnsureReconnectLoop('])]
assert len(selection)==8
assert all(x['CanonicalILEqual'] and x['BeforeLocals']==x['AfterLocals'] for x in selection)
result={'Scope':'Two persistent TLS/authenticated sessions, held proxy, ordinary calls before/after one compatible feature replacement. Prerequisite performs the same call phases without update. Native listings cover the complete process, not separate timed phases. Parser allocation is a separate prepared-Ready microprobe, not TLS or whole-RPC allocation. No throughput or zero-cost assertion.','Runs':rows,'Comparison':comparison}
(folder/'compact-metrics.json').write_text(json.dumps(result,indent=2)+'\n')
print('Validated',len(rows),'launches; compact-metrics.json written.')
