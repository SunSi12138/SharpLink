#!/usr/bin/env python3
"""Disposable preflight transformation; never edits the original PR branch."""
from pathlib import Path
import subprocess

path = Path('src/SharpLink.Runtime/StreamManager.Routing.cs')
expected = 'd5c9c8d7beff4ea53feb3f2c384a80f184364cd5'
actual = subprocess.check_output(['git', 'hash-object', str(path)], text=True).strip()
if actual != expected:
    raise SystemExit(f'Unexpected source blob: {actual}; expected {expected}')
text = path.read_text()

def once(old: str, new: str) -> None:
    global text
    if text.count(old) != 1:
        raise SystemExit(f'Expected exactly one source anchor: {old!r}')
    text = text.replace(old, new)

once('        private const long ReceiveRetirementPendingMask = 1L << 33;\n',
     '        private const long ReceiveRetirementPendingMask = 1L << 33;\n'
     '        private const long ReceiveTerminalPublishedMask = 1L << 34;\n'
     '        private const long PeerTerminalReceivedMask = 1L << 35;\n'
     '        private const long RetirementClaimedMask = 1L << 36;\n')
once('        private int _receiveTerminalPublished;\n'
     '        private int _peerTerminalReceived;\n'
     '        private int _retirementClaimed;\n',
     '        // Cold lifecycle flags share the atomic word; acquisition count and\n'
     '        // detach/cleanup ownership retain their existing, disjoint bits.\n')
once('        internal bool PeerTerminalReceived => Volatile.Read(ref _peerTerminalReceived) != 0;\n',
     '        internal bool PeerTerminalReceived => (Volatile.Read(ref _state) & PeerTerminalReceivedMask) != 0;\n')
once('            => Volatile.Write(ref _peerTerminalReceived, 1);\n',
     '            => _ = Interlocked.Or(ref _state, PeerTerminalReceivedMask);\n')
once('            => Interlocked.CompareExchange(ref _receiveTerminalPublished, 1, 0) == 0;\n',
     '            => (Interlocked.Or(ref _state, ReceiveTerminalPublishedMask) & ReceiveTerminalPublishedMask) == 0;\n')
once('            => Interlocked.CompareExchange(ref _retirementClaimed, 1, 0) == 0;\n',
     '            => (Interlocked.Or(ref _state, RetirementClaimedMask) & RetirementClaimedMask) == 0;\n')
path.write_text(text)
print('Applied only lifecycle-flag packing; no lease, credit, route publication or drain changes.')
subprocess.run(['git', 'diff', '--check'], check=True)
subprocess.run(['git', 'diff', '--', str(path)], check=True)
