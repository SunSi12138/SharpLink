import re,json,subprocess,hashlib
from pathlib import Path
out=Path(__file__).parent

def original_calls(s):
 a=[]
 for m in re.finditer(r'Interlocked\.\w+\(',s):
  j=m.end();n=1
  while n:n+=(s[j]=='(')-(s[j]==')');j+=1
  a.append(s[m.start():j])
 return a

def normalize(s):
 # Remove only diagnostic observations, retaining their exact original operation.
 while True:
  m=re.search(r'LifecycleDiagnosticCounters\.Observe(?:Cas)?\("[^"]+",\s*',s)
  if not m:break
  j=m.end();n=1
  while n:n+=(s[j]=='(')-(s[j]==')');j+=1
  original=original_calls(s[m.start():j]);assert len(original)==1
  s=s[:m.start()]+original[0]+s[j:]
 s=re.sub(r'LifecycleDiagnosticCounters\.Hit\("[^"]+"\);','',s)
 s=re.sub(r'(lock\s*\([^)]*\)\s*)\{\s*(return[^;]+;)\s*\}',r'\1\2',s)
 s=s.replace('void IStreamDispatchLease.OnDispatchesDrained() {  TryReturnToPool(); }','void IStreamDispatchLease.OnDispatchesDrained() => TryReturnToPool();')
 return re.sub(r'\s+','',s)

import sys
root, out = map(Path, sys.argv[1:3])
changed = subprocess.check_output(['git', 'diff', '--name-only'], cwd=root, text=True).splitlines()
rows = []
assert changed
for path in changed:
    assert path.startswith('src/SharpLink.Runtime/'), path
    original = subprocess.check_output(['git', 'show', f':{path}'], cwd=root, text=True)
    current = (root / path).read_text()
    assert original_calls(original) == original_calls(current), (path, 'changed atomic calls')
    assert normalize(original) == normalize(current), (path, 'non-counter mutation')
    rows.append(dict(path=path, atomic_calls_unchanged=len(original_calls(original)), normalized_source_unchanged=True,
        original_blob=subprocess.check_output(['git', 'rev-parse', ':'+path], cwd=root, text=True).strip(),
        original_sha256=hashlib.sha256(original.encode()).hexdigest(), instrumented_sha256=hashlib.sha256(current.encode()).hexdigest()))
proof = dict(base_tree=subprocess.check_output(['git', 'write-tree'], cwd=root, text=True).strip(), files=rows,
    counter_helper_sha256=hashlib.sha256((root/'src/SharpLink.Runtime/LifecycleDiagnosticCounters.cs').read_bytes()).hexdigest(),
    boundary='Counter-only observation; original atomics and normalized production source unchanged. Never used for acceptance timing.')
out.write_text(json.dumps(proof, indent=2)+'\n')
subprocess.run(['git','diff','--binary'],cwd=root,stdout=out.with_suffix('.patch').open('w'),check=True)
print(json.dumps(proof,indent=2))
