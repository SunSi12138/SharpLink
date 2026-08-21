namespace SharpLink.Client;

internal sealed partial class SharpLinkClient
{
    public ValueTask<TResponse> InvokeUnaryAsync<TRequest, TResponse>(
        RpcMethodDescriptor method,
        in TRequest request,
        IRpcCodec<TRequest> requestCodec,
        IRpcCodec<TResponse> responseCodec,
        SharpLinkCallOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requestCodec);
        ArgumentNullException.ThrowIfNull(responseCodec);
        cancellationToken.ThrowIfCancellationRequested();
        var interceptors = Volatile.Read(ref _clientInterceptors);
        Interlocked.Increment(ref _activeLogicalInvocations);
        try
        {
            ValueTask<TResponse> invocation;
            if (SharpLinkTelemetry.ClientCallsEnabled)
            {
                invocation = InvokeUnaryWithTelemetryAsync(
                    method, request, requestCodec, responseCodec, interceptors, options, cancellationToken);
            }
            else if (interceptors.Length != 0)
            {
                invocation = InvokeUnaryInterceptedAsync(
                    method, request, requestCodec, responseCodec, interceptors, options, cancellationToken);
            }
            else
            {
                var control = ResolveCallControl(
                    options,
                    includeClientDefault: true,
                    method.HasMethodTimeout,
                    method.MethodTimeout);
                invocation = InvokeUnaryWithOptionalRetryAsync(
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

    public ValueTask InvokeOneWayAsync<TRequest, TStreams>(
        RpcMethodDescriptor method,
        in TRequest request,
        IRpcCodec<TRequest> requestCodec,
        in TStreams streams,
        SharpLinkCallOptions options,
        CancellationToken cancellationToken = default)
        where TStreams : struct, IRpcClientStreamWriter
    {
        ArgumentNullException.ThrowIfNull(requestCodec);
        cancellationToken.ThrowIfCancellationRequested();
        var interceptors = Volatile.Read(ref _clientInterceptors);
        Interlocked.Increment(ref _activeLogicalInvocations);
        try
        {
            ValueTask invocation;
            if (SharpLinkTelemetry.ClientCallsEnabled)
            {
                invocation = InvokeOneWayWithTelemetryAsync(
                    method, request, requestCodec, streams, interceptors, options, cancellationToken);
            }
            else if (interceptors.Length != 0)
            {
                invocation = InvokeOneWayInterceptedAsync(
                    method, request, requestCodec, streams, interceptors, options, cancellationToken);
            }
            else
            {
                var control = ResolveCallControl(
                    options,
                    includeClientDefault: false,
                    method.HasMethodTimeout,
                    method.MethodTimeout);
                invocation = InvokeOneWayCoreAsync(
                    method,
                    request,
                    requestCodec,
                    streams,
                    control,
                    cancellationToken);
            }
            return CompleteLogicalInvocation(invocation);
        }
        catch
        {
            Interlocked.Decrement(ref _activeLogicalInvocations);
            throw;
        }
    }

    public ValueTask<TResponse> InvokeClientStreamingAsync<TRequest, TResponse, TStreams>(
        RpcMethodDescriptor method,
        in TRequest request,
        IRpcCodec<TRequest> requestCodec,
        IRpcCodec<TResponse> responseCodec,
        in TStreams streams,
        SharpLinkCallOptions options,
        CancellationToken cancellationToken = default)
        where TStreams : struct, IRpcClientStreamWriter
    {
        ArgumentNullException.ThrowIfNull(requestCodec);
        ArgumentNullException.ThrowIfNull(responseCodec);
        cancellationToken.ThrowIfCancellationRequested();
        var interceptors = Volatile.Read(ref _clientInterceptors);
        Interlocked.Increment(ref _activeLogicalInvocations);
        try
        {
            ValueTask<TResponse> invocation;
            if (SharpLinkTelemetry.ClientCallsEnabled)
            {
                invocation = InvokeClientStreamingWithTelemetryAsync(
                    method, request, requestCodec, responseCodec, streams, interceptors, options, cancellationToken);
            }
            else if (interceptors.Length != 0)
            {
                invocation = InvokeClientStreamingInterceptedAsync(
                    method, request, requestCodec, responseCodec, streams, interceptors, options, cancellationToken);
            }
            else
            {
                var control = ResolveCallControl(
                    options,
                    includeClientDefault: false,
                    method.HasMethodTimeout,
                    method.MethodTimeout);
                invocation = InvokeClientStreamingCoreAsync(
                    method,
                    request,
                    requestCodec,
                    responseCodec,
                    streams,
                    control,
                    cancellationToken);
            }
            return CompleteLogicalInvocation(invocation);
        }
        catch
        {
            Interlocked.Decrement(ref _activeLogicalInvocations);
            throw;
        }
    }

    public IAsyncEnumerable<TResponse> InvokeServerStreamingAsync<TRequest, TResponse>(
        RpcMethodDescriptor method,
        in TRequest request,
        IRpcCodec<TRequest> requestCodec,
        IRpcCodec<TResponse> responseCodec,
        SharpLinkCallOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requestCodec);
        ArgumentNullException.ThrowIfNull(responseCodec);
        var interceptors = Volatile.Read(ref _clientInterceptors);
        Interlocked.Increment(ref _activeLogicalInvocations);
        try
        {
            IAsyncEnumerable<TResponse> invocation;
            if (SharpLinkTelemetry.ClientCallsEnabled)
            {
                invocation = InvokeServerStreamingWithTelemetry(
                    method, request, requestCodec, responseCodec, interceptors, options, cancellationToken);
            }
            else if (interceptors.Length != 0)
            {
                invocation = InvokeServerStreamingIntercepted(
                    method, request, requestCodec, responseCodec, interceptors, options, cancellationToken);
            }
            else
            {
                invocation = InvokeServerStreamingCore(
                    method, request, requestCodec, responseCodec, options, cancellationToken);
            }
            return CompleteLogicalInvocation(invocation);
        }
        catch
        {
            Interlocked.Decrement(ref _activeLogicalInvocations);
            throw;
        }
    }

    private IAsyncEnumerable<TResponse> InvokeServerStreamingCore<TRequest, TResponse>(
        RpcMethodDescriptor method,
        TRequest request,
        IRpcCodec<TRequest> requestCodec,
        IRpcCodec<TResponse> responseCodec,
        SharpLinkCallOptions options,
        CancellationToken cancellationToken)
    {
        var control = ResolveCallControl(
            options,
            includeClientDefault: false,
            method.HasMethodTimeout,
            method.MethodTimeout);
        var dispatcher = PooledAsyncStreamDispatcher<TResponse>.Rent(
            cancellationToken,
            responseCodec,
            method.ResponseNullable);
        TrackFrameworkTask(
            StartServerStreamingInvokerAsync(
                dispatcher,
                method,
                request,
                requestCodec,
                control,
                cancellationToken),
            "ServerStreamingInvoker");
        return dispatcher;
    }

    public IAsyncEnumerable<TResponse> InvokeDuplexStreamingAsync<TRequest, TResponse, TStreams>(
        RpcMethodDescriptor method,
        in TRequest request,
        IRpcCodec<TRequest> requestCodec,
        IRpcCodec<TResponse> responseCodec,
        in TStreams streams,
        SharpLinkCallOptions options,
        CancellationToken cancellationToken = default)
        where TStreams : struct, IRpcClientStreamWriter
    {
        ArgumentNullException.ThrowIfNull(requestCodec);
        ArgumentNullException.ThrowIfNull(responseCodec);
        var interceptors = Volatile.Read(ref _clientInterceptors);
        Interlocked.Increment(ref _activeLogicalInvocations);
        try
        {
            IAsyncEnumerable<TResponse> invocation;
            if (SharpLinkTelemetry.ClientCallsEnabled)
            {
                invocation = InvokeDuplexStreamingWithTelemetry(
                    method, request, requestCodec, responseCodec, streams, interceptors, options, cancellationToken);
            }
            else if (interceptors.Length != 0)
            {
                invocation = InvokeDuplexStreamingIntercepted(
                    method, request, requestCodec, responseCodec, streams, interceptors, options, cancellationToken);
            }
            else
            {
                invocation = InvokeDuplexStreamingCore(
                    method, request, requestCodec, responseCodec, streams, options, cancellationToken);
            }
            return CompleteLogicalInvocation(invocation);
        }
        catch
        {
            Interlocked.Decrement(ref _activeLogicalInvocations);
            throw;
        }
    }

    private ValueTask<T> CompleteLogicalInvocation<T>(ValueTask<T> invocation)
    {
        if (invocation.IsCompleted)
        {
            Interlocked.Decrement(ref _activeLogicalInvocations);
            return invocation;
        }
        return AwaitLogicalInvocationAsync(invocation);
    }

    private ValueTask CompleteLogicalInvocation(ValueTask invocation)
    {
        if (invocation.IsCompleted)
        {
            Interlocked.Decrement(ref _activeLogicalInvocations);
            return invocation;
        }
        return AwaitLogicalInvocationAsync(invocation);
    }

    private async ValueTask<T> AwaitLogicalInvocationAsync<T>(ValueTask<T> invocation)
    {
        try
        {
            return await invocation.ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _activeLogicalInvocations);
        }
    }

    private async ValueTask AwaitLogicalInvocationAsync(ValueTask invocation)
    {
        try
        {
            await invocation.ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _activeLogicalInvocations);
        }
    }

