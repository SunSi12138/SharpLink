namespace SharpLink.Client;

internal sealed partial class SharpLinkClient
{
    public ValueTask<TResponse> InvokeGeneratedUnaryAsync<TRequest, TResponse, TRequestCodec, TResponseCodec>(
        RpcMethodDescriptor method,
        in TRequest request,
        in TRequestCodec requestCodec,
        in TResponseCodec responseCodec,
        SharpLinkMetadata? metadata,
        CancellationToken cancellationToken = default)
        where TRequestCodec : IRpcCodec<TRequest>
        where TResponseCodec : IRpcCodec<TResponse>
    {
        ArgumentNullException.ThrowIfNull(requestCodec);
        ArgumentNullException.ThrowIfNull(responseCodec);
        cancellationToken.ThrowIfCancellationRequested();
        var interceptors = CaptureInterceptorGenerationForInvocation();
        var control = ResolveCallControlForInvocation(
            method, metadata, includeClientDefault: true, interceptors);
        Interlocked.Increment(ref _activeLogicalInvocations);
        try
        {
            ValueTask<TResponse> invocation;
            if (SharpLinkTelemetry.ClientCallsEnabled)
            {
                invocation = InvokeGeneratedUnaryWithTelemetryAsync<TRequest, TResponse, TRequestCodec, TResponseCodec>(
                    method, request, requestCodec, responseCodec, interceptors, control, cancellationToken);
            }
            else if (interceptors.Count != 0)
            {
                invocation = InvokeGeneratedUnaryInterceptedAsync<TRequest, TResponse, TRequestCodec, TResponseCodec>(
                    method, request, requestCodec, responseCodec, interceptors, control, cancellationToken);
            }
            else if (control.RetryGeneration is { Enabled: true })
            {
                invocation = InvokeGeneratedUnaryWithOptionalRetryAsync<TRequest, TResponse, TRequestCodec, TResponseCodec>(
                    method, request, requestCodec, responseCodec, control, cancellationToken);
            }
            else
            {
                invocation = InvokeGeneratedUnaryCoreAsync<TRequest, TResponse, TRequestCodec, TResponseCodec>(
                    method, request, requestCodec, responseCodec, control, cancellationToken);
            }

            return CompleteLogicalInvocation(invocation);
        }
        catch
        {
            Interlocked.Decrement(ref _activeLogicalInvocations);
            throw;
        }
    }

    public ValueTask InvokeGeneratedOneWayAsync<TRequest, TRequestCodec, TStreams>(
        RpcMethodDescriptor method,
        in TRequest request,
        in TRequestCodec requestCodec,
        in TStreams streams,
        SharpLinkMetadata? metadata,
        CancellationToken cancellationToken = default)
        where TRequestCodec : IRpcCodec<TRequest>
        where TStreams : struct, IRpcClientStreamWriter
    {
        ArgumentNullException.ThrowIfNull(requestCodec);
        cancellationToken.ThrowIfCancellationRequested();
        var interceptors = CaptureInterceptorGenerationForInvocation();
        var control = ResolveCallControlForInvocation(
            method, metadata, includeClientDefault: false, interceptors);
        Interlocked.Increment(ref _activeLogicalInvocations);
        try
        {
            ValueTask invocation;
            if (SharpLinkTelemetry.ClientCallsEnabled)
            {
                invocation = InvokeGeneratedOneWayWithTelemetryAsync(
                    method, request, requestCodec, streams, interceptors, control, cancellationToken);
            }
            else if (interceptors.Count != 0)
            {
                invocation = InvokeGeneratedOneWayInterceptedAsync(
                    method, request, requestCodec, streams, interceptors, control, cancellationToken);
            }
            else
            {
                invocation = InvokeOneWayCoreAsync(
                    method, request, requestCodec, streams, control, cancellationToken);
            }

            return CompleteLogicalInvocation(invocation);
        }
        catch
        {
            Interlocked.Decrement(ref _activeLogicalInvocations);
            throw;
        }
    }

