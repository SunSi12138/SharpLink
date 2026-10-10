import json,statistics,pathlib,sys
root=pathlib.Path(sys.argv[1]);rows={}
for p in root.glob('*.json'):
 try:rows[p.stem]=json.loads(p.read_text())
 except:pass
for layer in ['known-route',1,8,32,128]:
 print('layer/active-requests',layer)
 for arity in [0,1,2,4,8,127]:
  values={arm:{} for arm in ['baseline','candidate','lazy']}
  for key,r in rows.items():
   arm,pair=key.rsplit('-',1);v=[x['ns'] for x in r if x.get('arity')==arity and (x.get('layer')==layer if isinstance(layer,str) else x.get('requests')==layer)]
   if v:values[arm][pair]=statistics.median(v)
  med={k:round(statistics.median(v.values()),3)for k,v in values.items()if v}
  diffs=[values['candidate'][p]-v for p,v in values['baseline'].items()if p in values['candidate']]
  pct=[(values['candidate'][p]/v-1)*100 for p,v in values['baseline'].items()if p in values['candidate']]
  print(arity,med,'delta_ns',round(statistics.median(diffs),3)if diffs else None,'delta_pct',round(statistics.median(pct),2)if pct else None,'paired_range',list(map(lambda x:round(x,2),pct)))
print('allocations')
for arm in ['baseline','candidate','lazy']:
 r=rows.get(arm+'-1',[]);print(arm,[x for x in r if 'registrationBytesPerRequest' in x or 'cleanupBytesPerRequest' in x])
