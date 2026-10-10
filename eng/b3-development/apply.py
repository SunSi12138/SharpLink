#!/usr/bin/env python3
"""Construct the development candidate. No normal RPC adapter is enabled."""
from pathlib import Path
import hashlib, json, shutil
root=Path(__file__).resolve().parents[2]
runtime=root/'src/SharpLink.Runtime'
base=Path(__file__).resolve().parent

def once(text, old, new):
    if text.count(old)!=1: raise RuntimeError('missing/ambiguous anchor: '+old[:100])
    return text.replace(old,new)
def read_pinned(name, blob):
    data=(runtime/name).read_bytes()
    actual=hashlib.sha1(b'blob '+str(len(data)).encode()+b'\0'+data).hexdigest()
    if actual!=blob: raise RuntimeError('source drift: '+name+' '+actual)
    return data.decode()

pump=read_pinned('RpcSession.SendPump.cs','5e52d8d8e5e43af6c238f6f7207def244ce593f1')
owned=read_pinned('OwnedFrame.cs','8efb1bf8de51bd20d413caba33775cd3ad8fc2b2')
pump=once(pump,'private sealed class SendPump','private sealed partial class SendPump')
pump=once(pump,'private bool HasNormalFrames() => _normalQueue.Reader.TryPeek(out _);',
    'private bool HasNormalFrames() => _normalQueue.Reader.TryPeek(out _) || HasWriterReadyWork();')
pump=once(pump,'while (_normalQueue.Reader.TryRead(out var frame))\n                    {',
    'while (TryReadOrdinaryOrWriterReadyFrame(out var frame))\n                    {')
pump=once(pump,'''                _flushPolicyState.UnregisterChanged(_flushPolicyChanged);
                ReleaseBatch(pending, terminalException);
                DrainQueuedFrames(terminalException);
                PulseCapacityWaiters();''','''                List<Exception>? cleanupFailures = null;
                try { _flushPolicyState.UnregisterChanged(_flushPolicyChanged); }
                catch (Exception error) { (cleanupFailures ??= []).Add(error); }
                try { ReleaseBatch(pending, terminalException); }
                catch (Exception error) { (cleanupFailures ??= []).Add(error); }
                try { DrainQueuedFrames(terminalException); }
                catch (Exception error) { (cleanupFailures ??= []).Add(error); }
                try { StopWriterReadySource(terminalException); }
                catch (Exception error) { (cleanupFailures ??= []).Add(error); }
                finally { PulseCapacityWaiters(); }
                if (cleanupFailures is not null)
                    throw new AggregateException("Send-pump cleanup failed.", cleanupFailures);''')
pump=once(pump,'''            for (var index = 0; index < pending.Count; index++)
                CompleteReserved(pending[index], exception, completeFlushWaiter: true);
            pending.Clear();''','''            List<Exception>? failures = null;
            for (var index = 0; index < pending.Count; index++)
            {
                try { CompleteReserved(pending[index], exception, completeFlushWaiter: true); }
                catch (Exception error) { (failures ??= []).Add(error); }
            }
            pending.Clear();
            if (failures is not null) throw new AggregateException("Send batch release failed.", failures);''')
pump=once(pump,'''            while (_progressQueue.Reader.TryRead(out var frame))
                CompleteReserved(frame, exception, completeFlushWaiter: true);
            while (_normalQueue.Reader.TryRead(out var frame))
                CompleteReserved(frame, exception, completeFlushWaiter: true);''','''            List<Exception>? failures = null;
            while (_progressQueue.Reader.TryRead(out var frame))
            {
                try { CompleteReserved(frame, exception, completeFlushWaiter: true); }
                catch (Exception error) { (failures ??= []).Add(error); }
            }
            while (_normalQueue.Reader.TryRead(out var frame))
            {
                try { CompleteReserved(frame, exception, completeFlushWaiter: true); }
                catch (Exception error) { (failures ??= []).Add(error); }
            }
            if (failures is not null) throw new AggregateException("Send queue drain failed.", failures);''')
pump=once(pump,'''            try
            {
                _returnBuffer(frame.Owner);
            }
            finally
            {''','''            var readyReleaseError = exception;
            try
            {
                _returnBuffer(frame.Owner);
            }
            catch (Exception error)
            {
                readyReleaseError ??= error;
                throw;
            }
            finally
            {''')
pump=once(pump,'''                PulseCapacityWaiters();
                // This belongs''','''                try { frame.WriterReadyCompletion?.Complete(frame.WriterReadyCreditBytes, readyReleaseError); }
                finally { WakeWriterReadyForCapacity(); PulseCapacityWaiters(); }
                // This belongs''')
owned=once(owned,'    private readonly object? _completionState;','''    private readonly object? _completionState;
    private readonly int _writerReadyCreditBytes;
    internal IWriterReadyCompletion? WriterReadyCompletion => _completionState as IWriterReadyCompletion;
    internal int WriterReadyCreditBytes => _writerReadyCreditBytes;
    internal OwnedFrame(WriterReadyFrame ready) : this(ready.Packet, ready.ForceFlush, null, false)
    { _completionState = ready.Completion; _writerReadyCreditBytes = ready.CreditBytes; }''')
owned=once(owned,'        Length = owner.WrittenCount;','        _writerReadyCreditBytes = 0;')
owned=once(owned,'    public int Length { get; }','    public int Length => Memory.Length;')
(runtime/'RpcSession.SendPump.cs').write_text(pump)
(runtime/'OwnedFrame.cs').write_text(owned)
shutil.copyfile(base/'RpcSession.WriterReady.cs',runtime/'RpcSession.WriterReady.cs')
scheduler=(base/'WriterReadyStreamScheduler.cs').read_text()
# Keep cancellation cleanup idempotence separate from a transport-release error.
# A release error may poison the stream before Stop discards its still-prepared tail.
scheduler=once(scheduler,'AbortApplied, Retired, ProducerBusy;', 'AbortApplied, PreparationAborted, Retired, ProducerBusy;')
scheduler=once(scheduler,'''            if (stream.Retired || stream.Aborted is not null) return;
            stream.Aborted = error;''','''            if (stream.Retired || stream.PreparationAborted) return;
            stream.PreparationAborted = true;
            stream.Aborted ??= error;''')
(runtime/'WriterReadyStreamScheduler.cs').write_text(scheduler)
flow=read_pinned('StreamFlowController.cs','4354ef1793fc256d0d9bf729a957f1262098086d')
flow=once(flow,'''            var updatedConnectionCredit = Math.Min(
                checked(_sendConnectionCredit + credit), _connectionWindow);''','''            var returnedCredit = updatedStreamCredit - state.Credit;
            var updatedConnectionCredit = Math.Min(
                checked(_sendConnectionCredit + returnedCredit), _connectionWindow);''')
(runtime/'StreamFlowController.cs').write_text(flow)
for file in base.glob('*Tests.cs'):
    shutil.copyfile(file,root/'test/SharpLink.UnitTests/Runtime'/file.name)
manifest={str(p.relative_to(root)):hashlib.sha256(p.read_bytes()).hexdigest() for p in
    [runtime/'RpcSession.SendPump.cs',runtime/'OwnedFrame.cs',runtime/'RpcSession.WriterReady.cs',runtime/'WriterReadyStreamScheduler.cs',runtime/'StreamFlowController.cs']}
(root/'artifacts/b3-development').mkdir(parents=True,exist_ok=True)
(root/'artifacts/b3-development/source-sha256.json').write_text(json.dumps(manifest,indent=2))
print('Constructed source-pinned development candidate; normal RPC not enabled.')