    public ValueTask<TResponse> InvokeGeneratedClientStreamingAsync<TRequest, TResponse, TRequestCodec, TResponseCodec, TStreams>(
        RpcMethodDescriptor method,
        in TRequest request,
        in TRequestCodec requestCodec,
        in TResponseCodec responseCodec,
        in TStreams streams,
        SharpLinkMetadata? metadata,
        CancellationToken cancellationToken = default)
        where TRequestCodec : IRpcCodec<TRequest>
        where TResponseCodec : IRpcCodec<TResponse>
        where TStreams : struct, IRpcClientStreamWriter
    {
        ArgumentNullException.ThrowIfNull(requestCodec);
        ArgumentNullException.ThrowIfNull(responseCodec);
        cancellationToken.ThrowIfCancellationRequested();
        var interceptors = CaptureInterceptorGenerationForInvocation();
        var control = ResolveCallControlForInvocation(
            method, metadata, includeClientDefault: false, interceptors);
        Interlocked.Increment(ref _activeLogicalInvocations);
        try
        {
            ValueTask<TResponse> invocation;
            if (SharpLinkTelemetry.ClientCallsEnabled)
            {
                invocation = InvokeGeneratedClientStreamingWithTelemetryAsync<TRequest, TResponse, TRequestCodec, TResponseCodec, TStreams>(
                    method, request, requestCodec, responseCodec, streams, interceptors, control, cancellationToken);
            }
            else if (interceptors.Count != 0)
            {
                invocation = InvokeGeneratedClientStreamingInterceptedAsync<TRequest, TResponse, TRequestCodec, TResponseCodec, TStreams>(
                    method, request, requestCodec, responseCodec, streams, interceptors, control, cancellationToken);
            }
            else
            {
                invocation = InvokeGeneratedClientStreamingCoreAsync<TRequest, TResponse, TRequestCodec, TResponseCodec, TStreams>(
                    method, request, requestCodec, responseCodec, streams, control, cancellationToken);
            }

            return CompleteLogicalInvocation(invocation);
        }
        catch
        {
            Interlocked.Decrement(ref _activeLogicalInvocations);
            throw;
        }
    }

    public IAsyncEnumerable<TResponse> InvokeGeneratedServerStreamingAsync<TRequest, TResponse, TRequestCodec, TResponseCodec>(
        RpcMethodDescriptor method,
        in TRequest request,
        in TRequestCodec requestCodec,
        in TResponseCodec responseCodec,
        SharpLinkMetadata? metadata,
        CancellationToken cancellationToken = default)
        where TRequestCodec : IRpcCodec<TRequest>
        where TResponseCodec : IRpcCodec<TResponse>
    {
        var interceptors = CaptureInterceptorGenerationForInvocation();
        var control = ResolveCallControlForInvocation(
            method, metadata, includeClientDefault: false, interceptors);
        ArgumentNullException.ThrowIfNull(requestCodec);
        ArgumentNullException.ThrowIfNull(responseCodec);
        EnsureLogicalCallProgress(control);
        Interlocked.Increment(ref _activeLogicalInvocations);
        try
        {
            IAsyncEnumerable<TResponse> invocation;
            if (SharpLinkTelemetry.ClientCallsEnabled)
            {
                invocation = InvokeGeneratedServerStreamingWithTelemetry<TRequest, TResponse, TRequestCodec, TResponseCodec>(
                    method, request, requestCodec, responseCodec, interceptors, control, cancellationToken);
            }
            else if (interceptors.Count != 0)
            {
                invocation = InvokeGeneratedServerStreamingIntercepted<TRequest, TResponse, TRequestCodec, TResponseCodec>(
                    method, request, requestCodec, responseCodec, interceptors, control, cancellationToken);
            }
            else
            {
                invocation = InvokeGeneratedServerStreamingCore<TRequest, TResponse, TRequestCodec, TResponseCodec>(
                    method, request, requestCodec, responseCodec, control, cancellationToken);
            }

            return CompleteLogicalInvocation(invocation);
        }
        catch
        {
            Interlocked.Decrement(ref _activeLogicalInvocations);
            throw;
        }
    }

