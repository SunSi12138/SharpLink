namespace SharpLink.Runtime;

internal sealed partial class RpcSession
{
    private readonly CompressionSendPolicyState _compressionSendPolicyState;
    private ResponseCompressionPreferenceSnapshot _appliedResponseCompressionPreference =
        ResponseCompressionPreferenceSnapshot.InitialAllowed;

    private readonly Lock _responseCompressionPreferenceControlGate = new();
    private ResponseCompressionPreferenceSnapshot? _latestResponseCompressionPreference;
    private ulong _remoteResponseCompressionAppliedGeneration;
    private ulong _responseCompressionPreferenceInFlightGeneration;
    private ulong _responseCompressionPreferenceMaximumSentGeneration;
    private Exception? _responseCompressionPreferenceControlFailure;
    private TaskCompletionSource _responseCompressionPreferenceProgress = CreateResponseCompressionPreferenceProgress();

    internal bool HasNegotiatedCompression
        => (NegotiatedCapabilities & ProtocolV2Capabilities.Compression) != 0 &&
           Volatile.Read(ref _protocolState).Options?.CompressionBinding is not null;

    internal void InitializeServerResponseCompressionPreference(
        ulong generation,
        bool allowResponseCompression)
    {
        if (Role != RpcSessionRole.Server)
            throw new InvalidOperationException("Only a server session can publish a client response-compression preference.");
        Volatile.Write(
            ref _appliedResponseCompressionPreference,
            new ResponseCompressionPreferenceSnapshot(generation, allowResponseCompression));
    }

    internal ulong ApplyServerResponseCompressionPreferenceUpdate(
        in ProtocolV2ResponseCompressionPreferenceUpdate update)
    {
        if (Role != RpcSessionRole.Server)
            throw ProtocolV2FrameParser.Violation("A response-compression preference update is valid only at the server.");
        if (!HasNegotiatedCompression)
        {
            throw new SharpLinkProtocolViolationException(
                ProtocolViolationReason.ProtocolState,
                "A response-compression preference update requires negotiated compression.");
        }

        var current = Volatile.Read(ref _appliedResponseCompressionPreference);
        if (update.Generation < current.Generation)
            return current.Generation;
        if (update.Generation == current.Generation)
        {
            if (update.AllowResponseCompression != current.Allowed)
            {
                throw ProtocolV2FrameParser.Violation(
                    "A response-compression preference generation cannot identify two different desired states.");
            }
            return current.Generation;
        }

        var candidate = new ResponseCompressionPreferenceSnapshot(
            update.Generation,
            update.AllowResponseCompression);
        Volatile.Write(ref _appliedResponseCompressionPreference, candidate);
        return candidate.Generation;
    }

    internal void InitializeClientResponseCompressionPreference(
        ResponseCompressionPreferenceSnapshot handshakePreference)
    {
        ArgumentNullException.ThrowIfNull(handshakePreference);
        if (Role != RpcSessionRole.Client)
            throw new InvalidOperationException("Only a client session tracks server response-compression convergence.");
        lock (_responseCompressionPreferenceControlGate)
        {
            _remoteResponseCompressionAppliedGeneration = handshakePreference.Generation;
            _responseCompressionPreferenceMaximumSentGeneration = handshakePreference.Generation;
            _latestResponseCompressionPreference = handshakePreference;
            _responseCompressionPreferenceControlFailure = null;
        }
    }

    internal void ReconcileResponseCompressionPreference(ResponseCompressionPreferenceSnapshot desired)
    {
        ArgumentNullException.ThrowIfNull(desired);
        if (Role != RpcSessionRole.Client)
            throw new InvalidOperationException("Only a client session can reconcile a response-compression preference.");
        if (!HasNegotiatedCompression || !IsConnected)
            return;

        ResponseCompressionPreferenceSnapshot? toSend = null;
        lock (_responseCompressionPreferenceControlGate)
        {
            if (!IsConnected || desired.Generation <= _remoteResponseCompressionAppliedGeneration)
                return;

            if (_latestResponseCompressionPreference is null ||
                desired.Generation > _latestResponseCompressionPreference.Generation)
            {
                _latestResponseCompressionPreference = desired;
            }
            else if (desired.Generation == _latestResponseCompressionPreference.Generation &&
                     desired.Allowed != _latestResponseCompressionPreference.Allowed)
            {
                throw new InvalidOperationException(
                    "A client response-compression preference generation cannot identify two desired states.");
            }

            _responseCompressionPreferenceControlFailure = null;
            if (_responseCompressionPreferenceInFlightGeneration == 0 &&
                _latestResponseCompressionPreference.Generation > _remoteResponseCompressionAppliedGeneration)
            {
                toSend = _latestResponseCompressionPreference;
                _responseCompressionPreferenceInFlightGeneration = toSend.Generation;
                _responseCompressionPreferenceMaximumSentGeneration = Math.Max(
                    _responseCompressionPreferenceMaximumSentGeneration,
                    toSend.Generation);
            }
        }

        if (toSend is not null)
            SendResponseCompressionPreferenceUpdateCore(toSend);
    }

