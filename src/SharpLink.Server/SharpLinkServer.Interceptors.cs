namespace SharpLink.Server;

internal sealed partial class SharpLinkServer
{
    internal Action<ServerCallCancellationLease>? BeforeGeneratedPhysicalInvocationForTesting { get; set; }

    private ValueTask InvokeServiceAsync(
        in ResolvedMethodCall resolved,
        ServerConnectionState connection,
        RpcSession session,
        long requestId,
        ReadOnlySequence<byte> arguments,
        IRpcByteBufferWriter? output,
        CancellationToken cancellationToken,
        SharpLinkCallContextSnapshot context,
        ServerRequestPermit requestOwner)
    {
        var registration = resolved.Registration;
        requestOwner.VerifyProtectedRegistration(registration);
        var clientStreamCount = resolved.ClientStreamCount;
        StreamManager.CapturedRequestStreamCleanup capturedInbound = default;
        if (clientStreamCount != 0 &&
            !session.StreamManager.TryCaptureRequestStreamsForInvocation(
                requestId, clientStreamCount, out capturedInbound))
            return ValueTask.FromException(new SharpLinkException(SharpLinkErrorCode.ConnectionClosed,
                "The complete inbound route set was closed before service entry."));
        try
        {
            if (clientStreamCount != 0)
                requestOwner.MarkInboundStreamCleanupOwned();
            IRpcGeneratedServerBridge generatedBridge = connection.GeneratedBridge;
            if (generatedBridge is ServerGeneratedBridge bridge &&
                resolved.Method.Kind is RpcMethodKind.ServerStreaming or RpcMethodKind.DuplexStreaming)
            {
                if (!connection.CallCancellations.TryCapture(requestId,
                        static (id, state) => state.CaptureLease(id), out var callLease))
                    throw new SharpLinkProtocolViolationException(ProtocolViolationReason.InternalState,
                        "Generated streaming requires its original call-state owner before invocation.");
                requestOwner.RememberPhysicalCallState(in callLease);
                BeforeGeneratedPhysicalInvocationForTesting?.Invoke(callLease);
                generatedBridge = bridge.WithPhysicalOwner(requestOwner);
            }
            if (registration.TryGetStaticSingleton(
                    generatedBridge,
                    requestId,
                    out var singleton))
            {
                if (resolved.ClientStreamCount != 0 ||
                    resolved.Method.Kind is RpcMethodKind.ServerStreaming or RpcMethodKind.DuplexStreaming)
                    return InvokeAcquiredServiceAsync(resolved, new ServiceLease(singleton), session,
                        generatedBridge, requestId, arguments, output, cancellationToken, context,
                        resolved.ClientStreamCount, requestOwner, capturedInbound);
                return InvokeServiceTrackedAsync(
                    resolved,
                    singleton,
                    session,
                    generatedBridge,
                    requestId,
                    arguments,
                    output,
                    cancellationToken,
                    context);
            }

            ValueTask<ServiceLease> acquisition;
            try
            {
                acquisition = registration.AcquirePreparedAsync(
                    connection,
                    requestOwner,
                    generatedBridge,
                    requestId);
            }
            catch (Exception exception)
            {
                try
                {
                    var failedTelemetry = StartServerTelemetryCall(resolved.Method, requestId);
                    failedTelemetry.Complete(exception);
                }
                catch (Exception telemetryFailure)
                {
                    throw CombineTerminalExceptions(exception, telemetryFailure);
                }
                throw;
            }

            if (!acquisition.IsCompletedSuccessfully)
            {
                return InvokeServiceAfterAcquisitionAsync(
                    acquisition,
                    resolved,
                    session,
                    generatedBridge,
                    requestId,
                    arguments,
                    output,
                    cancellationToken,
                    context,
                    clientStreamCount, requestOwner, capturedInbound);
            }

            return InvokeAcquiredServiceAsync(
                resolved,
                acquisition.Result,
                session,
                generatedBridge,
                requestId,
                arguments,
                output,
                cancellationToken,
                context,
                clientStreamCount, requestOwner, capturedInbound);
        }
        catch (Exception setupFailure)
        {
            // No service lease was transferred on synchronous setup/factory/acquisition failure.
            // The exact entry capture still owns child cleanup even if Session lookup is empty.
            return CompleteFailedCapturedInvocationSetupAsync(
                setupFailure, session, requestOwner, capturedInbound);
        }
    }

