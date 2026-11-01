namespace SharpLink.Runtime;

internal enum RpcFramePublicationRole : byte
{
    None,
    RequestHead,
    StreamData,
    StreamTerminal,
    ReleaseBinding,
    StreamFence,
    StreamCleanupFence
}

internal enum RpcStreamHeadDisposition : byte { Pending, Admitted, Rejected }

// Stable identities only. These controls do not own a CallSite, codec, collectible registration,
// or per-call shared count. Physical caller/producer owners retain those protections separately.
internal abstract class RpcFramePublication
{
    internal abstract RpcSession Session { get; }
}

internal abstract class RpcBindingPublication : RpcFramePublication
{
    protected RpcBindingPublication(RpcSession session, ulong token)
    {
        Session = session ?? throw new ArgumentNullException(nameof(session));
        if (!Protocol3BindingToken.IsValid(token, 65_536)) throw new ArgumentOutOfRangeException(nameof(token));
        Token = token;
    }
    internal override RpcSession Session { get; }
    internal ulong Token { get; }
    // Written/read for wire admission by this Session's one send-pump consumer only.
    internal bool ReleaseClaimed;
}

internal abstract class RpcStreamPublication : RpcFramePublication
{
    protected RpcStreamPublication(ushort declaredStreamCount)
    {
        if (declaredStreamCount is < 1 or > 127) throw new ArgumentOutOfRangeException(nameof(declaredStreamCount));
        DeclaredStreamCount = declaredStreamCount;
    }

    protected RpcStreamPublication(RpcBindingPublication binding, long requestId, ushort declaredStreamCount)
        : this(declaredStreamCount)
        => InitializeIdentity(binding, requestId);

    private RpcBindingPublication? _binding;
    private long _requestId;

    // Called exactly once under the derived group's EXISTING gate, after argument/session
    // validation and winning pending-slot CAS, before that completion gate can be released.
    // No callbacks, transport, allocations or new lock. The release-published binding is the
    // final adoption write; the request ID is immutable thereafter. This is not rebindable.
    protected void InitializeIdentity(RpcBindingPublication binding, long requestId)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (requestId <= 0) throw new ArgumentOutOfRangeException(nameof(requestId));
        if (Volatile.Read(ref _binding) is not null)
            throw new InvalidOperationException("Stream publication identity can only be adopted once.");
        _requestId = requestId;
        Volatile.Write(ref _binding, binding);
    }

    internal RpcBindingPublication Binding => Volatile.Read(ref _binding)
        ?? throw new InvalidOperationException("Stream publication has not adopted its exact invocation identity.");
    internal long RequestId
    {
        get { _ = Binding; return _requestId; }
    }
    internal ushort DeclaredStreamCount { get; }
    private long _queuedTerminalsLow;
    private long _queuedTerminalsHigh;
    private ulong _consumedTerminalsLow; // Sole pump consumer only.
    private ulong _consumedTerminalsHigh;
    private bool _cleanupFenceStarted; // Sole bounded cleanup publisher only.

    internal void ValidateStreamId(ushort streamId)
    {
        _ = Binding; // Unadopted controls cannot affect transport/flow/fence state.
        if (streamId == 0 || streamId > DeclaredStreamCount)
            throw new InvalidOperationException("Stream ID is outside this exact group's declared domain.");
    }

    internal bool IsTerminalQueued(ushort streamId)
    {
        ValidateStreamId(streamId);
        var index = streamId - 1;
        var bits = index < 64 ? Volatile.Read(ref _queuedTerminalsLow) : Volatile.Read(ref _queuedTerminalsHigh);
        return (unchecked((ulong)bits) & (1UL << (index & 63))) != 0;
    }

    // Validated before queue transfer. Atomic only at terminal enqueue, never per data item.
    internal void RecordTerminalQueued(ushort streamId)
    {
        _ = Binding; // Unadopted controls cannot affect transport/flow/fence state.
        var index = streamId - 1;
        var mask = unchecked((long)(1UL << (index & 63)));
        if (index < 64) Interlocked.Or(ref _queuedTerminalsLow, mask);
        else Interlocked.Or(ref _queuedTerminalsHigh, mask);
    }

    internal bool IsTerminalConsumed(ushort streamId)
    {
        _ = Binding; // Unadopted controls cannot affect transport/flow/fence state.
        var index = streamId - 1;
        var bits = index < 64 ? _consumedTerminalsLow : _consumedTerminalsHigh;
        return (bits & (1UL << (index & 63))) != 0;
    }

    internal void MarkTerminalConsumed(ushort streamId)
    {
        _ = Binding; // Unadopted controls cannot affect transport/flow/fence state.
        var index = streamId - 1;
        if (index < 64) _consumedTerminalsLow |= 1UL << index;
        else _consumedTerminalsHigh |= 1UL << (index - 64);
    }

    internal void BeginCleanupFence()
    {
        _ = Binding; // Unadopted controls cannot affect transport/flow/fence state.
        if (_cleanupFenceStarted || _fenceStarted)
            throw new InvalidOperationException("A group has only one early cleanup fence before its final fence.");
        _cleanupFenceStarted = true;
    }
    internal override RpcSession Session => Binding.Session;
    // Only the pump consumer mutates disposition and accepted-frame cleanup failure.
    internal RpcStreamHeadDisposition HeadDisposition;
    internal Exception? CleanupFailure;
    private bool _fenceStarted; // One physical producer-group owner, after joining every producer.
    internal void BeginFence()
    {
        _ = Binding; // Unadopted controls cannot affect transport/flow/fence state.
        if (_fenceStarted) throw new InvalidOperationException("A stream publication group can publish only one final fence.");
        _fenceStarted = true;
    }
    internal void ValidateProducer(RpcSession session, long requestId)
    {
        if (!ReferenceEquals(Session, session) || RequestId != requestId || _fenceStarted)
            throw new InvalidOperationException("Client stream emission does not belong to this live exact publication group.");
    }
}

internal static class RpcPublicationErrors
{
    internal static Exception? Combine(Exception? first, Exception? next)
        => first is null ? next : next is null || ReferenceEquals(first, next) ? first : new AggregateException(first, next);
    internal static Exception Rejected()
        => new SharpLinkException(SharpLinkErrorCode.FailedPrecondition,
            "Binding retired before Request wire admission (binding_retired_before_emission).");
}
