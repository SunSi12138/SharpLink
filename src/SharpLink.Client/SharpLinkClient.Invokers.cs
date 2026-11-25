namespace SharpLink.Client;

internal sealed partial class SharpLinkClient
{
    internal ValueTask<TResponse> InvokeUnary<TRequest, TResponse>(RpcMethodDescriptor method,
        in TRequest request, IRpcCodec<TRequest> requestCodec, IRpcCodec<TResponse> responseCodec,
        SharpLinkMetadata? metadata, CancellationToken cancellationToken, NStudyPreparedSite prepared)
    {
        ArgumentNullException.ThrowIfNull(requestCodec);
        ArgumentNullException.ThrowIfNull(responseCodec);
        cancellationToken.ThrowIfCancellationRequested();
        RequireNStudyShape(method);
        var interceptors = CaptureInterceptorGenerationForInvocation();
        var control = ResolveCallControlForInvocation(
            method, metadata, includeClientDefault: true, interceptors) with { PreparedSite = prepared };
        var telemetryEnabled = SharpLinkTelemetry.ClientCallsEnabled;
        Interlocked.Increment(ref _activeLogicalInvocations);
        // A bare one-attempt Unary has no logical work after its pending result. Keep that
        // result's asynchronous scheduling and release its logical count at completion.
        if (!telemetryEnabled && interceptors.Count == 0 && _endpointAdmissionPolicy is null && !method.IsIdempotent)
            return InvokeUnaryCoreAsync(method, request, requestCodec, responseCodec, control, cancellationToken,
                completeLogicalWithResult: true);
        try
        {
            ValueTask<TResponse> invocation;
            if (telemetryEnabled)
                invocation = InvokeUnaryWithTelemetryAsync(method, request, requestCodec, responseCodec, interceptors, control, cancellationToken);
            else if (interceptors.Count != 0)
                invocation = InvokeUnaryInterceptedAsync(method, request, requestCodec, responseCodec, interceptors, control, cancellationToken);
            else
                invocation = InvokeUnaryWithOptionalRetryAsync(method, request, requestCodec, responseCodec, control, cancellationToken);
            return CompleteLogicalInvocation(invocation);
        }
        catch
        {
            Interlocked.Decrement(ref _activeLogicalInvocations);
            throw;
        }
    }

    internal ValueTask InvokeOneWay<TRequest, TStreams>(RpcMethodDescriptor method,
        in TRequest request, IRpcCodec<TRequest> requestCodec, in TStreams streams, SharpLinkMetadata? metadata,
        CancellationToken cancellationToken, NStudyPreparedSite prepared)
        where TStreams : struct, IRpcClientStreamWriter
    {
        ArgumentNullException.ThrowIfNull(requestCodec);
        cancellationToken.ThrowIfCancellationRequested();
        RequireNStudyShape(method);
        var interceptors = CaptureInterceptorGenerationForInvocation();
        var control = ResolveCallControlForInvocation(
            method, metadata, includeClientDefault: false, interceptors) with { PreparedSite = prepared };
        Interlocked.Increment(ref _activeLogicalInvocations);
        try
        {
            ValueTask invocation;
            if (SharpLinkTelemetry.ClientCallsEnabled)
                invocation = InvokeOneWayWithTelemetryAsync(method, request, requestCodec, streams, interceptors, control, cancellationToken);
            else if (interceptors.Count != 0)
                invocation = InvokeOneWayInterceptedAsync(method, request, requestCodec, streams, interceptors, control, cancellationToken);
            else
                invocation = InvokeOneWayCoreAsync(method, request, requestCodec, streams, control, cancellationToken);
            return CompleteLogicalInvocation(invocation);
        }
        catch
        {
            Interlocked.Decrement(ref _activeLogicalInvocations);
            throw;
        }
    }

    internal ValueTask<TResponse> InvokeClientStreaming<TRequest, TResponse, TStreams>(RpcMethodDescriptor method,
        in TRequest request, IRpcCodec<TRequest> requestCodec, IRpcCodec<TResponse> responseCodec, in TStreams streams,
        SharpLinkMetadata? metadata, CancellationToken cancellationToken, NStudyPreparedSite prepared,
        SharpLinkClientStreamModuleLeaseOwner? producerLifetime)
        where TStreams : struct, IRpcClientStreamWriter
    {
        ArgumentNullException.ThrowIfNull(requestCodec);
        ArgumentNullException.ThrowIfNull(responseCodec);
        cancellationToken.ThrowIfCancellationRequested();
        RequireNStudyShape(method);
        var interceptors = CaptureInterceptorGenerationForInvocation();
        var control = ResolveCallControlForInvocation(
            method, metadata, includeClientDefault: false, interceptors)
            with { PreparedSite = prepared, InputLifetime = producerLifetime };
        Interlocked.Increment(ref _activeLogicalInvocations);
        try
        {
            ValueTask<TResponse> invocation;
            if (SharpLinkTelemetry.ClientCallsEnabled)
                invocation = InvokeClientStreamingWithTelemetryAsync(method, request, requestCodec, responseCodec, streams, interceptors, control, cancellationToken);
            else if (interceptors.Count != 0)
                invocation = InvokeClientStreamingInterceptedAsync(method, request, requestCodec, responseCodec, streams, interceptors, control, cancellationToken);
            else
                invocation = InvokeClientStreamingCoreAsync(method, request, requestCodec, responseCodec, streams, control, cancellationToken);
            return CompleteLogicalInvocation(invocation);
        }
        catch
        {
            Interlocked.Decrement(ref _activeLogicalInvocations);
            throw;
        }
    }

