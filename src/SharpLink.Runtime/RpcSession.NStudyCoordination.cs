using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace SharpLink.Runtime;

internal sealed partial class RpcSession
{
    private readonly Lock _nStudyCoordinationGate = new();
    private Channel<NStudyCoordinationMessage>? _nStudyCoordinationInbox;
    private NStudyCoordinationScope? _nStudyCoordinationScope;
    private ulong _nStudyTransitionId;
    private NStudyChange? _nStudyActivationNotSent;
    private bool _nStudyKeepTargetBlock;
    private NStudyChange? _nStudyCandidate;
    private NStudyClientRoute? _nStudyCandidateSource;
    private bool _nStudySourceFrozen;
    private NStudyCoordinationMode _nStudyHandshakeCoordinationMode;

    internal bool SupportsNStudyCoordination => (NegotiatedCapabilities & NStudyWire.CoordinationCapability) != 0;
    private bool SupportsNStudyPolicyDeferral => SupportsNStudyCoordination &&
        (NegotiatedCapabilities & NStudyWire.PolicyDeferralCapability) != 0;
    internal NStudyCoordinationMode NStudyHandshakeCoordinationMode => _nStudyHandshakeCoordinationMode;

    // Handshake capability belongs to the physical Session. Application policy
    // is captured separately by each subsequent coordination scope.
    internal void ConfigureNStudyCoordination(NStudyCoordinationMode mode)
    {
        if (ProtocolPhase != RpcSessionProtocolPhase.Handshaking)
            throw InvalidNStudy("Version coordination must be configured before this Session's handshake.");
        if (mode is < NStudyCoordinationMode.Disabled or > NStudyCoordinationMode.Automatic)
            throw new ArgumentOutOfRangeException(nameof(mode));
        _nStudyHandshakeCoordinationMode = mode;
    }

    internal NStudyCoordinationScope EnterNStudyCoordination(NStudyCoordinationMode mode, NStudyInitiator initiator,
        Func<NStudyChange, CancellationToken, ValueTask<SharpLinkVersionDecision>>? manualDecision,
        bool requirePolicyDeferral = false)
    {
        EnsureNStudyCoordinationAvailable();
        if (requirePolicyDeferral && mode == NStudyCoordinationMode.Manual && !SupportsNStudyPolicyDeferral)
            throw new SharpLinkException(SharpLinkErrorCode.FailedPrecondition,
                "Manual version policy requires both peers to support deferral.");
        lock (_nStudyCoordinationGate)
        {
            if (_nStudyCoordinationScope is not null)
                throw new SharpLinkException(SharpLinkErrorCode.ResourceExhausted, "This Session is already coordinating a version.");
            if (_nStudyKeepTargetBlock)
                throw new SharpLinkException(SharpLinkErrorCode.Unavailable, "This Session must refresh before another version transition.");
            if (mode is < NStudyCoordinationMode.Disabled or > NStudyCoordinationMode.Automatic)
                throw new ArgumentOutOfRangeException(nameof(mode));
            if (mode == NStudyCoordinationMode.Manual)
                ArgumentNullException.ThrowIfNull(manualDecision);
            else if (manualDecision is not null)
                throw new ArgumentException("An application approval callback is used only in Manual mode.", nameof(manualDecision));
            var scope = new NStudyCoordinationScope(this, mode, initiator, manualDecision);
            _nStudyCoordinationScope = scope;
            _nStudyIncomingOffer?.EnrollmentChanged.TrySetResult();
            return scope;
        }
    }

    internal ulong NextNStudyTransitionId()
    {
        lock (_nStudyCoordinationGate)
            return checked(++_nStudyTransitionId);
    }

    // Caller owns the coordination gate.
    private void ObserveNStudyTransitionId(ulong id)
    {
        if (id <= _nStudyTransitionId)
            throw InvalidNStudy("The version transition did not advance on this physical Session.");
        _nStudyTransitionId = id;
    }

