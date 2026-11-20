namespace SharpLink.Runtime;

internal sealed partial class RpcSession
{
    private static readonly TimeSpan NStudyTimeout = TimeSpan.FromSeconds(5);
    private readonly Lock _nStudyGate = new();
    private readonly Dictionary<long, NStudyClientRoute> _nStudyBindings = [];
    private uint[]? _nStudyRetiredThrough;
    private bool _nStudyReleasePending;
    private PendingNStudyBind? _pendingNStudyBind;
    private ulong _lastNStudyOperation;
    private NStudyAuthority _nStudyAuthority;

    internal long CaptureNStudyBinding(long contractId) => CaptureNStudyRoute(contractId).Token;

    internal int NStudyPreparedCount
    {
        get { lock (_nStudyGate) return _nStudyBindings.Count; }
    }

    internal NStudyClientRoute CaptureNStudyRoute(long contractId)
    {
        lock (_nStudyGate)
            return _nStudyBindings.TryGetValue(contractId, out var route) ? route :
                throw new SharpLinkException(SharpLinkErrorCode.Unavailable,
                    "No prepared private binding on the selected physical Session.");
    }

    internal async Task<NStudyAck> PrepareNStudyBindAsync(NStudyBind bind, CancellationToken cancellationToken,
        NStudyChange? resuming = null)
    {
        EnsureNStudyReady(RpcSessionRole.Client);
        cancellationToken.ThrowIfCancellationRequested();
        var pending = new PendingNStudyBind(bind);
        ulong operation;
        lock (_nStudyGate)
        {
            if (_nStudyCandidate is { } candidate && candidate.Contract == bind.Contract &&
                (!_nStudySourceFrozen || resuming != candidate || bind.Expected != candidate.Target || bind.Hash != candidate.Hash))
                throw new SharpLinkException(SharpLinkErrorCode.Unavailable, "The contract is awaiting its coordinated activation.");
            if (_pendingNStudyBind is not null || _nStudyReleasePending)
                throw new SharpLinkException(SharpLinkErrorCode.ResourceExhausted, "A private Bind is already pending.");
            operation = checked(++_lastNStudyOperation);
            _pendingNStudyBind = pending;
        }

        using var timer = new CancellationTokenSource(NStudyTimeout, RuntimeContext.TimeProvider);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(LifetimeToken, timer.Token, cancellationToken);
        var ct = timeout.Token;
        try
        {
            await SendNStudyBindAsync(operation, bind, ct).ConfigureAwait(false);
            var ack = await pending.Completion.Task.WaitAsync(ct).ConfigureAwait(false);
            NStudyClientRoute? prior = null;
            Task? drained = null;
            lock (_nStudyGate)
            {
                ct.ThrowIfCancellationRequested();
                if (_nStudyAuthority != default && _nStudyAuthority != ack.Authority)
                    throw InvalidNStudy("Private Bind did not come from the same Session authority.");
                _nStudyAuthority = ack.Authority;
                if (ack.Status == NStudyBindStatus.Bound)
                {
                    // Registry cut can reach the peer before the original BindAck.
                    if (IsNStudyRetired(ack.BindingId))
                        return ack with { Status = NStudyBindStatus.Superseded, BindingId = 0, Target = default };
                    var incarnation = NStudyWire.Incarnation(ack.BindingId);
                    if (incarnation > 1)
                    {
                        // Reuse itself confirms prior incarnations are closed,
                        // even if the lightweight notice is still in flight.
                        MarkNStudyRetiredThrough(NStudyWire.SlotIndex(ack.BindingId), incarnation - 1);
                        RetireNStudyRoutes();
                    }
                    var next = new NStudyClientRoute(this, ack);
                    if (_nStudyBindings.TryGetValue(ack.Contract, out prior))
                        drained = prior.Retire();
                    _nStudyBindings[ack.Contract] = next;
                }
            }
            if (drained is not null)
            {
                await drained.WaitAsync(ct).ConfigureAwait(false);
                await SendNStudyReleaseAsync(prior!.Token, ct).ConfigureAwait(false);
            }
            return ack;
        }
        catch (Exception error)
        {
            // A send or timeout with ambiguous publication cannot leave an
            // unbounded waiter/slot behind. Retire this physical Session.
            NotifyDisconnected(error);
            throw;
        }
        finally
        {
            lock (_nStudyGate)
                _pendingNStudyBind = null;
        }
    }

