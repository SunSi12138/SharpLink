namespace SharpLink.Server;

internal sealed partial class SharpLinkServer
{
    private ValueTask InvokeServiceAsync(
        ServerConnectionState connection,
        RpcSession session,
        NStudyMethodFacts facts,
        NStudyServerBindings.Use binding,
        ServerCallCancellationState? callState,
        long requestId,
        ReadOnlySequence<byte> arguments,
        IRpcByteBufferWriter? output,
        CancellationToken cancellationToken,
        SharpLinkCallContextSnapshot context)
    {
        var methodDescriptor = facts.Descriptor;
        if (context.Authentication?.IsExpired() == true)
        {
            // A valid connection can outlive its credential. A request already
            // expired here must not resolve a service factory or create a scope.
            var error = CreateExpiredAuthenticationError();
            var rejectedTelemetry = StartServerTelemetryCall(methodDescriptor, requestId);
            rejectedTelemetry.Complete(error);
            return ValueTask.FromException(error);
        }

        var oneWayInput = methodDescriptor.Kind == RpcMethodKind.OneWay && methodDescriptor.HasClientStreams;
        var isStream = oneWayInput || methodDescriptor.Kind is RpcMethodKind.ClientStreaming or
            RpcMethodKind.ServerStreaming or RpcMethodKind.DuplexStreaming;
        if (!binding.TryEnterExecution(isStream, out var registration, out var executionLease))
        {
            var error = NStudyBindingRetiredError();
            var rejectedTelemetry = StartServerTelemetryCall(methodDescriptor, requestId);
            rejectedTelemetry.Complete(error);
            return ValueTask.FromException(error);
        }
        if (executionLease.IsAcquired)
        {
            if (callState is null)
            {
                executionLease.Dispose();
                throw new InvalidOperationException("A dynamic call needs a physical owner.");
            }
            callState.AttachExecutionLease(executionLease);
        }

        var execute = registration.GetNStudyExecutor(facts.ExecutorSlot);
        if (registration.TryGetStaticSingleton(connection.GeneratedBridge, requestId, out var singleton))
            return InvokeServiceTrackedAsync(execute, singleton, session, connection.GeneratedBridge,
                requestId, arguments, output, cancellationToken, context, methodDescriptor);

        // Two-way typed inputs are finished at the invocation boundary for both
        // static and dynamic services. OneWay retains its existing physical join.
        var hasRequestStreams = registration.HasModule && oneWayInput;
        var inputDrain = registration.HasModule && oneWayInput
            ? session.StreamManager.CaptureInputStreamDrain(requestId, methodDescriptor.ClientStreamCount)
            : default;

        try
        {
            if (registration.TryGetAdmittedDynamicSingleton(
                    connection.GeneratedBridge,
                    requestId,
                    out var dynamicSingleton))
            {
                var invocation = InvokeServiceTrackedAsync(
                    execute,
                    dynamicSingleton,
                    session,
                    connection.GeneratedBridge,
                    requestId,
                    arguments,
                    output,
                    cancellationToken,
                    context,
                    methodDescriptor);
                if (!inputDrain.IsEmpty)
                    return CompleteDynamicOneWayInputAsync(invocation, default, inputDrain);
                return hasRequestStreams ? CompleteDynamicInputAsync(invocation, session, requestId) : invocation;
            }
        }
        catch (Exception exception)
        {
            var failedTelemetry = StartServerTelemetryCall(methodDescriptor, requestId);
            failedTelemetry.Complete(exception);
            var failed = ValueTask.FromException(exception);
            if (!inputDrain.IsEmpty)
                return CompleteDynamicOneWayInputAsync(failed, default, inputDrain);
            return hasRequestStreams ? CompleteDynamicInputAsync(failed, session, requestId) : failed;
        }

        ValueTask<ServiceLease> acquisition;
        try
        {
            acquisition = registration.AcquireAdmittedServiceAsync(
                connection,
                connection.GeneratedBridge,
                requestId,
                moduleLease: default); // Owned by the call, not by the service activation.
        }
        catch (Exception exception)
        {
            var failedTelemetry = StartServerTelemetryCall(methodDescriptor, requestId);
            failedTelemetry.Complete(exception);
            throw;
        }

        if (!acquisition.IsCompletedSuccessfully)
        {
            if (!acquisition.IsCompleted && methodDescriptor.HasClientStreams && !oneWayInput)
            {
                try
                {
                    ReservePreInvocationRequestStreams(connection, methodDescriptor.ClientStreamCount,
                        requestId, cancellationToken);
                }
                catch (Exception exception)
                {
                    // Acquisition is already live. Keep the original execution owner until
                    // it has either failed or handed back the unused service lease.
                    return DisposeUnusedAcquisitionAsync(acquisition, exception);
                }
            }
            return InvokeServiceAfterAcquisitionAsync(
                acquisition,
                execute,
                session,
                connection.GeneratedBridge,
                requestId,
                arguments,
                output,
                cancellationToken,
                context,
                hasRequestStreams,
                methodDescriptor,
                inputDrain);
        }

        return InvokeAcquiredServiceAsync(
            execute,
            acquisition.Result,
            session,
            connection.GeneratedBridge,
            requestId,
            arguments,
            output,
            cancellationToken,
            context,
            hasRequestStreams,
            methodDescriptor,
            inputDrain);

    }

