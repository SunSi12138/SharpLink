namespace SharpLink.Server;

internal sealed partial class SharpLinkServer
{
    private ValueTask InvokeServiceAsync(
        ServiceRegistration registration,
        ServerConnectionState connection,
        RpcSession session,
        long methodId,
        long requestId,
        ReadOnlySequence<byte> arguments,
        IRpcByteBufferWriter? output,
        CancellationToken cancellationToken,
        SharpLinkCallContextSnapshot context)
    {
        if (registration.TryGetStaticSingleton(
                connection.GeneratedBridge,
                requestId,
                out var singleton))
        {
            return InvokeServiceTrackedAsync(
                registration.Stub,
                singleton,
                session,
                connection.GeneratedBridge,
                methodId,
                requestId,
                arguments,
                output,
                cancellationToken,
                context);
        }

        var isStream = false;
        var hasRequestStreams = false;
        if (registration.Module is not null)
        {
            var descriptor = GetMethodDescriptor(registration.Stub, methodId);
            isStream = descriptor.Kind is RpcMethodKind.ClientStreaming or
                RpcMethodKind.ServerStreaming or RpcMethodKind.DuplexStreaming;
            hasRequestStreams = descriptor.Kind is RpcMethodKind.ClientStreaming or
                RpcMethodKind.DuplexStreaming;
        }

        SharpLinkDynamicModuleLease dynamicSingletonLease = default;
        try
        {
            if (registration.TryAcquireDynamicSingleton(
                    isStream,
                    connection.GeneratedBridge,
                    requestId,
                    out var dynamicSingleton,
                    out dynamicSingletonLease))
            {
                var invocation = InvokeServiceTrackedAsync(
                    registration.Stub,
                    dynamicSingleton,
                    session,
                    connection.GeneratedBridge,
                    methodId,
                    requestId,
                    arguments,
                    output,
                    cancellationToken,
                    context);
                return CompleteDynamicSingletonInvocationAsync(
                    invocation,
                    dynamicSingletonLease,
                    session,
                    requestId,
                    hasRequestStreams);
            }
        }
        catch (Exception exception)
        {
            var failedTelemetry = SharpLinkTelemetry.StartServerCall(
                GetMethodDescriptor(registration.Stub, methodId), requestId);
            failedTelemetry.Complete(exception);
            return CompleteDynamicSingletonInvocationAsync(
                ValueTask.FromException(exception),
                dynamicSingletonLease,
                session,
                requestId,
                hasRequestStreams);
        }

        ValueTask<ServiceLease> acquisition;
        try
        {
            acquisition = registration.AcquireAsync(
                connection,
                isStream,
                connection.GeneratedBridge,
                requestId);
        }
        catch (Exception exception)
        {
            var failedTelemetry = SharpLinkTelemetry.StartServerCall(
                GetMethodDescriptor(registration.Stub, methodId), requestId);
            failedTelemetry.Complete(exception);
            throw;
        }

        if (!acquisition.IsCompletedSuccessfully)
        {
            return InvokeServiceAfterAcquisitionAsync(
                acquisition,
                registration.Stub,
                session,
                connection.GeneratedBridge,
                methodId,
                requestId,
                arguments,
                output,
                cancellationToken,
                context,
                hasRequestStreams);
        }

        return InvokeAcquiredServiceAsync(
            registration.Stub,
            acquisition.Result,
            session,
            connection.GeneratedBridge,
            methodId,
            requestId,
            arguments,
            output,
            cancellationToken,
            context,
            hasRequestStreams);
    }

