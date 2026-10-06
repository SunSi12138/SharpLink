#!/usr/bin/env python3
"""Fuse first-delivery publication and acquisition release without changing their order."""
import hashlib
import json
import pathlib
import subprocess

ROOT = pathlib.Path(__file__).resolve().parents[1]
OUT = ROOT/'artifacts/first-receive-integration'
EXPECTED = {
    'src/SharpLink.Runtime/StreamManager.FirstReceive.cs': 'b845d749e338b436cfa825e4b101e9b80b92b0c3',
    'src/SharpLink.Runtime/StreamManager.cs': '50ea9e79d14dbb525ea0b11a5eb8cacef9bd93c0',
}

def git(*args):
    return subprocess.check_output(['git', *args], cwd=ROOT, text=True).strip()

def once(text, before, after):
    if text.count(before) != 1:
        raise RuntimeError(f'Expected one reviewed anchor: {before[:100]!r}; got {text.count(before)}')
    return text.replace(before, after, 1)

for name, expected in EXPECTED.items():
    if git('hash-object', name) != expected:
        raise RuntimeError(f'Unreviewed first-receive source: {name}')

name = 'src/SharpLink.Runtime/StreamManager.FirstReceive.cs'
text = (ROOT/name).read_text()
anchor = '    private async ValueTask AwaitFirstReceiveAndDispatchAsync(\n'
if text.count(anchor) != 1:
    raise RuntimeError('Missing reviewed follower boundary')
new = '''namespace SharpLink.Runtime;

internal sealed partial class StreamManager
{
    // The caller already owns the entry acquisition. The winner returns the raw
    // dispatcher operation; its publication and release share one completion owner.
    private ValueTask DispatchFirstReceiveAsync(
        long requestId, ushort streamId, DispatcherEntry entry,
        ReadOnlySequence<byte> payload, int encodedBytes, out bool ownsFirstReceive)
    {
        ownsFirstReceive = entry.TryStartFirstReceive();
        if (!ownsFirstReceive)
            return AwaitFirstReceiveAndDispatchAsync(entry, payload, encodedBytes);

        var lease = default(StreamFlowController.ResolvedReceiveCreditLease);
        try
        {
            lease = _acceptFirstReceiveCreditLease!(requestId, streamId, encodedBytes);
            entry.ReceiveCreditLease = lease;
            if (lease.IsResolved)
            {
                var consumptionAware = (IStreamConsumptionAwareDispatcher)entry.Dispatcher;
                _ = consumptionAware.TrySetResolvedBytesConsumedCallback(_resolvedBytesConsumed, in lease);
            }
        }
        catch (Exception error)
        {
            // No dispatcher has seen this frame. Only the winner's debit can be refunded.
            Exception failure = error;
            if (lease.IsResolved)
            {
                try { _resolvedBytesConsumed!(in lease, encodedBytes); }
                catch (Exception refundError) { failure = new AggregateException(error, refundError); }
            }
            entry.FailFirstReceive(failure);
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            throw;
        }

        try
        {
            return DispatchAfterReceiveCredit(entry.Dispatcher, payload, encodedBytes);
        }
        catch (Exception error)
        {
            // Delivery owns consumption, including errors; initialization must not refund twice.
            entry.FailFirstReceive(error);
            throw;
        }
    }

    private static ValueTask CompleteFirstDispatch(DispatcherEntry entry, ValueTask dispatch)
    {
        if (!dispatch.IsCompletedSuccessfully)
            return AwaitFirstDispatchAsync(entry, dispatch);

        try { entry.PublishFirstReceive(); }
        finally { entry.Release(); }
        return ValueTask.CompletedTask;
    }

    private static async ValueTask AwaitFirstDispatchAsync(DispatcherEntry entry, ValueTask dispatch)
    {
        try
        {
            try
            {
                await dispatch.ConfigureAwait(false);
            }
            catch (Exception error)
            {
                entry.FailFirstReceive(error);
                throw;
            }
            // Followers stay blocked until actual first delivery succeeds, not merely
            // until its callback or ValueTask is available.
            entry.PublishFirstReceive();
        }
        finally
        {
            // Cleanup failures must not enter the delivery catch or release again.
            entry.Release();
        }
    }

'''
text = new + text[text.index(anchor):]
(ROOT/name).write_text(text)

name = 'src/SharpLink.Runtime/StreamManager.cs'
text = (ROOT/name).read_text()
text = once(text,
    '            ValueTask dispatch;\n            try\n',
    '            ValueTask dispatch;\n            var ownsFirstReceive = false;\n            try\n')
text = once(text,
    'dispatch = DispatchFirstReceiveAsync(requestId, streamId, entry, payload, encodedByteCount);',
    'dispatch = DispatchFirstReceiveAsync(requestId, streamId, entry, payload, encodedByteCount, out ownsFirstReceive);')
text = once(text,
    '            return CompleteDispatch(entry, dispatch);',
    '            return ownsFirstReceive ? CompleteFirstDispatch(entry, dispatch) : CompleteDispatch(entry, dispatch);')
text = once(text,
    '        var matched = true;\n        try\n',
    '        var matched = true;\n        var ownsFirstReceive = false;\n        try\n')
text = once(text,
    'dispatch = DispatchFirstReceiveAsync(requestId, streamId, entry, wirePayload, originalByteCount);',
    'dispatch = DispatchFirstReceiveAsync(requestId, streamId, entry, wirePayload, originalByteCount, out ownsFirstReceive);')
text = once(text,
    '        dispatch = CompleteDispatch(entry, dispatch);',
    '        dispatch = ownsFirstReceive ? CompleteFirstDispatch(entry, dispatch) : CompleteDispatch(entry, dispatch);')
(ROOT/name).write_text(text)

old = json.loads((OUT/'provenance.json').read_text())
(OUT/'prior-integration-provenance.json').write_text(json.dumps(old, indent=2)+'\n')
paths = sorted(set(old['source_blobs']) | {'test/SharpLink.UnitTests/Runtime/StreamManagerFirstReceiveAwaitAllocationTests.cs'})
git('add', '--', *paths)
provenance = dict(control=old['control'], preflight_head=git('rev-parse','HEAD'),
                  disposable_tree=git('write-tree'),
                  source_blobs={name:git('hash-object',name) for name in paths},
                  prior_patch_sha256=old['patch_sha256'],
                  change='one first-delivery publication/release await owner; no early follower publication',
                  acceptance='pending actual correctness, allocation, RPC and NativeAOT validation')
patch = subprocess.check_output(['git','diff','--cached','--binary'],cwd=ROOT)
(OUT/'candidate.patch').write_bytes(patch)
provenance['patch_sha256'] = hashlib.sha256(patch).hexdigest()
(OUT/'provenance.json').write_text(json.dumps(provenance,indent=2)+'\n')
(OUT/'candidate-tree.txt').write_text(provenance['disposable_tree']+'\n')
for name in paths:
    target=OUT/'sources'/name
    target.parent.mkdir(parents=True,exist_ok=True)
    target.write_bytes((ROOT/name).read_bytes())
print(json.dumps(provenance,indent=2))
