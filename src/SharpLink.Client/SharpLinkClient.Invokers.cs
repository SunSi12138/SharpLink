namespace SharpLink.Client;

internal sealed partial class SharpLinkClient
{

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
        try { return await invocation.ConfigureAwait(false); }
        finally { Interlocked.Decrement(ref _activeLogicalInvocations); }
    }

    private async ValueTask AwaitLogicalInvocationAsync(ValueTask invocation)
    {
        try { await invocation.ConfigureAwait(false); }
        finally { Interlocked.Decrement(ref _activeLogicalInvocations); }
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
            try { await dispose.ConfigureAwait(false); }
            finally { Complete(); }
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
        ResolvedCallControl control,
        CancellationToken cancellationToken)
        where TStreams : struct, IRpcClientStreamWriter
    {
        var dispatcher = PooledAsyncStreamDispatcher<TResponse>.Rent(cancellationToken, responseCodec, method.ResponseNullable);
        var registrationLease = dispatcher.RetainForRegistration();
        var registrationOwner = SharpLinkClientModuleCallContext.Current?.Retain();
        SharpLinkClientStreamPhysicalLease producerLease = default;
        try
        {
            // Transfer before queuing: consumer disposal may otherwise release an unclaimed
            // producer owner while the queued invoker still retains its original Codec.
            producerLease = SharpLinkClientStreamModuleLeaseContext.Current?.TakeLease() ?? default;
            _ = TrackBoundStreamWork(() => StartDuplexStreamingInvokerAsync(dispatcher, method, request, requestCodec, streams, control, cancellationToken, registrationLease, producerLease, registrationOwner), "DuplexStreamingInvoker");
        }
        catch (Exception error)
        { dispatcher.Complete(error); dispatcher.ReleaseRegistrationRetention(registrationLease); producerLease.Dispose(); registrationOwner?.Dispose(); }
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
        var outcome = _endpointAdmissionPolicy is null ? null : new AttemptOutcomeState(this, method);
        if (outcome is null)
            SharpLinkTelemetry.RecordClientAttempt();
        long routingId = 0;
        ClientConnection? connection = null;
        var reservationOwned = false;
        try
        {
            // Connection selection is the first step that can block, so the pre-registration
            // checkpoint below is the first place a fresh sample carries new information. A sample
            // here would only repeat the stage-local validation the call control already did.
            var target = GetReadyStaticTarget(control.BindingSite, method, outcome);
            connection = target.Connection;
            routingId = unchecked((long)target.Token);
            reservationOwned = true;
            EnsureLogicalCallProgress(control);
            var operation = connection.PendingCalls.Rent(
                responseCodec,
                PendingCallKind.Unary,
                control.Deadline,
                cancellationToken,
                out var requestId,
                outcome,
                hasResponsePayload: method.HasResponsePayload,
                responseNullable: method.ResponseNullable);
            reservationOwned = false;
            return StartUnaryCall(
                connection,
                routingId,
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

    /// <summary>
    /// Races a plain OneWay emission wait against the call's own deadline.
    /// </summary>
    /// <remarks>
    /// A plain OneWay registers no pending entry, so there is no deadline scheduler and no cancel
    /// path behind it: the emission wait is the only place the caller can still observe its own
    /// end-to-end lifetime. The Request has already been published when this runs, so losing the
    /// race never retracts or fails the frame in the transport - it only stops the caller from
    /// blocking past its deadline, which is what the previous deferred-compaction design enforced
    /// by dropping expired frames at emission, and it publishes the cancel that terminates the
    /// remote call. A deadline that already elapsed before the frame was published is refused
    /// earlier, in the send itself.
    /// </remarks>
    private async ValueTask AwaitPlainOneWayEmissionOrDeadlineAsync(
        ValueTask emission,
        ClientConnection connection,
        long requestId,
        ResolvedCallControl control)
    {
        var emissionTask = emission.AsTask();
        var remaining = control.Deadline.GetRemaining(_runtimeContext.TimeProvider);
        if (remaining > TimeSpan.Zero)
        {
            try
            {
                await emissionTask.WaitAsync(remaining, _runtimeContext.TimeProvider).ConfigureAwait(false);
                return;
            }
            catch (TimeoutException)
            {
            }
        }

        ObserveAbandonedEmission(emissionTask);
        _ = control.LogicalCall?.TryClaimDeadline();
        // This shape owns no pending entry, so no terminal transition can tell the peer to stop.
        // The Request is already in the normal queue ahead of this cancel, so the peer either
        // cancels the call it received or discards it for a request it never got.
        connection.TrySendDeadlineCancel(requestId);
        throw CreateDeadlineExceededException();
    }

    private static void ObserveAbandonedEmission(Task emission)
    {
        if (emission.IsCompleted)
        {
            _ = emission.Exception;
            return;
        }

        _ = emission.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }

    private ValueTask InvokeOneWayCoreAsync<TRequest, TStreams>(
        RpcMethodDescriptor method,
        TRequest request,
        IRpcCodec<TRequest> requestCodec,
        TStreams streams,
        ResolvedCallControl control,
        CancellationToken cancellationToken)
        where TStreams : struct, IRpcClientStreamWriter
        => method.HasClientStreams
            ? InvokeOneWayStreamingCoreAsync(method, request, requestCodec, streams, control, cancellationToken)
            : InvokeOneWayPlainCoreAsync(method, request, requestCodec, control, cancellationToken);

    private async ValueTask InvokeOneWayPlainCoreAsync<TRequest>(
        RpcMethodDescriptor method,
        TRequest request,
        IRpcCodec<TRequest> requestCodec,
        ResolvedCallControl control,
        CancellationToken cancellationToken)
    {
        var outcome = _endpointAdmissionPolicy is null ? null : new AttemptOutcomeState(this, method);
        if (outcome is null)
            SharpLinkTelemetry.RecordClientAttempt();

        long routingId = 0;
        ClientConnection? connection = null;
        var reservationOwned = false;
        try
        {
            EnsureLogicalCallProgress(control);
            var target = GetReadyStaticTarget(control.BindingSite, method, outcome);
            connection = target.Connection;
            routingId = unchecked((long)target.Token);
            reservationOwned = true;
            EnsureLogicalCallProgress(control);
        }
        catch (Exception exception)
        {
            if (reservationOwned)
                connection!.ReleaseCallAdmissionReservation();
            exception = ArbitrateLogicalCallFailure(control, exception);
            outcome?.CompleteLocalFailure(exception);
            throw exception;
        }

        var flags = ProtocolV2FrameFlags.OneWay;
        if (control.Deadline.HasValue)
            flags |= ProtocolV2FrameFlags.Cancellable;

        long requestId;
        try
        {
            EnsureLogicalCallProgress(control);
            requestId = connection!.PendingCalls.AllocateRequestId();
        }
        catch (Exception exception)
        {
            if (reservationOwned)
            {
                connection!.ReleaseCallAdmissionReservation();
                reservationOwned = false;
            }
            exception = ArbitrateLogicalCallFailure(control, exception);
            outcome?.CompleteLocalFailure(exception);
            throw exception;
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureLogicalCallProgress(control);
        }
        catch
        {
            if (reservationOwned)
            {
                connection!.ReleaseCallAdmissionReservation();
                reservationOwned = false;
            }
            throw;
        }

        var began = connection!.TryBeginUntrackedCall();
        reservationOwned = false;
        if (!began)
        {
            Exception exception = new SharpLinkException(
                SharpLinkErrorCode.Unavailable,
                "The selected connection is draining.");
            exception = ArbitrateLogicalCallFailure(control, exception);
            outcome?.CompleteWithoutPending(
                exception is SharpLinkException { Code: SharpLinkErrorCode.DeadlineExceeded }
                    ? PendingCallCompletionReason.DeadlineExceeded
                    : PendingCallCompletionReason.ConnectionClosed,
                exception);
            throw exception;
        }

        try
        {
            try
            {
                var emission = SendRpcCall(
                    connection.Session,
                    routingId,
                    method.MethodId,
                    requestId,
                    flags,
                    request,
                    requestCodec,
                    control.Deadline,
                    control.Metadata,
                    observeEmission: control.Deadline.HasValue,
                    cancellationToken: CancellationToken.None,
                    publicationTable: null);
                if (control.Deadline.HasValue)
                {
                    // Plain OneWay has no pending owner to enforce an emission deadline.
                    await AwaitPlainOneWayEmissionOrDeadlineAsync(
                        emission, connection, requestId, control).ConfigureAwait(false);
                }
                else
                {
                    await emission.ConfigureAwait(false);
                }
                outcome?.CompleteWithoutPending(PendingCallCompletionReason.LocalStreamComplete);
            }
            catch (Exception exception)
            {
                exception = ArbitrateLogicalCallFailure(control, exception);
                outcome?.CompleteWithoutPending(
                    exception is SharpLinkException { Code: SharpLinkErrorCode.DeadlineExceeded }
                        ? PendingCallCompletionReason.DeadlineExceeded
                        : PendingCallCompletionReason.SendFailure,
                    exception);
                throw exception;
            }
        }
        finally
        {
            connection.EndUntrackedCall();
        }
    }

    private async ValueTask InvokeOneWayStreamingCoreAsync<TRequest, TStreams>(
        RpcMethodDescriptor method,
        TRequest request,
        IRpcCodec<TRequest> requestCodec,
        TStreams streams,
        ResolvedCallControl control,
        CancellationToken cancellationToken)
        where TStreams : struct, IRpcClientStreamWriter
    {
        using var modulePhysicalOwner = SharpLinkClientModuleCallContext.Current?.Retain();
        PendingStreamAdmission? physicalProducer = null;
        long routingId = 0;
        var outcome = _endpointAdmissionPolicy is null ? null : new AttemptOutcomeState(this, method);
        if (outcome is null)
            SharpLinkTelemetry.RecordClientAttempt();

        ClientConnection? connection = null;
        var reservationOwned = false;
        try
        {
            EnsureLogicalCallProgress(control);
            var target = GetReadyStaticTarget(control.BindingSite, method, outcome);
            connection = target.Connection; routingId = unchecked((long)target.Token);
            reservationOwned = true;
            EnsureLogicalCallProgress(control);
        }
        catch (Exception exception)
        {
            if (reservationOwned)
                connection!.ReleaseCallAdmissionReservation();
            exception = ArbitrateLogicalCallFailure(control, exception);
            outcome?.CompleteLocalFailure(exception);
            throw exception;
        }

        var flags = ProtocolV2FrameFlags.OneWay;
        if (control.Deadline.HasValue || cancellationToken.CanBeCanceled)
            flags |= ProtocolV2FrameFlags.Cancellable;

        PendingRequestLease<RpcEmptyRequest> oneWayStreamLease;
        long requestId;
        try
        {
            EnsureLogicalCallProgress(control);
            oneWayStreamLease = connection!.PendingCalls.RegisterOneWayClientStream(
                control.Deadline, cancellationToken, outcome);
            reservationOwned = false;
            requestId = oneWayStreamLease.Id;
        }
        catch (Exception exception)
        {
            if (reservationOwned)
            {
                connection!.ReleaseCallAdmissionReservation();
                reservationOwned = false;
            }
            exception = ArbitrateLogicalCallFailure(control, exception);
            outcome?.CompleteLocalFailure(exception);
            throw exception;
        }

        try
        {
            if (connection!.PendingCalls.Contains(requestId))
            {
                physicalProducer = connection.PendingCalls.CaptureStreamAdmission(requestId, producer: true);
                var streamCancellationToken = physicalProducer.Token;
                var emission = SendRpcCall(
                    connection.Session,
                    routingId,
                    method.MethodId,
                    requestId,
                    flags,
                    request,
                    requestCodec,
                    control.Deadline,
                    control.Metadata,
                    observeEmission: control.Deadline.HasValue,
                    // The pending call owns producer cancellation and request publication.
                    cancellationToken: streamCancellationToken,
                    publicationTable: connection.PendingCalls
                    , oneWayInputCount: method.ClientStreamCount
                    );
                await emission.ConfigureAwait(false);
                await streams.WriteAsync(connection, requestId, streamCancellationToken).ConfigureAwait(false);
                connection.PendingCalls.TryComplete(requestId, PendingCallCompletionReason.LocalStreamComplete);
            }
        }
        catch (Exception exception)
        {
            // Preserve the pending owner's terminal arbitration and observe its lease once.
            connection.PendingCalls.TryComplete(requestId, PendingCallCompletionReason.SendFailure, exception);
        }

        finally { physicalProducer?.ReleaseProducer(); }

        // Registration may already have completed and removed the pending entry. Its operation
        // still owns that terminal result and must be consumed exactly once to return it to the pool.
        _ = await oneWayStreamLease.Operation.AsValueTask().ConfigureAwait(false);
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
        ClientConnection? connection = null;
        var reservationOwned = false;
        long routingId = 0;
        PendingStreamAdmission? streamAdmission = null;
        long requestId;
        RpcRequestOperation<TResponse> operation;
        try
        {
            EnsureLogicalCallProgress(control);
            var target = GetReadyStaticTarget(control.BindingSite, method, outcome);
            connection = target.Connection; routingId = unchecked((long)target.Token);
            reservationOwned = true;
            EnsureLogicalCallProgress(control);
            operation = connection.PendingCalls.Rent(
                responseCodec,
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
        SharpLinkClientStreamPhysicalLease producerLease = default;
        try
        {
            if (connection.PendingCalls.Contains(requestId))
            {
                streamAdmission = connection.PendingCalls.CaptureStreamAdmission(requestId, producer: true);
                var streamCancellationToken = streamAdmission.Token;
                producerLease = moduleProducerLifetime?.TakeLease() ?? default;
                await SendRpcCall(
                    connection.Session,
                    routingId,
                    method.MethodId,
                    requestId,
                    flags,
                    request,
                    requestCodec,
                    control.Deadline,
                    control.Metadata,
                    observeEmission: control.Deadline.HasValue,
                    // Same ownership rule as the one-way client-stream shape: this call is owned by
                    // its pending entry, so the emission wait has to observe the token that entry
                    // cancels when it reaches a terminal reason.
                    cancellationToken: streamCancellationToken,
                    publicationTable: connection.PendingCalls).ConfigureAwait(false);
                var capturedAdmission = streamAdmission!; var capturedLease = producerLease;
                _ = TrackBoundStreamWork(() => RunAcceptedClientStreamsAsync(connection, streams, requestId, capturedAdmission, capturedLease), "ClientStreamingProducer");
                streamAdmission = null; producerLease = default;
            }
        }
        catch (Exception exception)
        {
            connection.PendingCalls.TryComplete(requestId, PendingCallCompletionReason.SendFailure, exception);
        }
        finally
        {
            producerLease.Dispose();
            streamAdmission?.ReleaseProducer();
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
            connection.PendingCalls.TryComplete(requestId, PendingCallCompletionReason.SendFailure, exception);
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
        CancellationToken cancellationToken
        , long registrationLease, SharpLinkClientModuleCallOwner? registrationOwner
        )
    {
        ClientConnection? connection = null;
        var requestId = 0L;
        try
        {
            if (BeforeBoundStreamRegistrationTestHook is { } registrationHook) await registrationHook("ServerStreamingInvoker").ConfigureAwait(false);
            var registration = await PrepareGeneratedServerStreamAsync(
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
                    registration.BindingToken,
                    method.MethodId,
                    requestId,
                    ProtocolV2FrameFlags.Cancellable,
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
            CompleteFailedGeneratedStream(dispatcher, connection, requestId, exception);
        }
        finally
        {
            registrationOwner?.Dispose();
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
        CancellationToken cancellationToken
        , long registrationLease, SharpLinkClientStreamPhysicalLease producerLease, SharpLinkClientModuleCallOwner? registrationOwner
        )
        where TStreams : struct, IRpcClientStreamWriter
    {
        ClientConnection? connection = null;
        var requestId = 0L;
        PendingStreamAdmission? streamAdmission = null;
        try
        {
            if (BeforeBoundStreamRegistrationTestHook is { } registrationHook) await registrationHook("DuplexStreamingInvoker").ConfigureAwait(false);
            var registration = await PrepareGeneratedServerStreamAsync(
                dispatcher,
                PendingCallKind.DuplexStreaming,
                method,
                control,
                cancellationToken).ConfigureAwait(false);
            connection = registration.Connection;
            requestId = registration.RequestId;
            streamAdmission = connection.PendingCalls.CaptureStreamAdmission(requestId, producer: true);
            var streamCancellationToken = streamAdmission.Token;
            await SendRpcCall(
                connection.Session,
                registration.BindingToken,
                method.MethodId,
                requestId,
                ProtocolV2FrameFlags.Cancellable,
                request,
                requestCodec,
                control.Deadline,
                control.Metadata,
                observeEmission: control.Deadline.HasValue,
                // This head Request is owned by its pending entry, so the emission wait has to
                // observe the token that entry cancels: the deadline that ends the call also has
                // to release the producer side from a stalled transport write.
                cancellationToken: streamCancellationToken,
                publicationTable: connection.PendingCalls).ConfigureAwait(false);
            registrationOwner?.Dispose(); registrationOwner = null;
            if (await streamAdmission.Acceptance.ConfigureAwait(false))
            {
                if (BeforeStreamProducerClaimTestHook is { } hook) await hook(requestId, streamAdmission.Token).ConfigureAwait(false);
                if (connection.PendingCalls.TryStartStreamProducer(requestId, streamAdmission))
                    await streams.WriteAsync(connection, requestId, streamAdmission.Token).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            CompleteFailedGeneratedStream(dispatcher, connection, requestId, exception);
        }
        finally
        {
            producerLease.Dispose();
            registrationOwner?.Dispose();
            streamAdmission?.ReleaseProducer();
            dispatcher.ReleaseRegistrationRetention(registrationLease);
        }
    }

    private ValueTask<StreamCallRegistration> PrepareGeneratedServerStreamAsync<TResponse>(
        PooledAsyncStreamDispatcher<TResponse> dispatcher,
        PendingCallKind kind,
        RpcMethodDescriptor method,
        ResolvedCallControl control,
        CancellationToken cancellationToken,
        bool captureTerminalSignal = false)
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
            var target = GetReadyStaticTarget(control.BindingSite, method, outcome);
            connection = target.Connection;
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
            return ValueTask.FromResult(new StreamCallRegistration(connection, requestId, terminalSignal, unchecked((long)target.Token)));
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
                    exception is SharpLinkException { Code: SharpLinkErrorCode.DeadlineExceeded } ? null : exception);
            }
            else
            {
                outcome?.CompleteLocalFailure(exception);
            }
            terminalSignal?.StopObservingTerminal();
            throw exception;
        }
    }

    private void CompleteFailedGeneratedStream<TResponse>(
        PooledAsyncStreamDispatcher<TResponse> dispatcher,
        ClientConnection? connection,
        long requestId,
        Exception exception)
    {
        if (connection is not null && requestId != 0)
            connection.PendingCalls.TryComplete(requestId, PendingCallCompletionReason.SendFailure, exception);
        else
            dispatcher.Complete(exception);
    }

    private readonly record struct StreamCallRegistration(
        ClientConnection Connection,
        long RequestId,
        PendingCallTerminalSignal? TerminalSignal
        , long BindingToken
        );
}
