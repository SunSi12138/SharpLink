import json,statistics,sys
from pathlib import Path
rows=[json.loads(x) for x in Path(sys.argv[1]).read_text().splitlines()]
out=[]
for p in sorted({(x['profile'],x['shape']) for x in rows}):
 b={x['pair']:x for x in rows if (x['profile'],x['shape'])==p and x['arm']=='baseline'}
 c={x['pair']:x for x in rows if (x['profile'],x['shape'])==p and x['arm']=='candidate'}
 keys=b.keys()&c.keys();ratio=[c[k]['ns']/b[k]['ns'] for k in keys]
 out.append(dict(profile=p[0],shape=p[1],pairs=len(keys),baseline_ns=statistics.median([b[k]['ns'] for k in keys]),candidate_ns=statistics.median([c[k]['ns'] for k in keys]),median_ratio=statistics.median(ratio),min_ratio=min(ratio),max_ratio=max(ratio),wins=sum(r<1 for r in ratio),baseline_bytes=statistics.median([b[k]['bytes'] for k in keys]),candidate_bytes=statistics.median([c[k]['bytes'] for k in keys]),baseline_cpu_ns=statistics.median([b[k]['cpuNs'] for k in keys]),candidate_cpu_ns=statistics.median([c[k]['cpuNs'] for k in keys]),median_cpu_ratio=statistics.median([c[k]['cpuNs']/b[k]['cpuNs'] for k in keys])))
print(json.dumps(out,indent=2))