    private ValueTask InvokeAcquiredServiceAsync(
        in ResolvedMethodCall resolved,
        ServiceLease lease,
        RpcSession session,
        IRpcGeneratedServerBridge generatedBridge,
        long requestId,
        ReadOnlySequence<byte> arguments,
        IRpcByteBufferWriter? output,
        CancellationToken cancellationToken,
        SharpLinkCallContextSnapshot context,
        int clientStreamCount,
        ServerRequestPermit requestOwner,
        StreamManager.CapturedRequestStreamCleanup capturedInbound)
    {
        if (!lease.RequiresDisposal && clientStreamCount == 0 &&
            resolved.Method.Kind is not (RpcMethodKind.ServerStreaming or RpcMethodKind.DuplexStreaming))
        {
            return InvokeServiceTrackedAsync(
                resolved,
                lease.Service,
                session,
                generatedBridge,
                requestId,
                arguments,
                output,
                cancellationToken,
                context);
        }

        return InvokeServiceWithLeaseAsync(
            resolved,
            lease,
            session,
            generatedBridge,
            requestId,
            arguments,
            output,
            cancellationToken,
            context,
            clientStreamCount, requestOwner, capturedInbound);
    }

    private async ValueTask InvokeServiceAfterAcquisitionAsync(
        ValueTask<ServiceLease> acquisition,
        ResolvedMethodCall resolved,
        RpcSession session,
        IRpcGeneratedServerBridge generatedBridge,
        long requestId,
        ReadOnlySequence<byte> arguments,
        IRpcByteBufferWriter? output,
        CancellationToken cancellationToken,
        SharpLinkCallContextSnapshot context,
        int clientStreamCount,
        ServerRequestPermit requestOwner,
        StreamManager.CapturedRequestStreamCleanup capturedInbound)
    {
        ServiceLease lease;
        try
        {
            lease = await acquisition.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Exception failure = exception;
            try
            {
                var failedTelemetry = StartServerTelemetryCall(resolved.Method, requestId);
                failedTelemetry.Complete(exception);
            }
            catch (Exception telemetryFailure) { failure = CombineTerminalExceptions(failure, telemetryFailure); }
            await CompleteFailedCapturedInvocationSetupAsync(
                failure, session, requestOwner, capturedInbound).ConfigureAwait(false);
            return;
        }

        await InvokeAcquiredServiceAsync(
            resolved,
            lease,
            session,
            generatedBridge,
            requestId,
            arguments,
            output,
            cancellationToken,
            context,
            clientStreamCount, requestOwner, capturedInbound).ConfigureAwait(false);
    }

    private ValueTask InvokeServiceTrackedAsync(
        in ResolvedMethodCall resolved,
        object service,
        RpcSession session,
        IRpcGeneratedServerBridge generatedBridge,
        long requestId,
        ReadOnlySequence<byte> arguments,
        IRpcByteBufferWriter? output,
        CancellationToken cancellationToken,
        SharpLinkCallContextSnapshot context)
    {
        var telemetry = StartServerTelemetryCall(
            resolved.Method, requestId);
        try
        {
            var invocation = InvokeServiceCoreAsync(
                resolved.Stub,
                service,
                session,
                generatedBridge,
                resolved.Method,
                requestId,
                arguments,
                output,
                cancellationToken,
                context);
            if (!telemetry.IsEnabled)
                return invocation;
            if (invocation.IsCompletedSuccessfully)
            {
                telemetry.Complete();
                return invocation;
            }
            return ObserveServerCallAsync(invocation, telemetry);
        }
        catch (Exception exception)
        {
            telemetry.Complete(exception);
            throw;
        }
    }

