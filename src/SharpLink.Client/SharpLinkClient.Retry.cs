namespace SharpLink.Client;

internal sealed partial class SharpLinkClient
{
    private ValueTask<TResponse> InvokeUnaryWithOptionalRetryAsync<TRequest, TResponse>(
        RpcMethodDescriptor method,
        TRequest request,
        IRpcCodec<TRequest> requestCodec,
        IRpcCodec<TResponse> responseCodec,
        ResolvedCallControl control,
        CancellationToken cancellationToken)
    {
        var options = _retryOptions;
        if (options is null || method.Kind != RpcMethodKind.Unary || !method.IsIdempotent)
        {
            return InvokeUnaryCoreAsync(
                method,
                request, requestCodec, responseCodec, control, cancellationToken);
        }

        return InvokeUnaryWithRetryAsync(
            method, request, requestCodec, responseCodec, control, options, cancellationToken);
    }

    private async ValueTask<TResponse> InvokeUnaryWithRetryAsync<TRequest, TResponse>(
        RpcMethodDescriptor method,
        TRequest request,
        IRpcCodec<TRequest> requestCodec,
        IRpcCodec<TResponse> responseCodec,
        ResolvedCallControl control,
        SharpLinkRetryOptions options,
        CancellationToken cancellationToken)
    {
        Exception? lastFailure = null;
        var selection = new EndpointRetrySelectionState();
        for (var attempt = 1; attempt <= options.MaxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var outcome = new AttemptOutcomeState(this, method);
            var attemptScope = SharpLinkTelemetry.StartClientAttempt(method, attempt);
            try
            {
                var response = await InvokeUnaryRetryAttemptAsync(
                    method,
                    method.ContractId, method.MethodId, method.HasResponsePayload,
                    request, requestCodec, responseCodec, control, selection, outcome, cancellationToken).ConfigureAwait(false);
                attemptScope.Complete();
                return response;
            }
            catch (OperationCanceledException exception)
            {
                attemptScope.Complete(exception);
                throw;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                attemptScope.Complete(exception);
                lastFailure = exception;
                if (attempt == options.MaxAttempts)
                    throw;

                var attemptOutcome = outcome.CreateRetryOutcome(exception);
                var context = new SharpLinkRetryContext(
                    method,
                    attempt,
                    attemptOutcome.ErrorCode,
                    attemptOutcome.ResponseObserved,
                    attemptOutcome.Elapsed);
                var decision = EvaluateRetryDecision(context, options);
                if (!decision.ShouldRetry)
                    throw;
                SharpLinkTelemetry.RecordClientRetry();
                if (decision.Delay < TimeSpan.Zero)
                {
                    throw new SharpLinkException(
                        SharpLinkErrorCode.FailedPrecondition,
                        "The retry policy returned a negative delay.");
                }
                var delay = decision.Delay;
                if (outcome.ShouldHonorAdmissionRetryAfter &&
                    outcome.RetryAfter is { } admissionDelay && admissionDelay > delay)
                    delay = admissionDelay;
                if (delay == TimeSpan.Zero)
                    continue;

                if (control.Deadline is { } deadline && DateTimeOffset.UtcNow + delay >= deadline)
                    throw CreateDeadlineExceededException();
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }

        throw lastFailure ?? new SharpLinkException(SharpLinkErrorCode.Internal, "Retry exhausted without an attempt outcome.");
    }

    private SharpLinkRetryDecision EvaluateRetryDecision(
        in SharpLinkRetryContext context,
        SharpLinkRetryOptions options)
    {
        if (_retryPolicy is not null)
        {
            try
            {
                return _retryPolicy.Evaluate(context);
            }
            catch (Exception exception)
            {
                throw new SharpLinkException(
                    SharpLinkErrorCode.FailedPrecondition,
                    "The retry policy failed.",
                    exception);
            }
        }

        var retryable = context.ErrorCode is SharpLinkErrorCode.Unavailable or SharpLinkErrorCode.ConnectionClosed;
        return retryable
            ? new SharpLinkRetryDecision(true, GetRetryDelay(context.Attempt, options))
            : default;
    }

    private static TimeSpan GetRetryDelay(int completedAttempt, SharpLinkRetryOptions options)
    {
        var ticks = options.InitialBackoff.Ticks;
        for (var index = 1; index < completedAttempt && ticks < options.MaxBackoff.Ticks; index++)
            ticks = Math.Min(ticks > long.MaxValue / 2 ? long.MaxValue : ticks * 2, options.MaxBackoff.Ticks);
        if (ticks == 0 || options.JitterRatio == 0)
            return TimeSpan.FromTicks(ticks);

        var multiplier = 1 - options.JitterRatio + Random.Shared.NextDouble() * options.JitterRatio * 2;
        return TimeSpan.FromTicks(Math.Min(options.MaxBackoff.Ticks, (long)(ticks * multiplier)));
    }

    private ValueTask<TResponse> InvokeUnaryRetryAttemptAsync<TRequest, TResponse>(
        RpcMethodDescriptor method,
        long contractId,
        long methodId,
        bool hasResponsePayload,
        TRequest request,
        IRpcCodec<TRequest> requestCodec,
        IRpcCodec<TResponse> responseCodec,
        ResolvedCallControl control,
        EndpointRetrySelectionState selection,
        AttemptOutcomeState outcome,
        CancellationToken cancellationToken)
    {
        if (control.WaitForReady)
        {
            return InvokeUnaryRetryWaitForReadyAsync(
                contractId, methodId, hasResponsePayload, request, requestCodec, responseCodec,
                method, control, selection, outcome, cancellationToken);
        }

        try
        {
            var connection = GetReadyConnection(method, selection, outcome);
            outcome.SetConnection(connection);
            var operation = connection.PendingCalls.Rent(
                responseCodec,
                PendingCallKind.Unary,
                control.DeadlineTimestamp,
                cancellationToken,
                out var requestId,
                outcome);
            return StartUnaryCall(
                connection,
                contractId,
                methodId,
                requestId,
                hasResponsePayload,
                request,
                requestCodec,
                operation,
                control,
                cancellationToken);
        }
        catch (Exception exception)
        {
            outcome.CompleteLocalFailure(exception);
            return ValueTask.FromException<TResponse>(exception);
        }
    }

    private async ValueTask<TResponse> InvokeUnaryRetryWaitForReadyAsync<TRequest, TResponse>(
        long contractId,
        long methodId,
        bool hasResponsePayload,
        TRequest request,
        IRpcCodec<TRequest> requestCodec,
        IRpcCodec<TResponse> responseCodec,
        RpcMethodDescriptor method,
        ResolvedCallControl control,
        EndpointRetrySelectionState selection,
        AttemptOutcomeState outcome,
        CancellationToken cancellationToken)
    {
        try
        {
            var connection = await GetReadyConnectionForRetryAsync(
                method, selection, outcome, control.Deadline, cancellationToken).ConfigureAwait(false);
            outcome.SetConnection(connection);
            var lease = await connection.PendingCalls.RentAsync(
                responseCodec,
                PendingCallKind.Unary,
                control.DeadlineTimestamp,
                waitForSlot: true,
                control.Deadline,
                cancellationToken,
                outcome).ConfigureAwait(false);
            return await StartUnaryCall(
                connection,
                contractId,
                methodId,
                lease.Id,
                hasResponsePayload,
                request,
                requestCodec,
                lease.Operation,
                control,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            outcome.CompleteLocalFailure(exception);
            throw;
        }
    }

    private async ValueTask<ClientConnection> GetReadyConnectionForRetryAsync(
        RpcMethodDescriptor method,
        EndpointRetrySelectionState selection,
        AttemptOutcomeState outcome,
        DateTimeOffset? deadline,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            try
            {
                return GetReadyConnection(method, selection, outcome);
            }
            catch (SharpLinkException exception) when (exception.Code == SharpLinkErrorCode.Unavailable)
            {
                if (State == SharpLinkConnectionState.Stopped || _shutdownCts.IsCancellationRequested)
                    throw CreateConnectionClosedException("Client has stopped.");

                if (outcome.HasAdmissionRejection)
                {
                    if (outcome.RetryAfter is not { } retryAfter)
                        throw;
                    var delay = retryAfter > TimeSpan.Zero
                        ? retryAfter
                        : TimeSpan.FromMilliseconds(1);
                    if (deadline is { } retryDeadline && DateTimeOffset.UtcNow + delay >= retryDeadline)
                        throw CreateDeadlineExceededException();
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var signal = Volatile.Read(ref _readySignal).Task;
                if (deadline is not { } absoluteDeadline)
                {
                    await signal.WaitAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var remaining = absoluteDeadline - DateTimeOffset.UtcNow;
                if (remaining <= TimeSpan.Zero)
                    throw CreateDeadlineExceededException();
                try
                {
                    await signal.WaitAsync(remaining, cancellationToken).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    throw CreateDeadlineExceededException();
                }
            }
        }
    }

}