    private IAsyncEnumerable<T> CompleteLogicalInvocation<T>(IAsyncEnumerable<T> invocation)
        => new LogicalInvocationAsyncEnumerable<T>(this, invocation);

    private sealed class LogicalInvocationAsyncEnumerable<T>(
        SharpLinkClient client,
        IAsyncEnumerable<T> invocation) : IAsyncEnumerable<T>, IAsyncEnumerator<T>
    {
        private int _enumerated;
        private int _completed;
        private IAsyncEnumerator<T>? _enumerator;

        public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _enumerated, 1) != 0)
                throw new InvalidOperationException("A logical RPC stream can only be enumerated once.");
            try
            {
                _enumerator = invocation.GetAsyncEnumerator(cancellationToken);
                return this;
            }
            catch
            {
                Complete();
                throw;
            }
        }

        public T Current => (_enumerator ?? throw new InvalidOperationException(
            "The logical RPC stream has not been enumerated.")).Current;

        public ValueTask<bool> MoveNextAsync()
        {
            try
            {
                var move = (_enumerator ?? throw new InvalidOperationException(
                    "The logical RPC stream has not been enumerated.")).MoveNextAsync();
                if (!move.IsCompletedSuccessfully)
                    return AwaitMoveNextAsync(move);
                if (!move.Result)
                    Complete();
                return move;
            }
            catch
            {
                Complete();
                throw;
            }
        }

        public ValueTask DisposeAsync()
        {
            try
            {
                var dispose = _enumerator?.DisposeAsync() ?? ValueTask.CompletedTask;
                if (!dispose.IsCompleted)
                    return AwaitDisposeAsync(dispose);
                Complete();
                return dispose;
            }
            catch
            {
                Complete();
                throw;
            }
        }

        private async ValueTask<bool> AwaitMoveNextAsync(ValueTask<bool> move)
        {
            try
            {
                var hasNext = await move.ConfigureAwait(false);
                if (!hasNext)
                    Complete();
                return hasNext;
            }
            catch
            {
                Complete();
                throw;
            }
        }

        private async ValueTask AwaitDisposeAsync(ValueTask dispose)
        {
            try
            {
                await dispose.ConfigureAwait(false);
            }
            finally
            {
                Complete();
            }
        }

        private void Complete()
        {
            if (Interlocked.Exchange(ref _completed, 1) == 0)
                Interlocked.Decrement(ref client._activeLogicalInvocations);
        }
    }

    private IAsyncEnumerable<TResponse> InvokeDuplexStreamingCore<TRequest, TResponse, TStreams>(
        RpcMethodDescriptor method,
        TRequest request,
        IRpcCodec<TRequest> requestCodec,
        IRpcCodec<TResponse> responseCodec,
        TStreams streams,
        SharpLinkCallOptions options,
        CancellationToken cancellationToken)
        where TStreams : struct, IRpcClientStreamWriter
    {
        var control = ResolveCallControl(
            options,
            includeClientDefault: false,
            method.HasMethodTimeout,
            method.MethodTimeout);
        var dispatcher = PooledAsyncStreamDispatcher<TResponse>.Rent(
            cancellationToken,
            responseCodec,
            method.ResponseNullable);
        TrackFrameworkTask(
            StartDuplexStreamingInvokerAsync(
                dispatcher,
                method,
                request,
                requestCodec,
                streams,
                control,
                cancellationToken),
            "DuplexStreamingInvoker");
        return dispatcher;
    }

    private ValueTask<TResponse> InvokeUnaryCoreAsync<TRequest, TResponse>(
        RpcMethodDescriptor method,
        TRequest request,
        IRpcCodec<TRequest> requestCodec,
        IRpcCodec<TResponse> responseCodec,
        ResolvedCallControl control,
        CancellationToken cancellationToken)
    {
        if (control.WaitForReady)
        {
            return InvokeUnaryWaitForReadyAsync(
                method,
                request,
                requestCodec,
                responseCodec,
                control,
                cancellationToken);
        }

        var outcome = _endpointAdmissionPolicy is null ? null : new AttemptOutcomeState(this, method);
        if (outcome is null)
            SharpLinkTelemetry.RecordClientAttempt();
        try
        {
            var connection = GetReadyConnection(method, retrySelection: null, outcome);
            var operation = connection.PendingCalls.Rent(
                responseCodec,
                PendingCallKind.Unary,
                control.Deadline,
                cancellationToken,
                out var requestId,
                outcome,
                hasResponsePayload: method.HasResponsePayload,
                responseNullable: method.ResponseNullable);
            return StartUnaryCall(
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
            outcome?.CompleteLocalFailure(exception);
            return ValueTask.FromException<TResponse>(exception);
        }
    }

    private async ValueTask<TResponse> InvokeUnaryWaitForReadyAsync<TRequest, TResponse>(
        RpcMethodDescriptor method,
        TRequest request,
        IRpcCodec<TRequest> requestCodec,
        IRpcCodec<TResponse> responseCodec,
        ResolvedCallControl control,
        CancellationToken cancellationToken)
    {
        var outcome = _endpointAdmissionPolicy is null ? null : new AttemptOutcomeState(this, method);
        if (outcome is null)
            SharpLinkTelemetry.RecordClientAttempt();
        try
        {
            var connection = await GetReadyConnectionAsync(
                waitForReady: true,
                control.Deadline,
                cancellationToken,
                method,
                outcome).ConfigureAwait(false);
            var lease = await connection.PendingCalls.RentAsync(
                responseCodec,
                PendingCallKind.Unary,
                control.Deadline,
                waitForSlot: true,
                cancellationToken,
                outcome,
                hasResponsePayload: method.HasResponsePayload,
                responseNullable: method.ResponseNullable).ConfigureAwait(false);
            return await StartUnaryCall(
                connection,
                method.ContractId,
                method.MethodId,
                lease.Id,
                method.HasResponsePayload,
                request,
                requestCodec,
                lease.Operation,
                control,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            outcome?.CompleteLocalFailure(exception);
            throw;
        }
    }

    private ValueTask<TResponse> StartUnaryCall<TRequest, TResponse>(
        ClientConnection connection,
        long contractId,
        long methodId,
        long requestId,
        bool hasResponsePayload,
        TRequest request,
        IRpcCodec<TRequest> requestCodec,
        RpcRequestOperation<TResponse> operation,
        ResolvedCallControl control,
        CancellationToken cancellationToken)
    {
        var flags = hasResponsePayload
            ? ProtocolV2FrameFlags.HasReturn
            : ProtocolV2FrameFlags.None;
        if (cancellationToken.CanBeCanceled || control.Deadline.HasValue)
            flags |= ProtocolV2FrameFlags.Cancellable;

        try
        {
            if (connection.PendingCalls.Contains(requestId))
            {
                SendRpcCall(
                    connection.Session,
                    contractId,
                    methodId,
                    requestId,
                    flags,
                    request,
                    requestCodec,
                    control.Deadline.UtcDeadline,
                    control.Metadata);
            }
        }
        catch (Exception exception)
        {
            connection.PendingCalls.TryComplete(
                requestId,
                PendingCallCompletionReason.SendFailure,
                exception);
        }

        return operation.AsValueTask();
    }

    private async ValueTask InvokeOneWayCoreAsync<TRequest, TStreams>(
        RpcMethodDescriptor method,
        TRequest request,
        IRpcCodec<TRequest> requestCodec,
        TStreams streams,
        ResolvedCallControl control,
        CancellationToken cancellationToken)
        where TStreams : struct, IRpcClientStreamWriter
    {
        var outcome = _endpointAdmissionPolicy is null ? null : new AttemptOutcomeState(this, method);
        if (outcome is null)
            SharpLinkTelemetry.RecordClientAttempt();
        var connection = control.WaitForReady
            ? await GetReadyConnectionAsync(
                waitForReady: true,
                control.Deadline,
                cancellationToken,
                method,
                outcome).ConfigureAwait(false)
            : GetReadyConnection(method, retrySelection: null, outcome);
        var flags = ProtocolV2FrameFlags.OneWay;
        if (method.HasClientStreams && (cancellationToken.CanBeCanceled || control.Deadline.HasValue))
            flags |= ProtocolV2FrameFlags.Cancellable;

        PendingRequestLease<RpcEmptyRequest> oneWayStreamLease = default;
        long requestId;
        try
        {
            if (method.HasClientStreams)
            {
                oneWayStreamLease = connection.PendingCalls.RegisterOneWayClientStream(
                    control.Deadline,
                    cancellationToken,
                    outcome);
                requestId = oneWayStreamLease.Id;
            }
            else
            {
                requestId = connection.PendingCalls.AllocateRequestId();
            }
        }
        catch (Exception exception)
        {
            outcome?.CompleteLocalFailure(exception);
            throw;
        }
        var streamCancellationToken = method.HasClientStreams
            ? connection.PendingCalls.GetProducerCancellationToken(requestId)
            : CancellationToken.None;
        if (method.HasClientStreams && !connection.PendingCalls.Contains(requestId))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var exception = CreateDeadlineExceededException();
            outcome?.CompleteLocalFailure(exception);
            throw exception;
        }
        if (!method.HasClientStreams && !connection.TryBeginUntrackedCall())
        {
            var exception = new SharpLinkException(
                SharpLinkErrorCode.Unavailable,
                "The selected connection is draining.");
            outcome?.CompleteWithoutPending(PendingCallCompletionReason.ConnectionClosed, exception);
            throw exception;
        }
        try
        {
            try
            {
                SendRpcCall(
                    connection.Session,
                    method.ContractId,
                    method.MethodId,
                    requestId,
                    flags,
                    request,
                    requestCodec,
                    control.Deadline.UtcDeadline,
                    control.Metadata);
                if (method.HasClientStreams)
                {
                    await streams.WriteAsync(connection, requestId, streamCancellationToken).ConfigureAwait(false);
                    connection.PendingCalls.TryComplete(
                        requestId,
                        PendingCallCompletionReason.LocalStreamComplete);
                    _ = await oneWayStreamLease.Operation.AsValueTask().ConfigureAwait(false);
                }
                else
                {
                    outcome?.CompleteWithoutPending(PendingCallCompletionReason.LocalStreamComplete);
                }
            }
            catch (Exception exception)
            {
                if (method.HasClientStreams)
                {
                    connection.PendingCalls.TryComplete(
                        requestId,
                        PendingCallCompletionReason.SendFailure,
                        exception);
                    _ = await oneWayStreamLease.Operation.AsValueTask().ConfigureAwait(false);
                }
                else
                {
                    outcome?.CompleteWithoutPending(PendingCallCompletionReason.SendFailure, exception);
                }
                throw;
            }
        }
        finally
        {
            if (!method.HasClientStreams)
                connection.EndUntrackedCall();
        }
    }

    private async ValueTask<TResponse> InvokeClientStreamingCoreAsync<TRequest, TResponse, TStreams>(
        RpcMethodDescriptor method,
        TRequest request,
        IRpcCodec<TRequest> requestCodec,
        IRpcCodec<TResponse> responseCodec,
        TStreams streams,
        ResolvedCallControl control,
        CancellationToken cancellationToken)
        where TStreams : struct, IRpcClientStreamWriter
    {
        var moduleProducerLifetime = SharpLinkClientStreamModuleLeaseContext.Current;
        var outcome = _endpointAdmissionPolicy is null ? null : new AttemptOutcomeState(this, method);
        if (outcome is null)
            SharpLinkTelemetry.RecordClientAttempt();
        ClientConnection connection;
        long requestId;
        RpcRequestOperation<TResponse> operation;
        try
        {
            if (!control.WaitForReady)
            {
                connection = GetReadyConnection(method, retrySelection: null, outcome);
                operation = connection.PendingCalls.Rent(
                    responseCodec,
                    PendingCallKind.ClientStreaming,
                    control.Deadline,
                    cancellationToken,
                    out requestId,
                    outcome,
                    hasResponsePayload: method.HasResponsePayload,
                    responseNullable: method.ResponseNullable);
            }
            else
            {
                connection = await GetReadyConnectionAsync(
                    waitForReady: true,
                    control.Deadline,
                    cancellationToken,
                    method,
                    outcome).ConfigureAwait(false);
                var lease = await connection.PendingCalls.RentAsync(
                    responseCodec,
                    PendingCallKind.ClientStreaming,
                    control.Deadline,
                    waitForSlot: true,
                    cancellationToken,
                    outcome,
                    hasResponsePayload: method.HasResponsePayload,
                    responseNullable: method.ResponseNullable).ConfigureAwait(false);
                requestId = lease.Id;
                operation = lease.Operation;
            }
        }
        catch (Exception exception)
        {
            outcome?.CompleteLocalFailure(exception);
            throw;
        }
        var flags = method.HasResponsePayload
            ? ProtocolV2FrameFlags.HasReturn | ProtocolV2FrameFlags.Cancellable
            : ProtocolV2FrameFlags.Cancellable;
        var streamCancellationToken = connection.PendingCalls.GetProducerCancellationToken(requestId);
        SharpLinkDynamicModuleLease producerLease = default;
        try
        {
            if (connection.PendingCalls.Contains(requestId))
            {
                producerLease = moduleProducerLifetime?.TakeLease() ?? default;
                SendRpcCall(
                    connection.Session,
                    method.ContractId,
                    method.MethodId,
                    requestId,
                    flags,
                    request,
                    requestCodec,
                    control.Deadline.UtcDeadline,
                    control.Metadata);
                var producerTask = RunGeneratedClientStreamsAsync(
                    connection,
                    streams,
                    requestId,
                    streamCancellationToken,
                    producerLease);
                producerLease = default;
                TrackFrameworkTask(producerTask, "ClientStreamingProducer");
            }
        }
        catch (Exception exception)
        {
            connection.PendingCalls.TryComplete(
                requestId,
                PendingCallCompletionReason.SendFailure,
                exception);
        }
        finally
        {
            producerLease.Dispose();
        }

        return await operation.AsValueTask().ConfigureAwait(false);
    }

    private async Task RunGeneratedClientStreamsAsync<TStreams>(
        ClientConnection connection,
        TStreams streams,
        long requestId,
        CancellationToken cancellationToken,
        SharpLinkDynamicModuleLease producerLease)
        where TStreams : struct, IRpcClientStreamWriter
    {
        try
        {
            await streams.WriteAsync(connection, requestId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            connection.PendingCalls.TryComplete(
                requestId,
                PendingCallCompletionReason.SendFailure,
                exception);
        }
        finally
        {
            producerLease.Dispose();
        }
    }

    private async Task StartServerStreamingInvokerAsync<TRequest, TResponse>(
        PooledAsyncStreamDispatcher<TResponse> dispatcher,
        RpcMethodDescriptor method,
        TRequest request,
        IRpcCodec<TRequest> requestCodec,
        ResolvedCallControl control,
        CancellationToken cancellationToken)
    {
        var registrationLease = dispatcher.RetainForRegistration();
        ClientConnection? connection = null;
        var requestId = 0L;
        try
        {
            var registration = await PrepareGeneratedServerStreamAsync(
                dispatcher,
                PendingCallKind.ServerStreaming,
                method,
                control,
                cancellationToken).ConfigureAwait(false);
            connection = registration.Connection;
            requestId = registration.RequestId;
            SendRpcCall(
                connection.Session,
                method.ContractId,
                method.MethodId,
                requestId,
                cancellationToken.CanBeCanceled || control.Deadline.HasValue
                    ? ProtocolV2FrameFlags.Cancellable
                    : ProtocolV2FrameFlags.None,
                request,
                requestCodec,
                control.Deadline.UtcDeadline,
                control.Metadata);
        }
        catch (Exception exception)
        {
            CompleteFailedGeneratedStream(dispatcher, connection, requestId, exception);
        }
        finally
        {
            dispatcher.ReleaseRegistrationRetention(registrationLease);
        }
    }

    private async Task StartDuplexStreamingInvokerAsync<TRequest, TResponse, TStreams>(
        PooledAsyncStreamDispatcher<TResponse> dispatcher,
        RpcMethodDescriptor method,
        TRequest request,
        IRpcCodec<TRequest> requestCodec,
        TStreams streams,
        ResolvedCallControl control,
        CancellationToken cancellationToken)
        where TStreams : struct, IRpcClientStreamWriter
    {
        var moduleProducerLifetime = SharpLinkClientStreamModuleLeaseContext.Current;
        SharpLinkDynamicModuleLease producerLease = default;
        var registrationLease = dispatcher.RetainForRegistration();
        ClientConnection? connection = null;
        var requestId = 0L;
        try
        {
            var registration = await PrepareGeneratedServerStreamAsync(
                dispatcher,
                PendingCallKind.DuplexStreaming,
                method,
                control,
                cancellationToken).ConfigureAwait(false);
            connection = registration.Connection;
            requestId = registration.RequestId;
            var streamCancellationToken = connection.PendingCalls.GetProducerCancellationToken(requestId);
            producerLease = moduleProducerLifetime?.TakeLease() ?? default;
            SendRpcCall(
                connection.Session,
                method.ContractId,
                method.MethodId,
                requestId,
                ProtocolV2FrameFlags.Cancellable,
                request,
                requestCodec,
                control.Deadline.UtcDeadline,
                control.Metadata);
            await streams.WriteAsync(connection, requestId, streamCancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            CompleteFailedGeneratedStream(dispatcher, connection, requestId, exception);
        }
        finally
        {
            producerLease.Dispose();
            dispatcher.ReleaseRegistrationRetention(registrationLease);
        }
    }

    private async ValueTask<StreamCallRegistration> PrepareGeneratedServerStreamAsync<TResponse>(
        PooledAsyncStreamDispatcher<TResponse> dispatcher,
        PendingCallKind kind,
        RpcMethodDescriptor method,
        ResolvedCallControl control,
        CancellationToken cancellationToken)
    {
        var outcome = _endpointAdmissionPolicy is null ? null : new AttemptOutcomeState(this, method);
        if (outcome is null)
            SharpLinkTelemetry.RecordClientAttempt();
        ClientConnection? connection = null;
        var requestId = 0L;
        try
        {
            connection = await GetReadyConnectionAsync(
                control.WaitForReady,
                control.Deadline,
                cancellationToken,
                method,
                outcome).ConfigureAwait(false);
            requestId = connection.PendingCalls.RegisterStream(
                kind,
                dispatcher,
                control.Deadline,
                cancellationToken,
                outcome);
            if (!connection.PendingCalls.Contains(requestId))
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw CreateDeadlineExceededException();
            }
            dispatcher.SetConsumerAbandonedCallback(connection.ConsumerAbandonedCallback, requestId);
            connection.Session.StreamManager.Register(requestId, 0, dispatcher);
            return new StreamCallRegistration(connection, requestId);
        }
        catch (Exception exception)
        {
            if (connection is not null && requestId != 0)
            {
                connection.PendingCalls.TryComplete(
                    requestId,
                    PendingCallCompletionReason.SendFailure,
                    exception);
            }
            else
            {
                outcome?.CompleteLocalFailure(exception);
            }
            throw;
        }
    }

    private void CompleteFailedGeneratedStream<TResponse>(
        PooledAsyncStreamDispatcher<TResponse> dispatcher,
        ClientConnection? connection,
        long requestId,
        Exception exception)
    {
        if (connection is not null && requestId != 0)
        {
            connection.PendingCalls.TryComplete(
                requestId,
                PendingCallCompletionReason.SendFailure,
                exception);
        }
        else
        {
            dispatcher.Complete(exception);
        }
    }

    private readonly record struct StreamCallRegistration(
        ClientConnection Connection,
        long RequestId);

    private void SendRpcCall<TRequest>(
        RpcSession session,
        long contractId,
        long methodId,
        long requestId,
        ProtocolV2FrameFlags flags,
        in TRequest request,
        IRpcCodec<TRequest> requestCodec,
        DateTimeOffset? deadline,
        SharpLinkMetadata? metadata)
    {
        var hasMetadata = metadata is { Count: > 0 };
        var metadataLength = 0;
        if (deadline is not null)
            flags |= ProtocolV2FrameFlags.HasDeadline;
        if (hasMetadata)
        {
            if ((session.NegotiatedCapabilities & ProtocolV2Capabilities.Metadata) == 0)
            {
                throw new SharpLinkException(
                    SharpLinkErrorCode.Unimplemented,
                    "The connected server did not negotiate request metadata support.");
            }
            metadataLength = ProtocolV2PayloadCodec.GetMetadataPayloadLength(metadata!);
            if (metadataLength > _protocolOptions.MaxMetadataBytes)
            {
                throw new SharpLinkException(
                    SharpLinkErrorCode.ResourceExhausted,
                    $"Request metadata exceeds {_protocolOptions.MaxMetadataBytes} bytes.");
            }
            flags |= ProtocolV2FrameFlags.HasMetadata;
        }

        var writer = session.RentFrameWriter();
        var ownsWriter = true;
        try
        {
            using (writer.BeginPacketScope(
                       ProtocolV2FrameType.Request,
                       flags,
                       unchecked((ulong)requestId)))
            {
                var span = writer.GetSpan(ProtocolV2Constants.RequestPrefixBytes);
                BinaryPrimitives.WriteInt64LittleEndian(span, contractId);
                BinaryPrimitives.WriteInt64LittleEndian(span[8..], methodId);
                writer.Advance(ProtocolV2Constants.RequestPrefixBytes);
                if (deadline is { } absoluteDeadline)
                {
                    var deadlineSpan = writer.GetSpan(sizeof(long));
                    BinaryPrimitives.WriteInt64LittleEndian(
                        deadlineSpan,
                        absoluteDeadline.ToUnixTimeMilliseconds());
                    writer.Advance(sizeof(long));
                }
                if (hasMetadata)
                {
                    ProtocolV2PayloadCodec.WriteVarUInt32(writer, checked((uint)metadataLength));
                    ProtocolV2PayloadCodec.WriteMetadata(writer, metadata!);
                }
                requestCodec.Serialize(request, writer);
            }

            ownsWriter = false;
            session.SendPacket(writer);
        }
        finally
        {
            if (ownsWriter)
                _runtimeContext.Buffers.Return(writer);
        }
    }
}
