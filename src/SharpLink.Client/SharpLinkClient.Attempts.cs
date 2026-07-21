namespace SharpLink.Client;

internal sealed partial class SharpLinkClient
{
    /// <summary>
    /// Owns the outcome of one endpoint-bound attempt. When admission is enabled the same state is
    /// registered as the PendingCall completion observer, preserving the existing one-winner race.
    /// </summary>
    private sealed class AttemptOutcomeState : IPendingCallCompletionObserver
    {
        private readonly SharpLinkClient _client;
        private readonly RpcMethodDescriptor _method;
        private readonly long _attemptStarted;
        private long _endpointStarted;
        private PendingCallCompletionReason? _completionReason;
        private int _responseObserved;
        private SharpLinkErrorCode? _localErrorCode;
        private SharpLinkEndpointCandidate _admissionEndpoint;
        private long _admissionToken;
        private int _hasAdmissionLease;
        private int _admissionGranted;
        private int _reported;
        private int _admissionLeaseVersion;
        private int _completionLeaseVersion;
        private int _admissionRejected;
        private TimeSpan? _retryAfter;

        public AttemptOutcomeState(SharpLinkClient client, RpcMethodDescriptor method)
        {
            _client = client;
            _method = method;
            _attemptStarted = Stopwatch.GetTimestamp();
            SharpLinkTelemetry.RecordClientAttempt();
        }

        public string? EndpointId { get; private set; }
        public long EndpointGeneration { get; private set; }
        public string? ConnectionId { get; private set; }

        public TimeSpan? RetryAfter => _retryAfter;

        public bool HasAdmissionRejection => Volatile.Read(ref _admissionRejected) != 0;

        public bool HasAdmissionGrant => Volatile.Read(ref _admissionGranted) != 0;

        public bool HasCompletion => Volatile.Read(ref _admissionLeaseVersion) != 0 &&
                                     Volatile.Read(ref _completionLeaseVersion) == Volatile.Read(ref _admissionLeaseVersion);

        public bool ShouldHonorAdmissionRetryAfter
            => HasAdmissionRejection && Volatile.Read(ref _admissionGranted) == 0;

        public void BeginAdmissionSelection()
        {
            Volatile.Write(ref _admissionRejected, 0);
            Volatile.Write(ref _admissionGranted, 0);
            _retryAfter = null;
        }

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
                Volatile.Write(ref _admissionRejected, 1);
                if (decision.RetryAfter is { } delay && (_retryAfter is null || delay < _retryAfter.Value))
                    _retryAfter = delay;
                if (policy is not SharpLinkCircuitBreaker)
                    SharpLinkTelemetry.RecordEndpointAdmissionRejected("policy");
                return false;
            }

            _admissionEndpoint = endpoint;
            _admissionToken = decision.Token;
            Interlocked.Increment(ref _admissionLeaseVersion);
            Volatile.Write(ref _completionLeaseVersion, 0);
            _completionReason = null;
            _localErrorCode = null;
            Volatile.Write(ref _responseObserved, 0);
            Volatile.Write(ref _endpointStarted, Stopwatch.GetTimestamp());
            Volatile.Write(ref _admissionGranted, 1);
            Volatile.Write(ref _hasAdmissionLease, 1);
            Volatile.Write(ref _reported, 0);
            return true;
        }

        public void SetConnection(ClientConnection connection)
        {
            EndpointId = connection.EndpointId;
            EndpointGeneration = connection.EndpointGeneration;
            ConnectionId = connection.Session.Id;
        }

        public void SetLocalFailure(Exception exception)
            => _localErrorCode = GetErrorCode(exception);

        public void CompleteWithoutPending(PendingCallCompletionReason reason, Exception? exception = null)
        {
            SetLocalFailureIfPresent(exception);
            _completionReason = reason;
            Volatile.Write(ref _completionLeaseVersion, Volatile.Read(ref _admissionLeaseVersion));
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
            _completionReason = completion.Reason;
            Volatile.Write(ref _completionLeaseVersion, Volatile.Read(ref _admissionLeaseVersion));
            if (completion.Reason is PendingCallCompletionReason.Response or PendingCallCompletionReason.RemoteError)
                Volatile.Write(ref _responseObserved, 1);
            SetLocalFailureIfPresent(completion.Exception);
            Report(completion.Reason, completion.Exception);
        }

        public RetryAttemptOutcome CreateRetryOutcome(Exception exception)
            => new(
                EndpointId,
                EndpointGeneration,
                ConnectionId,
                _completionReason,
                Volatile.Read(ref _responseObserved) != 0,
                _localErrorCode ?? GetErrorCode(exception),
                Stopwatch.GetElapsedTime(_attemptStarted));

        private void Report(PendingCallCompletionReason reason, Exception? exception)
        {
            if (Volatile.Read(ref _hasAdmissionLease) == 0 || Interlocked.Exchange(ref _reported, 1) != 0)
                return;

            var policy = _client._endpointAdmissionPolicy;
            if (policy is null)
                return;
            var outcome = new SharpLinkEndpointOutcome(
                _admissionEndpoint,
                _method,
                ToOutcomeKind(reason, exception),
                _localErrorCode ?? (exception is null ? null : GetErrorCode(exception)),
                Volatile.Read(ref _responseObserved) != 0,
                Stopwatch.GetElapsedTime(Volatile.Read(ref _endpointStarted)));
            try
            {
                policy.Report(outcome, _admissionToken);
            }
            catch (Exception reportException)
            {
                _client._logger.LogError(reportException, "SharpLink endpoint admission policy report failed.");
            }
            finally
            {
                Volatile.Write(ref _hasAdmissionLease, 0);
            }
        }

        private void SetLocalFailureIfPresent(Exception? exception)
        {
            if (exception is not null)
                SetLocalFailure(exception);
        }
    }

    private readonly record struct RetryAttemptOutcome(
        string? EndpointId,
        long EndpointGeneration,
        string? ConnectionId,
        PendingCallCompletionReason? CompletionReason,
        bool ResponseObserved,
        SharpLinkErrorCode? ErrorCode,
        TimeSpan Elapsed);

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
