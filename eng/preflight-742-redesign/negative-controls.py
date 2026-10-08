#!/usr/bin/env python3
"""Preserve deterministic original failures on C plus explicit test-only barriers."""
import hashlib
import json
import pathlib
import re
import shutil
import subprocess
import sys

control, candidate, out = (pathlib.Path(value).resolve() for value in sys.argv[1:4])
kit = pathlib.Path(__file__).resolve().parent
out.mkdir(parents=True, exist_ok=True)
base = json.loads((control / 'artifacts/first-receive-integration/provenance.json').read_text())
assert base['disposable_tree'] == 'ea21543ca4d267938834ac24bead069aac2327c0'
patch = kit / 'registry-control-hook.patch'
subprocess.run(['git', 'apply', '--check', str(patch)], cwd=control, check=True)
subprocess.run(['git', 'apply', str(patch)], cwd=control, check=True)
files = {
    'StreamManagerRegistrationTransactionTests.cs': kit / 'registry-control.cs',
    'StreamManagerRetainedPeerTerminalTests.cs': kit / 'retained-terminal-control.cs',
    'StreamFlowControllerTests.TransientTail.cs': candidate / 'test/SharpLink.UnitTests/Runtime/StreamFlowControllerTests.TransientTail.cs',
}
for name, source in files.items():
    target = control / 'test/SharpLink.UnitTests/Runtime' / name
    assert not target.exists(), target
    shutil.copyfile(source, target)
proof = dict(base=base, hook_sha256=hashlib.sha256(patch.read_bytes()).hexdigest(),
    tests={name: hashlib.sha256(path.read_bytes()).hexdigest() for name, path in files.items()},
    boundary='Correctness controls and layout only: C plus a thread-local barrier and new regression fixtures. Never used for timing.')
(out / 'negative-controls-provenance.json').write_text(json.dumps(proof, indent=2) + '\n')
def run(command, log_name, expected_failures=None):
    with (out / log_name).open('w') as log:
        result = subprocess.run(command, cwd=control, stdout=log, stderr=subprocess.STDOUT)
    text = re.sub(r'\x1b\[[0-9;]*m', '', (out / log_name).read_text())
    print(text)
    if expected_failures is None:
        assert result.returncode == 0, log_name
    else:
        assert result.returncode != 0, log_name
        assert re.search(rf'total:\s+{expected_failures}\b', text), log_name
        assert re.search(rf'failed:\s+{expected_failures}\b', text), log_name
        assert re.search(r'succeeded:\s+0\b', text), log_name
        assert re.search(r'skipped:\s+0\b', text), log_name
run(['dotnet', 'build', 'test/SharpLink.UnitTests', '-c', 'Release', '-p:Platform=AnyCPU', '-p:TreatWarningsAsErrors=true'], 'control-build.log')
for name, filter_, count in (
    ('orphan', '/*/*/StreamManagerTests/RegistrationMustNotPublishIntoAnUnlinkedRequestContainer', 1),
    ('replacement', '/*/*/StreamManagerTests/PeerTerminalMustNotRetireAReplacementEntry*', 2),
    ('retained-terminal', '/*/*/StreamManagerRetainedPeerTerminalTests/RetainedPeerTerminalMustWaitForAlreadyAcquiredDataBeforeReceiveFlush', 1),
    ('transient-tail', '/*/*/StreamFlowControllerTests/OrderedTailAdmissionShouldNotAllocateTransientWaiters*', 6),
):
    run(['dotnet', 'test', '--project', 'test/SharpLink.UnitTests', '-c', 'Release', '--no-build',
         '-p:Platform=AnyCPU', '--', '--treenode-filter', filter_], f'{name}-negative.log', count)