    private async ValueTask InvokeServiceWithLeaseAsync(
        ResolvedMethodCall resolved,
        ServiceLease lease,
        RpcSession session,
        IRpcGeneratedServerBridge generatedBridge,
        long requestId,
        ReadOnlySequence<byte> arguments,
        IRpcByteBufferWriter? output,
        CancellationToken cancellationToken,
        SharpLinkCallContextSnapshot context,
        int clientStreamCount,
        ServerRequestPermit requestOwner,
        StreamManager.CapturedRequestStreamCleanup capturedInbound)
    {
        Exception? terminalException = null;
        try
        {
            await InvokeServiceTrackedAsync(
                resolved,
                lease.Service,
                session,
                generatedBridge,
                requestId,
                arguments,
                output,
                cancellationToken,
                context).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            terminalException = exception;
        }

        if (terminalException is PhysicalTaskFailureException physicalTerminal)
        {
            try { ReportStrictPhysicalRuntimeFailure(physicalTerminal.RawException, acceptedReceipt: null); }
            catch (Exception reportingFailure)
            { terminalException = CombineTerminalExceptions(terminalException, reportingFailure); }
        }

        // Seal/claim before starting a task: a synchronously finished tail may still have
        // an error, and a synchronously finished cleanup must not race task attachment.
        var hasPhysicalCleanup = requestOwner.BeginPhysicalServiceCleanup();
        var outgoingTails = hasPhysicalCleanup ? requestOwner.JoinPhysicalTailsAsync().AsTask() : null;
        // Execute the original entry capture from BEFORE factory/stub/user entry. Session
        // CompleteAll cannot turn an empty later lookup into a false physical-completion proof.
        ValueTask inboundCleanup;
        try { inboundCleanup = CompleteCapturedRequestStreamsAsync(session, ref capturedInbound, outgoingTails); }
        catch (Exception setupFailure) { inboundCleanup = ValueTask.FromException(setupFailure); }
        if (hasPhysicalCleanup)
        {
            // No cycle: this task joins outgoing tails/children, marks its phase finished,
            // then performs any deferred permit release itself under strict supervision.
            var cleanup = CompletePhysicalServiceCleanupAsync(requestOwner, lease, outgoingTails!, inboundCleanup);
            FrameworkTaskRegistration? receipt = null;
            try
            {
                requestOwner.AttachPhysicalServiceCleanup(cleanup);
                TrackStrictPhysicalRuntimeTask(cleanup, "GeneratedPhysicalServiceCleanup", ref receipt);
            }
            catch (Exception setupFailure)
            {
                Exception physicalFailure = setupFailure;
                try { await cleanup.ConfigureAwait(false); }
                catch (Exception cleanupFailure)
                {
                    // Accepted registration owns its task outcome even if later setup threw.
                    if (receipt is null)
                        physicalFailure = CombineTerminalExceptions(physicalFailure,
                            cleanupFailure is PhysicalTaskFailureException marked ? marked.RawException : cleanupFailure);
                }
                if (receipt is not null)
                {
                    try { receipt.Supervisor.CompleteAcceptedRegistration(receipt); }
                    catch (Exception completeFailure)
                    { physicalFailure = CombineTerminalExceptions(physicalFailure, completeFailure); }
                }
                try { ReportStrictPhysicalRuntimeFailure(physicalFailure, acceptedReceipt: null); }
                catch (Exception reportingFailure)
                { physicalFailure = CombineTerminalExceptions(physicalFailure, reportingFailure); }
                terminalException = CombineTerminalExceptions(terminalException,
                    PhysicalTaskFailureException.From(physicalFailure));
            }
            if (terminalException is not null)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(terminalException).Throw();
            return;
        }

        try { await inboundCleanup.ConfigureAwait(false); }
        catch (Exception exception)
        {
            terminalException = CombineTerminalExceptions(terminalException, exception);
        }

        try
        {
            await lease.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            terminalException = CombineTerminalExceptions(terminalException, exception);
        }

        if (terminalException is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(terminalException).Throw();
    }

    private static async Task CompletePhysicalServiceCleanupAsync(
        ServerRequestPermit owner, ServiceLease lease, Task outgoingTails, ValueTask inboundCleanup)
    {
        Exception? failure = null;
        try { await outgoingTails.ConfigureAwait(false); }
        catch (Exception exception) { failure = exception; }
        // This is the originally captured operation, not a later request-table lookup.
        try { await inboundCleanup.ConfigureAwait(false); }
        catch (Exception exception) { failure = CombineTerminalExceptions(failure, exception); }
        try { await lease.DisposeAsync().ConfigureAwait(false); }
        catch (Exception exception) { failure = CombineTerminalExceptions(failure, exception); }
        try { owner.FinishPhysicalServiceCleanup(); }
        catch (Exception exception) { failure = CombineTerminalExceptions(failure, exception); }
        if (failure is not null)
            throw PhysicalTaskFailureException.From(failure);
    }

    private static Exception CombineTerminalExceptions(Exception? first, Exception next)
        => first is null ? next : new AggregateException(first, next);

    private static ValueTask CompleteCapturedRequestStreamsAsync(
        RpcSession session, ref StreamManager.CapturedRequestStreamCleanup capture,
        Task? beforeAbandon = null)
    {
        try { return session.StreamManager.AbandonCapturedRequestStreamsAsync(
            ref capture, beforeAbandon, prerequisiteFailureOwnedByCaller: true); }
        finally { capture.Dispose(); }
    }

    private static async ValueTask CompleteFailedCapturedInvocationSetupAsync(
        Exception failure, RpcSession session, ServerRequestPermit requestOwner,
        StreamManager.CapturedRequestStreamCleanup capture)
    {
        var physicalOwned = requestOwner.BeginPhysicalServiceCleanup();
        var outgoing = physicalOwned ? requestOwner.JoinPhysicalTailsAsync().AsTask() : null;
        ValueTask childCleanup;
        try { childCleanup = CompleteCapturedRequestStreamsAsync(session, ref capture, outgoing); }
        catch (Exception cleanup) { childCleanup = ValueTask.CompletedTask; failure = CombineTerminalExceptions(failure, cleanup); }
        if (outgoing is not null)
        {
            try { await outgoing.ConfigureAwait(false); }
            catch (Exception cleanup) { failure = CombineTerminalExceptions(failure, cleanup); }
        }
        try { await childCleanup.ConfigureAwait(false); }
        catch (Exception cleanup) { failure = CombineTerminalExceptions(failure, cleanup); }
        if (physicalOwned)
        {
            try { requestOwner.FinishPhysicalServiceCleanup(); }
            catch (Exception cleanup) { failure = CombineTerminalExceptions(failure, cleanup); }
        }
        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private ValueTask InvokeServiceCoreAsync(
        IRpcStub stub,
        object service,
        RpcSession session,
        IRpcGeneratedServerBridge generatedBridge,
        RpcMethodFacts method,
        long requestId,
        ReadOnlySequence<byte> arguments,
        IRpcByteBufferWriter? output,
        CancellationToken cancellationToken,
        SharpLinkCallContextSnapshot context)
    {
        if (context.Authentication?.IsExpired() == true)
        {
            return ValueTask.FromException(new SharpLinkException(
                SharpLinkErrorCode.AuthenticationExpired,
                "Authentication token has expired."));
        }
        var interceptors = (context as SharpLinkServerInvocationContext)?.InterceptorGeneration as ServerInterceptorGeneration;
        if (interceptors is null || interceptors.Count == 0)
        {
            return output is null
                ? stub.InvokeNoReturnCancellableAsync(
                    service, generatedBridge, method, requestId, arguments, cancellationToken)
                : stub.InvokeCancellableAsync(
                    service, generatedBridge, method, requestId, arguments, output, cancellationToken);
        }

        return InvokeInterceptedWithOwnedArgumentsAsync(
            interceptors,
            stub,
            service,
            session,
            generatedBridge,
            method,
            requestId,
            arguments,
            output,
            cancellationToken,
            (SharpLinkServerInvocationContext)context);
    }

    private async ValueTask InvokeInterceptedWithOwnedArgumentsAsync(
        ServerInterceptorGeneration interceptors,
        IRpcStub stub,
        object service,
        RpcSession session,
        IRpcGeneratedServerBridge generatedBridge,
        RpcMethodFacts method,
        long requestId,
        ReadOnlySequence<byte> arguments,
        IRpcByteBufferWriter? output,
        CancellationToken cancellationToken,
        SharpLinkServerInvocationContext context)
    {
        var length = checked((int)arguments.Length);
        var maxArgumentsBytes = session.NegotiatedMaxFramePayloadBytes;
        if (length > maxArgumentsBytes)
        {
            throw new SharpLinkException(
                SharpLinkErrorCode.ResourceExhausted,
                $"RPC arguments exceed the negotiated {maxArgumentsBytes}-byte frame limit.");
        }

        if (length == 0)
        {
            await InvokeComposedServerInterceptorsAsync(
                interceptors,
                stub,
                service,
                generatedBridge,
                method,
                requestId,
                ReadOnlySequence<byte>.Empty,
                output,
                cancellationToken,
                context).ConfigureAwait(false);
            return;
        }

        var rented = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            arguments.CopyTo(rented);
            var ownedArguments = new ReadOnlySequence<byte>(rented.AsMemory(0, length));
            await InvokeComposedServerInterceptorsAsync(
                interceptors,
                stub,
                service,
                generatedBridge,
                method,
                requestId,
                ownedArguments,
                output,
                cancellationToken,
                context).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented, clearArray: true);
        }
    }

