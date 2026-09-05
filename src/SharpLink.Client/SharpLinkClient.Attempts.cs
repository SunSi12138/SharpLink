namespace SharpLink.Client;

internal sealed partial class SharpLinkClient
{
    /// <summary>
    /// Owns the retry metadata and optional endpoint-admission lease for one endpoint-bound attempt.
    /// PendingCall remains the exactly-once terminal owner; this observer only projects that terminal
    /// result into retry/admission reporting without maintaining a second completion state machine.
    /// </summary>
    private sealed class AttemptOutcomeState : IPendingCallCompletionObserver
    {
        private readonly SharpLinkClient _client;
        private readonly RpcMethodDescriptor _method;
        private readonly long _attemptStarted;
        private long _endpointStarted;
        private int _responseObserved;
        private SharpLinkEndpointCandidate _admissionEndpoint;
        private long _admissionToken;
        private int _hasAdmissionLease;
        private TimeSpan? _retryAfter;

        public AttemptOutcomeState(SharpLinkClient client, RpcMethodDescriptor method)
        {
            _client = client;
            _method = method;
            _attemptStarted = _client._runtimeContext.TimeProvider.GetTimestamp();
            SharpLinkTelemetry.RecordClientAttempt();
        }

        public TimeSpan? RetryAfter => _retryAfter;

        public bool ShouldHonorAdmissionRetryAfter => _retryAfter is not null;

        public bool TryAcquire(in SharpLinkEndpointCandidate endpoint)
        {
            var policy = _client._endpointAdmissionPolicy;
            if (policy is null)
                return true;

            SharpLinkEndpointAdmissionDecision decision;
            try
            {
                decision = policy.TryAcquire(endpoint, _method);
            }
            catch (Exception exception)
            {
                throw new SharpLinkException(
                    SharpLinkErrorCode.FailedPrecondition,
                    "The endpoint admission policy failed while acquiring an attempt.",
                    exception);
            }

            if (decision.RetryAfter is { } retryAfter && retryAfter < TimeSpan.Zero)
            {
                throw new SharpLinkException(
                    SharpLinkErrorCode.FailedPrecondition,
                    "The endpoint admission policy returned a negative retry delay.");
            }
            if (!decision.IsAllowed)
            {
                if (decision.RetryAfter is { } delay && (_retryAfter is null || delay < _retryAfter.Value))
                    _retryAfter = delay;
                if (policy is not SharpLinkCircuitBreaker)
                    SharpLinkTelemetry.RecordEndpointAdmissionRejected("policy");
                return false;
            }

            _admissionEndpoint = endpoint;
            _admissionToken = decision.Token;
            _retryAfter = null;
            Volatile.Write(ref _responseObserved, 0);
            Volatile.Write(
                ref _endpointStarted,
                _client._runtimeContext.TimeProvider.GetTimestamp());
            Volatile.Write(ref _hasAdmissionLease, 1);
            return true;
        }

        public void CompleteWithoutPending(PendingCallCompletionReason reason, Exception? exception = null)
        {
            if (reason is PendingCallCompletionReason.Response or PendingCallCompletionReason.RemoteError)
                Volatile.Write(ref _responseObserved, 1);
            Report(reason, exception);
        }

        public void CompleteLocalFailure(Exception exception)
        {
            var reason = exception switch
            {
                OperationCanceledException => PendingCallCompletionReason.UserCancellation,
                SharpLinkException { Code: SharpLinkErrorCode.DeadlineExceeded } => PendingCallCompletionReason.DeadlineExceeded,
                SharpLinkException { Code: SharpLinkErrorCode.ConnectionClosed } => PendingCallCompletionReason.ConnectionClosed,
                _ => PendingCallCompletionReason.SendFailure
            };
            CompleteWithoutPending(reason, exception);
        }

        public void OnResponseObserved() => Volatile.Write(ref _responseObserved, 1);

        public void OnPendingCallCompleted(in PendingCallCompletion completion)
        {
            if (completion.Reason is PendingCallCompletionReason.Response or PendingCallCompletionReason.RemoteError)
                Volatile.Write(ref _responseObserved, 1);
            Report(completion.Reason, completion.Exception);
        }

        public SharpLinkRetryContext CreateRetryContext(int attempt, Exception exception)
            => new(
                _method,
                attempt,
                GetErrorCode(exception),
                Volatile.Read(ref _responseObserved) != 0,
                _client._runtimeContext.TimeProvider.GetElapsedTime(_attemptStarted));

        private void Report(PendingCallCompletionReason reason, Exception? exception)
        {
            if (Interlocked.Exchange(ref _hasAdmissionLease, 0) == 0)
                return;

            var policy = _client._endpointAdmissionPolicy;
            if (policy is null)
                return;
            var outcome = new SharpLinkEndpointOutcome(
                _admissionEndpoint,
                _method,
                ToOutcomeKind(reason, exception),
                exception is null ? null : GetErrorCode(exception),
                Volatile.Read(ref _responseObserved) != 0,
                _client._runtimeContext.TimeProvider.GetElapsedTime(
                    Volatile.Read(ref _endpointStarted)));
            try
            {
                policy.Report(outcome, _admissionToken);
            }
            catch (Exception reportException)
            {
                _client._logger.LogError(reportException, "SharpLink endpoint admission policy report failed.");
            }
        }
    }

    private static SharpLinkEndpointOutcomeKind ToOutcomeKind(
        PendingCallCompletionReason reason,
        Exception? exception)
        => reason switch
        {
            PendingCallCompletionReason.Response or PendingCallCompletionReason.LocalStreamComplete => SharpLinkEndpointOutcomeKind.Success,
            PendingCallCompletionReason.RemoteError => SharpLinkEndpointOutcomeKind.RemoteError,
            PendingCallCompletionReason.RemoteStreamComplete when exception is null => SharpLinkEndpointOutcomeKind.Success,
            PendingCallCompletionReason.RemoteStreamComplete => SharpLinkEndpointOutcomeKind.RemoteError,
            PendingCallCompletionReason.SendFailure => SharpLinkEndpointOutcomeKind.SendFailure,
            PendingCallCompletionReason.ConnectionClosed => SharpLinkEndpointOutcomeKind.ConnectionClosed,
            PendingCallCompletionReason.GoAway => SharpLinkEndpointOutcomeKind.GoAway,
            PendingCallCompletionReason.DeadlineExceeded => SharpLinkEndpointOutcomeKind.DeadlineExceeded,
            _ => SharpLinkEndpointOutcomeKind.Cancelled
        };

    private static SharpLinkErrorCode? GetErrorCode(Exception exception)
        => exception switch
        {
            SharpLinkException sharpLinkException => sharpLinkException.Code,
            IOException or ObjectDisposedException => SharpLinkErrorCode.ConnectionClosed,
            _ => null
        };
}