    private static async ValueTask CompleteDynamicSingletonInvocationAsync(
        ValueTask invocation,
        SharpLinkDynamicModuleLease moduleLease,
        RpcSession session,
        long requestId,
        bool hasRequestStreams)
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
            await CompleteDynamicRequestStreamsAsync(session, requestId, hasRequestStreams)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            terminalException = CombineTerminalExceptions(terminalException, exception);
        }

        try
        {
            moduleLease.Dispose();
        }
        catch (Exception exception)
        {
            terminalException = CombineTerminalExceptions(terminalException, exception);
        }

        if (terminalException is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(terminalException).Throw();
    }

    private ValueTask InvokeAcquiredServiceAsync(
        IRpcStub stub,
        ServiceLease lease,
        RpcSession session,
        IRpcGeneratedServerBridge generatedBridge,
        long methodId,
        long requestId,
        ReadOnlySequence<byte> arguments,
        IRpcByteBufferWriter? output,
        CancellationToken cancellationToken,
        SharpLinkCallContextSnapshot context,
        bool hasRequestStreams)
    {

        if (!lease.RequiresDisposal)
        {
            return InvokeServiceTrackedAsync(
                stub,
                lease.Service,
                session,
                generatedBridge,
                methodId,
                requestId,
                arguments,
                output,
                cancellationToken,
                context);
        }

        return InvokeServiceWithLeaseAsync(
            stub,
            lease,
            session,
            generatedBridge,
            methodId,
            requestId,
            arguments,
            output,
            cancellationToken,
            context,
            hasRequestStreams);
    }

    private async ValueTask InvokeServiceAfterAcquisitionAsync(
        ValueTask<ServiceLease> acquisition,
        IRpcStub stub,
        RpcSession session,
        IRpcGeneratedServerBridge generatedBridge,
        long methodId,
        long requestId,
        ReadOnlySequence<byte> arguments,
        IRpcByteBufferWriter? output,
        CancellationToken cancellationToken,
        SharpLinkCallContextSnapshot context,
        bool hasRequestStreams)
    {
        ServiceLease lease;
        try
        {
            lease = await acquisition.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            var failedTelemetry = SharpLinkTelemetry.StartServerCall(
                GetMethodDescriptor(stub, methodId), requestId);
            failedTelemetry.Complete(exception);
            throw;
        }

        await InvokeAcquiredServiceAsync(
            stub,
            lease,
            session,
            generatedBridge,
            methodId,
            requestId,
            arguments,
            output,
            cancellationToken,
            context,
            hasRequestStreams).ConfigureAwait(false);
    }

    private ValueTask InvokeServiceTrackedAsync(
        IRpcStub stub,
        object service,
        RpcSession session,
        IRpcGeneratedServerBridge generatedBridge,
        long methodId,
        long requestId,
        ReadOnlySequence<byte> arguments,
        IRpcByteBufferWriter? output,
        CancellationToken cancellationToken,
        SharpLinkCallContextSnapshot context)
    {
        var telemetry = SharpLinkTelemetry.StartServerCall(
            GetMethodDescriptor(stub, methodId), requestId);
        try
        {
            var invocation = InvokeServiceCoreAsync(
                stub,
                service,
                session,
                generatedBridge,
                methodId,
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
        IRpcStub stub,
        ServiceLease lease,
        RpcSession session,
        IRpcGeneratedServerBridge generatedBridge,
        long methodId,
        long requestId,
        ReadOnlySequence<byte> arguments,
        IRpcByteBufferWriter? output,
        CancellationToken cancellationToken,
        SharpLinkCallContextSnapshot context,
        bool hasRequestStreams)
    {
        Exception? terminalException = null;
        try
        {
            await InvokeServiceTrackedAsync(
                stub,
                lease.Service,
                session,
                generatedBridge,
                methodId,
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
        IRpcStub stub,
        object service,
        RpcSession session,
        IRpcGeneratedServerBridge generatedBridge,
        long methodId,
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
                    service, generatedBridge, methodId, requestId, arguments, cancellationToken)
                : stub.InvokeCancellableAsync(
                    service, generatedBridge, methodId, requestId, arguments, output, cancellationToken);
        }

        return InvokeInterceptedWithOwnedArgumentsAsync(
            interceptors,
            stub,
            service,
            session,
            generatedBridge,
            methodId,
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
        long methodId,
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
                methodId,
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
                methodId,
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
        long methodId,
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
        context.InterceptorMethodId = methodId;
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
        IRpcStub stub,
        long methodId,
        long requestId,
        CancellationToken cancellationToken)
    {
        var invocationContext = callContext as SharpLinkServerInvocationContext ??
            CreateServerInvocationContext(
                session,
                stub,
                methodId,
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
        long contractId,
        long methodId,
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
        if (SharpLinkCallContext.Current is { } callContext &&
            Volatile.Read(ref _services).TryGetValue(contractId, out var serviceInfo))
        {
            return MapServiceException(
                exception,
                callContext,
                session,
                serviceInfo.Stub,
                methodId,
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
