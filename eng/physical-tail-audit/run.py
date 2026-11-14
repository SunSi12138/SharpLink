from pathlib import Path
import json, os, re, shutil, subprocess, time

repo = Path.cwd()
out = repo / 'artifacts/physical-tail-hosted'
out.mkdir(parents=True, exist_ok=True)
shutil.copy(repo / 'eng/physical-tail-audit/PROTOCOL.md', out)
records = []

def run(name, command, timeout=60):
    start = time.time()
    with (out/(name+'.stdout.log')).open('w') as stdout, (out/(name+'.stderr.log')).open('w') as stderr:
        try:
            result = subprocess.run(command, cwd=repo, stdout=stdout, stderr=stderr, timeout=timeout)
            code, timed_out = result.returncode, False
        except subprocess.TimeoutExpired:
            code, timed_out = None, True
    records.append(dict(name=name, command=command, exitCode=code, timedOut=timed_out, seconds=time.time()-start))
    (out/'processes.json').write_text(json.dumps(records, indent=2))
    return code

run('environment', ['dotnet','--info'])
run('uname', ['uname','-a'])
run('cpu', ['lscpu'])
run('identity', ['git','rev-parse','HEAD'])
run('product-delta', ['git','diff','2c77ffb55b10c111abea8fac0c128c1ac2dc93ad','HEAD','--','src'])
if (out/'product-delta.stdout.log').read_text().strip():
    raise SystemExit('Product source changed; stop')
env = (out/'environment.stdout.log').read_text()
if '10.0.115' not in env or 'Microsoft.NETCore.App 10.0.15' not in env:
    raise SystemExit('Frozen SDK/runtime unavailable; no RPC invocation')
if run('build', ['dotnet','build','test/SharpLink.IntegrationTests/SharpLink.IntegrationTests.csproj','-c','Release','-v','minimal'],480) != 0:
    raise SystemExit('Build failed; stop')
dll = 'test/SharpLink.IntegrationTests/bin/Release/net10.0/SharpLink.IntegrationTests.dll'
selector = '/*/*/*/PublicDeadlineShouldRetainPhysicalOutputTail*'
run('discovery', ['dotnet',dll,'--list-tests','--treenode-filter',selector])
discovery = (out/'discovery.stdout.log').read_text()
expected = [f'PublicDeadlineShouldRetainPhysicalOutputTail({shape}, {tail})' for shape in ('Server','Duplex') for tail in ('MoveNext','Dispose')]
actual = re.findall(r'^\s*(PublicDeadlineShouldRetainPhysicalOutputTail\([^\r\n]+\))\s*$', discovery, re.M)
(out/'inventory.json').write_text(json.dumps(dict(expected=expected,actual=actual,exact=sorted(expected)==sorted(actual)),indent=2))
if sorted(expected) != sorted(actual):
    raise SystemExit('Exact inventory rejected; stop')
code = run('baseline-once', ['dotnet',dll,'--treenode-filter',selector,'--maximum-parallel-tests','1','--timeout','120s'],150)
reports = repo/'test/SharpLink.IntegrationTests/bin/Release/net10.0/TestResults'
if reports.exists(): shutil.copytree(reports,out/'reports')
print('One frozen baseline invocation finished. Read phase observations, not workflow color. No retries.')
raise SystemExit(code if code is not None else 124)
