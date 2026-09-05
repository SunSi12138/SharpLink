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
        var selection = _cluster is null ? null : new EndpointRetrySelectionState();
        var requiresAttemptOutcome = _endpointAdmissionPolicy is not null || _retryPolicy is not null;
        AttemptOutcomeState? outcome = null;
        for (var attempt = 1; attempt <= options.MaxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureLogicalCallProgress(control);
            if (requiresAttemptOutcome)
            {
                if (outcome is null)
                    outcome = new AttemptOutcomeState(this, method);
                else
                    outcome.ResetForRetryAttempt();
            }
            else
            {
                SharpLinkTelemetry.RecordClientAttempt();
            }
            var attemptScope = SharpLinkTelemetry.StartClientAttempt(method, attempt);
            try
            {
                var response = await InvokeUnaryRetryAttemptAsync(
                    method,
                    method.ContractId, method.MethodId, method.HasResponsePayload,
                    request, requestCodec, responseCodec, control, selection, outcome, cancellationToken).ConfigureAwait(false);
                EnsureLogicalCallProgress(control);
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
                if (exception is SharpLinkException { Code: SharpLinkErrorCode.DeadlineExceeded })
                    _ = control.LogicalCall?.TryClaimDeadline();
                EnsureLogicalCallProgress(control);

                lastFailure = exception;
                if (attempt == options.MaxAttempts)
                    throw;

                SharpLinkRetryDecision decision;
                try
                {
                    decision = outcome is null
                        ? EvaluateDefaultRetryDecision(attempt, GetErrorCode(exception), options)
                        : EvaluateRetryDecision(outcome.CreateRetryContext(attempt, exception), options);
                }
                catch
                {
                    EnsureLogicalCallProgress(control);
                    throw;
                }
                EnsureLogicalCallProgress(control);
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
                if (outcome?.RetryAfter is { } admissionDelay && admissionDelay > delay)
                    delay = admissionDelay;
                if (delay == TimeSpan.Zero)
                {
                    EnsureLogicalCallProgress(control);
                    continue;
                }

                await DelayForRetryOrAdmissionAsync(
                    delay, control.Deadline, cancellationToken).ConfigureAwait(false);
                EnsureLogicalCallProgress(control);
            }
        }

        throw lastFailure ?? new SharpLinkException(SharpLinkErrorCode.Internal, "Retry exhausted without an attempt result.");
    }

    internal static void EnsureLogicalCallProgress(in ResolvedCallControl control)
    {
        if (control.LogicalCall is { } logicalCall && !logicalCall.TryEnterProgress())
            throw CreateDeadlineExceededException();
    }

    internal static Exception ArbitrateLogicalCallFailure(
        in ResolvedCallControl control,
        Exception exception)
    {
        if (exception is SharpLinkException { Code: SharpLinkErrorCode.DeadlineExceeded })
            _ = control.LogicalCall?.TryClaimDeadline();
        if (control.LogicalCall is { } logicalCall && !logicalCall.TryEnterProgress())
            return CreateDeadlineExceededException();
        return exception;
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

        return EvaluateDefaultRetryDecision(context.Attempt, context.ErrorCode, options);
    }

    private static SharpLinkRetryDecision EvaluateDefaultRetryDecision(
        int attempt,
        SharpLinkErrorCode? errorCode,
        SharpLinkRetryOptions options)
    {
        var retryable = errorCode is SharpLinkErrorCode.Unavailable or SharpLinkErrorCode.ConnectionClosed;
        return retryable
            ? new SharpLinkRetryDecision(true, GetRetryDelay(attempt, options))
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
        var jitteredTicks = ticks * multiplier;
        var clampedTicks = jitteredTicks >= options.MaxBackoff.Ticks
            ? options.MaxBackoff.Ticks
            : (long)jitteredTicks;
        return TimeSpan.FromTicks(clampedTicks);
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
        EndpointRetrySelectionState? selection,
        AttemptOutcomeState? outcome,
        CancellationToken cancellationToken)
    {
        try
        {
            EnsureLogicalCallProgress(control);
            var connection = GetReadyConnection(method, selection, outcome);
            EnsureLogicalCallProgress(control);
            var operation = connection.PendingCalls.Rent(
                responseCodec,
                PendingCallKind.Unary,
                control.Deadline,
                cancellationToken,
                out var requestId,
                outcome,
                hasResponsePayload: hasResponsePayload,
                responseNullable: method.ResponseNullable);
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
            exception = ArbitrateLogicalCallFailure(control, exception);
            outcome?.CompleteLocalFailure(exception);
            return ValueTask.FromException<TResponse>(exception);
        }
    }

}
