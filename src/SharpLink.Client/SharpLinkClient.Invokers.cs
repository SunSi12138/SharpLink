namespace SharpLink.Client;

internal sealed partial class SharpLinkClient
{
    private static Exception CompleteFailedPending(ClientConnection connection, long requestId, Exception failure)
    {
        try { connection.PendingCalls.TryComplete(requestId, PendingCallCompletionReason.SendFailure, failure); }
        catch (Exception cleanup) { failure = RpcPublicationErrors.Combine(failure, cleanup)!; }
        return failure;
    }

    // Selected producing failures already own exact pending identity. Do not consult an
    // application clock again while resolving a failed serializer/setup/head publication.
    internal static Exception CompleteFailedProducingPending(ClientConnection connection,
        ClientStreamPublicationGroup group, Exception failure)
    {
        var reason = failure is SharpLinkException { Code: SharpLinkErrorCode.DeadlineExceeded }
            ? PendingCallCompletionReason.DeadlineExceeded : PendingCallCompletionReason.SendFailure;
        try { connection.PendingCalls.CompleteProducingSetupFailure(group, failure, reason); }
        catch (Exception cleanup) { failure = ClientStreamPublicationGroup.AddPhysical(failure, cleanup)!; }
        return failure;
    }

    private static async ValueTask<T> ObserveFailedOperationAsync<T>(RpcRequestOperation<T> operation, Exception failure)
    {
        try { _ = await operation.AsValueTask().ConfigureAwait(false); }
        catch (Exception terminal) { failure = RpcPublicationErrors.Combine(failure, terminal)!; }
        return await ValueTask.FromException<T>(failure).ConfigureAwait(false);
    }

    internal sealed partial class ContractCallSite
    {
        public ValueTask<TResponse> InvokeUnaryAsync<TRequest, TResponse>(
            RpcMethodDescriptor method,
            in TRequest request,
            IRpcCodec<TRequest> requestCodec,
            IRpcCodec<TResponse> responseCodec,
            SharpLinkMetadata? metadata,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(requestCodec);
            ArgumentNullException.ThrowIfNull(responseCodec);
            cancellationToken.ThrowIfCancellationRequested();
            var interceptors = CaptureInterceptorGenerationForInvocation();
            var control = ResolveCallControlForInvocation(
                method, metadata, includeClientDefault: true, interceptors);
            ValidateInvocationIdentity(method, RpcMethodKind.Unary);
            Interlocked.Increment(ref _owner._activeLogicalInvocations);
            try
            {
                ValueTask<TResponse> invocation;
                if (SharpLinkTelemetry.ClientCallsEnabled)
                    invocation = InvokeUnaryWithTelemetryAsync(method, request, requestCodec, responseCodec, interceptors, control, cancellationToken);
                else if (interceptors.Count != 0)
                    invocation = InvokeUnaryInterceptedAsync(method, request, requestCodec, responseCodec, interceptors, control, cancellationToken);
                else
                    invocation = InvokeUnaryWithOptionalRetryAsync(method, request, requestCodec, responseCodec, control, cancellationToken);
                return CompleteLogicalInvocation(invocation);
            }
            catch
            {
                Interlocked.Decrement(ref _owner._activeLogicalInvocations);
                throw;
            }
        }

        public ValueTask InvokeOneWayAsync<TRequest, TStreams>(
            RpcMethodDescriptor method,
            in TRequest request,
            IRpcCodec<TRequest> requestCodec,
            in TStreams streams,
            SharpLinkMetadata? metadata,
            CancellationToken cancellationToken = default)
            where TStreams : struct, IRpcClientStreamWriter
        {
            ArgumentNullException.ThrowIfNull(requestCodec);
            cancellationToken.ThrowIfCancellationRequested();
            var interceptors = CaptureInterceptorGenerationForInvocation();
            var control = ResolveCallControlForInvocation(
                method, metadata, includeClientDefault: false, interceptors);
            ValidateInvocationIdentity(method, RpcMethodKind.OneWay);
            Interlocked.Increment(ref _owner._activeLogicalInvocations);
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
                Interlocked.Decrement(ref _owner._activeLogicalInvocations);
                throw;
            }
        }