    internal Task? OnNStudyCoordination(ulong id, ReadOnlySequence<byte> payload,
        Func<NStudyCoordinationMode>? readLivePolicy = null)
    {
        EnsureNStudyCoordinationAvailable();
        var message = NStudyWire.ReadCoordination(id, payload);
        ValidateNStudyCoordinationCapabilities(message);

        var fromClient = message.Phase is NStudyPhase.Ready or NStudyPhase.Declined or NStudyPhase.Enrolled ||
            (message.Phase == NStudyPhase.Propose && message.Change.Initiator == NStudyInitiator.Client);
        var fromServer = message.Phase is NStudyPhase.Candidate or NStudyPhase.Prepare or NStudyPhase.Activated or
            NStudyPhase.Declined or NStudyPhase.Offer ||
            (message.Phase == NStudyPhase.Propose && message.Change.Initiator == NStudyInitiator.Server);
        if (!(Role == RpcSessionRole.Server ? fromClient : fromServer))
            throw InvalidNStudy("Version coordination exceeded the Session's role or inbox.");
        if (message.Phase == NStudyPhase.Offer)
            return ObserveNStudyOffer(message.Change, readLivePolicy!);

        if (message.Phase == NStudyPhase.Candidate)
        {
            var change = message.Change;
            lock (_nStudyCoordinationGate)
            {
                if (SupportsNStudyOfferAdmission)
                {
                    if (_nStudyCoordinationScope?.AdmittedOffer != change)
                        throw InvalidNStudy("A candidate did not match the enrolled offer.");
                }
                else
                {
                    ObserveNStudyTransitionId(change.Id);
                }

                lock (_nStudyGate)
                {
                    if (_nStudyCandidate.HasValue || _nStudyAuthority != change.Authority)
                        throw InvalidNStudy("A candidate did not belong to this Session's negotiated authority.");
                    _nStudyCandidate = change;
                    // Capture on the ordered reader, before Invalidate can remove a lagging peer's old route.
                    if (_nStudyBindings.TryGetValue(change.Contract, out var route) &&
                        route.Target == change.Original && route.Hash == change.Hash)
                        _nStudyCandidateSource = route;
                }
            }
        }
        if (!GetNStudyCoordinationInbox().Writer.TryWrite(message))
            throw InvalidNStudy("The bounded version coordination inbox was full.");
        return null;
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

    internal bool MayBindNStudyTarget(NStudyTarget target)
    {
        lock (_nStudyCoordinationGate)
            return _nStudyActivationNotSent is not { } change || change.Target != target;
    }

    internal void KeepNStudyTargetBlockUntilActivated(NStudyChange change)
    {
        lock (_nStudyCoordinationGate)
        {
            if (_nStudyActivationNotSent != change)
                throw InvalidNStudy("A coordinated publication lost its per-Session target gate.");
            _nStudyKeepTargetBlock = true;
        }
    }

    internal async ValueTask SendNStudyCoordinationAsync(NStudyCoordinationMessage message, CancellationToken ct)
    {
        EnsureNStudyCoordinationAvailable();
        ValidateNStudyCoordinationCapabilities(message);
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
                _nStudyKeepTargetBlock = false;
            }
            await emitted.ConfigureAwait(false);
        }
        finally
        {
            if (!transferred)
                RuntimeContext.Buffers.Return(writer);
        }
    }

    internal NStudyClientRoute? ReadNStudyCandidateSource(NStudyChange change)
    {
        lock (_nStudyGate)
            return _nStudyCandidate == change ? _nStudyCandidateSource : null;
    }

    internal bool TryFreezeNStudySource(NStudyChange change, out Task drained)
    {
        lock (_nStudyGate)
        {
            if (_nStudyCandidate != change || _nStudyCandidateSource is not { } route ||
                (_nStudyBindings.TryGetValue(change.Contract, out var current)
                    ? !ReferenceEquals(current, route) : !IsNStudyRetired(route.Token)) ||
                _nStudySourceFrozen)
                throw InvalidNStudy("Preparation lost its captured original binding on this Session.");
            if (_pendingNStudyBind is not null || _nStudyReleasePending)
            {
                drained = Task.CompletedTask;
                return false;
            }
            // From here through resume or decline, the transition owns the Session's
            // binding control. Already-prepared RPCs use their existing routes.
            _nStudySourceFrozen = true;
            _nStudyBindings.Remove(change.Contract);
            drained = route.Retire();
            return true;
        }
    }

    internal ValueTask ReleaseFrozenNStudySourceAsync(NStudyChange change, CancellationToken ct)
    {
        long token;
        lock (_nStudyGate)
        {
            if (_nStudyCandidate != change || !_nStudySourceFrozen || _nStudyCandidateSource is not { } route)
                throw InvalidNStudy("Releasing a frozen binding requires its exact version transition.");
            token = route.Token;
        }
        // Ready was sent only after the last source publisher exited. Release
        // follows it on that same FIFO and is harmless if a registry cut won.
        return SendNStudyReleaseAsync(token, ct);
    }

    private void EnsureNStudyCoordinationAvailable()
    {
        EnsureNStudyReady(Role);
        if (!SupportsNStudyCoordination)
            throw new SharpLinkException(SharpLinkErrorCode.FailedPrecondition, "Version coordination was not negotiated by both peers.");
    }

    private void ValidateNStudyCoordinationCapabilities(NStudyCoordinationMessage message)
    {
        if (UsesNStudyOfferAdmission(message) && !SupportsNStudyOfferAdmission)
            throw InvalidNStudy("Offer admission was not negotiated on this physical Session.");
        if (message.DeclineReason == NStudyDeclineReason.PolicyDeferred && !SupportsNStudyPolicyDeferral)
            throw InvalidNStudy("Policy deferral was not negotiated on this physical Session.");
    }

    internal sealed class NStudyCoordinationScope(RpcSession session, NStudyCoordinationMode mode,
        NStudyInitiator initiator, Func<NStudyChange, CancellationToken, ValueTask<SharpLinkVersionDecision>>? manualDecision) : IDisposable
    {
        internal NStudyCoordinationMode Mode => mode;
        internal NStudyInitiator Initiator => initiator;
        internal NStudyChange? AdmittedOffer { get; set; }
        internal bool EnrollmentClosed { get; private set; }

        internal bool MatchesAdmittedOffer(NStudyChange change)
        {
            lock (session._nStudyCoordinationGate)
                return AdmittedOffer == change;
        }

        internal bool CloseEnrollmentForAbort()
        {
            lock (session._nStudyCoordinationGate)
            {
                EnrollmentClosed = true;
                return AdmittedOffer.HasValue;
            }
        }

        // A live policy update never recategorizes a decision admitted under the old policy,
        // and never changes what this Session is able to send or read while settling it.
        internal ValueTask<SharpLinkVersionDecision> DecideAsync(NStudyChange change, ILogger logger,
            RpcDeadline controlDeadline, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return mode == NStudyCoordinationMode.Manual
                ? session.RuntimeContext.NStudyApprovals.DecideAsync(change, manualDecision!, logger,
                    session.RuntimeContext.TimeProvider, controlDeadline, ct)
                : ValueTask.FromResult(mode == NStudyCoordinationMode.Automatic
                    ? SharpLinkVersionDecision.Approve : SharpLinkVersionDecision.Reject);
        }

        public void Dispose()
        {
            lock (session._nStudyCoordinationGate)
            {
                lock (session._nStudyGate)
                {
                    session._nStudyCandidate = null;
                    session._nStudyCandidateSource = null;
                    if (session._nStudySourceFrozen)
                    {
                        session._nStudySourceFrozen = false;
                        session.SignalNStudyBindingControlSettled();
                    }
                }
                if (!session._nStudyKeepTargetBlock)
                    session._nStudyActivationNotSent = null;
                session._nStudyCoordinationScope = null;
            }
        }
    }
}
