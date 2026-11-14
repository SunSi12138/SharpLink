#if SHARPLINK_775_STATIC_SLICE
namespace SharpLink.Client;

// Owned by the existing pending generation. A physical producer holds one additional
// reference, so logical completion cannot dispose its CTS while user cleanup still runs.
internal sealed class PendingStreamAdmission(bool producer)
{
    private readonly CancellationTokenSource? _cancellation = producer ? new() : null;
    private readonly TaskCompletionSource<bool> _accepted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _references = 1;
    private PendingRequestTable? _physicalTable;
    internal bool Confirmed;
    internal bool Started;
    internal Task<bool> Acceptance => _accepted.Task;
    internal CancellationToken Token => _cancellation?.Token ?? CancellationToken.None;
    internal void RetainProducer(PendingRequestTable? table = null) { _physicalTable = table; Interlocked.Increment(ref _references); }
    internal void ReleaseProducer()
    {
        var table = Interlocked.Exchange(ref _physicalTable, null);
        try { Release(); } finally { table?.ReleaseStreamProducer(); }
    }
    internal void Confirm() { Confirmed = true; _accepted.TrySetResult(true); }
    internal Exception? Complete()
    {
        _accepted.TrySetResult(false);
        try { _cancellation?.Cancel(); return null; }
        catch (Exception failure) { return failure; }
        finally { Release(); }
    }
    internal void Release()
    {
        if (Interlocked.Decrement(ref _references) == 0) _cancellation?.Dispose();
    }
}

partial class PendingRequestTable
{
    internal Action<long>? StreamAcceptanceObservedTestHook;
    private int _physicalStreamProducers;
    private readonly TaskCompletionSource _streamProducerDrain = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal int PhysicalStreamProducers => Volatile.Read(ref _physicalStreamProducers);
    internal Task StreamProducerDrain => _streamProducerDrain.Task;
    internal void ReleaseStreamProducer()
    {
        if (Interlocked.Decrement(ref _physicalStreamProducers) == 0)
        {
            if (Volatile.Read(ref _disposed) != 0) _streamProducerDrain.TrySetResult();
            _owner.OnPendingCallCapacityIdle();
        }
    }
    private void SealStreamProducerDrain()
    {
        if (Volatile.Read(ref _physicalStreamProducers) == 0) _streamProducerDrain.TrySetResult();
    }
    internal PendingStreamAdmission CaptureStreamAdmission(long id, bool producer)
    {
        var slots = Volatile.Read(ref _slots);
        if (slots is not null && Volatile.Read(ref slots[(int)(id & _indexMask)]) is { } call)
            lock (call.CompletionGate)
                if (Volatile.Read(ref _disposed) == 0 && call.Id == id && ReferenceEquals(slots[(int)(id & _indexMask)], call) && call.StreamAdmission is { } admission)
                {
                    if (producer)
                    {
                        if (Interlocked.Increment(ref _physicalStreamProducers) > _capacity)
                        { Interlocked.Decrement(ref _physicalStreamProducers); throw CreateResourceExhaustedException(); }
                        admission.RetainProducer(this);
                    }
                    return admission;
                }
        // The operation has already selected its terminal; a completed false signal never starts user code.
        var closed = new PendingStreamAdmission(producer);
        if (producer) closed.RetainProducer();
        closed.Complete();
        return closed;
    }

    internal bool AcceptStreamRequest(long id)
    {
        var slots = Volatile.Read(ref _slots);
        if (slots is null || Volatile.Read(ref slots[(int)(id & _indexMask)]) is not { } call || call.Id != id)
        {
            if (id <= 0 || id > Volatile.Read(ref _nextId)) throw StaticBindingWire.Malformed("Unissued RequestAccepted correlation.");
            return false;
        }
        lock (call.CompletionGate)
        {
            if (call.Id != id || !ReferenceEquals(slots[(int)(id & _indexMask)], call)) return false;
            if (!call.RequestPublished || call.StreamAdmission is not { } admission)
                throw StaticBindingWire.Malformed("RequestAccepted does not refer to a published streaming request.");
            if (!call.Deadline.IsExpired(_timeProvider)) { admission.Confirm(); return true; }
        }
        TryComplete(id, PendingCallCompletionReason.DeadlineExceeded);
        return false;
    }

    internal void RequireStreamAcceptance(long id)
    {
        var slots = Volatile.Read(ref _slots);
        if (slots is null || Volatile.Read(ref slots[(int)(id & _indexMask)]) is not { } call) return;
        lock (call.CompletionGate)
            if (call.Id == id && ReferenceEquals(slots[(int)(id & _indexMask)], call) && call.StreamAdmission is { Confirmed: false })
                throw StaticBindingWire.Malformed("Successful stream progress preceded RequestAccepted.");
    }

    internal bool TryStartStreamProducer(long id, PendingStreamAdmission expected)
    {
        var slots = Volatile.Read(ref _slots);
        if (slots is null || Volatile.Read(ref slots[(int)(id & _indexMask)]) is not { } call) return false;
        lock (call.CompletionGate)
        {
            if (call.Id != id || !ReferenceEquals(slots[(int)(id & _indexMask)], call) ||
                !ReferenceEquals(call.StreamAdmission, expected) || !expected.Confirmed || expected.Started) return false;
            if (!call.Deadline.IsExpired(_timeProvider)) { expected.Started = true; return true; }
        }
        TryComplete(id, PendingCallCompletionReason.DeadlineExceeded);
        return false;
    }
}
#endif