    private static async ValueTask DisposeUnusedAcquisitionAsync(
        ValueTask<ServiceLease> acquisition, Exception failure)
    {
        try
        {
            var lease = await acquisition.ConfigureAwait(false);
            await lease.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            throw new AggregateException(failure, exception);
        }
        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static async ValueTask CompleteDynamicInputAsync(
        ValueTask invocation,
        RpcSession session,
        long requestId)
    {
        Exception? terminalException = null;
        try
        {
            await invocation.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            terminalException = exception;
        }

        try
        {
            await CompleteDynamicRequestStreamsAsync(session, requestId, hasRequestStreams: true)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            terminalException = CombineTerminalExceptions(terminalException, exception);
        }

        if (terminalException is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(terminalException).Throw();
    }

    private ValueTask InvokeAcquiredServiceAsync(
        RpcGeneratedMethodExecutor execute,
        ServiceLease lease,
        RpcSession session,
        IRpcGeneratedServerBridge generatedBridge,
        long requestId,
        ReadOnlySequence<byte> arguments,
        IRpcByteBufferWriter? output,
        CancellationToken cancellationToken,
        SharpLinkCallContextSnapshot context,
        bool hasRequestStreams,
        RpcMethodDescriptor methodDescriptor,
        StreamManager.InputStreamDrain inputDrain)
    {
        if (!lease.RequiresDisposal && !hasRequestStreams && inputDrain.IsEmpty)
        {
            return InvokeServiceTrackedAsync(
                execute,
                lease.Service,
                session,
                generatedBridge,
                requestId,
                arguments,
                output,
                cancellationToken,
                context,
                methodDescriptor);
        }

        if (!inputDrain.IsEmpty)
        {
            ValueTask invocation;
            try
            {
                invocation = InvokeServiceTrackedAsync(execute, lease.Service, session, generatedBridge,
                    requestId, arguments, output, cancellationToken, context, methodDescriptor);
            }
            catch (Exception exception)
            {
                invocation = ValueTask.FromException(exception);
            }
            return CompleteDynamicOneWayInputAsync(invocation, lease, inputDrain);
        }
        return InvokeServiceWithLeaseAsync(
            execute,
            lease,
            session,
            generatedBridge,
            requestId,
            arguments,
            output,
            cancellationToken,
            context,
            hasRequestStreams,
            methodDescriptor);
    }

    private async ValueTask InvokeServiceAfterAcquisitionAsync(
        ValueTask<ServiceLease> acquisition,
        RpcGeneratedMethodExecutor execute,
        RpcSession session,
        IRpcGeneratedServerBridge generatedBridge,
        long requestId,
        ReadOnlySequence<byte> arguments,
        IRpcByteBufferWriter? output,
        CancellationToken cancellationToken,
        SharpLinkCallContextSnapshot context,
        bool hasRequestStreams,
        RpcMethodDescriptor methodDescriptor,
        StreamManager.InputStreamDrain inputDrain)
    {
        ServiceLease lease;
        try
        {
            lease = await acquisition.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            var failedTelemetry = StartServerTelemetryCall(methodDescriptor, requestId);
            failedTelemetry.Complete(exception);
            throw;
        }

        await InvokeAcquiredServiceAsync(
            execute,
            lease,
            session,
            generatedBridge,
            requestId,
            arguments,
            output,
            cancellationToken,
            context,
            hasRequestStreams,
            methodDescriptor,
            inputDrain).ConfigureAwait(false);
    }

    private ValueTask InvokeServiceTrackedAsync(
        RpcGeneratedMethodExecutor execute,
        object service,
        RpcSession session,
        IRpcGeneratedServerBridge generatedBridge,
        long requestId,
        ReadOnlySequence<byte> arguments,
        IRpcByteBufferWriter? output,
        CancellationToken cancellationToken,
        SharpLinkCallContextSnapshot context,
        RpcMethodDescriptor methodDescriptor)
    {
        var telemetry = StartServerTelemetryCall(methodDescriptor, requestId);
        try
        {
            var inputOwner = methodDescriptor.Kind is RpcMethodKind.ClientStreaming or RpcMethodKind.DuplexStreaming
                ? (ServerGeneratedBridge)generatedBridge
                : null;
            ValueTask invocation;
            try
            {
                invocation = InvokeServiceCoreAsync(execute, service, session, generatedBridge,
                    requestId, arguments, output, cancellationToken, context, methodDescriptor);
            }
            catch (Exception exception) when (inputOwner is not null)
            {
                invocation = ValueTask.FromException(exception);
            }
            if (inputOwner is not null)
                invocation = FinishGeneratedInputInvocationAsync(invocation, inputOwner, requestId);
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

    private static async ValueTask FinishGeneratedInputInvocationAsync(
        ValueTask invocation, ServerGeneratedBridge inputOwner, long requestId)
    {
        Exception? failure = null;
        try { await invocation.ConfigureAwait(false); }
        catch (Exception exception) { failure = exception; }
        try { await inputOwner.FinishGeneratedInputsAsync(requestId).ConfigureAwait(false); }
        catch (Exception exception) { failure = CombineTerminalExceptions(failure, exception); }
        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private async ValueTask InvokeServiceWithLeaseAsync(
        RpcGeneratedMethodExecutor execute,
        ServiceLease lease,
        RpcSession session,
        IRpcGeneratedServerBridge generatedBridge,
        long requestId,
        ReadOnlySequence<byte> arguments,
        IRpcByteBufferWriter? output,
        CancellationToken cancellationToken,
        SharpLinkCallContextSnapshot context,
        bool hasRequestStreams,
        RpcMethodDescriptor methodDescriptor)
    {
        Exception? terminalException = null;
        try
        {
            await InvokeServiceTrackedAsync(
                execute,
                lease.Service,
                session,
                generatedBridge,
                requestId,
                arguments,
                output,
                cancellationToken,
                context,
                methodDescriptor).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            terminalException = exception;
        }

        try
        {
            await CompleteDynamicRequestStreamsAsync(session, requestId, hasRequestStreams)
                .ConfigureAwait(false);
        }
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

    private static Exception CombineTerminalExceptions(Exception? first, Exception next)
        => first is null ? next : new AggregateException(first, next);

    private static ValueTask CompleteDynamicRequestStreamsAsync(
        RpcSession session,
        long requestId,
        bool hasRequestStreams)
    {
        if (hasRequestStreams && session.StreamManager is StreamManager manager)
        {
            return manager.CompleteRequestStreamsAfterDispatchesAsync(
                requestId,
                new OperationCanceledException(
                    "The RPC handler completed before its request streams drained."));
        }
        return ValueTask.CompletedTask;
    }

    private ValueTask InvokeServiceCoreAsync(
        RpcGeneratedMethodExecutor execute,
        object service,
        RpcSession session,
        IRpcGeneratedServerBridge generatedBridge,
        long requestId,
        ReadOnlySequence<byte> arguments,
        IRpcByteBufferWriter? output,
        CancellationToken cancellationToken,
        SharpLinkCallContextSnapshot context,
        in RpcMethodDescriptor method)
    {
        // The credential can also expire while service acquisition awaits.
        if (context.Authentication?.IsExpired() == true)
            return ValueTask.FromException(CreateExpiredAuthenticationError());
        var interceptors = (context as SharpLinkServerInvocationContext)?.InterceptorGeneration as ServerInterceptorGeneration;
        if (interceptors is null || interceptors.Count == 0)
        {
            return execute(service, generatedBridge, in method, requestId, arguments, output, cancellationToken);
        }

        return InvokeInterceptedWithOwnedArgumentsAsync(
            interceptors,
            execute,
            service,
            session,
            generatedBridge,
            requestId,
            arguments,
            output,
            cancellationToken,
            (SharpLinkServerInvocationContext)context);
    }

    private static SharpLinkException CreateExpiredAuthenticationError()
        => new(SharpLinkErrorCode.AuthenticationExpired, "Authentication token has expired.");

    private async ValueTask InvokeInterceptedWithOwnedArgumentsAsync(
        ServerInterceptorGeneration interceptors,
        RpcGeneratedMethodExecutor execute,
        object service,
        RpcSession session,
        IRpcGeneratedServerBridge generatedBridge,
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
                execute,
                service,
                generatedBridge,
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
                execute,
                service,
                generatedBridge,
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
        RpcGeneratedMethodExecutor execute,
        object service,
        IRpcGeneratedServerBridge generatedBridge,
        long requestId,
        ReadOnlySequence<byte> arguments,
        IRpcByteBufferWriter? output,
        CancellationToken cancellationToken,
        SharpLinkServerInvocationContext context)
    {
        var timeProvider = _runtimeContext.TimeProvider;
        context.InterceptorExecutor = execute;
        context.InterceptorService = service;
        context.InterceptorGeneratedBridge = generatedBridge;
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
            context.InterceptorExecutor = null;
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
        RpcMethodDescriptor method,
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
        RpcMethodDescriptor method,
        Exception exception)
    {
        if (exception is OperationCanceledException && callCancellations.TryCapture(
                requestId,
                static (capturedRequestId, state) => state.CaptureLease(capturedRequestId),
                out var callLease) &&
            callLease.TryAcquire())
        {
            try
            {
                exception = MapServerCancellationException(callLease.State, callLease.State.Deadline);
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
