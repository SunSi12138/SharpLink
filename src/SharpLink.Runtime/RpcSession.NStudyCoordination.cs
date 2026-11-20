using System.Threading.Channels;

namespace SharpLink.Runtime;

internal sealed partial class RpcSession
{
    private readonly Lock _nStudyCoordinationGate = new();
    private Channel<NStudyCoordinationMessage>? _nStudyCoordinationInbox;
    private bool _nStudyCoordinationOwned;
    private ulong _nStudyTransitionId;
    private NStudyChange? _nStudyActivationNotSent;
    private NStudyChange? _nStudyFrozen;
    private NStudyCoordinationMode _nStudyCoordinationMode;

    internal bool SupportsNStudyCoordination => (NegotiatedCapabilities & NStudyWire.CoordinationCapability) != 0;
    internal NStudyCoordinationMode NStudyLocalCoordinationMode => _nStudyCoordinationMode;

    // Both advertisement and decisions use this physical Session's one local
    // configuration. Reconnect samples a new setting, not an old receipt.
    internal void ConfigureNStudyCoordination(NStudyCoordinationMode mode)
    {
        if (ProtocolPhase != RpcSessionProtocolPhase.Handshaking)
            throw InvalidNStudy("Version coordination must be configured before this Session's handshake.");
        if (mode is < NStudyCoordinationMode.Disabled or > NStudyCoordinationMode.Automatic)
            throw new ArgumentOutOfRangeException(nameof(mode));
        _nStudyCoordinationMode = mode;
    }

    internal void ValidateNStudyLocalDecision(Func<NStudyChange, CancellationToken, ValueTask<bool>>? manualDecision)
    {
        if (_nStudyCoordinationMode == NStudyCoordinationMode.Manual)
            ArgumentNullException.ThrowIfNull(manualDecision);
        else if (manualDecision is not null)
            throw new ArgumentException("An application approval callback is used only in Manual mode.", nameof(manualDecision));
    }

    // Callers invoke this only after verifying their own actually prepared
    // material. Automatic approves that fact; it never replaces preparation.
    internal async ValueTask<bool> DecideNStudyLocallyAsync(NStudyChange change,
        Func<NStudyChange, CancellationToken, ValueTask<bool>>? manualDecision, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return _nStudyCoordinationMode switch
        {
            NStudyCoordinationMode.Automatic => true,
            NStudyCoordinationMode.Manual => await manualDecision!(change, ct).AsTask().WaitAsync(ct).ConfigureAwait(false),
            _ => false
        };
    }

    internal IDisposable EnterNStudyCoordination()
    {
        EnsureNStudyCoordinationAvailable();
        lock (_nStudyCoordinationGate)
        {
            if (_nStudyCoordinationOwned)
                throw new SharpLinkException(SharpLinkErrorCode.ResourceExhausted, "This Session is already coordinating a version.");
            _nStudyCoordinationOwned = true;
        }
        return new NStudyCoordinationScope(this);
    }

    internal ulong NextNStudyTransitionId()
    {
        lock (_nStudyCoordinationGate)
            return checked(++_nStudyTransitionId);
    }

    internal void ObserveNStudyTransitionId(ulong id)
    {
        lock (_nStudyCoordinationGate)
        {
            if (id <= _nStudyTransitionId)
                throw InvalidNStudy("The version transition did not advance on this physical Session.");
            _nStudyTransitionId = id;
        }
    }

    internal void OnNStudyCoordination(ulong id, ReadOnlySequence<byte> payload)
    {
        EnsureNStudyCoordinationAvailable();
        var message = NStudyWire.ReadCoordination(id, payload);
        var fromClient = message.Phase is NStudyPhase.Ready or NStudyPhase.Declined ||
            (message.Phase == NStudyPhase.Propose && message.Change.Initiator == NStudyInitiator.Client);
        var fromServer = message.Phase is NStudyPhase.Candidate or NStudyPhase.Prepare or NStudyPhase.Activated or NStudyPhase.Declined ||
            (message.Phase == NStudyPhase.Propose && message.Change.Initiator == NStudyInitiator.Server);
        if (!(Role == RpcSessionRole.Server ? fromClient : fromServer) ||
            !GetNStudyCoordinationInbox().Writer.TryWrite(message))
            throw InvalidNStudy("Version coordination exceeded the Session's role or inbox.");
    }

    internal ValueTask<NStudyCoordinationMessage> ReadNStudyCoordinationAsync(CancellationToken cancellationToken)
        => GetNStudyCoordinationInbox().Reader.ReadAsync(cancellationToken);