    public IAsyncEnumerable<TResponse> InvokeGeneratedDuplexStreamingAsync<TRequest, TResponse, TRequestCodec, TResponseCodec, TStreams>(
        RpcMethodDescriptor method,
        in TRequest request,
        in TRequestCodec requestCodec,
        in TResponseCodec responseCodec,
        in TStreams streams,
        SharpLinkMetadata? metadata,
        CancellationToken cancellationToken = default)
        where TRequestCodec : IRpcCodec<TRequest>
        where TResponseCodec : IRpcCodec<TResponse>
        where TStreams : struct, IRpcClientStreamWriter
    {
        var interceptors = CaptureInterceptorGenerationForInvocation();
        var control = ResolveCallControlForInvocation(
            method, metadata, includeClientDefault: false, interceptors);
        ArgumentNullException.ThrowIfNull(requestCodec);
        ArgumentNullException.ThrowIfNull(responseCodec);
        EnsureLogicalCallProgress(control);
        Interlocked.Increment(ref _activeLogicalInvocations);
        try
        {
            IAsyncEnumerable<TResponse> invocation;
            if (SharpLinkTelemetry.ClientCallsEnabled)
            {
                invocation = InvokeGeneratedDuplexStreamingWithTelemetry<TRequest, TResponse, TRequestCodec, TResponseCodec, TStreams>(
                    method, request, requestCodec, responseCodec, streams, interceptors, control, cancellationToken);
            }
            else if (interceptors.Count != 0)
            {
                invocation = InvokeGeneratedDuplexStreamingIntercepted<TRequest, TResponse, TRequestCodec, TResponseCodec, TStreams>(
                    method, request, requestCodec, responseCodec, streams, interceptors, control, cancellationToken);
            }
            else
            {
                invocation = InvokeGeneratedDuplexStreamingCore<TRequest, TResponse, TRequestCodec, TResponseCodec, TStreams>(
                    method, request, requestCodec, responseCodec, streams, control, cancellationToken);
            }

            return CompleteLogicalInvocation(invocation);
        }
        catch
        {
            Interlocked.Decrement(ref _activeLogicalInvocations);
            throw;
        }
    }

    private ValueTask<TResponse> InvokeGeneratedUnaryCoreAsync<TRequest, TResponse, TRequestCodec, TResponseCodec>(
        RpcMethodDescriptor method,
        TRequest request,
        TRequestCodec requestCodec,
        TResponseCodec responseCodec,
        ResolvedCallControl control,
        CancellationToken cancellationToken)
        where TRequestCodec : IRpcCodec<TRequest>
        where TResponseCodec : IRpcCodec<TResponse>
    {
        var outcome = _endpointAdmissionPolicy is null ? null : new AttemptOutcomeState(this, method);
        if (outcome is null)
            SharpLinkTelemetry.RecordClientAttempt();

        ClientConnection? connection = null;
        var reservationOwned = false;
        try
        {
            connection = GetReadyConnection(method, retrySelection: null, outcome);
            reservationOwned = true;
            EnsureLogicalCallProgress(control);
            var operation = connection.PendingCalls.RentGenerated<TResponse, TResponseCodec>(
                in responseCodec,
                PendingCallKind.Unary,
                control.Deadline,
                cancellationToken,
                out var requestId,
                outcome,
                hasResponsePayload: method.HasResponsePayload,
                responseNullable: method.ResponseNullable);
            reservationOwned = false;
            return StartGeneratedUnaryCall(
                connection,
                method.ContractId,
                method.MethodId,
                requestId,
                method.HasResponsePayload,
                request,
                requestCodec,
                operation,
                control,
                cancellationToken);
        }
        catch (Exception exception)
        {
            if (reservationOwned)
                connection!.ReleaseCallAdmissionReservation();
            exception = ArbitrateLogicalCallFailure(control, exception);
            outcome?.CompleteLocalFailure(exception);
            return ValueTask.FromException<TResponse>(exception);
        }
    }

    private ValueTask<TResponse> StartGeneratedUnaryCall<TRequest, TResponse, TRequestCodec, TResponseCodec>(
        ClientConnection connection,
        long contractId,
        long methodId,
        long requestId,
        bool hasResponsePayload,
        TRequest request,
        TRequestCodec requestCodec,
        RpcRequestOperation<TResponse, TResponseCodec> operation,
        ResolvedCallControl control,
        CancellationToken cancellationToken)
        where TRequestCodec : IRpcCodec<TRequest>
        where TResponseCodec : IRpcCodec<TResponse>
    {
        var flags = hasResponsePayload ? ProtocolV2FrameFlags.HasReturn : ProtocolV2FrameFlags.None;
        if (cancellationToken.CanBeCanceled || control.Deadline.HasValue)
            flags |= ProtocolV2FrameFlags.Cancellable;

        try
        {
            if (connection.PendingCalls.Contains(requestId))
            {
                _ = SendRpcCall(
                    connection.Session,
                    contractId,
                    methodId,
                    requestId,
                    flags,
                    request,
                    requestCodec,
                    control.Deadline,
                    control.Metadata,
                    cancellationToken: CancellationToken.None,
                    failureObserver: control.Deadline.HasValue ? connection.PendingCalls : null,
                    publicationTable: connection.PendingCalls);
            }
        }
        catch (Exception exception)
        {
            connection.PendingCalls.TryComplete(requestId, PendingCallCompletionReason.SendFailure, exception);
        }

        return operation.AsValueTask();
    }