        public ValueTask<TResponse> InvokeClientStreamingAsync<TRequest, TResponse, TStreams>(
            RpcMethodDescriptor method,
            in TRequest request,
            IRpcCodec<TRequest> requestCodec,
            IRpcCodec<TResponse> responseCodec,
            in TStreams streams,
            SharpLinkMetadata? metadata,
            CancellationToken cancellationToken = default)
            where TStreams : struct, IRpcClientStreamWriter
        {
            ArgumentNullException.ThrowIfNull(requestCodec);
            ArgumentNullException.ThrowIfNull(responseCodec);
            cancellationToken.ThrowIfCancellationRequested();
            var interceptors = CaptureInterceptorGenerationForInvocation();
            var control = ResolveCallControlForInvocation(
                method, metadata, includeClientDefault: false, interceptors);
            ValidateInvocationIdentity(method, RpcMethodKind.ClientStreaming);
            Interlocked.Increment(ref _owner._activeLogicalInvocations);
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
                Interlocked.Decrement(ref _owner._activeLogicalInvocations);
                throw;
            }
        }

        public IAsyncEnumerable<TResponse> InvokeServerStreamingAsync<TRequest, TResponse>(
            RpcMethodDescriptor method,
            in TRequest request,
            IRpcCodec<TRequest> requestCodec,
            IRpcCodec<TResponse> responseCodec,
            SharpLinkMetadata? metadata,
            CancellationToken cancellationToken = default)
        {
            var interceptors = CaptureInterceptorGenerationForInvocation();
            var control = ResolveCallControlForInvocation(
                method, metadata, includeClientDefault: false, interceptors);
            return InvokeServerStreamingResolved(
                method, request, requestCodec, responseCodec, interceptors, control, cancellationToken);
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
            ValidateInvocationIdentity(method, RpcMethodKind.ServerStreaming);
            Interlocked.Increment(ref _owner._activeLogicalInvocations);
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
                Interlocked.Decrement(ref _owner._activeLogicalInvocations);
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
            var dispatcher = PooledAsyncStreamDispatcher<TResponse>.Rent(cancellationToken, responseCodec, method.ResponseNullable);
            _owner.TrackFrameworkTask(
                StartServerStreamingInvokerAsync(dispatcher, method, request, requestCodec, control, cancellationToken),
                "ServerStreamingInvoker");
            return dispatcher;
        }

        public IAsyncEnumerable<TResponse> InvokeDuplexStreamingAsync<TRequest, TResponse, TStreams>(
            RpcMethodDescriptor method,
            in TRequest request,
            IRpcCodec<TRequest> requestCodec,
            IRpcCodec<TResponse> responseCodec,
            in TStreams streams,
            SharpLinkMetadata? metadata,
            CancellationToken cancellationToken = default)
            where TStreams : struct, IRpcClientStreamWriter
        {
            var interceptors = CaptureInterceptorGenerationForInvocation();
            var control = ResolveCallControlForInvocation(
                method, metadata, includeClientDefault: false, interceptors);
            return InvokeDuplexStreamingResolved(
                method, request, requestCodec, responseCodec, streams, interceptors, control, cancellationToken);
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
            ValidateInvocationIdentity(method, RpcMethodKind.DuplexStreaming);
            Interlocked.Increment(ref _owner._activeLogicalInvocations);
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
                Interlocked.Decrement(ref _owner._activeLogicalInvocations);
                throw;
            }
        }

        private ValueTask<T> CompleteLogicalInvocation<T>(ValueTask<T> invocation)
        {
            if (invocation.IsCompleted)
            {
                Interlocked.Decrement(ref _owner._activeLogicalInvocations);
                return invocation;
            }
            return AwaitLogicalInvocationAsync(invocation);
        }

        private ValueTask CompleteLogicalInvocation(ValueTask invocation)
        {
            if (invocation.IsCompleted)
            {
                Interlocked.Decrement(ref _owner._activeLogicalInvocations);
                return invocation;
            }
            return AwaitLogicalInvocationAsync(invocation);
        }

        private async ValueTask<T> AwaitLogicalInvocationAsync<T>(ValueTask<T> invocation)
        {
            try { return await invocation.ConfigureAwait(false); }
            finally { Interlocked.Decrement(ref _owner._activeLogicalInvocations); }
        }

        private async ValueTask AwaitLogicalInvocationAsync(ValueTask invocation)
        {
            try { await invocation.ConfigureAwait(false); }
            finally { Interlocked.Decrement(ref _owner._activeLogicalInvocations); }
        }

        private IAsyncEnumerable<T> CompleteLogicalInvocation<T>(IAsyncEnumerable<T> invocation)
            => new LogicalInvocationAsyncEnumerable<T>(this, invocation);

        private sealed class LogicalInvocationAsyncEnumerable<T>(
            ContractCallSite client,
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
                    Interlocked.Decrement(ref client._owner._activeLogicalInvocations);
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
            var group = new ClientStreamPublicationGroup(method.ClientStreamCount);
            group.AdmitSupervision(_owner, "DuplexStreamingInvocation");
            PooledAsyncStreamDispatcher<TResponse> dispatcher;
            try { dispatcher = PooledAsyncStreamDispatcher<TResponse>.Rent(cancellationToken, responseCodec, method.ResponseNullable); }
            catch
            {
                group.MarkNoPending();
                group.AttachOriginTask(Task.CompletedTask);
                throw;
            }
            var origin = StartDuplexStreamingInvokerAsync(dispatcher, group, method, request, requestCodec, streams, control, cancellationToken);
            group.AttachOriginTask(origin);
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
            var outcome = _owner._endpointAdmissionPolicy is null ? null : new AttemptOutcomeState(_owner, method);
            if (outcome is null)
                SharpLinkTelemetry.RecordClientAttempt();
            ClientConnection? connection = null;
            var reservationOwned = false;
            try
            {
                // Connection selection is the first step that can block, so the pre-registration
                // checkpoint below is the first place a fresh sample carries new information. A sample
                // here would only repeat the stage-local validation the call control already did.
                connection = ReserveBoundConnection(method, retrySelection: null, outcome, out var bindingId);
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
                try { connection.ReleaseCallAdmissionReservation(); }
                catch (Exception transferFailure)
                {
                    return ObserveFailedOperationAsync(operation, CompleteFailedPending(connection, requestId, transferFailure));
                }
                return StartUnaryCall(
                    connection,
                    bindingId,
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
            RpcBindingPublication binding,
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
                        binding,
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
                try { connection.PendingCalls.TryComplete(requestId, PendingCallCompletionReason.SendFailure, exception); }
                catch (Exception cleanup)
                {
                    return ObserveFailedOperationAsync(operation, RpcPublicationErrors.Combine(exception, cleanup)!);
                }
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
            var remaining = control.Deadline.GetRemaining(_owner._runtimeContext.TimeProvider);
            if (remaining > TimeSpan.Zero)
            {
                try
                {
                    await emissionTask.WaitAsync(remaining, _owner._runtimeContext.TimeProvider).ConfigureAwait(false);
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
            var outcome = _owner._endpointAdmissionPolicy is null ? null : new AttemptOutcomeState(_owner, method);
            if (outcome is null)
                SharpLinkTelemetry.RecordClientAttempt();

            ClientConnection? connection = null;
            BoundTarget bindingId;
            var reservationOwned = false;
            try
            {
                EnsureLogicalCallProgress(control);
                connection = ReserveBoundConnection(method, retrySelection: null, outcome, out bindingId);
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
                        bindingId,
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
            var outcome = _owner._endpointAdmissionPolicy is null ? null : new AttemptOutcomeState(_owner, method);
            if (outcome is null)
                SharpLinkTelemetry.RecordClientAttempt();

            var group = new ClientStreamPublicationGroup(method.ClientStreamCount);
            group.AdmitSupervision(_owner, "OneWayStreamingInvocation");
            ClientConnection? connection = null;
            BoundTarget bindingId;
            var reservationOwned = false;
            try
            {
                EnsureLogicalCallProgress(control);
                connection = ReserveBoundConnection(method, retrySelection: null, outcome, out bindingId);
                reservationOwned = true;
                EnsureLogicalCallProgress(control);
            }
            catch (Exception exception)
            {
                if (reservationOwned)
                {
                    reservationOwned = false;
                    try { connection!.ReleaseCallAdmissionReservation(); }
                    catch (Exception cleanup) { exception = RpcPublicationErrors.Combine(exception, cleanup)!; }
                }
                group.AttachOriginTask(group.FinishAsync());
                try { exception = ArbitrateLogicalCallFailure(control, exception); }
                catch (Exception arbitration) { exception = ClientStreamPublicationGroup.AddPhysical(exception, arbitration)!; }
                try { outcome?.CompleteLocalFailure(exception); }
                catch (Exception cleanup) { exception = ClientStreamPublicationGroup.AddPhysical(exception, cleanup)!; }
                throw exception;
            }

            var flags = ProtocolV2FrameFlags.OneWay;
            if (control.Deadline.HasValue || cancellationToken.CanBeCanceled)
                flags |= ProtocolV2FrameFlags.Cancellable;

            PendingRequestLease<RpcEmptyRequest> oneWayStreamLease = default;
            var requestId = 0L;
            try
            {
                EnsureLogicalCallProgress(control);
                group.PrepareRegistration(connection!, bindingId);
                reservationOwned = false;
                oneWayStreamLease = connection!.PendingCalls.RegisterOneWayClientStream(
                    control.Deadline, cancellationToken, outcome, group);
                requestId = oneWayStreamLease.Id;
                _ = group.TakeRegisteredOperation<RpcEmptyRequest>();
            }
            catch (Exception exception)
            {
                if (group.IsAdopted)
                {
                    requestId = group.RequestId;
                    var original = group.TakeRegisteredOperation<RpcEmptyRequest>();
                    if (oneWayStreamLease.Operation is null && original is not null)
                        oneWayStreamLease = new PendingRequestLease<RpcEmptyRequest>(requestId, original);
                }
                if (reservationOwned)
                {
                    reservationOwned = false;
                    try { connection!.ReleaseCallAdmissionReservation(); }
                    catch (Exception cleanup) { exception = RpcPublicationErrors.Combine(exception, cleanup)!; }
                }
                if (requestId != 0)
                {
                    try { connection!.PendingCalls.CompleteProducingSetupFailure(group, exception); }
                    catch (Exception cleanup) { exception = ClientStreamPublicationGroup.AddPhysical(exception, cleanup)!; }
                }
                // Own real cleanup before any further awaited result or user clock/outcome callback.
                // Setup failure belongs to this caller. The immutable logical winner is not
                // rewritten; this task owns ONLY independent physical cleanup/refund failures.
                group.AttachOriginTask(group.FinishAsync());
                if (oneWayStreamLease.Operation is not null)
                {
                    try { _ = await oneWayStreamLease.Operation.AsValueTask().ConfigureAwait(false); }
                    catch (Exception terminal) { exception = RpcPublicationErrors.Combine(exception, terminal)!; }
                }
                try { exception = ArbitrateLogicalCallFailure(control, exception); }
                catch (Exception arbitration) { exception = ClientStreamPublicationGroup.AddPhysical(exception, arbitration)!; }
                try { outcome?.CompleteLocalFailure(exception); }
                catch (Exception cleanup) { exception = ClientStreamPublicationGroup.AddPhysical(exception, cleanup)!; }
                throw exception;
            }

            var producerStarted = false;
            Exception? publicationFailure = null;
            try
            {
                if (group!.HasProducerLifetime)
                {
                    group.TakeProducerOwner(SharpLinkClientStreamModuleLeaseContext.Current);
                    var streamCancellationToken = group.ProducerToken;
                    var emission = SendRpcCall(
                        connection.Session,
                        group!,
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
                        publicationTable: connection.PendingCalls);
                    if (emission.IsCompletedSuccessfully) await emission.ConfigureAwait(false);
                    else
                    {
                        var headTask = emission.AsTask(); // The original ValueTask is consumed ONCE.
                        group.RetainHeadTask(headTask);
                        await headTask.ConfigureAwait(false);
                    }
                    var producer = group!.HeadPublicationConfirmed
                        ? RunGeneratedClientStreamsAsync(connection, group, streams, streamCancellationToken, completeOneWay: true)
                        : FinishUnstartedClientStreamsAsync(connection, group, null);
                    producerStarted = true;
                    group.AttachOriginTask(producer);
                }
            }
            catch (Exception exception)
            {
                publicationFailure = CompleteFailedProducingPending(connection!, group, exception);
            }

            if (!producerStarted)
            {
                var cleanup = FinishUnstartedClientStreamsAsync(connection!, group!, publicationFailure);
                group.AttachOriginTask(cleanup);
            }

            // Registration may already have completed and removed the pending entry. Its operation
            // still owns that terminal result and must be consumed exactly once to return it to the pool.
            if (publicationFailure is not null)
                _ = await ObserveFailedOperationAsync(oneWayStreamLease.Operation, publicationFailure).ConfigureAwait(false);
            else
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
            var outcome = _owner._endpointAdmissionPolicy is null ? null : new AttemptOutcomeState(_owner, method);
            if (outcome is null)
                SharpLinkTelemetry.RecordClientAttempt();
            var group = new ClientStreamPublicationGroup(method.ClientStreamCount);
            group.AdmitSupervision(_owner, "ClientStreamingInvocation");
            ClientConnection? connection = null;
            BoundTarget bindingId;
            var reservationOwned = false;
            var requestId = 0L;
            RpcRequestOperation<TResponse>? operation = null;
            try
            {
                EnsureLogicalCallProgress(control);
                connection = ReserveBoundConnection(method, retrySelection: null, outcome, out bindingId);
                reservationOwned = true;
                EnsureLogicalCallProgress(control);
                group.PrepareRegistration(connection, bindingId);
                reservationOwned = false;
                operation = connection.PendingCalls.Rent(
                    responseCodec,
                    PendingCallKind.ClientStreaming,
                    control.Deadline,
                    cancellationToken,
                    out requestId,
                    outcome,
                    hasResponsePayload: method.HasResponsePayload,
                    responseNullable: method.ResponseNullable,
                    producerGroup: group);
                _ = group.TakeRegisteredOperation<TResponse>();
            }
            catch (Exception exception)
            {
                if (group.IsAdopted)
                {
                    requestId = group.RequestId;
                    operation ??= group.TakeRegisteredOperation<TResponse>();
                }
                if (reservationOwned)
                {
                    reservationOwned = false;
                    try { connection!.ReleaseCallAdmissionReservation(); }
                    catch (Exception cleanup) { exception = RpcPublicationErrors.Combine(exception, cleanup)!; }
                }
                if (requestId != 0)
                {
                    try { connection!.PendingCalls.CompleteProducingSetupFailure(group, exception); }
                    catch (Exception cleanup) { exception = ClientStreamPublicationGroup.AddPhysical(exception, cleanup)!; }
                }
                // Own real cleanup before any further awaited result or user clock/outcome callback.
                // Setup failure belongs to this caller. The immutable logical winner is not
                // rewritten; this task owns ONLY independent physical cleanup/refund failures.
                group.AttachOriginTask(group.FinishAsync());
                if (operation is not null)
                {
                    try { _ = await operation.AsValueTask().ConfigureAwait(false); }
                    catch (Exception terminal) { exception = RpcPublicationErrors.Combine(exception, terminal)!; }
                }
                try { exception = ArbitrateLogicalCallFailure(control, exception); }
                catch (Exception arbitration) { exception = ClientStreamPublicationGroup.AddPhysical(exception, arbitration)!; }
                try { outcome?.CompleteLocalFailure(exception); }
                catch (Exception cleanup) { exception = ClientStreamPublicationGroup.AddPhysical(exception, cleanup)!; }
                throw exception;
            }
            var flags = method.HasResponsePayload
                ? ProtocolV2FrameFlags.HasReturn | ProtocolV2FrameFlags.Cancellable
                : ProtocolV2FrameFlags.Cancellable;
            var streamCancellationToken = group!.ProducerToken;
            var producerStarted = false;
            Exception? publicationFailure = null;
            try
            {
                if (group.HasProducerLifetime)
                {
                    group.TakeProducerOwner(moduleProducerLifetime);
                    var emission = SendRpcCall(
                        connection.Session,
                        group!,
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
                        publicationTable: connection.PendingCalls);
                    if (emission.IsCompletedSuccessfully) await emission.ConfigureAwait(false);
                    else
                    {
                        var headTask = emission.AsTask(); // No second await/AsTask of the ValueTask.
                        group.RetainHeadTask(headTask);
                        await headTask.ConfigureAwait(false);
                    }
                    var producerTask = group!.HeadPublicationConfirmed
                        ? RunGeneratedClientStreamsAsync(connection, group, streams, streamCancellationToken)
                        : FinishUnstartedClientStreamsAsync(connection, group, null);
                    producerStarted = true;
                    group.AttachOriginTask(producerTask);
                }
            }
            catch (Exception exception)
            {
                publicationFailure = CompleteFailedProducingPending(connection!, group, exception);
            }
            finally
            {
                if (!producerStarted)
                {
                    var cleanup = FinishUnstartedClientStreamsAsync(connection!, group!, publicationFailure);
                    group.AttachOriginTask(cleanup);
                }
            }

            return publicationFailure is null
                ? await operation!.AsValueTask().ConfigureAwait(false)
                : await ObserveFailedOperationAsync(operation!, publicationFailure).ConfigureAwait(false);
        }

        private static async Task RunGeneratedClientStreamsAsync<TStreams>(
            ClientConnection connection, ClientStreamPublicationGroup group, TStreams streams,
            CancellationToken cancellationToken, bool completeOneWay = false)
            where TStreams : struct, IRpcClientStreamWriter
        {
            try
            {
                await group.RunWriterAsync(streams, cancellationToken).ConfigureAwait(false);
                // Open/no-error group success MUST precede this logical completion (no self-join).
                if (completeOneWay)
                    connection.PendingCalls.TryComplete(group.RequestId, PendingCallCompletionReason.LocalStreamComplete);
            }
            catch (PhysicalTaskFailureException) { throw; }
            catch (Exception independent) { throw PhysicalTaskFailureException.From(independent); }
        }

        private static async Task FinishUnstartedClientStreamsAsync(
            ClientConnection connection, ClientStreamPublicationGroup group, Exception? publicationFailure)
        {
            try { await group.FinishAsync(publicationFailure).ConfigureAwait(false); }
            catch (PhysicalTaskFailureException) { throw; }
            catch (Exception independent) { throw PhysicalTaskFailureException.From(independent); }
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
                        registration.Binding,
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
            ClientStreamPublicationGroup group,
            RpcMethodDescriptor method,
            TRequest request,
            IRpcCodec<TRequest> requestCodec,
            TStreams streams,
            ResolvedCallControl control,
            CancellationToken cancellationToken)
            where TStreams : struct, IRpcClientStreamWriter
        {
            var moduleProducerLifetime = SharpLinkClientStreamModuleLeaseContext.Current;
            var registrationLease = 0L;
            ClientConnection? connection = null;
            var requestId = 0L;
            var reservationOwned = false;
            var groupFinished = false;
            Exception? publicationFailure = null;
            Exception? physicalFailure = null;
            try
            {
                registrationLease = dispatcher.RetainForRegistration();
                group.BeginResponseRoutePublication();
                StreamCallRegistration registration;
                try
                {
                    registration = await PrepareGeneratedServerStreamAsync(
                        dispatcher, PendingCallKind.DuplexStreaming, method, control, cancellationToken,
                        producerGroup: group, dispatcherLease: registrationLease).ConfigureAwait(false);
                }
                finally
                {
                    // Settle successful, throwing and never-attempted publication BEFORE any
                    // physical Finish that may depend on terminal route cleanup. No self-join.
                    group.CompleteResponseRoutePublication();
                }
                connection = registration.Connection;
                requestId = registration.RequestId;
                reservationOwned = false;
                if (!group.HasProducerLifetime)
                {
                    groupFinished = true;
                    await group.FinishAsync().ConfigureAwait(false);
                }
                else
                {
                    var streamCancellationToken = group.ProducerToken;
                    group.TakeProducerOwner(moduleProducerLifetime);
                    await SendRpcCall(
                        connection.Session,
                        group,
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
                    groupFinished = true; // RunWriter owns its terminal join even when it fails.
                    if (group.HeadPublicationConfirmed)
                        await group.RunWriterAsync(streams, streamCancellationToken).ConfigureAwait(false);
                    else
                        await group.FinishAsync().ConfigureAwait(false);
                }
            }
            catch (PhysicalTaskFailureException reduced) { physicalFailure = reduced.RawException; }
            catch (Exception exception)
            {
                publicationFailure = exception;
                connection ??= group.PreparedConnection;
                requestId = group.IsAdopted ? group.RequestId : 0;
                try { if (group.IsAdopted) publicationFailure = CompleteFailedProducingPending(connection!, group, exception); }
                catch (Exception independent) { physicalFailure = ClientStreamPublicationGroup.AddPhysical(physicalFailure, independent); }
                if (!group.IsAdopted)
                {
                    var delivered = StreamSinkPublicationDisposition.NotPublished;
                    var sink = (IStreamSinkPublicationReceipt)dispatcher;
                    long retained = 0;
                    try
                    {
                        if (sink.TryRetainSinkPublication(registrationLease, out retained))
                            sink.PublishSinkTerminal(exception, false, registrationLease, ref delivered);
                    }
                    catch (Exception independent) { physicalFailure = ClientStreamPublicationGroup.AddPhysical(physicalFailure, independent); }
                    finally
                    {
                        if (retained != 0)
                        {
                            try { sink.ReleaseSinkPublication(retained); }
                            catch (Exception independent) { physicalFailure = ClientStreamPublicationGroup.AddPhysical(physicalFailure, independent); }
                        }
                    }
                    if (delivered == StreamSinkPublicationDisposition.NotPublished)
                        physicalFailure = ClientStreamPublicationGroup.AddPhysical(physicalFailure, exception);
                }
            }
            finally
            {
                if (!groupFinished)
                {
                    try { await group.FinishAsync(publicationFailure).ConfigureAwait(false); }
                    catch (PhysicalTaskFailureException reduced) { physicalFailure = ClientStreamPublicationGroup.AddPhysical(physicalFailure, reduced.RawException); }
                    catch (Exception independent) { physicalFailure = ClientStreamPublicationGroup.AddPhysical(physicalFailure, independent); }
                }
                try { if (reservationOwned) connection!.ReleaseCallAdmissionReservation(); }
                catch (Exception independent) { physicalFailure = ClientStreamPublicationGroup.AddPhysical(physicalFailure, independent); }
                try
                {
                    if (registrationLease != 0)
                    {
                        // Before successful adoption the original origin owns this pin alone.
                        // After adoption, leave it with the exact pending sink until BOTH uses
                        // and route publication are done. No second retain or consumer wait.
                        var owned = group.IsAdopted ? group.FinishOriginSinkUseForRetention() : registrationLease;
                        if (owned != 0) dispatcher.ReleaseRegistrationRetention(owned);
                    }
                }
                catch (Exception independent) { physicalFailure = ClientStreamPublicationGroup.AddPhysical(physicalFailure, independent); }
            }
            if (physicalFailure is not null) throw PhysicalTaskFailureException.From(physicalFailure);
        }

        private ValueTask<StreamCallRegistration> PrepareGeneratedServerStreamAsync<TResponse>(
            PooledAsyncStreamDispatcher<TResponse> dispatcher,
            PendingCallKind kind,
            RpcMethodDescriptor method,
            ResolvedCallControl control,
            CancellationToken cancellationToken,
            bool captureTerminalSignal = false,
            ClientStreamPublicationGroup? producerGroup = null,
            long dispatcherLease = 0)
        {
            var outcome = _owner._endpointAdmissionPolicy is null ? null : new AttemptOutcomeState(_owner, method);
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
                connection = ReserveBoundConnection(method, retrySelection: null, outcome, out var bindingId);
                reservationOwned = true;
                EnsureLogicalCallProgress(control);
                if (producerGroup is not null)
                {
                    producerGroup.PrepareRegistration(connection, bindingId, dispatcherLease);
                    reservationOwned = false;
                }
                requestId = connection.PendingCalls.RegisterStream(
                    kind,
                    dispatcher,
                    control.Deadline,
                    cancellationToken,
                    completionObserver,
                    producerGroup);
                if (kind == PendingCallKind.ServerStreaming)
                {
                    reservationOwned = false;
                    connection.ReleaseCallAdmissionReservation();
                }
                if (!connection.PendingCalls.Contains(requestId))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    _ = control.LogicalCall?.TryClaimDeadline();
                    throw CreateDeadlineExceededException();
                }
                if (producerGroup is not null)
                {
                    using var publication = PrepareDuplexResponseRoute(connection, requestId, dispatcher, producerGroup);
                    publication.Publish();
                }
                else
                {
                    dispatcher.SetConsumerAbandonedCallback(connection.ConsumerAbandonedCallback, requestId);
                    connection.Session.StreamManager.Register(requestId, 0, dispatcher);
                }
                reservationOwned = false; // Duplex transfers the original reservation to its producer group.
                return ValueTask.FromResult(new StreamCallRegistration(connection, requestId, terminalSignal, bindingId));
            }
            catch (Exception exception)
            {
                if (producerGroup?.IsAdopted == true) requestId = producerGroup.RequestId;
                var failure = exception;
                try
                {
                    exception = ArbitrateLogicalCallFailure(control, exception);
                    failure = exception;
                }
                catch (Exception arbitration) { failure = RpcPublicationErrors.Combine(failure, arbitration)!; }
                if (reservationOwned)
                {
                    reservationOwned = false;
                    try { connection!.ReleaseCallAdmissionReservation(); }
                    catch (Exception cleanup) { failure = RpcPublicationErrors.Combine(failure, cleanup)!; }
                }
                try
                {
                    if (connection is not null && requestId != 0)
                    {
                        if (producerGroup is not null)
                            connection.PendingCalls.CompleteProducingSetupFailure(producerGroup, exception,
                                exception is SharpLinkException { Code: SharpLinkErrorCode.DeadlineExceeded }
                                    ? PendingCallCompletionReason.DeadlineExceeded : PendingCallCompletionReason.SendFailure);
                        else
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
                }
                catch (Exception cleanup) { failure = RpcPublicationErrors.Combine(failure, cleanup)!; }
                try { terminalSignal?.StopObservingTerminal(); }
                catch (Exception cleanup) { failure = RpcPublicationErrors.Combine(failure, cleanup)!; }
                throw failure;
            }
        }

        // Production-used steps expose the actual setup/publication gap without a test hook,
        // callback field or global seam. The value owns only this exact group's phase-finally.
        internal static DuplexResponseRoutePublication<T> PrepareDuplexResponseRoute<T>(ClientConnection connection,
            long requestId, PooledAsyncStreamDispatcher<T> dispatcher, ClientStreamPublicationGroup group)
        {
            dispatcher.SetConsumerAbandonedCallback(connection.ConsumerAbandonedCallback, requestId);
            return new DuplexResponseRoutePublication<T>(connection, requestId, dispatcher, group);
        }

        internal readonly struct DuplexResponseRoutePublication<T>(ClientConnection connection, long requestId,
            PooledAsyncStreamDispatcher<T> dispatcher, ClientStreamPublicationGroup group) : IDisposable
        {
            internal void Publish()
            {
                group.ClaimResponseRoutePublication();
                connection.Session.StreamManager.Register(requestId, 0, dispatcher);
            }
            public void Dispose() => group.CompleteResponseRoutePublication();
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
            PendingCallTerminalSignal? TerminalSignal,
            BoundTarget Binding);
    }
}
