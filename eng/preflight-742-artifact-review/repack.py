#!/usr/bin/env python3
"""Copy immutable completed evidence into size-bounded review artifacts; execute nothing."""
import hashlib,json,os,pathlib,shutil,urllib.request
source=pathlib.Path('artifacts/original')
out=pathlib.Path('artifacts/review')
out.mkdir(parents=True,exist_ok=False)
expected={'id':11569879475,'size_in_bytes':73447474,'digest':'sha256:c92301d7eac0854ed07f824a811069b49c6ff7aab8fa050257dfad7e9e32f55e'}
request=urllib.request.Request('https://api.github.com/repos/SunSi12138/SharpLink/actions/artifacts/11569879475',headers={'Authorization':'Bearer '+os.environ['GITHUB_TOKEN'],'Accept':'application/vnd.github+json'})
with urllib.request.urlopen(request,timeout=30) as response: metadata=json.load(response)
assert all(metadata[k]==v for k,v in expected.items())
assert metadata['workflow_run']['id']==37823636416 and metadata['workflow_run']['head_sha']=='815c1ac1ec1ec4141f38cfe06814d8d0caf4b257'
assert not metadata['expired']
files=sorted((p for p in source.rglob('*') if p.is_file()),key=lambda p:(-p.stat().st_size,str(p)))
assert files and not any(p.is_symlink() for p in source.rglob('*'))
limit=28*1024*1024
bins=[]
manifest={'source_artifact':expected,'source_run':37823636416,'source_head':metadata['workflow_run']['head_sha'],'files':[],'parts':[], 'boundary':'Official download of existing artifact only. No builds, tests, timings or file-content transformations. Original ZIP digest is asserted from GitHub metadata; every extracted file is rehashed before and after copying. Parts have at most28MiB raw content, below the32MiB transfer limit.'}
for path in files:
 size=path.stat().st_size
 assert size<=limit,('One existing file exceeds the bounded artifact size; no file is silently omitted',str(path),size)
 index=next((i for i,b in enumerate(bins) if b['bytes']+size<=limit),len(bins))
 if index==len(bins): bins.append({'bytes':0,'files':0})
 assert index<12, 'Expected at most12 evidence partitions'
 relative=path.relative_to(source)
 target=out/f'part-{index:02d}'/relative
 target.parent.mkdir(parents=True,exist_ok=True)
 before=hashlib.sha256(path.read_bytes()).hexdigest()
 shutil.copyfile(path,target)
 assert hashlib.sha256(target.read_bytes()).hexdigest()==before
 bins[index]['bytes']+=size;bins[index]['files']+=1
 manifest['files'].append({'path':str(relative),'size':size,'sha256':before,'part':index})
manifest['parts']=[dict(index=i,**b) for i,b in enumerate(bins)]
(out/'manifest.json').write_text(json.dumps(manifest,indent=2)+'\n')
with open(os.environ['GITHUB_OUTPUT'],'a') as result:
 for i in range(len(bins)): result.write(f'part{i}=true\n')
print(json.dumps({'source':expected,'parts':manifest['parts'],'files':len(files)}))
