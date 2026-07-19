namespace SharpLink.Server;

internal sealed partial class SharpLinkServer
{
    private ValueTask InvokeServiceAsync(
        ServiceRegistration registration,
        ServerConnectionState connection,
        IRpcSession session,
        long methodId,
        long requestId,
        ReadOnlySequence<byte> arguments,
        IRpcByteBufferWriter? output,
        CancellationToken cancellationToken,
        SharpLinkCallContextSnapshot context)
    {
        if (registration.TryGetStaticSingleton(out var singleton))
        {
            return InvokeServiceTrackedAsync(
                registration.Stub,
                singleton,
                session,
                methodId,
                requestId,
                arguments,
                output,
                cancellationToken,
                context);
        }

        ValueTask<ServiceLease> acquisition;
        try
        {
            var isStream = false;
            if (registration.Module is not null)
            {
                var descriptor = GetMethodDescriptor(registration.Stub, methodId);
                isStream = descriptor.Kind is RpcMethodKind.ClientStreaming or
                    RpcMethodKind.ServerStreaming or RpcMethodKind.DuplexStreaming;
            }
            acquisition = registration.AcquireAsync(connection, isStream);
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
                methodId,
                requestId,
                arguments,
                output,
                cancellationToken,
                context);
        }

        return InvokeAcquiredServiceAsync(
            registration.Stub,
            acquisition.Result,
            session,
            methodId,
            requestId,
            arguments,
            output,
            cancellationToken,
            context);
    }

    private ValueTask InvokeAcquiredServiceAsync(
        IRpcStub stub,
        ServiceLease lease,
        IRpcSession session,
        long methodId,
        long requestId,
        ReadOnlySequence<byte> arguments,
        IRpcByteBufferWriter? output,
        CancellationToken cancellationToken,
        SharpLinkCallContextSnapshot context)
    {

        if (!lease.RequiresDisposal)
        {
            return InvokeServiceTrackedAsync(
                stub,
                lease.Service,
                session,
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
            methodId,
            requestId,
            arguments,
            output,
            cancellationToken,
            context);
    }

    private async ValueTask InvokeServiceAfterAcquisitionAsync(
        ValueTask<ServiceLease> acquisition,
        IRpcStub stub,
        IRpcSession session,
        long methodId,
        long requestId,
        ReadOnlySequence<byte> arguments,
        IRpcByteBufferWriter? output,
        CancellationToken cancellationToken,
        SharpLinkCallContextSnapshot context)
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
            methodId,
            requestId,
            arguments,
            output,
            cancellationToken,
            context).ConfigureAwait(false);
    }

    private ValueTask InvokeServiceTrackedAsync(
        IRpcStub stub,
        object service,
        IRpcSession session,
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
        IRpcSession session,
        long methodId,
        long requestId,
        ReadOnlySequence<byte> arguments,
        IRpcByteBufferWriter? output,
        CancellationToken cancellationToken,
        SharpLinkCallContextSnapshot context)
    {
        Exception? invocationException = null;
        try
        {
            await InvokeServiceTrackedAsync(
                stub,
                lease.Service,
                session,
                methodId,
                requestId,
                arguments,
                output,
                cancellationToken,
                context).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            invocationException = exception;
            throw;
        }
        finally
        {
            try
            {
                await lease.DisposeAsync().ConfigureAwait(false);
            }
            catch when (invocationException is not null)
            {
            }
        }
    }

    private ValueTask InvokeServiceCoreAsync(
        IRpcStub stub,
        object service,
        IRpcSession session,
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
        if (_serverInterceptors.Length == 0)
        {
            return output is null
                ? stub.InvokeNoReturnCancellableAsync(
                    service, session, methodId, requestId, arguments, cancellationToken)
                : stub.InvokeCancellableAsync(
                    service, session, methodId, requestId, arguments, output, cancellationToken);
        }

        return InvokeInterceptedWithOwnedArgumentsAsync(
            stub,
            service,
            session,
            methodId,
            requestId,
            arguments,
            output,
            cancellationToken,
            (SharpLinkServerInvocationContext)context);
    }

    private async ValueTask InvokeInterceptedWithOwnedArgumentsAsync(
        IRpcStub stub,
        object service,
        IRpcSession session,
        long methodId,
        long requestId,
        ReadOnlySequence<byte> arguments,
        IRpcByteBufferWriter? output,
        CancellationToken cancellationToken,
        SharpLinkServerInvocationContext context)
    {
        var length = checked((int)arguments.Length);
        var maxArgumentsBytes = ((RpcSession)session).NegotiatedMaxFramePayloadBytes;
        if (length > maxArgumentsBytes)
        {
            throw new SharpLinkException(
                SharpLinkErrorCode.ResourceExhausted,
                $"RPC arguments exceed the negotiated {maxArgumentsBytes}-byte frame limit.");
        }

        if (length == 0)
        {
            await new ServerInterceptorPipeline(
                _serverInterceptors,
                stub,
                service,
                session,
                methodId,
                requestId,
                ReadOnlySequence<byte>.Empty,
                output,
                cancellationToken).InvokeAsync(context).ConfigureAwait(false);
            return;
        }

        var rented = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            arguments.CopyTo(rented);
            var ownedArguments = new ReadOnlySequence<byte>(rented.AsMemory(0, length));
            await new ServerInterceptorPipeline(
                _serverInterceptors,
                stub,
                service,
                session,
                methodId,
                requestId,
                ownedArguments,
                output,
                cancellationToken).InvokeAsync(context).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented, clearArray: true);
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
        IRpcSession session,
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
                callContext.Deadline,
                callContext.Metadata,
                cancellationToken);
        return MapServiceException(exception, invocationContext);
    }

    private SharpLinkException MapServiceException(
        Exception exception,
        SharpLinkServerInvocationContext context)
    {
        context.Exception = exception;
        context.Status = exception is OperationCanceledException
            ? SharpLinkInvocationStatus.Cancelled
            : SharpLinkInvocationStatus.Failed;
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

    private SharpLinkException MapStreamServiceException(
        IRpcSession session,
        long requestId,
        long contractId,
        long methodId,
        Exception exception)
    {
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

    private sealed class ServerInterceptorPipeline
    {
        private readonly ISharpLinkServerInterceptor[] _interceptors;
        private readonly IRpcStub _stub;
        private readonly object _service;
        private readonly IRpcSession _session;
        private readonly long _methodId;
        private readonly long _requestId;
        private readonly ReadOnlySequence<byte> _arguments;
        private readonly IRpcByteBufferWriter? _output;
        private readonly CancellationToken _cancellationToken;
        private readonly SharpLinkServerInvocationDelegate _next;
        private int _index;
        private long _started;

        public ServerInterceptorPipeline(
            ISharpLinkServerInterceptor[] interceptors,
            IRpcStub stub,
            object service,
            IRpcSession session,
            long methodId,
            long requestId,
            ReadOnlySequence<byte> arguments,
            IRpcByteBufferWriter? output,
            CancellationToken cancellationToken)
        {
            _interceptors = interceptors;
            _stub = stub;
            _service = service;
            _session = session;
            _methodId = methodId;
            _requestId = requestId;
            _arguments = arguments;
            _output = output;
            _cancellationToken = cancellationToken;
            _next = InvokeNextAsync;
        }

        public async ValueTask InvokeAsync(SharpLinkServerInvocationContext context)
        {
            _started = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                await InvokeNextAsync(context).ConfigureAwait(false);
                if (context.Status == SharpLinkInvocationStatus.Pending)
                    context.Status = SharpLinkInvocationStatus.Succeeded;
            }
            catch (OperationCanceledException exception)
            {
                context.Status = SharpLinkInvocationStatus.Cancelled;
                context.ErrorCode = SharpLinkErrorCode.Cancelled;
                context.Exception = exception;
                throw;
            }
            catch (Exception exception)
            {
                context.Status = SharpLinkInvocationStatus.Failed;
                context.ErrorCode = exception is SharpLinkException sharpLinkException
                    ? sharpLinkException.Code
                    : SharpLinkErrorCode.Internal;
                context.Exception = exception;
                throw;
            }
            finally
            {
                context.Elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(_started);
            }
        }

        private ValueTask InvokeNextAsync(SharpLinkServerInvocationContext context)
        {
            var index = _index++;
            if (index < _interceptors.Length)
                return _interceptors[index].InvokeAsync(context, _next);

            return InvokeTerminalTrackedAsync(context);
        }

        private async ValueTask InvokeTerminalTrackedAsync(SharpLinkServerInvocationContext context)
        {
            try
            {
                if (_output is null)
                {
                    await _stub.InvokeNoReturnCancellableAsync(
                        _service, _session, _methodId, _requestId, _arguments, _cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    await _stub.InvokeCancellableAsync(
                        _service, _session, _methodId, _requestId, _arguments, _output, _cancellationToken)
                        .ConfigureAwait(false);
                }
                context.Status = SharpLinkInvocationStatus.Succeeded;
            }
            finally
            {
                context.Elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(_started);
            }
        }
    }
}
