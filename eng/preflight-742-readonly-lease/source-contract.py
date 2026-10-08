#!/usr/bin/env python3
"""Require only the three approved token edits; this is not runtime/codegen proof."""
import pathlib

FILES = ('src/SharpLink.Runtime/RpcSession.ClientStreamPublication.cs',
         'src/SharpLink.Runtime/RpcSession.GeneratedServerBridge.cs',
         'src/SharpLink.Runtime/RpcSession.PreCreditStreaming.cs')
LEASE = 'StreamFlowController.ResolvedSendCreditLease creditLease'
INNER = 'SendUnsizedStreamChunkResolvedAsync'


def candidate_text(name, original):
    if name.endswith('PreCreditStreaming.cs'):
        start = original.index(INNER + '<T>(')
        end = original.index('\n    {', start)
        signature = original[start:end]
        assert signature.count(LEASE) == 1 and 'in ' + LEASE not in signature
        return original[:start] + signature.replace(LEASE, 'in ' + LEASE) + original[end:]
    start = original.index('return ' + INNER + '(')
    end = original.index(');', start) + 2
    call = original[start:end]
    assert original.count('return ' + INNER + '(') == 1
    assert call.count(', creditLease,') == 1
    return original[:start] + call.replace(', creditLease,', ', in creditLease,') + original[end:]


def verify(control, candidate):
    for name in FILES:
        old = (control / name).read_text()
        new = (candidate / name).read_text()
        assert new == candidate_text(name, old), ('Out-of-scope production edit', name)
    print('Exact three readonly-inner token edits verified; both owned wrapper snapshots and deferred copies preserved.')


if __name__ == '__main__':
    import sys
    verify(pathlib.Path(sys.argv[1]), pathlib.Path(sys.argv[2]))
