#!/usr/bin/env python3
"""Require two portable progress failures on exact N and restore all test changes."""
import hashlib
import json
import pathlib
import re
import subprocess
import sys

root, out = (pathlib.Path(value).resolve() for value in sys.argv[1:3])
kit = pathlib.Path(__file__).resolve().parent
out.mkdir(parents=True, exist_ok=True)
patch = kit / 'negative-controls.patch'
assert hashlib.sha256(patch.read_bytes()).hexdigest() == '824ea110fdf5c105c9b4d5a01b41571edd2b04376859588b4e8ebc8f69bf5a24'
proof = json.loads((root / 'artifacts/first-receive-integration/provenance.json').read_text())
assert proof['disposable_tree'] == '4c5943fec3a85089cefa8a1b6dc8c0f6502567ce'
assert subprocess.check_output(['git', 'write-tree'], cwd=root, text=True).strip() == proof['disposable_tree']
target = root / 'test/SharpLink.UnitTests/Runtime/StreamFlowControllerDirectionalGateTests.cs'
assert not target.exists(), target
subprocess.run(['git', 'diff', '--exit-code', '--', 'src', 'test'], cwd=root, check=True)
(out / 'gate-negative-provenance.json').write_text(json.dumps(dict(base=proof,
    test_only_patch_sha256=hashlib.sha256(patch.read_bytes()).hexdigest(),
    filter='/*/SharpLink.UnitTests.Runtime/StreamFlowControllerDirectionalGateTests/OppositeDirectionShouldProgressWhileGateHeld*',
    expected_total=2, expected_failed=2, expected_succeeded=0, expected_skipped=0,
    boundary='Untimed portable correctness controls. Source remains exact N. Restore test fixture even on failure.'), indent=2) + '\n')
try:
    subprocess.run(['git', 'apply', '--check', str(patch)], cwd=root, check=True)
    subprocess.run(['git', 'apply', str(patch)], cwd=root, check=True)
    with (out / 'gate-negative-build.log').open('w') as log:
        built = subprocess.run(['dotnet', 'build', 'test/SharpLink.UnitTests', '-c', 'Release',
            '-p:Platform=AnyCPU', '-p:TreatWarningsAsErrors=true', '-m:1', '/nodeReuse:false', '-p:UseSharedCompilation=false'], cwd=root, stdout=log, stderr=subprocess.STDOUT)
    assert built.returncode == 0, (out / 'gate-negative-build.log').read_text()
    with (out / 'gate-negative.log').open('w') as log:
        result = subprocess.run(['dotnet', 'test', '--project', 'test/SharpLink.UnitTests', '-c', 'Release',
            '--no-build', '-p:Platform=AnyCPU', '--', '--treenode-filter',
            '/*/SharpLink.UnitTests.Runtime/StreamFlowControllerDirectionalGateTests/OppositeDirectionShouldProgressWhileGateHeld*',
            '--minimum-expected-tests', '2'], cwd=root, stdout=log, stderr=subprocess.STDOUT)
    text = re.sub(r'\x1b\[[0-9;]*m', '', (out / 'gate-negative.log').read_text())
    print(text)
    assert result.returncode != 0, 'Old N unexpectedly passes opposite-direction progress'
    for field, count in (('total', 2), ('failed', 2), ('succeeded', 0), ('skipped', 0)):
        assert re.search(rf'{field}:\s+{count}\b', text), (field, text)
    assert 'the unrelated direction was blocked by the held gate' in text, text
finally:
    target.unlink(missing_ok=True)
    subprocess.run(['git', 'diff', '--exit-code', '--', 'src', 'test'], cwd=root, check=True)
    assert subprocess.check_output(['git', 'write-tree'], cwd=root, text=True).strip() == proof['disposable_tree']