    private async ValueTask InvokeComposedServerInterceptorsAsync(
        ServerInterceptorGeneration interceptors,
        IRpcStub stub,
        object service,
        IRpcGeneratedServerBridge generatedBridge,
        RpcMethodFacts method,
        long requestId,
        ReadOnlySequence<byte> arguments,
        IRpcByteBufferWriter? output,
        CancellationToken cancellationToken,
        SharpLinkServerInvocationContext context)
    {
        var timeProvider = _runtimeContext.TimeProvider;
        context.InterceptorStub = stub;
        context.InterceptorService = service;
        context.InterceptorGeneratedBridge = generatedBridge;
        // The context already owns the same immutable scalar facts.
        context.InterceptorArguments = arguments;
        context.InterceptorOutput = output;
        context.InterceptorTimeProvider = timeProvider;
        context.InterceptorStarted = timeProvider.GetTimestamp();
        context.InterceptorTerminalReached = false;

        try
        {
            await interceptors.Entry(context).ConfigureAwait(false);
            if (output is not null && !context.InterceptorTerminalReached)
            {
                throw new InvalidOperationException(
                    "A Server interceptor must invoke its continuation for a response-bearing RPC.");
            }
            if (context.Status == SharpLinkInvocationStatus.Pending)
                context.Status = SharpLinkInvocationStatus.Succeeded;
        }
        catch (Exception exception)
        {
            RecordInvocationFailure(context, exception);
            throw;
        }
        finally
        {
            context.Elapsed = timeProvider.GetElapsedTime(context.InterceptorStarted);
            context.InterceptorStub = null;
            context.InterceptorService = null;
            context.InterceptorGeneratedBridge = null;
            context.InterceptorArguments = default;
            context.InterceptorOutput = null;
            context.InterceptorTimeProvider = null;
        }
    }