    internal IAsyncEnumerable<TResponse> InvokeServerStreamingResolved<TRequest, TResponse>(
        RpcMethodDescriptor method,
        in TRequest request,
        IRpcCodec<TRequest> requestCodec,
        IRpcCodec<TResponse> responseCodec,
        ClientInterceptorGeneration interceptors,
        ResolvedCallControl control,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requestCodec);
        ArgumentNullException.ThrowIfNull(responseCodec);
        ArgumentNullException.ThrowIfNull(interceptors);
        EnsureLogicalCallProgress(control);
        RequireNStudyShape(method);
        Interlocked.Increment(ref _activeLogicalInvocations);
        try
        {
            IAsyncEnumerable<TResponse> invocation;
            if (SharpLinkTelemetry.ClientCallsEnabled)
                invocation = InvokeServerStreamingWithTelemetry(method, request, requestCodec, responseCodec, interceptors, control, cancellationToken);
            else if (interceptors.Count != 0)
                invocation = InvokeServerStreamingIntercepted(method, request, requestCodec, responseCodec, interceptors, control, cancellationToken);
            else
                invocation = InvokeServerStreamingCore(method, request, requestCodec, responseCodec, control, cancellationToken);
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
        ResolvedCallControl control,
        CancellationToken cancellationToken)
    {
        var owner = control.OutputLifetime;
        var dispatcher = RentGeneratedOutputDispatcher(cancellationToken, responseCodec, method.ResponseNullable, owner,
            out var close);
        try
        {
            TrackFrameworkTask(
                StartServerStreamingInvokerAsync(dispatcher, method, request, requestCodec, control, cancellationToken),
                "ServerStreamingInvoker");
        }
        finally
        {
            if (close is not null)
                owner!.AttachDispatcher(close);
        }
        return dispatcher;
    }

    private static PooledAsyncStreamDispatcher<T> RentGeneratedOutputDispatcher<T>(CancellationToken cancellationToken,
        IRpcCodec<T> responseCodec, bool payloadNullable, NStudyServerOutputLifetime? owner, out Func<ValueTask>? close)
    {
        owner?.ClaimDispatcher();
        try
        {
            var dispatcher = PooledAsyncStreamDispatcher<T>.Rent(cancellationToken, responseCodec, payloadNullable, owner);
            close = owner is null ? null : dispatcher.CapturePhysicalLeaseClose();
            return dispatcher;
        }
        catch { owner?.Dispose(); throw; }
    }

