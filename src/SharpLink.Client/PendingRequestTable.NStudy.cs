namespace SharpLink.Client;

// The pending slot owns terminal notification. Its producer keeps a second
// reference through the real writer/iterator exit, including user DisposeAsync.
internal sealed class NStudyProducerAdmission
{
    private readonly CancellationTokenSource _source = new();
    private readonly TaskCompletionSource<bool>? _acceptance;
    private int _owners = 1;
    private bool _confirmed;
    private bool _requestPublished;
    internal bool RequiresAcceptance => _acceptance is not null;
    internal bool Confirmed => !RequiresAcceptance || _confirmed;
    internal bool Started { get; set; }
    internal Task<bool> Acceptance => _acceptance!.Task;
    internal CancellationToken Token { get; }
    internal bool RequestPublished => Volatile.Read(ref _requestPublished);

    internal NStudyProducerAdmission(bool requiresAcceptance)
    {
        if (requiresAcceptance)
            _acceptance = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Token = _source.Token;
    }

    internal void MarkRequestPublished() => Volatile.Write(ref _requestPublished, true);

    internal void Confirm()
    {
        _confirmed = true;
        _acceptance!.TrySetResult(true);
    }

    internal void Retain() => Interlocked.Increment(ref _owners);

    internal void Release()
    {
        if (Interlocked.Decrement(ref _owners) == 0)
            _source.Dispose();
    }

    internal Exception? Terminate(PendingCallCompletionReason reason)
    {
        _acceptance?.TrySetResult(false);
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
            if (!call.RequestPublished || call.NStudyAdmission is not { RequiresAcceptance: true } admission)
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
                !call.RequestPublished || !ReferenceEquals(call.NStudyAdmission, admission) ||
                !admission.Confirmed || admission.Started)
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