    private static async ValueTask ObserveServerCallAsync(
        ValueTask invocation,
        SharpLinkTelemetry.CallScope telemetry)
    {
        try
        {
            await invocation.ConfigureAwait(false);
            telemetry.Complete();
        }
        catch (Exception exception)
        {
            telemetry.Complete(exception);
            throw;
        }
    }

    private SharpLinkException MapServiceException(
        Exception exception,
        SharpLinkCallContextSnapshot callContext,
        RpcSession session,
        RpcMethodFacts method,
        long requestId,
        CancellationToken cancellationToken)
    {
        var invocationContext = callContext as SharpLinkServerInvocationContext ??
            CreateServerInvocationContext(
                session,
                method,
                requestId,
                callContext.Authentication,
                callContext.LocalRpcDeadline,
                callContext.DeadlineTimeProvider ?? _runtimeContext.TimeProvider,
                callContext.Metadata,
                cancellationToken);
        return MapServiceException(exception, invocationContext);
    }

    private SharpLinkException MapServiceException(
        Exception exception,
        SharpLinkServerInvocationContext context)
    {
        RecordInvocationFailure(context, exception);
        try
        {
            var mapped = _exceptionMapper.Map(exception, context)
                ?? throw new InvalidOperationException("The RPC exception mapper returned null.");
            context.ErrorCode = mapped.Code;
            return mapped;
        }
        catch (Exception mapperException)
        {
            LogRpcDispatchUnhandledException(_logger, mapperException);
            context.ErrorCode = SharpLinkErrorCode.Internal;
            return new SharpLinkException(
                SharpLinkErrorCode.Internal,
                "Internal service error.",
                exception);
        }
    }

    internal SharpLinkException MapStreamServiceException(
        StripedLongMap<ServerCallCancellationState> callCancellations,
        RpcSession session,
        long requestId,
        RpcMethodFacts method,
        Exception exception)
    {
        if (exception is OperationCanceledException &&
            callCancellations.TryCapture(
                requestId,
                static (capturedRequestId, state) => state.CaptureLease(capturedRequestId),
                out var callLease) &&
            callLease.TryAcquire())
        {
            try
            {
                exception = MapServerCancellationException(
                    callLease.State,
                    callLease.State.Deadline);
            }
            finally
            {
                callLease.ReleaseUse();
            }
        }

        if (SharpLinkCallContext.Current is SharpLinkServerInvocationContext context)
            return MapServiceException(exception, context);
        if (SharpLinkCallContext.Current is { } callContext)
        {
            return MapServiceException(
                exception,
                callContext,
                session,
                method,
                requestId,
                CancellationToken.None);
        }
        return exception as SharpLinkException ?? new SharpLinkException(
            SharpLinkErrorCode.Internal,
            "Internal stream error.",
            exception);
    }

    private static bool IsCancellationException(Exception exception)
        => exception is OperationCanceledException or
           SharpLinkException { Code: SharpLinkErrorCode.Cancelled };

    private static void RecordInvocationFailure(
        SharpLinkServerInvocationContext context,
        Exception exception)
    {
        var cancelled = IsCancellationException(exception);
        context.Status = cancelled
            ? SharpLinkInvocationStatus.Cancelled
            : SharpLinkInvocationStatus.Failed;
        context.ErrorCode = cancelled
            ? SharpLinkErrorCode.Cancelled
            : exception is SharpLinkException sharpLinkException
                ? sharpLinkException.Code
                : SharpLinkErrorCode.Internal;
        context.Exception = exception;
    }
}
