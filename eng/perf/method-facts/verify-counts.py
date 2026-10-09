import json,pathlib,sys
rows=[json.loads(x) for x in pathlib.Path(sys.argv[1]).read_text().splitlines()]
assert len(rows)==320,f'expected 320 success characterization rows, got {len(rows)}'
keys=set()
for r in rows:
 key=tuple(r[k] for k in ('arm','profile','kind','intercepted','dynamic','admission','cancellable'))
 assert key not in keys,key
 keys.add(key)
 d=(2 if r['kind']=='OneWay' else 1+int(r['admission']))
 if r['arm']=='baseline':d+=int(r['intercepted'])
 assert r['Descriptors']==d,r
 assert r['Cancellations']==int(r['dynamic'] or r['cancellable']),r
 assert r['Invocations']==1,r
print('320 exact success-path descriptor/cancellation/invocation assertions passed')
if len(sys.argv)>2:
 edges=[json.loads(x) for x in pathlib.Path(sys.argv[2]).read_text().splitlines()]
 assert len(edges)==160,len(edges)
 seen=set()
 for r in edges:
  key=tuple(r[k] for k in ('arm','profile','edge','oneWay','intercepted'));assert key not in seen,key;seen.add(key)
  e=r['edge'];w=int(r['oneWay']);i=int(r['intercepted']);candidate=r['arm']=='candidate'
  if e=='missing-service':d=0;v=0
  elif e in ('stale-module','expired-deadline'):d=w;v=0
  elif e=='admission-rejected':d=1;v=0
  elif e=='admission-queued':d=2+w+(i if not candidate else 0);v=1
  elif e=='unknown-method' and w:d=1;v=0
  elif e in ('unknown-method','service-error'):d=2+w-(i if candidate else 0);v=1
  else:d=1+w+(i if not candidate else 0);v=int(not(i and e in ('short-circuit','interceptor-error')))
  assert r['Descriptors']==d,r
  assert r['Invocations']==v,r
  assert r['Cancellations']==int(e=='cancellation-unsupported'),r
 print('160 exact edge-path descriptor/cancellation/invocation assertions passed')