    internal async Task<bool> ReleaseNStudyBindingAsync(long contract, CancellationToken cancellationToken)
    {
        EnsureNStudyReady(RpcSessionRole.Client);
        cancellationToken.ThrowIfCancellationRequested();
        NStudyClientRoute route;
        Task drained;
        lock (_nStudyGate)
        {
            if (_pendingNStudyBind is not null || _nStudyReleasePending)
                throw new SharpLinkException(SharpLinkErrorCode.ResourceExhausted, "A private binding control is already pending.");
            if (!_nStudyBindings.Remove(contract, out route!))
                return false;
            drained = route.Retire();
            _nStudyReleasePending = true;
        }

        using var timer = new CancellationTokenSource(NStudyTimeout, RuntimeContext.TimeProvider);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(LifetimeToken, timer.Token, cancellationToken);
        try
        {
            await drained.WaitAsync(budget.Token).ConfigureAwait(false);
            await SendNStudyReleaseAsync(route.Token, budget.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception error)
        {
            NotifyDisconnected(error);
            throw;
        }
        finally
        {
            lock (_nStudyGate)
                _nStudyReleasePending = false;
        }
    }

    internal bool TryEnqueueNStudyRequest(NStudyClientRoute route, IRpcByteBufferWriter writer,
        bool observeEmission, CancellationToken ct, IRequestEmissionFailureObserver? observer,
        out ValueTask emission, out Exception? rejection)
    {
        if (!route.TryEnter(this))
        {
            RuntimeContext.Buffers.Return(writer);
            emission = default;
            rejection = new SharpLinkException(SharpLinkErrorCode.Unavailable, "This private binding is retired.");
            return false;
        }
        try
        {
            return TryEnqueuePreparedFrame(writer, observeEmission, ct, observer, out emission, out rejection);
        }
        finally { route.Leave(); }
    }

    internal void OnNStudyBindAck(ulong operation, ReadOnlySequence<byte> payload)
    {
        EnsureNStudyReady(RpcSessionRole.Client);
        var ack = NStudyWire.ReadAck(payload);
        lock (_nStudyGate)
        {
            if (_pendingNStudyBind is not { } pending || _lastNStudyOperation != operation ||
                pending.Bind.Contract != ack.Contract || pending.Bind.Hash != ack.Hash ||
                (ack.Status == NStudyBindStatus.Bound && pending.Bind.Expected != default &&
                 ack.Target != pending.Bind.Expected) || !pending.Completion.TrySetResult(ack))
                throw InvalidNStudy("Private Bind acknowledgement does not match its outstanding operation.");
        }
    }

    internal void OnNStudyInvalidated(ReadOnlySequence<byte> payload)
    {
        EnsureNStudyRetirementPhase(RpcSessionRole.Client);
        Span<long> tokens = stackalloc long[NStudyWire.BindingLimit];
        var count = NStudyWire.ReadInvalidated(payload, tokens);
        lock (_nStudyGate)
        {
            foreach (var token in tokens[..count])
                MarkNStudyRetiredThrough(NStudyWire.SlotIndex(token), NStudyWire.Incarnation(token));
            RetireNStudyRoutes();
        }
    }

    private void MarkNStudyRetiredThrough(int slot, uint incarnation)
    {
        var retired = _nStudyRetiredThrough ??= new uint[NStudyWire.BindingLimit];
        retired[slot] = Math.Max(retired[slot], incarnation);
    }

    private void RetireNStudyRoutes()
    {
        // Dictionary removal does not invalidate enumeration. Closing the
        // append gate never joins old calls or blocks the receive loop.
        foreach (var (contract, route) in _nStudyBindings)
        {
            if (IsNStudyRetired(route.Token))
            {
                _ = route.Retire();
                _nStudyBindings.Remove(contract);
            }
        }
    }

    private bool IsNStudyRetired(long token) =>
        _nStudyRetiredThrough is { } retired &&
        NStudyWire.Incarnation(token) <= retired[NStudyWire.SlotIndex(token)];

    internal ValueTask SendNStudyInvalidatedAsync(ReadOnlyMemory<long> tokens, CancellationToken ct)
    {
        EnsureNStudyRetirementPhase(RpcSessionRole.Server);
        return SendNStudyControlAsync(NStudyWire.Invalidated, 0, default, default, ct, retired: tokens);
    }

    private void EnsureNStudyRetirementPhase(RpcSessionRole role)
    {
        if (Role != role || ProtocolPhase is not (RpcSessionProtocolPhase.Ready or RpcSessionProtocolPhase.Draining) ||
            NegotiatedOptions?.ProtocolMinorVersion != NStudyWire.Minor)
            throw InvalidNStudy("Private retirement requires a compatible physical Session.");
    }

    internal ValueTask SendNStudyBindAckAsync(ulong operation, NStudyAck ack, CancellationToken ct)
    {
        EnsureNStudyReady(RpcSessionRole.Server);
        return SendNStudyControlAsync(NStudyWire.BindAck, operation, default, ack, ct);
    }

    private ValueTask SendNStudyBindAsync(ulong operation, NStudyBind bind, CancellationToken ct)
        => SendNStudyControlAsync(NStudyWire.Bind, operation, bind, default, ct);

    private ValueTask SendNStudyReleaseAsync(long token, CancellationToken ct)
        => SendNStudyControlAsync(NStudyWire.Release, 0, default, default, ct, token);

    private async ValueTask SendNStudyControlAsync(ProtocolV2FrameType type, ulong operation,
        NStudyBind bind, NStudyAck ack, CancellationToken ct, long retiredToken = 0,
        ReadOnlyMemory<long> retired = default)
    {
        var writer = RentFrameWriter();
        var ownsWriter = true;
        try
        {
            using (writer.BeginPacketScope(type, ProtocolV2FrameFlags.None, operation))
            {
                if (type == NStudyWire.Bind)
                    NStudyWire.WriteBind(writer, bind);
                else if (type == NStudyWire.Release)
                    NStudyWire.WriteRelease(writer, retiredToken);
                else if (type == NStudyWire.Invalidated)
                    NStudyWire.WriteInvalidated(writer, retired.Span);
                else
                    NStudyWire.WriteAck(writer, ack);
            }
            ownsWriter = false;
            // Normal existing FIFO, and actual send completion, not a local
            // promise that business or a remote replacement has completed.
            await SendPacketAndFlushAsync(writer, ct).ConfigureAwait(false);
        }
        finally
        {
            if (ownsWriter)
                RuntimeContext.Buffers.Return(writer);
        }
    }

    internal void EnsureNStudyReady(RpcSessionRole role)
    {
        if (Role != role || ProtocolPhase != RpcSessionProtocolPhase.Ready ||
            NegotiatedOptions?.ProtocolMinorVersion != NStudyWire.Minor)
            throw InvalidNStudy("Private Bind requires a compatible Ready physical Session.");
    }

    internal static SharpLinkException InvalidNStudy(string reason) =>
        new SharpLinkProtocolViolationException(ProtocolViolationReason.ProtocolState, reason);

    private sealed class PendingNStudyBind(NStudyBind bind)
    {
        internal NStudyBind Bind { get; } = bind;
        internal TaskCompletionSource<NStudyAck> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
