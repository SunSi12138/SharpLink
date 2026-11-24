namespace SharpLink.Runtime;

internal sealed partial class RpcSession
{
    private static readonly TimeSpan NStudyEnrollmentWindow = TimeSpan.FromMilliseconds(500);
    private PendingNStudyOffer? _nStudyIncomingOffer;

    internal bool SupportsNStudyOfferAdmission => SupportsNStudyCoordination &&
        (NegotiatedCapabilities & NStudyWire.OfferAdmissionCapability) != 0;

    internal bool NStudyOfferPendingForDiagnostics
    {
        get { lock (_nStudyCoordinationGate) return _nStudyIncomingOffer is not null; }
    }

    private static bool UsesNStudyOfferAdmission(in NStudyCoordinationMessage message) =>
        message.Phase is NStudyPhase.Offer or NStudyPhase.Enrolled ||
        message.DeclineReason > NStudyDeclineReason.CandidateChanged;

    private Task ObserveNStudyOffer(NStudyChange change, Func<NStudyCoordinationMode> readLivePolicy)
    {
        PendingNStudyOffer pending;
        lock (_nStudyCoordinationGate)
        {
            if (_nStudyIncomingOffer is not null || _nStudyCoordinationScope?.AdmittedOffer is not null)
                throw InvalidNStudy("The physical Session already has an offer.");
            lock (_nStudyGate)
            {
                if (_nStudyAuthority != default && _nStudyAuthority != change.Authority)
                    throw InvalidNStudy("An incoming offer changed the physical Session authority.");
                // An authenticated Ready peer can offer before any business Binding.
                // This observes identity only; Candidate still needs the exact source route.
                _nStudyAuthority = change.Authority;
            }
            ObserveNStudyTransitionId(change.Id);
            _nStudyIncomingOffer = pending = new(change);
        }
        return SettleNStudyOfferAsync(pending, readLivePolicy);
    }

    private async Task SettleNStudyOfferAsync(PendingNStudyOffer pending, Func<NStudyCoordinationMode> readLivePolicy)
    {
        try
        {
            bool mayAwaitEnrollment;
            lock (_nStudyCoordinationGate)
                mayAwaitEnrollment = _nStudyCoordinationScope is null && readLivePolicy() != NStudyCoordinationMode.Disabled;

            // A server may start first. Leave the regular receive loop free for unrelated
            // RPCs while an application decides whether to enroll; no policy is invoked here.
            if (mayAwaitEnrollment)
            {
                try
                {
                    await pending.EnrollmentChanged.Task.WaitAsync(NStudyEnrollmentWindow,
                        RuntimeContext.TimeProvider, LifetimeToken).ConfigureAwait(false);
                }
                catch (TimeoutException) { }
            }

            NStudyDeclineReason reason;
            lock (_nStudyCoordinationGate)
            {
                if (_nStudyCoordinationScope is { EnrollmentClosed: false } scope &&
                    scope.Initiator == pending.Change.Initiator)
                {
                    scope.AdmittedOffer = pending.Change;
                    reason = NStudyDeclineReason.None;
                }
                else
                {
                    reason = readLivePolicy() switch
                    {
                        NStudyCoordinationMode.Disabled => NStudyDeclineReason.PeerDisabled,
                        NStudyCoordinationMode.Manual => NStudyDeclineReason.ApprovalRequired,
                        _ => NStudyDeclineReason.PeerNotEnrolled
                    };
                }
                _nStudyIncomingOffer = null;
            }

            using var timer = new CancellationTokenSource(NStudyTimeout, RuntimeContext.TimeProvider);
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(timer.Token, LifetimeToken);
            var phase = reason == NStudyDeclineReason.None ? NStudyPhase.Enrolled : NStudyPhase.Declined;
            await SendNStudyCoordinationAsync(new(phase, pending.Change, DeclineReason: reason), budget.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (LifetimeToken.IsCancellationRequested) { }
        catch (Exception error)
        {
            NotifyDisconnected(error);
            throw;
        }
        finally
        {
            lock (_nStudyCoordinationGate)
            {
                if (ReferenceEquals(_nStudyIncomingOffer, pending))
                    _nStudyIncomingOffer = null;
            }
        }
    }

    private sealed class PendingNStudyOffer(NStudyChange change)
    {
        internal NStudyChange Change { get; } = change;
        internal TaskCompletionSource EnrollmentChanged { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
