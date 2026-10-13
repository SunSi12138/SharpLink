namespace SharpLink.Client;

/// <summary>Owns one pooled call generation, its producer lifetime and bounded admission waiter.</summary>
internal sealed class PendingCall
{
    private const int MaxRetained = 4096;
    private static readonly ConcurrentQueue<PendingCall> Pool = new();
    private static int s_retainedCount;

    private PendingRequestTable? _table;
    private CancellationTokenRegistration _cancellationRegistration;
    private CancellationTokenSource? _producerCancellation;
    private IPendingCallCompletionObserver? _completionObserver;
    private int _registered;
    private bool _requestPublished;
    private bool _requestAccepted;
    private RequestAdmissionWaiter? _requestAdmission;

    public object CompletionGate { get; } = new();

    public long Id { get; private set; }
    public PendingCallKind Kind { get; private set; }
    public IRpcOperation? Operation { get; private set; }
    public IStreamDispatcher? Dispatcher { get; private set; }
    public RpcDeadline Deadline { get; private set; }
    public CancellationToken CancellationToken { get; private set; }
    public CancellationToken ProducerCancellationToken
        => _producerCancellation?.Token ?? CancellationToken.None;
    public IPendingCallCompletionObserver? CompletionObserver => _completionObserver;

    /// <summary>
    /// Whether the owning Request frame reached the session send queue while this call was
    /// still live.
    /// </summary>
    /// <remarks>
    /// Written once by the publisher while it holds <see cref="CompletionGate"/> and only read
    /// after the call left the slot, so the gate orders the flag against every terminal
    /// transition. A terminal transition that observes this as false must not emit a cancel:
    /// the peer would discard a cancel for a request it never received.
    /// </remarks>
    public bool RequestPublished => _requestPublished;

    public void MarkRequestPublished() => _requestPublished = true;

    // The table calls these while this exact slot generation owns CompletionGate. Completion
    // rejects any outstanding waiter after removing the slot, before the call can be pooled.
    public ValueTask<bool> WaitForRequestAdmissionAsync()
        => _requestAccepted
            ? ValueTask.FromResult(true)
            : (_requestAdmission ??= RequestAdmissionWaiter.Rent()).WaitAsync();

    public bool TryAcceptRequest()
    {
        if (_requestAccepted)
            return false;
        _requestAccepted = true;
        _requestAdmission?.Complete(true);
        _requestAdmission = null;
        return true;
    }

    public void RejectRequestAdmission()
    {
        _requestAdmission?.Complete(false);
        _requestAdmission = null;
    }

    public static PendingCall Rent(
        PendingRequestTable table,
        long id,
        PendingCallKind kind,
        IRpcOperation? operation,
        IStreamDispatcher? dispatcher,
        RpcDeadline deadline,
        CancellationToken cancellationToken,
        IPendingCallCompletionObserver? completionObserver)
    {
        if (!Pool.TryDequeue(out var call))
            call = new PendingCall();
        else
            Interlocked.Decrement(ref s_retainedCount);

        call._table = table;
        Volatile.Write(ref call._registered, 0);
        call._requestPublished = false;
        call._requestAccepted = false;
        call.Id = id;
        call.Kind = kind;
        call.Operation = operation;
        call.Dispatcher = dispatcher;
        call.Deadline = deadline;
        call.CancellationToken = cancellationToken;
        call._completionObserver = completionObserver;
        call._producerCancellation = kind is
            PendingCallKind.ClientStreaming or
            PendingCallKind.DuplexStreaming or
            PendingCallKind.OneWayClientStreaming
            ? new CancellationTokenSource()
            : null;
        call._cancellationRegistration = cancellationToken.CanBeCanceled
            ? cancellationToken.UnsafeRegister(static state =>
            {
                var pending = (PendingCall)state!;
                pending._table?.TryComplete(
                    pending.Id,
                    PendingCallCompletionReason.UserCancellation);
            }, call)
            : default;
        return call;
    }

    public void DisposeCancellationRegistration() => _cancellationRegistration.Dispose();

    public void MarkRegistered() => Volatile.Write(ref _registered, 1);

    public void WaitUntilRegistered()
    {
        var spinner = new SpinWait();
        while (Volatile.Read(ref _registered) == 0)
            spinner.SpinOnce();
    }

    public Exception? CancelProducer(PendingCallCompletionReason reason)
    {
        var producerCancellation = _producerCancellation;
        if (producerCancellation is null)
            return null;
        _producerCancellation = null;
        if (reason == PendingCallCompletionReason.LocalStreamComplete)
        {
            producerCancellation.Dispose();
            return null;
        }
        return CancelAndDisposeProducer(producerCancellation);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Exception? CancelAndDisposeProducer(
        CancellationTokenSource producerCancellation)
    {
        Exception? callbackFailure = null;
        try
        {
            producerCancellation.Cancel();
        }
        catch (Exception exception)
        {
            callbackFailure = exception;
        }
        finally
        {
            producerCancellation.Dispose();
        }
        return callbackFailure;
    }

    public void ReturnUnused()
    {
        _cancellationRegistration.Dispose();
        _producerCancellation?.Dispose();
        _producerCancellation = null;
        ReturnCore();
    }

    public void ReturnCompleted() => ReturnCore();

    private void ReturnCore()
    {
        _table = null;
        _requestAdmission = null;
        _requestAccepted = false;
        Id = 0;
        Kind = default;
        Operation = null;
        Dispatcher = null;
        Deadline = default;
        CancellationToken = CancellationToken.None;
        _producerCancellation = null;
        _completionObserver = null;
        _cancellationRegistration = default;
        Volatile.Write(ref _registered, 0);

        while (true)
        {
            var retained = Volatile.Read(ref s_retainedCount);
            if (retained >= MaxRetained)
                return;
            if (Interlocked.CompareExchange(ref s_retainedCount, retained + 1, retained) == retained)
                break;
        }
        Pool.Enqueue(this);
    }
}
