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
        if (method.Kind != RpcMethodKind.Unary || !method.IsIdempotent)
        {
            return InvokeUnaryCoreAsync(
                method,
                request, requestCodec, responseCodec, control, cancellationToken);
        }

        var generation = control.LogicalCall?.RetryGeneration ?? CaptureRetryGeneration();
        if (!generation.Enabled)
        {
            return InvokeUnaryCoreAsync(
                method,
                request, requestCodec, responseCodec, control, cancellationToken);
        }

        return InvokeUnaryWithRetryAsync(
            method, request, requestCodec, responseCodec, control, generation, cancellationToken);
    }

    private async ValueTask<TResponse> InvokeUnaryWithRetryAsync<TRequest, TResponse>(
        RpcMethodDescriptor method,
        TRequest request,
        IRpcCodec<TRequest> requestCodec,
        IRpcCodec<TResponse> responseCodec,
        ResolvedCallControl control,
        ClientRetryGeneration generation,
        CancellationToken cancellationToken)
    {
        var settings = generation.Settings;
        Exception? lastFailure = null;
        var selection = _cluster is null ? null : new EndpointRetrySelectionState();
        var requiresRetryOutcome = generation.Policy is not null;
        AttemptOutcomeState? outcome = null;
        for (var attempt = 1; attempt <= settings.MaxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureLogicalCallProgress(control);
            if (outcome is not null)
            {
                outcome.ResetForRetryAttempt();
            }
            else if (requiresRetryOutcome || Volatile.Read(ref _endpointAdmissionPolicy) is not null)
            {
                outcome = new AttemptOutcomeState(this, method);
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
                if (attempt == settings.MaxAttempts)
                    throw;

                SharpLinkRetryDecision decision;
                try
                {
                    decision = outcome is null
                        ? EvaluateDefaultRetryDecision(attempt, GetErrorCode(exception), settings)
                        : EvaluateRetryDecision(outcome.CreateRetryContext(attempt, exception), generation);
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

    private static SharpLinkRetryDecision EvaluateRetryDecision(
        in SharpLinkRetryContext context,
        ClientRetryGeneration generation)
    {
        if (generation.Policy is { } policy)
        {
            try
            {
                return policy.Evaluate(context);
            }
            catch (Exception exception)
            {
                throw new SharpLinkException(
                    SharpLinkErrorCode.FailedPrecondition,
                    "The retry policy failed.",
                    exception);
            }
        }

        return EvaluateDefaultRetryDecision(context.Attempt, context.ErrorCode, generation.Settings);
    }

    private static SharpLinkRetryDecision EvaluateDefaultRetryDecision(
        int attempt,
        SharpLinkErrorCode? errorCode,
        ClientRetrySettings settings)
    {
        var retryable = errorCode is SharpLinkErrorCode.Unavailable or SharpLinkErrorCode.ConnectionClosed;
        return retryable
            ? new SharpLinkRetryDecision(true, GetRetryDelay(attempt, settings))
            : default;
    }

    private static TimeSpan GetRetryDelay(int completedAttempt, ClientRetrySettings settings)
    {
        var ticks = settings.InitialBackoff.Ticks;
        for (var index = 1; index < completedAttempt && ticks < settings.MaxBackoff.Ticks; index++)
            ticks = Math.Min(ticks > long.MaxValue / 2 ? long.MaxValue : ticks * 2, settings.MaxBackoff.Ticks);
        if (ticks == 0 || settings.JitterRatio == 0)
            return TimeSpan.FromTicks(ticks);

        var multiplier = 1 - settings.JitterRatio + Random.Shared.NextDouble() * settings.JitterRatio * 2;
        var jitteredTicks = ticks * multiplier;
        var clampedTicks = jitteredTicks >= settings.MaxBackoff.Ticks
            ? settings.MaxBackoff.Ticks
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
