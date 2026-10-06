#!/usr/bin/env python3
"""Source-pinned compact receive identity experiment; temporary preflight only."""
import json
import pathlib
import subprocess

ROOT = pathlib.Path(__file__).resolve().parents[1]
EXPECTED = {
    'src/SharpLink.Runtime/StreamManager.cs': '93a07e2a22574f4747e3332416b8ca031f523ac4',
    'src/SharpLink.Runtime/StreamManager.Routing.cs': 'a8fa8e890ceaf341a552651fc289c3bddc0b1c4a',
}

def replace_once(text, old, new):
    if text.count(old) != 1:
        raise RuntimeError(f'Expected one source anchor, found {text.count(old)}: {old[:100]!r}')
    return text.replace(old, new, 1)

sources = {}
for name, expected in EXPECTED.items():
    actual = subprocess.check_output(['git', 'hash-object', name], cwd=ROOT, text=True).strip()
    if actual != expected:
        raise RuntimeError(f'Unreviewed control source {name}: {actual} != {expected}')
    sources[name] = (ROOT/name).read_text()

name = 'src/SharpLink.Runtime/StreamManager.Routing.cs'
text = sources[name]
text = replace_once(text,
    '                    entry.ReceiveCreditLease = receiveCreditLease;',
    '                    entry.SetReceiveCreditLease(in receiveCreditLease);')
text = replace_once(text,
    '                    var lease = entry.ReceiveCreditLease;',
    '                    var lease = entry.GetReceiveCreditLease(requestId, streamId);')
text = replace_once(text,
    '            ReceiveCreditLease = receiveCreditLease;',
    '            SetReceiveCreditLease(in receiveCreditLease);')
text = replace_once(text,
    '        internal StreamFlowController.ResolvedReceiveCreditLease ReceiveCreditLease { get; set; }',
    '''        // Capture ownership independently of the dispatcher, but do not duplicate
        // keys already supplied by the acquired route or its retained retirement record.
        // Never reconstruct identity from mutable pooled ReceiveState fields.
        private StreamFlowController? _receiveCreditOwner;
        private object? _receiveCreditState;
        private long _receiveCreditGeneration;

        internal bool HasReceiveCreditLease => _receiveCreditOwner is not null;

        internal void SetReceiveCreditLease(in StreamFlowController.ResolvedReceiveCreditLease lease)
        {
            _receiveCreditOwner = lease.Owner;
            _receiveCreditState = lease.State;
            _receiveCreditGeneration = lease.Generation;
        }

        internal StreamFlowController.ResolvedReceiveCreditLease GetReceiveCreditLease(
            long requestId, ushort streamId)
            => _receiveCreditOwner is { } owner
                ? new(owner, requestId, streamId, _receiveCreditState!, _receiveCreditGeneration)
                : default;''')
sources[name] = text

name = 'src/SharpLink.Runtime/StreamManager.cs'
text = sources[name]
counts = {
    'has': text.count('entry.ReceiveCreditLease.IsResolved'),
    'local': text.count('var receiveCreditLease = entry.ReceiveCreditLease;'),
    'terminal': text.count('_resolvedStreamCompleted(entry.ReceiveCreditLease);'),
}
if counts != {'has': 5, 'local': 3, 'terminal': 1}:
    raise RuntimeError(f'Unreviewed receive identity call sites: {counts}')
text = text.replace('entry.ReceiveCreditLease.IsResolved', 'entry.HasReceiveCreditLease')
text = text.replace('var receiveCreditLease = entry.ReceiveCreditLease;',
                    'var receiveCreditLease = entry.GetReceiveCreditLease(requestId, streamId);')
text = text.replace('_resolvedStreamCompleted(entry.ReceiveCreditLease);',
                    '_resolvedStreamCompleted(entry.GetReceiveCreditLease(requestId, streamId));')
if 'entry.ReceiveCreditLease' in text:
    raise RuntimeError('A full-lease route access was not updated')
sources[name] = text

out = ROOT/'artifacts/compact-route'
out.mkdir(parents=True, exist_ok=True)
blobs = {}
for name, content in sources.items():
    (ROOT/name).write_text(content)
    (out/pathlib.Path(name).name).write_text(content)
    blobs[name] = subprocess.check_output(['git','hash-object',name],cwd=ROOT,text=True).strip()
(out/'candidate-blobs.json').write_text(json.dumps(blobs,indent=2)+'\n')
print(json.dumps({'control_blobs': EXPECTED, 'candidate_blobs': blobs, 'call_sites': counts},indent=2))