    private async ValueTask<TResponse> InvokeGeneratedClientStreamingCoreAsync<TRequest, TResponse, TRequestCodec, TResponseCodec, TStreams>(
        RpcMethodDescriptor method,
        TRequest request,
        TRequestCodec requestCodec,
        TResponseCodec responseCodec,
        TStreams streams,
        ResolvedCallControl control,
        CancellationToken cancellationToken)
        where TRequestCodec : IRpcCodec<TRequest>
        where TResponseCodec : IRpcCodec<TResponse>
        where TStreams : struct, IRpcClientStreamWriter
    {
        var moduleProducerLifetime = SharpLinkClientStreamModuleLeaseContext.Current;
        var outcome = _endpointAdmissionPolicy is null ? null : new AttemptOutcomeState(this, method);
        if (outcome is null)
            SharpLinkTelemetry.RecordClientAttempt();

        ClientConnection? connection = null;
        var reservationOwned = false;
        long requestId;
        RpcRequestOperation<TResponse, TResponseCodec> operation;
        try
        {
            EnsureLogicalCallProgress(control);
            connection = GetReadyConnection(method, retrySelection: null, outcome);
            reservationOwned = true;
            EnsureLogicalCallProgress(control);
            operation = connection.PendingCalls.RentGenerated<TResponse, TResponseCodec>(
                in responseCodec,
                PendingCallKind.ClientStreaming,
                control.Deadline,
                cancellationToken,
                out requestId,
                outcome,
                hasResponsePayload: method.HasResponsePayload,
                responseNullable: method.ResponseNullable);
            reservationOwned = false;
        }
        catch (Exception exception)
        {
            if (reservationOwned)
                connection!.ReleaseCallAdmissionReservation();
            exception = ArbitrateLogicalCallFailure(control, exception);
            outcome?.CompleteLocalFailure(exception);
            throw exception;
        }

        var flags = method.HasResponsePayload
            ? ProtocolV2FrameFlags.HasReturn | ProtocolV2FrameFlags.Cancellable
            : ProtocolV2FrameFlags.Cancellable;
        var streamCancellationToken = connection!.PendingCalls.GetProducerCancellationToken(requestId);
        SharpLinkDynamicModuleLease producerLease = default;
        try
        {
            if (connection.PendingCalls.Contains(requestId))
            {
                producerLease = moduleProducerLifetime?.TakeLease() ?? default;
                await SendRpcCall(
                    connection.Session,
                    method.ContractId,
                    method.MethodId,
                    requestId,
                    flags,
                    request,
                    requestCodec,
                    control.Deadline,
                    control.Metadata,
                    observeEmission: control.Deadline.HasValue,
                    cancellationToken: streamCancellationToken,
                    publicationTable: connection.PendingCalls).ConfigureAwait(false);
                var producerTask = RunGeneratedClientStreamsAsync(
                    connection, streams, requestId, streamCancellationToken, producerLease);
                producerLease = default;
                TrackFrameworkTask(producerTask, "ClientStreamingProducer");
            }
        }
        catch (Exception exception)
        {
            connection.PendingCalls.TryComplete(requestId, PendingCallCompletionReason.SendFailure, exception);
        }
        finally
        {
            producerLease.Dispose();
        }

        return await operation.AsValueTask().ConfigureAwait(false);
    }

    private IAsyncEnumerable<TResponse> InvokeGeneratedServerStreamingCore<TRequest, TResponse, TRequestCodec, TResponseCodec>(
        RpcMethodDescriptor method,
        TRequest request,
        TRequestCodec requestCodec,
        TResponseCodec responseCodec,
        ResolvedCallControl control,
        CancellationToken cancellationToken)
        where TRequestCodec : IRpcCodec<TRequest>
        where TResponseCodec : IRpcCodec<TResponse>
    {
        var dispatcher = PooledAsyncStreamDispatcher<TResponse, TResponseCodec>.Rent(
            cancellationToken,
            in responseCodec,
            method.ResponseNullable);
        TrackFrameworkTask(
            StartGeneratedServerStreamingInvokerAsync(
                dispatcher, method, request, requestCodec, control, cancellationToken),
            "ServerStreamingInvoker");
        return dispatcher;
    }

