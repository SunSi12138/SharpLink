namespace SharpLink.Client;

internal sealed partial class SharpLinkClient
{
    private ValueTask<TResponse> InvokeGeneratedUnaryWithOptionalRetryAsync<TRequest, TResponse, TRequestCodec, TResponseCodec>(
        RpcMethodDescriptor method,
        TRequest request,
        TRequestCodec requestCodec,
        TResponseCodec responseCodec,
        ResolvedCallControl control,
        CancellationToken cancellationToken)
        where TRequestCodec : IRpcCodec<TRequest>
        where TResponseCodec : IRpcCodec<TResponse>
    {
        if (method.Kind != RpcMethodKind.Unary || !method.IsIdempotent)
        {
            return InvokeGeneratedUnaryCoreAsync(
                method, request, requestCodec, responseCodec, control, cancellationToken);
        }

        var generation = control.RetryGeneration ?? CaptureRetryGeneration();
        if (!generation.Enabled)
        {
            return InvokeGeneratedUnaryCoreAsync(
                method, request, requestCodec, responseCodec, control, cancellationToken);
        }

        return InvokeGeneratedUnaryWithRetryAsync(
            method, request, requestCodec, responseCodec, control, generation, cancellationToken);
    }

    private async ValueTask<TResponse> InvokeGeneratedUnaryWithRetryAsync<TRequest, TResponse, TRequestCodec, TResponseCodec>(
        RpcMethodDescriptor method,
        TRequest request,
        TRequestCodec requestCodec,
        TResponseCodec responseCodec,
        ResolvedCallControl control,
        ClientRetryGeneration generation,
        CancellationToken cancellationToken)
        where TRequestCodec : IRpcCodec<TRequest>
        where TResponseCodec : IRpcCodec<TResponse>
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
            else if (requiresRetryOutcome || _endpointAdmissionPolicy is not null)
            {
                outcome = new AttemptOutcomeState(this, method);
            }
            else
            {
                SharpLinkTelemetry.RecordClientAttempt();
            }

            var attemptScope = StartClientAttemptTelemetry(control, method, attempt);
            try
            {
                var response = await InvokeGeneratedUnaryRetryAttemptAsync(
                    method,
                    method.ContractId,
                    method.MethodId,
                    method.HasResponsePayload,
                    request,
                    requestCodec,
                    responseCodec,
                    control,
                    selection,
                    outcome,
                    cancellationToken).ConfigureAwait(false);
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

        throw lastFailure ?? new SharpLinkException(
            SharpLinkErrorCode.Internal,
            "Retry exhausted without an attempt result.");
    }

    private ValueTask<TResponse> InvokeGeneratedUnaryRetryAttemptAsync<TRequest, TResponse, TRequestCodec, TResponseCodec>(
        RpcMethodDescriptor method,
        long contractId,
        long methodId,
        bool hasResponsePayload,
        TRequest request,
        TRequestCodec requestCodec,
        TResponseCodec responseCodec,
        ResolvedCallControl control,
        EndpointRetrySelectionState? selection,
        AttemptOutcomeState? outcome,
        CancellationToken cancellationToken)
        where TRequestCodec : IRpcCodec<TRequest>
        where TResponseCodec : IRpcCodec<TResponse>
    {
        ClientConnection? connection = null;
        try
        {
            EnsureLogicalCallProgress(control);
            connection = GetReadyConnection(method, selection, outcome);
            try
            {
                EnsureLogicalCallProgress(control);
            }
            catch
            {
                connection.ReleaseCallAdmissionReservation();
                throw;
            }

            RpcRequestOperation<TResponse, TResponseCodec> operation;
            long requestId;
            try
            {
                operation = connection.PendingCalls.RentGenerated(
                    in responseCodec,
                    PendingCallKind.Unary,
                    control.Deadline,
                    cancellationToken,
                    out requestId,
                    outcome,
                    hasResponsePayload: hasResponsePayload,
                    responseNullable: method.ResponseNullable);
            }
            catch
            {
                connection.ReleaseCallAdmissionReservation();
                throw;
            }

            return StartGeneratedUnaryCall(
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