    internal async ValueTask<NStudyCoordinationMessage> ReadNStudyCoordinationAsync(NStudyChange change,
        NStudyPhase expected, CancellationToken ct, bool allowDecline = false)
    {
        var response = await ReadNStudyCoordinationAsync(ct).ConfigureAwait(false);
        if (response.Change != change ||
            (response.Phase != expected && (!allowDecline || response.Phase != NStudyPhase.Declined)))
            throw InvalidNStudy("Unexpected phase or identity in this version transition.");
        return response;
    }

    private Channel<NStudyCoordinationMessage> GetNStudyCoordinationInbox() =>
        LazyInitializer.EnsureInitialized(ref _nStudyCoordinationInbox, static () =>
            Channel.CreateBounded<NStudyCoordinationMessage>(new BoundedChannelOptions(4)
            {
                SingleReader = true,
                SingleWriter = true,
                AllowSynchronousContinuations = false
            }));

    internal void BlockNStudyTargetBind(NStudyChange change)
    {
        lock (_nStudyCoordinationGate)
            _nStudyActivationNotSent = change;
    }

    internal bool MayBindNStudyTarget(long contract, NStudyTarget target)
    {
        lock (_nStudyCoordinationGate)
            return _nStudyActivationNotSent is not { } change || change.Contract != contract || change.Target != target;
    }

    internal async ValueTask SendNStudyCoordinationAsync(NStudyCoordinationMessage message, CancellationToken ct)
    {
        EnsureNStudyCoordinationAvailable();
        ct.ThrowIfCancellationRequested();
        var writer = RentFrameWriter();
        var transferred = false;
        try
        {
            using (writer.BeginPacketScope(NStudyWire.Coordination, ProtocolV2FrameFlags.None, message.Change.Id))
                NStudyWire.WriteCoordination(writer, message);

            if (message.Phase != NStudyPhase.Activated)
            {
                transferred = true;
                await SendPacketAndFlushAsync(writer, ct).ConfigureAwait(false);
                return;
            }

            ValueTask emitted;
            lock (_nStudyCoordinationGate)
            {
                if (_nStudyActivationNotSent != message.Change)
                    throw InvalidNStudy("Activation does not match the one prepared target.");
                transferred = true;
                if (!TryEnqueuePreparedFrame(writer, true, ct, null, out emitted, out var rejection))
                    throw rejection!;
                // R2 Bind may pass only after the activation was queued in this
                // physical Session's ordinary FIFO. A racing receiver sees this cut.
                _nStudyActivationNotSent = null;
            }
            await emitted.ConfigureAwait(false);
        }
        finally
        {
            if (!transferred)
                RuntimeContext.Buffers.Return(writer);
        }
    }

    internal void ValidateNStudySource(NStudyChange change)
    {
        lock (_nStudyGate)
            _ = GetNStudySourceLocked(change);
    }

    internal Task FreezeNStudySource(NStudyChange change)
    {
        lock (_nStudyGate)
        {
            var route = GetNStudySourceLocked(change);
            if (_pendingNStudyBind is not null || _nStudyReleasePending)
                throw new SharpLinkException(SharpLinkErrorCode.Unavailable, "A local Bind or Release still owns the Session.");
            _nStudyFrozen = change;
            _nStudyBindings.Remove(change.Contract);
            return route.Retire();
        }
    }

    private NStudyClientRoute GetNStudySourceLocked(NStudyChange change) =>
        _nStudyAuthority == change.Authority &&
        _nStudyBindings.TryGetValue(change.Contract, out var route) &&
        route.Target == change.Original && route.Hash == change.Hash
            ? route : throw InvalidNStudy("Version preparation does not match this Session's exact source binding.");

    private void EnsureNStudyCoordinationAvailable()
    {
        EnsureNStudyReady(Role);
        if (!SupportsNStudyCoordination || _nStudyCoordinationMode == NStudyCoordinationMode.Disabled)
            throw new SharpLinkException(SharpLinkErrorCode.FailedPrecondition, "Version coordination was not negotiated by both peers.");
    }

    private sealed class NStudyCoordinationScope(RpcSession session) : IDisposable
    {
        public void Dispose()
        {
            lock (session._nStudyGate)
                session._nStudyFrozen = null;
            lock (session._nStudyCoordinationGate)
            {
                session._nStudyActivationNotSent = null;
                session._nStudyCoordinationOwned = false;
            }
        }
    }
}
