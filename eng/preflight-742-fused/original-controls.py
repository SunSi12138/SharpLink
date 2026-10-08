#!/usr/bin/env python3
"""Retain original registry/admission controls and restore exact source afterward."""
import pathlib
import subprocess
import sys

control, candidate, out = (pathlib.Path(arg).resolve() for arg in sys.argv[1:4])
source = pathlib.Path(__file__).resolve().parents[2]
fixtures = [control / 'test/SharpLink.UnitTests/Runtime' / name for name in (
    'StreamManagerRegistrationTransactionTests.cs', 'StreamManagerRetainedPeerTerminalTests.cs',
    'StreamFlowControllerTests.TransientTail.cs')]
assert all(not path.exists() for path in fixtures)
subprocess.run(['git', 'diff', '--exit-code', '--', 'src', 'test'], cwd=control, check=True)
try:
    subprocess.run(['python3', str(source / 'eng/preflight-742-redesign/negative-controls.py'),
        str(control), str(candidate), str(out)], check=True)
finally:
    subprocess.run(['git', 'restore', '--worktree', '--', 'src', 'test'], cwd=control, check=True)
    for path in fixtures:
        path.unlink(missing_ok=True)
    subprocess.run(['git', 'diff', '--exit-code', '--', 'src', 'test'], cwd=control, check=True)
