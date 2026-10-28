#!/usr/bin/env python3
"""Retain same-mode IL/layout/native differences; byte counts are not performance estimates."""
from pathlib import Path
import re,json,hashlib,difflib,argparse

root=Path(__file__).resolve().parent
p=argparse.ArgumentParser(description=__doc__)
p.add_argument('--group')
p.add_argument('--candidate',default='candidate')
a=p.parse_args()
if a.group:
    arms={'dev':root/'evidence'/a.group/'dev','published-prerequisites':root/'evidence'/a.group/'published-prerequisites',
          'candidate':root/'evidence'/a.group/a.candidate}
else:
    arms={'dev':root/'evidence/initial-2arm/dev','prerequisite-shim':root/'evidence/prerequisite-shim/prerequisite',
          'candidate':root/'evidence/initial-2arm/candidate','published-prerequisites':root/'evidence/published-prerequisites-run1/published-prerequisites'}
out=root/('comparison-'+a.group if a.group else 'comparison');out.mkdir(exist_ok=True)

def name(s):return re.sub(r'd__\d+', 'd__N',s)
def canonical(s):
    s=name(s)
    s=re.sub(r'0x[0-9A-F]{10,16}', '0xADDRESS',s)
    return s

def methods(p):
    result={}
    for mode in ['tier0','tier1','pgo','fullopts']:
        for body in (p/(mode+'.asm')).read_text().split('; Assembly listing for method ')[1:]:
            if not re.search(r'Total bytes of code (\d+)',body):continue
            key=(mode,name(body.splitlines()[0]))
            result[key]=canonical(body)
    return result

data={a:json.loads((p/'metadata.json').read_text()) for a,p in arms.items() if (p/'metadata.json').exists()}
native={a:methods(arms[a]) for a in data}
summary={}
for base in ['dev','prerequisite-shim','published-prerequisites']:
    if base not in data:continue
    dest=out/(base+'-to-candidate');dest.mkdir(exist_ok=True)
    result={'Layouts':[],'IL':[],'Native':[]}
    before={x['Type']:x for x in data[base]['Classes']}
    for row in data['candidate']['Classes']:
        old=before[row['Type']]
        fields={x['Name']:x for x in old['Fields']}
        deltas=[{'Field':x['Name'],'Before':fields.get(x['Name']),'After':x} for x in row['Fields'] if x!=fields.get(x['Name'])]
        result['Layouts'].append({'Type':row['Type'],'BeforeBytes':old['ManagedAllocationBytes'],'AfterBytes':row['ManagedAllocationBytes'],'FieldDeltas':deltas,'CompleteFieldSetEqual':old['Fields']==row['Fields']})
    before={name(x['Key']):x for x in data[base]['Methods']}
    for row in data['candidate']['Methods']:
        key=name(row['Key']); old=before.get(key)
        if old is None:continue
        oldil='\n'.join(map(name,old['IL'])); newil='\n'.join(map(name,row['IL']))
        result['IL'].append({'Key':key,'BeforeBytes':old['Bytes'],'AfterBytes':row['Bytes'],'CanonicalILEqual':oldil==newil,
                             'BeforeLocals':old['Locals'],'AfterLocals':row['Locals']})
        if oldil!=newil:
            short=re.sub('[^A-Za-z0-9._-]+','_',key.split('::')[0])[:100]+hashlib.sha256(key.encode()).hexdigest()[:12]
            (dest/(short+'.il.diff')).write_text(''.join(difflib.unified_diff(oldil.splitlines(True),newil.splitlines(True),fromfile=base,tofile='candidate')))
    for key,a in native[base].items():
        b=native['candidate'].get(key)
        if b is None:continue
        size=lambda s:int(re.search(r'Total bytes of code (\d+)',s)[1])
        stack=lambda s:re.findall(r'sub\s+rsp, (\w+)',s)
        result['Native'].append({'Mode':key[0],'Label':key[1],'BeforeBytes':size(a),'AfterBytes':size(b),'BeforeStackSubtract':stack(a),'AfterStackSubtract':stack(b),'CanonicalTextEqual':a==b})
        short=re.sub('[^A-Za-z0-9._-]+','_',key[0]+'_'+key[1].split('(')[0])[:100]+hashlib.sha256(str(key).encode()).hexdigest()[:12]
        (dest/(short+'.asm.diff')).write_text(''.join(difflib.unified_diff(a.splitlines(True),b.splitlines(True),fromfile=base,tofile='candidate')))
    (dest/'summary.json').write_text(json.dumps(result,indent=2)+'\n')
    summary[base]=result
(out/'summary.json').write_text(json.dumps(summary,indent=2)+'\n')
print(out)