    private IAsyncEnumerable<TResponse> InvokeGeneratedDuplexStreamingCore<TRequest, TResponse, TRequestCodec, TResponseCodec, TStreams>(
        RpcMethodDescriptor method,
        TRequest request,
        TRequestCodec requestCodec,
        TResponseCodec responseCodec,
        TStreams streams,
        ResolvedCallControl control,
        CancellationToken cancellationToken)
        where TRequestCodec : IRpcCodec<TRequest>
        where TResponseCodec : IRpcCodec<TResponse>
        where TStreams : struct, IRpcClientStreamWriter
    {
        var dispatcher = PooledAsyncStreamDispatcher<TResponse, TResponseCodec>.Rent(
            cancellationToken,
            in responseCodec,
            method.ResponseNullable);
        TrackFrameworkTask(
            StartGeneratedDuplexStreamingInvokerAsync(
                dispatcher, method, request, requestCodec, streams, control, cancellationToken),
            "DuplexStreamingInvoker");
        return dispatcher;
    }

    private async Task StartGeneratedServerStreamingInvokerAsync<TRequest, TResponse, TRequestCodec, TResponseCodec>(
        PooledAsyncStreamDispatcher<TResponse, TResponseCodec> dispatcher,
        RpcMethodDescriptor method,
        TRequest request,
        TRequestCodec requestCodec,
        ResolvedCallControl control,
        CancellationToken cancellationToken)
        where TRequestCodec : IRpcCodec<TRequest>
        where TResponseCodec : IRpcCodec<TResponse>
    {
        var registrationLease = dispatcher.RetainForRegistration();
        ClientConnection? connection = null;
        var requestId = 0L;
        try
        {
            var registration = await PrepareGeneratedStaticServerStreamAsync(
                dispatcher,
                PendingCallKind.ServerStreaming,
                method,
                control,
                cancellationToken,
                captureTerminalSignal: control.Deadline.HasValue).ConfigureAwait(false);
            connection = registration.Connection;
            requestId = registration.RequestId;
            try
            {
                await SendRpcCall(
                    connection.Session,
                    method.ContractId,
                    method.MethodId,
                    requestId,
                    cancellationToken.CanBeCanceled || control.Deadline.HasValue
                        ? ProtocolV2FrameFlags.Cancellable
                        : ProtocolV2FrameFlags.None,
                    request,
                    requestCodec,
                    control.Deadline,
                    control.Metadata,
                    observeEmission: control.Deadline.HasValue,
                    cancellationToken: registration.TerminalSignal?.Token ?? CancellationToken.None,
                    publicationTable: connection.PendingCalls).ConfigureAwait(false);
            }
            finally
            {
                registration.TerminalSignal?.StopObservingTerminal();
            }
        }
        catch (Exception exception)
        {
            CompleteFailedGeneratedStaticStream(dispatcher, connection, requestId, exception);
        }
        finally
        {
            dispatcher.ReleaseRegistrationRetention(registrationLease);
        }
    }

