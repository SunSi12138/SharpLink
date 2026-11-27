namespace SharpLink.Client;

// The pending slot owns terminal notification. Its producer keeps a second
// reference through the real writer/iterator exit, including user DisposeAsync.
internal sealed class PendingInputProducer
{
    private readonly CancellationTokenSource _source = new();
    private int _owners = 1;
    private bool _requestPublished;
    internal bool Started { get; set; }
    internal CancellationToken Token { get; }
    internal bool RequestPublished => Volatile.Read(ref _requestPublished);

    internal PendingInputProducer() => Token = _source.Token;

    internal void MarkRequestPublished() => Volatile.Write(ref _requestPublished, true);

    internal void Retain() => Interlocked.Increment(ref _owners);

    internal void Release()
    {
        if (Interlocked.Decrement(ref _owners) == 0)
            _source.Dispose();
    }

    internal Exception? Terminate(PendingCallCompletionReason reason)
    {
        if (reason == PendingCallCompletionReason.LocalStreamComplete)
        {
            Release();
            return null;
        }
        try { _source.Cancel(); return null; }
        catch (Exception exception) { return exception; }
        finally { Release(); }
    }
}

internal sealed partial class PendingRequestTable
{
    private int _nStudyProducerCount;
    internal int NStudyProducerCount => Volatile.Read(ref _nStudyProducerCount);

    internal PendingInputProducer? RetainNStudyProducer(long id)
    {
        ref var slot = ref ReadSlot(id);
        if (Volatile.Read(ref slot) is not { } call)
            return null;
        lock (call.CompletionGate)
        {
            if (call.Id != id || !ReferenceEquals(Volatile.Read(ref slot), call) ||
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

    internal void ReleaseNStudyProducer(PendingInputProducer admission)
    {
        admission.Release();
        if (Interlocked.Decrement(ref _nStudyProducerCount) == 0)
            _owner.OnPendingCallCapacityIdle();
    }

    internal bool TryStartNStudyProducer(long id, PendingInputProducer admission)
    {
        ref var slot = ref ReadSlot(id);
        if (Volatile.Read(ref slot) is not { } call)
            return false;
        lock (call.CompletionGate)
        {
            if (call.Id != id || !ReferenceEquals(Volatile.Read(ref slot), call) ||
                !call.RequestPublished || !ReferenceEquals(call.NStudyAdmission, admission) ||
                admission.Started)
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
}
