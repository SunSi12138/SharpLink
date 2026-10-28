import json, pathlib, statistics as s, math
r=next(pathlib.Path(__file__).parent.rglob('completion.json')).parent
cells=['I/T','P/T','I/U','P/U']; processes=[]
for f in sorted(r.glob('process-*.samples.jsonl')):
 rows=[json.loads(x) for x in f.read_text().splitlines()]; assert len(rows)==20
 for i,x in enumerate(rows): assert x['cell']==cells[i//5] and x['sample']==i%5 and x['completed']==2000 and len(x['ticks'])==2000 and x['failure'] is None
 processes.append({'name':f.name.split('.')[0],'arm':f.name.split('.')[0][-1],'rows':rows})
assert len(processes)==36
out={'processes':36,'samples':720,'calls':sum(x['completed'] for p in processes for x in p['rows']),'drift':{}}
for arm in ['all','O','A','B']:
 ps=processes if arm=='all' else [p for p in processes if p['arm']==arm];d={}
 for c in cells:
  rr=[[x for x in p['rows'] if x['cell']==c] for p in ps]
  ms=[[x['elapsedTicks']/x['frequency']/x['completed']*1e6 for x in rows] for rows in rr]
  d[c]={'n':len(ps),'sampleMeanMedianUs':[s.median(z[i] for z in ms) for i in range(5)],'firstSlowerThanLast':sum(z[0]>z[-1] for z in ms),'firstLastMedianRatio':s.median(z[0]/z[-1] for z in ms),'gcCounts':[sum(x['gcCollections'][g] for rows in rr for x in rows) for g in range(3)],'allocationMedianBytes':s.median(sum(x['bytes'] for x in rows)/10000 for rows in rr)}
 out['drift'][arm]=d
rows=[x for p in processes for x in p['rows']]
out['sampleDurationMs']={'min':min(x['elapsedTicks']/x['frequency']*1000 for x in rows),'max':max(x['elapsedTicks']/x['frequency']*1000 for x in rows),'median':s.median(x['elapsedTicks']/x['frequency']*1000 for x in rows)}
a=json.load(open(r/'analysis.json'));out['recordedAnalysisKeys']=list(a)
# Independently apply exact paired formulas, without using recorded aggregate values.
ag={p['name']:{c:{'mean':sum(x['elapsedTicks']/x['frequency'] for x in p['rows'] if x['cell']==c)/10000,'p99':sorted(t/x['frequency'] for x in p['rows'] if x['cell']==c for t in x['ticks'])[9899]} for c in cells} for p in processes}
manifest=json.load(open(r/'manifest.json'));out['pairs']=manifest['pairs'];out['recordedContrastSummary']={k:v for k,v in a.items() if k!='processes'}
path=pathlib.Path(__file__).parent/'retained-analysis.json';path.write_text(json.dumps(out,indent=2));print(json.dumps({k:v for k,v in out.items() if k not in ['recordedContrastSummary']},indent=2));print('ANALYSIS NON-PROCESS',json.dumps(out['recordedContrastSummary'])[:10000])
checks={}
for contrast,rec in a['comparisons'].items():
 X,Y=contrast.split('/');cross=[];same=[]
 for i,label in enumerate(manifest['pairs']):
  p0,p1=processes[2*i:2*i+2]
  assert p0['arm']+p1['arm']==label
  if set(label)=={X,Y}: x,y=(p0,p1) if p0['arm']==X else (p1,p0); dest=cross
  elif label in [X*2,Y*2]: x,y=p1,p0;dest=same
  else:continue
  logs={c:math.log(ag[x['name']][c]['mean']/ag[y['name']][c]['mean']) for c in cells}
  dest.append({'pair':i,'logs':logs,'d':{t:logs['I/'+t]-logs['P/'+t] for t in ['T','U']},'p99':{c:ag[x['name']][c]['p99']/ag[y['name']][c]['p99'] for c in cells}})
 noise={t:max(abs(p['d'][t]) for p in same) for t in ['T','U']}
 decisions={t:'favorable exploratory signal' if all(p['logs']['I/'+t]<0 and p['d'][t]<-noise[t] for p in cross) else 'adverse exploratory signal' if all(p['logs']['I/'+t]>0 and p['d'][t]>noise[t] for p in cross) else 'inconclusive' for t in ['T','U']}
 for t in ['T','U']:
  assert math.isclose(noise[t],rec['noiseEnvelopes'][t],rel_tol=1e-12)
  assert decisions[t]==rec['decisions'][t]
 for c in cells:
  for value,key in [(math.exp(s.mean(p['logs'][c] for p in cross)),'meanGeometricRatio'),(math.exp(s.mean(math.log(p['p99'][c]) for p in cross)),'p99GeometricRatio')]: assert math.isclose(value,rec['cellSummary'][c][key],rel_tol=1e-12)
 checks[contrast]={'noise':noise,'decisions':decisions,'meanGM':math.exp(s.mean(p['logs']['I/T'] for p in cross)),'p99GM':math.exp(s.mean(math.log(p['p99']['I/T']) for p in cross)),'adverseTimedP99PairIndices':[p['pair'] for p in cross if p['p99']['I/T']>1]}
out['independentRecalculation']=checks
out.pop('recordedContrastSummary');path.write_text(json.dumps(out,indent=2));print('RECOMPUTED',json.dumps(checks,indent=2))
