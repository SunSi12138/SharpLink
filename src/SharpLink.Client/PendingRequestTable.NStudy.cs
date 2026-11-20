namespace SharpLink.Client;

// The pending slot owns terminal notification. Its producer keeps a second
// reference through the real writer/iterator exit, including user DisposeAsync.
internal sealed class NStudyProducerAdmission
{
    private readonly CancellationTokenSource _source = new();
    private readonly TaskCompletionSource<bool> _acceptance = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _owners = 1;
    internal bool Confirmed { get; private set; }
    internal bool Started { get; set; }
    internal Task<bool> Acceptance => _acceptance.Task;
    internal CancellationToken Token { get; }

    internal NStudyProducerAdmission() => Token = _source.Token;

    internal void Confirm()
    {
        Confirmed = true;
        _acceptance.TrySetResult(true);
    }

    internal void Retain() => Interlocked.Increment(ref _owners);

    internal void Release()
    {
        if (Interlocked.Decrement(ref _owners) == 0)
            _source.Dispose();
    }

    internal Exception? Terminate()
    {
        _acceptance.TrySetResult(false);
        try { _source.Cancel(); return null; }
        catch (Exception exception) { return exception; }
        finally { Release(); }
    }
}

internal sealed partial class PendingRequestTable
{
    private int _nStudyProducerCount;
    internal int NStudyProducerCount => Volatile.Read(ref _nStudyProducerCount);

    internal NStudyProducerAdmission? RetainNStudyProducer(long id)
    {
        var slots = Volatile.Read(ref _slots);
        var index = (int)(id & _indexMask);
        if (slots is null || Volatile.Read(ref slots[index]) is not { } call)
            return null;
        lock (call.CompletionGate)
        {
            if (call.Id != id || !ReferenceEquals(Volatile.Read(ref slots[index]), call) ||
                call.NStudyAdmission is not { } admission || Volatile.Read(ref _disposed) != 0)
                return null;
            if (Interlocked.Increment(ref _nStudyProducerCount) > _capacity)
            {
                Interlocked.Decrement(ref _nStudyProducerCount);
                throw CreateResourceExhaustedException();
            }
            admission.Retain();
            return admission;
        }
    }

    internal void ReleaseNStudyProducer(NStudyProducerAdmission admission)
    {
        admission.Release();
        if (Interlocked.Decrement(ref _nStudyProducerCount) == 0)
            _owner.OnPendingCallCapacityIdle();
    }

    internal void AcceptNStudyRequest(long id)
    {
        if (id <= 0 || id > Volatile.Read(ref _nextId))
            throw RpcSession.InvalidNStudy("Stream acceptance does not match an issued request.");
        var slots = Volatile.Read(ref _slots);
        var index = (int)(id & _indexMask);
        if (slots is null || Volatile.Read(ref slots[index]) is not { } call)
            return;
        lock (call.CompletionGate)
        {
            if (call.Id != id || !ReferenceEquals(Volatile.Read(ref slots[index]), call))
                return; // Ordinary terminal completion, concurrent with the receipt.
            if (!call.RequestPublished || call.NStudyAdmission is not { } admission)
                throw RpcSession.InvalidNStudy("Stream acceptance does not match a published input stream.");
            if (!call.Deadline.IsExpired(_timeProvider))
            {
                admission.Confirm();
                return;
            }
        }
        TryComplete(id, PendingCallCompletionReason.DeadlineExceeded);
    }

    internal bool TryStartNStudyProducer(long id, NStudyProducerAdmission admission)
    {
        var slots = Volatile.Read(ref _slots);
        var index = (int)(id & _indexMask);
        if (slots is null || Volatile.Read(ref slots[index]) is not { } call)
            return false;
        lock (call.CompletionGate)
        {
            if (call.Id != id || !ReferenceEquals(Volatile.Read(ref slots[index]), call) ||
                !ReferenceEquals(call.NStudyAdmission, admission) || !admission.Confirmed || admission.Started)
                return false;
            if (!call.Deadline.IsExpired(_timeProvider))
            {
                admission.Started = true;
                return true;
            }
        }
        TryComplete(id, PendingCallCompletionReason.DeadlineExceeded);
        return false;
    }

    internal void RequireNStudyAcceptance(long id)
    {
        var slots = Volatile.Read(ref _slots);
        var index = (int)(id & _indexMask);
        if (slots is not null && Volatile.Read(ref slots[index]) is { } call)
        {
            lock (call.CompletionGate)
            {
                if (call.Id == id && ReferenceEquals(Volatile.Read(ref slots[index]), call) &&
                    call.NStudyAdmission is { Confirmed: false })
                    throw RpcSession.InvalidNStudy("Successful stream progress preceded admission.");
            }
        }
    }
}
