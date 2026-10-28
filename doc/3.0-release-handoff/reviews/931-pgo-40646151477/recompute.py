import json,pathlib,hashlib,math,statistics,random,re
ROOT=pathlib.Path(__file__).parent
B='cc7c458b559295e0fc61ec0062bb220faf880a76';C='ac17c8d8ea0203edbe7b8cf19ddb95eaa24d4d2c'
def read(p):return json.loads(p.read_text())
result={}
for label in ['931-pgo-12','931-fullopts-12']:
 r=next((ROOT/label).glob('*/ownership-931-evidence'));m=read(r/'client-prepared/prepared-manifest.json'); samples={};hosts=[]
 for arm in ['baseline','candidate']:
  expected=m[arm+'BinaryHashes']
  assert len(expected)==9
  for f,h in expected.items():assert hashlib.sha256((r/'client-prepared'/arm/f).read_bytes()).hexdigest()==h,(label,arm,f)
  pre=read(r/f'preflight/{arm}.json'); assert pre['binaryHashes']==expected
  assert pre['operations']==(4352 if label=='931-pgo-12' else 256)
  for i in range(1,7):
   stem=f'dynamic-intercepted-n100-c1-r{i}-{arm}';s=read(r/f'client-tcp/runs/{stem}.json');h=read(r/f'client-tcp/runs/{stem}.host.json');t=read(r/f'client-tcp/ticks/{stem}.json')
   assert h['status']=='success' and h['exitCode']==0
   assert s['binaryHashes']==expected
   assert s['runtimeVersion']=='10.0.13' and s['processorCount']==2 and s['validationFailures']==0 and s['readyConnections']==1
   assert s['warmupSeconds']==3 and s['requestedSeconds']==6 and s['elapsedSeconds']>=6
   assert s['tieredCompilation']==('default' if label=='931-pgo-12' else '0') and s['tieredPgo']=='default'
   assert s['interceptorObserved'] and any('PooledAsyncStreamDispatcher' in x for x in s['wrapperTypes'])
   for a in s['loadedAssemblies']:
    assert pathlib.Path(a['location']).parent.name==arm
    if a['name'].startswith('SharpLink.'):
     assert a['productVersion'].split('+')[-1]==(B if a['name']=='SharpLink.Client' and arm=='baseline' else C)
   ticks=t['samples'][0]['durationTicks'];assert len(t['samples'])==1 and t['samples'][0]['worker']==0
   assert len(ticks)==s['operations']==t['operations'] and t['workers']==1
   assert all(type(x)==int and x>=0 for x in ticks)
   ordered=sorted(ticks);f=t['stopwatchFrequency']; n=len(ticks)
   vals={'p50Us':50,'p95Us':95,'p99Us':99,'p999Us':99.9,'maxUs':100}
   for key,p in vals.items():assert math.isclose(ordered[math.ceil(p/100*n)-1]/f*1e6,s[key],rel_tol=1e-12),(stem,key)
   assert math.isclose(statistics.mean(ticks)/f*1e6,s['meanUs'],rel_tol=1e-12)
   assert math.isclose(n/s['elapsedSeconds'],s['operationsPerSecond'],rel_tol=1e-12)
   samples[i,arm]=s
   cpu={}
   for k in ['cpu0','cpu1','cpu2','cpu3']:
    delta=[y-x for x,y in zip(h['before']['cpu'][k],h['after']['cpu'][k])];total=sum(delta[:8]);cpu[k]={'busyFraction':1-(delta[3]+delta[4])/total,'steal':delta[7]}
   hosts.append({'round':i,'arm':arm,'start':h['before']['timestampUtc'],'cpu':cpu,'loadBefore':h['before']['loadavg'],'operations':n,'meanUs':s['meanUs'],'p99Us':s['p99Us'],'p999Us':s['p999Us'],'gc':[s['gc0'],s['gc1'],s['gc2']]})
 assert [k for k in m['baselineBinaryHashes'] if m['baselineBinaryHashes'][k]!=m['candidateBinaryHashes'][k]]==['SharpLink.Client.dll']
 summary=read(r/'client-tcp/summary.json')[0];metrics={}
 for key,original in summary['metrics'].items():
  ratios=[samples[i,'candidate'][key]/samples[i,'baseline'][key] for i in range(1,7)]
  rng=random.Random(529);boot=sorted(statistics.mean(rng.choices(ratios,k=6)) for _ in range(20000));ci=[boot[499],boot[19499]]
  assert ratios==original['pairedRatios'] and statistics.mean(ratios)==original['pairedMeanRatio'] and ci==original['pairedMeanBootstrap95']
  metrics[key]={'pairedRatios':ratios,'pairedMeanRatio':statistics.mean(ratios),'bootstrap95':ci}
 result[label]={'verifiedProcesses':12,'verifiedOperations':sum(s['operations'] for s in samples.values()),'onlyDifferingDll':'SharpLink.Client.dll','metrics':metrics,'hosts':sorted(hosts,key=lambda x:x['start'])}
(ROOT/'independent-recompute.json').write_text(json.dumps(result,indent=2)+'\n')
for k,v in result.items():
 print(k,'processes',v['verifiedProcesses'],'RPC',v['verifiedOperations']);print(json.dumps(v['metrics'],indent=2))
 print('HOSTS');print(json.dumps(v['hosts'],indent=2))