    private async Task StartGeneratedDuplexStreamingInvokerAsync<TRequest, TResponse, TRequestCodec, TResponseCodec, TStreams>(
        PooledAsyncStreamDispatcher<TResponse, TResponseCodec> dispatcher,
        RpcMethodDescriptor method,
        TRequest request,
        TRequestCodec requestCodec,
        TStreams streams,
        ResolvedCallControl control,
        CancellationToken cancellationToken)
        where TRequestCodec : IRpcCodec<TRequest>
        where TResponseCodec : IRpcCodec<TResponse>
        where TStreams : struct, IRpcClientStreamWriter
    {
        var moduleProducerLifetime = SharpLinkClientStreamModuleLeaseContext.Current;
        SharpLinkDynamicModuleLease producerLease = default;
        var registrationLease = dispatcher.RetainForRegistration();
        ClientConnection? connection = null;
        var requestId = 0L;
        try
        {
            var registration = await PrepareGeneratedStaticServerStreamAsync(
                dispatcher,
                PendingCallKind.DuplexStreaming,
                method,
                control,
                cancellationToken).ConfigureAwait(false);
            connection = registration.Connection;
            requestId = registration.RequestId;
            var streamCancellationToken = connection.PendingCalls.GetProducerCancellationToken(requestId);
            producerLease = moduleProducerLifetime?.TakeLease() ?? default;
            await SendRpcCall(
                connection.Session,
                method.ContractId,
                method.MethodId,
                requestId,
                ProtocolV2FrameFlags.Cancellable,
                request,
                requestCodec,
                control.Deadline,
                control.Metadata,
                observeEmission: control.Deadline.HasValue,
                cancellationToken: streamCancellationToken,
                publicationTable: connection.PendingCalls).ConfigureAwait(false);
            await streams.WriteAsync(connection, requestId, streamCancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            CompleteFailedGeneratedStaticStream(dispatcher, connection, requestId, exception);
        }
        finally
        {
            producerLease.Dispose();
            dispatcher.ReleaseRegistrationRetention(registrationLease);
        }
    }

    private ValueTask<StreamCallRegistration> PrepareGeneratedStaticServerStreamAsync<TResponse, TResponseCodec>(
        PooledAsyncStreamDispatcher<TResponse, TResponseCodec> dispatcher,
        PendingCallKind kind,
        RpcMethodDescriptor method,
        ResolvedCallControl control,
        CancellationToken cancellationToken,
        bool captureTerminalSignal = false)
        where TResponseCodec : IRpcCodec<TResponse>
    {
        var outcome = _endpointAdmissionPolicy is null ? null : new AttemptOutcomeState(this, method);
        if (outcome is null)
            SharpLinkTelemetry.RecordClientAttempt();
        var terminalSignal = captureTerminalSignal ? new PendingCallTerminalSignal(outcome) : null;
        IPendingCallCompletionObserver? completionObserver = terminalSignal;
        completionObserver ??= outcome;
        ClientConnection? connection = null;
        var reservationOwned = false;
        var requestId = 0L;
        try
        {
            EnsureLogicalCallProgress(control);
            connection = GetReadyConnection(method, retrySelection: null, outcome);
            reservationOwned = true;
            EnsureLogicalCallProgress(control);
            requestId = connection.PendingCalls.RegisterStream(
                kind,
                dispatcher,
                control.Deadline,
                cancellationToken,
                completionObserver);
            reservationOwned = false;
            if (!connection.PendingCalls.Contains(requestId))
            {
                cancellationToken.ThrowIfCancellationRequested();
                _ = control.LogicalCall?.TryClaimDeadline();
                throw CreateDeadlineExceededException();
            }

            dispatcher.SetConsumerAbandonedCallback(connection.ConsumerAbandonedCallback, requestId);
            connection.Session.StreamManager.Register(requestId, 0, dispatcher);
            return ValueTask.FromResult(new StreamCallRegistration(connection, requestId, terminalSignal));
        }
        catch (Exception exception)
        {
            if (reservationOwned)
                connection!.ReleaseCallAdmissionReservation();
            exception = ArbitrateLogicalCallFailure(control, exception);
            if (connection is not null && requestId != 0)
            {
                connection.PendingCalls.TryComplete(
                    requestId,
                    exception is SharpLinkException { Code: SharpLinkErrorCode.DeadlineExceeded }
                        ? PendingCallCompletionReason.DeadlineExceeded
                        : PendingCallCompletionReason.SendFailure,
                    exception is SharpLinkException { Code: SharpLinkErrorCode.DeadlineExceeded }
                        ? null
                        : exception);
            }
            else
            {
                outcome?.CompleteLocalFailure(exception);
            }

            terminalSignal?.StopObservingTerminal();
            throw exception;
        }
    }

    private void CompleteFailedGeneratedStaticStream<TResponse, TResponseCodec>(
        PooledAsyncStreamDispatcher<TResponse, TResponseCodec> dispatcher,
        ClientConnection? connection,
        long requestId,
        Exception exception)
        where TResponseCodec : IRpcCodec<TResponse>
    {
        if (connection is not null && requestId != 0)
            connection.PendingCalls.TryComplete(requestId, PendingCallCompletionReason.SendFailure, exception);
        else
            dispatcher.Complete(exception);
    }
}
