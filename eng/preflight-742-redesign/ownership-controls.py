#!/usr/bin/env python3
"""Reproduce single-consumption and wrapper-allocation failures without undoing R."""
import os
import pathlib
import re
import subprocess
import sys

root, out = (pathlib.Path(value).resolve() for value in sys.argv[1:3])
def run(command, name, expected=None, env=None):
    with (out / name).open('w') as log:
        result = subprocess.run(command, cwd=root, env=env, stdout=log, stderr=subprocess.STDOUT)
    text = re.sub(r'\x1b\[[0-9;]*m', '', (out / name).read_text())
    print(text)
    if expected is None:
        assert result.returncode == 0, name
    else:
        total, failed, message = expected
        assert result.returncode != 0 and message in text, name
        assert re.search(rf'total:\s+{total}\b', text), name
        assert re.search(rf'failed:\s+{failed}\b', text), name
        assert re.search(rf'succeeded:\s+{total-failed}\b', text), name
        assert re.search(r'skipped:\s+0\b', text), name

def build(name):
    run(['dotnet', 'build', 'test/SharpLink.UnitTests', '-c', 'Release', '-p:Platform=AnyCPU', '-p:TreatWarningsAsErrors=true'], name)

def test(suite, name, expected, env=None):
    run(['dotnet', 'test', '--project', 'test/SharpLink.UnitTests', '-c', 'Release', '--no-build',
         '-p:Platform=AnyCPU', '--', '--treenode-filter', f'/*/*/{suite}/*'], name, expected, env)

paths = ['src/SharpLink.Client/SharpLinkClient.Invokers.cs', 'src/SharpLink.Client/SharpLinkClient.Telemetry.cs']
saved = {path: (root / path).read_bytes() for path in paths}
try:
    for path in paths:
        (root / path).write_bytes(subprocess.check_output(['git', 'show', f'e834d3c28c87ad496989af925515cf21babd308d:{path}'], cwd=root))
    build('wrapper-negative-build.log')
    test('ClientStreamValueTaskConsumptionTests', 'wrapper-negative.log', (12, 6, 'failed: 6'))
finally:
    for path, data in saved.items():
        (root / path).write_bytes(data)

path = root / 'src/SharpLink.Runtime/PooledAsyncStreamDispatcher.cs'
saved = path.read_bytes()
try:
    text = saved.decode()
    marker = '    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]\n'
    assert text.count(marker) == 1, 'Unexpected consumer builder attribute population'
    path.write_text(text.replace(marker, '', 1))
    build('pool-negative-build.log')
    env = dict(os.environ, SHARPLINK_SLOW_WAIT_EVIDENCE=str(out / 'pool-negative'))
    test('PooledDispatcherSlowWaitTests', 'pool-negative.log',
         (6, 1, 'suspended MoveNext still allocates one fresh wrapper for every item'), env)
finally:
    path.write_bytes(saved)
subprocess.run(['git', 'diff', '--exit-code', '--', 'src', 'test'], cwd=root, check=True)