    internal IAsyncEnumerable<TResponse> InvokeDuplexStreamingResolved<TRequest, TResponse, TStreams>(
        RpcMethodDescriptor method,
        in TRequest request,
        IRpcCodec<TRequest> requestCodec,
        IRpcCodec<TResponse> responseCodec,
        in TStreams streams,
        ClientInterceptorGeneration interceptors,
        ResolvedCallControl control,
        CancellationToken cancellationToken = default)
        where TStreams : struct, IRpcClientStreamWriter
    {
        ArgumentNullException.ThrowIfNull(requestCodec);
        ArgumentNullException.ThrowIfNull(responseCodec);
        ArgumentNullException.ThrowIfNull(interceptors);
        EnsureLogicalCallProgress(control);
        RequireNStudyShape(method);
        Interlocked.Increment(ref _activeLogicalInvocations);
        try
        {
            IAsyncEnumerable<TResponse> invocation;
            if (SharpLinkTelemetry.ClientCallsEnabled)
                invocation = InvokeDuplexStreamingWithTelemetry(method, request, requestCodec, responseCodec, streams, interceptors, control, cancellationToken);
            else if (interceptors.Count != 0)
                invocation = InvokeDuplexStreamingIntercepted(method, request, requestCodec, responseCodec, streams, interceptors, control, cancellationToken);
            else
                invocation = InvokeDuplexStreamingCore(method, request, requestCodec, responseCodec, streams, control, cancellationToken);
            return CompleteLogicalInvocation(invocation);
        }
        catch
        {
            Interlocked.Decrement(ref _activeLogicalInvocations);
            throw;
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
        var owner = control.OutputLifetime;
        var dispatcher = RentGeneratedOutputDispatcher(cancellationToken, responseCodec, method.ResponseNullable, owner,
            out var close);
        try
        {
            TrackFrameworkTask(
                StartDuplexStreamingInvokerAsync(dispatcher, method, request, requestCodec, streams, control, cancellationToken),
                "DuplexStreamingInvoker");
        }
        finally
        {
            if (close is not null)
                owner!.AttachDispatcher(close);
        }
        return dispatcher;
    }

    private ValueTask<TResponse> InvokeUnaryCoreAsync<TRequest, TResponse>(
        RpcMethodDescriptor method,
        TRequest request,
        IRpcCodec<TRequest> requestCodec,
        IRpcCodec<TResponse> responseCodec,
        ResolvedCallControl control,
        CancellationToken cancellationToken,
        bool completeLogicalWithResult = false)
    {
        var logicalTransferred = false;
        try
        {
            var outcome = _endpointAdmissionPolicy is null ? null : new AttemptOutcomeState(this, method);
            if (outcome is null)
                SharpLinkTelemetry.RecordClientAttempt();
            ClientConnection? connection = null;
            var reservationOwned = false;
            try
            {
                // Connection selection is the first step that can block, so the pre-registration
                // checkpoint below is the first place a fresh sample carries new information.
                connection = GetReadyConnection(method, retrySelection: null, outcome, control.SiteForDispatch);
                reservationOwned = true;
                EnsureLogicalCallProgress(control);
                logicalTransferred = completeLogicalWithResult;
                var operation = connection.PendingCalls.Rent(
                    responseCodec,
                    PendingCallKind.Unary,
                    control.Deadline,
                    cancellationToken,
                    out var requestId,
                    outcome,
                    hasResponsePayload: method.HasResponsePayload,
                    responseNullable: method.ResponseNullable,
                    logicalOwner: completeLogicalWithResult ? this : null);
                reservationOwned = false;
                return StartUnaryCall(
                    connection,
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
        finally
        {
            if (completeLogicalWithResult && !logicalTransferred)
                ReleaseDirectLogicalInvocation();
        }
    }

    private ValueTask<TResponse> StartUnaryCall<TRequest, TResponse>(
        ClientConnection connection,
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
                    control.SiteForDispatch.RequireRoute(connection),
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

        ClientConnection? connection = null;
        var reservationOwned = false;
        try
        {
            EnsureLogicalCallProgress(control);
            connection = GetReadyConnection(method, retrySelection: null, outcome, control.SiteForDispatch);
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
                    control.SiteForDispatch.RequireRoute(connection),
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
        var outcome = _endpointAdmissionPolicy is null ? null : new AttemptOutcomeState(this, method);
        if (outcome is null)
            SharpLinkTelemetry.RecordClientAttempt();

        ClientConnection? connection = null;
        var reservationOwned = false;
        try
        {
            EnsureLogicalCallProgress(control);
            connection = GetReadyConnection(method, retrySelection: null, outcome, control.SiteForDispatch);
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

        var table = connection!.PendingCalls;
        NStudyProducerAdmission? admission = null;
        var sentEof = false;
        try
        {
            admission = table.RetainNStudyProducer(requestId);
            if (admission is not null)
            {
                var streamCancellationToken = admission.Token;
                var emission = SendRpcCall(
                    connection.Session,
                    control.SiteForDispatch.RequireRoute(connection),
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
                    publicationTable: table,
                    oneWayInputCount: method.ClientStreamCount);
                await emission.ConfigureAwait(false);
                // No receipt is involved: the exact local head append is sufficient.
                if (table.TryStartNStudyProducer(requestId, admission))
                {
                    await streams.WriteAsync(connection, requestId, streamCancellationToken).ConfigureAwait(false);
                    sentEof = true;
                    table.TryComplete(requestId, PendingCallCompletionReason.LocalStreamComplete);
                }
            }
        }
        catch (Exception exception)
        {
            // Preserve the pending owner's terminal arbitration and observe its lease once.
            table.TryComplete(requestId, PendingCallCompletionReason.SendFailure, exception);
        }
        finally
        {
            if (admission is not null)
            {
                try
                {
                    if (!sentEof && admission.RequestPublished)
                        EndNStudyOneWayInput(connection.Session, requestId, method.ClientStreamCount);
                }
                finally { table.ReleaseNStudyProducer(admission); }
            }
        }

        // Registration may already have completed and removed the pending entry. Its operation
        // still owns that terminal result and must be consumed exactly once to return it to the pool.
        _ = await oneWayStreamLease.Operation.AsValueTask().ConfigureAwait(false);
    }

    private void EndNStudyOneWayInput(RpcSession session, long requestId, int count)
    {
        if (!session.IsConnected)
            return;
        var end = new SharpLinkException(SharpLinkErrorCode.Cancelled, "The OneWay input producer stopped.");
        try
        {
            // Physical writer exit precedes this FIFO tail. Its terminal frames
            // retire discard routes even when the caller's Cancel arrived earlier.
            for (var index = 1; index <= count; index++)
                session.SendClientStreamError(requestId, (ushort)index, end, default,
                    _runtimeContext.TimeProvider, CancellationToken.None);
        }
        catch (Exception exception)
        {
            // Incomplete terminal publication cannot leave remote routes waiting
            // for input. Fail only the affected physical peer.
            session.NotifyDisconnected(exception);
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
        var moduleProducerLifetime = control.InputLifetime;
        var outcome = _endpointAdmissionPolicy is null ? null : new AttemptOutcomeState(this, method);
        if (outcome is null)
            SharpLinkTelemetry.RecordClientAttempt();
        ClientConnection? connection = null;
        var reservationOwned = false;
        long requestId;
        RpcRequestOperation<TResponse> operation;
        try
        {
            EnsureLogicalCallProgress(control);
            connection = GetReadyConnection(method, retrySelection: null, outcome, control.SiteForDispatch);
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
        var table = connection!.PendingCalls;
        NStudyProducerAdmission? admission = null;
        SharpLinkDynamicModuleLease producerLease = default;
        try
        {
            admission = table.RetainNStudyProducer(requestId);
            if (admission is not null)
            {
                var streamCancellationToken = admission.Token;
                producerLease = moduleProducerLifetime?.TakeLease() ?? default;
                await SendRpcCall(
                    connection.Session,
                    control.SiteForDispatch.RequireRoute(connection),
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
                if (await admission.Acceptance.ConfigureAwait(false) && table.TryStartNStudyProducer(requestId, admission))
                {
                    var producerTask = RunGeneratedClientStreamsAsync(connection, streams, requestId, admission, producerLease);
                    producerLease = default;
                    admission = null;
                    TrackFrameworkTask(producerTask, "ClientStreamingProducer");
                }
            }
        }
        catch (Exception exception)
        {
            table.TryComplete(requestId, PendingCallCompletionReason.SendFailure, exception);
        }
        finally
        {
            try { producerLease.Dispose(); }
            finally
            {
                if (admission is not null)
                    table.ReleaseNStudyProducer(admission);
            }
        }

        return await operation.AsValueTask().ConfigureAwait(false);
    }

    private async Task RunGeneratedClientStreamsAsync<TStreams>(
        ClientConnection connection,
        TStreams streams,
        long requestId,
        NStudyProducerAdmission admission,
        SharpLinkDynamicModuleLease producerLease)
        where TStreams : struct, IRpcClientStreamWriter
    {
        try
        {
            await streams.WriteAsync(connection, requestId, admission.Token).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            connection.PendingCalls.TryComplete(requestId, PendingCallCompletionReason.SendFailure, exception);
        }
        finally
        {
            try { producerLease.Dispose(); }
            finally { connection.PendingCalls.ReleaseNStudyProducer(admission); }
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
                cancellationToken,
                captureTerminalSignal: control.Deadline.HasValue).ConfigureAwait(false);
            connection = registration.Connection;
            requestId = registration.RequestId;
            try
            {
                await SendRpcCall(
                    connection.Session,
                    control.SiteForDispatch.RequireRoute(connection),
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
        var moduleProducerLifetime = control.InputLifetime;
        SharpLinkDynamicModuleLease producerLease = default;
        NStudyProducerAdmission? admission = null;
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
            admission = connection.PendingCalls.RetainNStudyProducer(requestId);
            if (admission is null)
                return;
            var streamCancellationToken = admission.Token;
            producerLease = moduleProducerLifetime?.TakeLease() ?? default;
            await SendRpcCall(
                connection.Session,
                control.SiteForDispatch.RequireRoute(connection),
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
            if (await admission.Acceptance.ConfigureAwait(false) &&
                connection.PendingCalls.TryStartNStudyProducer(requestId, admission))
            {
                await streams.WriteAsync(connection, requestId, streamCancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            CompleteFailedGeneratedStream(dispatcher, connection, requestId, exception);
        }
        finally
        {
            try { producerLease.Dispose(); }
            finally
            {
                try
                {
                    if (admission is not null)
                        connection!.PendingCalls.ReleaseNStudyProducer(admission);
                }
                finally { dispatcher.ReleaseRegistrationRetention(registrationLease); }
            }
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
            connection = GetReadyConnection(method, retrySelection: null, outcome, control.SiteForDispatch);
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
        PendingCallTerminalSignal? TerminalSignal);
}
