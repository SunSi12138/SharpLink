namespace SharpLink.Server;

internal sealed partial class SharpLinkServer
{
    private ValueTask InvokeServiceAsync(
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

        return new ServerInterceptorPipeline(
            _serverInterceptors,
            stub,
            service,
            session,
            methodId,
            requestId,
            arguments,
            output,
            cancellationToken).InvokeAsync((SharpLinkServerInvocationContext)context);
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
            services.TryGetValue(contractId, out var serviceInfo))
        {
            return MapServiceException(
                exception,
                callContext,
                session,
                serviceInfo.stub,
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
