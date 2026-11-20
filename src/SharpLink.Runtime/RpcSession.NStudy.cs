namespace SharpLink.Runtime;

internal sealed partial class RpcSession
{
    private static readonly TimeSpan NStudyTimeout = TimeSpan.FromSeconds(5);
    private readonly Lock _nStudyGate = new();
    private readonly Dictionary<long, NStudyAck> _nStudyBindings = [];
    private PendingNStudyBind? _pendingNStudyBind;
    private ulong _lastNStudyOperation;
    private NStudyAuthority _nStudyAuthority;

    internal long CaptureNStudyBinding(long contractId)
    {
        lock (_nStudyGate)
            return _nStudyBindings.TryGetValue(contractId, out var ack) ? ack.BindingId :
                throw new SharpLinkException(SharpLinkErrorCode.Unavailable,
                    "No prepared private binding on the selected physical Session.");
    }

    internal async Task<NStudyAck> PrepareNStudyBindAsync(NStudyBind bind, CancellationToken cancellationToken)
    {
        EnsureNStudyReady(RpcSessionRole.Client);
        cancellationToken.ThrowIfCancellationRequested();
        var pending = new PendingNStudyBind(bind);
        ulong operation;
        lock (_nStudyGate)
        {
            if (_pendingNStudyBind is not null)
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
            lock (_nStudyGate)
            {
                ct.ThrowIfCancellationRequested();
                if (_nStudyAuthority != default && _nStudyAuthority != ack.Authority)
                    throw InvalidNStudy("Private Bind did not come from the same Session authority.");
                _nStudyAuthority = ack.Authority;
                if (ack.Status == NStudyBindStatus.Bound)
                    _nStudyBindings[ack.Contract] = ack;
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

    internal ValueTask SendNStudyBindAckAsync(ulong operation, NStudyAck ack, CancellationToken ct)
    {
        EnsureNStudyReady(RpcSessionRole.Server);
        return SendNStudyControlAsync(NStudyWire.BindAck, operation, default, ack, ct);
    }

    private ValueTask SendNStudyBindAsync(ulong operation, NStudyBind bind, CancellationToken ct)
        => SendNStudyControlAsync(NStudyWire.Bind, operation, bind, default, ct);

    private async ValueTask SendNStudyControlAsync(ProtocolV2FrameType type, ulong operation,
        NStudyBind bind, NStudyAck ack, CancellationToken ct)
    {
        var writer = RentFrameWriter();
        var ownsWriter = true;
        try
        {
            using (writer.BeginPacketScope(type, ProtocolV2FrameFlags.None, operation))
            {
                if (type == NStudyWire.Bind)
                    NStudyWire.WriteBind(writer, bind);
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