    internal void ApplyResponseCompressionPreferenceAck(ulong appliedGeneration)
    {
        if (Role != RpcSessionRole.Client)
            throw ProtocolV2FrameParser.Violation("A response-compression preference ACK is valid only at the client.");
        if (!HasNegotiatedCompression)
        {
            throw new SharpLinkProtocolViolationException(
                ProtocolViolationReason.ProtocolState,
                "A response-compression preference ACK requires negotiated compression.");
        }

        ResponseCompressionPreferenceSnapshot? toSend = null;
        TaskCompletionSource? progress = null;
        lock (_responseCompressionPreferenceControlGate)
        {
            if (appliedGeneration > _responseCompressionPreferenceMaximumSentGeneration)
            {
                throw ProtocolV2FrameParser.Violation(
                    "A response-compression preference ACK exceeded every generation sent on this session.");
            }

            var changed = false;
            if (appliedGeneration > _remoteResponseCompressionAppliedGeneration)
            {
                _remoteResponseCompressionAppliedGeneration = appliedGeneration;
                _responseCompressionPreferenceControlFailure = null;
                changed = true;
            }
            if (_responseCompressionPreferenceInFlightGeneration != 0 &&
                appliedGeneration >= _responseCompressionPreferenceInFlightGeneration)
            {
                _responseCompressionPreferenceInFlightGeneration = 0;
                changed = true;
            }

            if (_latestResponseCompressionPreference is not null &&
                _latestResponseCompressionPreference.Generation > _remoteResponseCompressionAppliedGeneration &&
                _responseCompressionPreferenceInFlightGeneration == 0)
            {
                toSend = _latestResponseCompressionPreference;
                _responseCompressionPreferenceInFlightGeneration = toSend.Generation;
                _responseCompressionPreferenceMaximumSentGeneration = Math.Max(
                    _responseCompressionPreferenceMaximumSentGeneration,
                    toSend.Generation);
            }

            if (changed)
            {
                progress = _responseCompressionPreferenceProgress;
                _responseCompressionPreferenceProgress = CreateResponseCompressionPreferenceProgress();
            }
        }

        progress?.TrySetResult();
        if (toSend is null)
            return;
        try
        {
            SendResponseCompressionPreferenceUpdateCore(toSend);
        }
        catch (SharpLinkException exception) when (
            exception.Code is SharpLinkErrorCode.ResourceExhausted or SharpLinkErrorCode.ConnectionClosed)
        {
            // The explicit convergence waiter observes the stored failure, or the session-close
            // path removes this session from its fixed cohort. Do not turn control congestion into
            // an unrelated receive-loop protocol failure.
        }
        catch (ObjectDisposedException)
        {
        }
    }

    internal async ValueTask WaitForResponseCompressionPreferenceAsync(
        ulong requestedGeneration,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Task progress;
            Exception? failure;
            lock (_responseCompressionPreferenceControlGate)
            {
                if (_remoteResponseCompressionAppliedGeneration >= requestedGeneration || !IsConnected)
                    return;
                failure = _responseCompressionPreferenceControlFailure;
                progress = _responseCompressionPreferenceProgress.Task;
            }

            if (failure is not null)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            if (!IsConnected)
                return;

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                LifetimeToken);
            try
            {
                await progress.WaitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (
                LifetimeToken.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private void SendResponseCompressionPreferenceUpdateCore(ResponseCompressionPreferenceSnapshot desired)
    {
        try
        {
            this.SendResponseCompressionPreferenceUpdate(
                new ProtocolV2ResponseCompressionPreferenceUpdate(
                    desired.Generation,
                    desired.Allowed));
        }
        catch (Exception exception)
        {
            TaskCompletionSource progress;
            lock (_responseCompressionPreferenceControlGate)
            {
                if (_responseCompressionPreferenceInFlightGeneration == desired.Generation)
                    _responseCompressionPreferenceInFlightGeneration = 0;
                _responseCompressionPreferenceControlFailure = exception;
                progress = _responseCompressionPreferenceProgress;
                _responseCompressionPreferenceProgress = CreateResponseCompressionPreferenceProgress();
            }
            progress.TrySetResult();
            throw;
        }
    }

    private static TaskCompletionSource CreateResponseCompressionPreferenceProgress()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
