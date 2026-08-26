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
        var interceptors = (context as SharpLinkServerInvocationContext)?.Interceptors;
        if (interceptors is null || interceptors.Length == 0)
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
        ISharpLinkServerInterceptor[] interceptors,
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
            await new ServerPipelineFacts(
                interceptors,
                stub,
                service,
                session,
                generatedBridge,
                methodId,
                requestId,
                ReadOnlySequence<byte>.Empty,
                output,
                _runtimeContext.TimeProvider,
                cancellationToken).InvokeAsync(context).ConfigureAwait(false);
            return;
        }

        var rented = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            arguments.CopyTo(rented);
            var ownedArguments = new ReadOnlySequence<byte>(rented.AsMemory(0, length));
            await new ServerPipelineFacts(
                interceptors,
                stub,
                service,
                session,
                generatedBridge,
                methodId,
                requestId,
                ownedArguments,
                output,
                _runtimeContext.TimeProvider,
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

    private struct ServerPipelineFacts
    {
        private readonly ISharpLinkServerInterceptor[] _interceptors;
        private readonly IRpcStub _stub;
        private readonly object _service;
        private readonly RpcSession _session;
        private readonly IRpcGeneratedServerBridge _generatedBridge;
        private readonly long _methodId;
        private readonly long _requestId;
        private readonly ReadOnlySequence<byte> _arguments;
        private readonly IRpcByteBufferWriter? _output;
        private readonly TimeProvider _timeProvider;
        private readonly CancellationToken _cancellationToken;
        private long _started;

        public ServerPipelineFacts(
            ISharpLinkServerInterceptor[] interceptors,
            IRpcStub stub,
            object service,
            RpcSession session,
            IRpcGeneratedServerBridge generatedBridge,
            long methodId,
            long requestId,
            ReadOnlySequence<byte> arguments,
            IRpcByteBufferWriter? output,
            TimeProvider timeProvider,
            CancellationToken cancellationToken)
        {
            _interceptors = interceptors;
            _stub = stub;
            _service = service;
            _session = session;
            _generatedBridge = generatedBridge;
            _methodId = methodId;
            _requestId = requestId;
            _arguments = arguments;
            _output = output;
            _timeProvider = timeProvider;
            _cancellationToken = cancellationToken;
        }

        public async ValueTask InvokeAsync(SharpLinkServerInvocationContext context)
        {
            _started = _timeProvider.GetTimestamp();
            try
            {
                await InvokeNextAsync(0, context).ConfigureAwait(false);
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
                context.Elapsed = _timeProvider.GetElapsedTime(_started);
            }
        }

        private ValueTask InvokeNextAsync(int index, SharpLinkServerInvocationContext context)
        {
            if (index >= _interceptors.Length)
                return InvokeTerminalTrackedAsync(context);

            // Every interceptor invocation is a user-code re-entry boundary. Route it through
            // the same Server call-state/deadline claimant used by generated service methods
            // and server-stream MoveNextAsync so no later interceptor can run after terminal.
            _generatedBridge.EnsureUserCodeEntry(_requestId);

            var continuation = new ServerInterceptorContinuation(
                ServerContinuationState.Rent(this, index + 1));
            ValueTask invocation;
            try
            {
                invocation = _interceptors[index].InvokeAsync(context, continuation.InvokeAsync);
            }
            catch (Exception exception)
            {
                invocation = ValueTask.FromException(exception);
            }
            if (!invocation.IsCompletedSuccessfully)
            {
                if (continuation.IsSameInvocation(invocation))
                    return invocation;
                return AwaitInterceptorAsync(invocation, continuation);
            }
            EnsureResponseContinuationInvoked(continuation);
            return continuation.JoinAsync();
        }

        private async ValueTask AwaitInterceptorAsync(
            ValueTask invocation,
            ServerInterceptorContinuation continuation)
        {
            Exception? invocationException = null;
            try
            {
                await invocation.ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                invocationException = exception;
            }

            if (invocationException is null)
                EnsureResponseContinuationInvoked(continuation);
            try
            {
                await continuation.JoinAsync().ConfigureAwait(false);
            }
            catch (Exception continuationException) when (
                ReferenceEquals(invocationException, continuationException))
            {
                // The interceptor awaited next and propagated the same failure.
            }
            catch (Exception continuationException) when (invocationException is not null)
            {
                throw new AggregateException(invocationException, continuationException);
            }
            if (invocationException is not null)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(invocationException).Throw();
        }

        private void EnsureResponseContinuationInvoked(ServerInterceptorContinuation continuation)
        {
            if (_output is not null && !continuation.WasInvoked)
            {
                throw new InvalidOperationException(
                    "A Server interceptor must invoke its continuation for a response-bearing RPC.");
            }
        }

        private sealed class ServerInterceptorContinuation(ServerContinuationState state)
        {
            private int _invoked;
            private ServerContinuationState? _state = state;

            public bool WasInvoked => Volatile.Read(ref _invoked) != 0;

            public ValueTask InvokeAsync(SharpLinkServerInvocationContext context)
            {
                if (Interlocked.Exchange(ref _invoked, 1) != 0)
                {
                    return ValueTask.FromException(
                        new InvalidOperationException("An interceptor continuation can only be invoked once."));
                }
                return (_state ?? throw new InvalidOperationException("The interceptor continuation has expired."))
                    .InvokeAsync(context);
            }

            public ValueTask JoinAsync()
            {
                var state = Interlocked.Exchange(ref _state, null);
                return state is null ? ValueTask.CompletedTask : state.JoinAndReturnAsync();
            }

            public bool IsSameInvocation(ValueTask invocation)
            {
                var state = _state;
                if (state is null || !state.IsSameInvocation(invocation))
                    return false;
                if (!ReferenceEquals(Interlocked.CompareExchange(ref _state, null, state), state))
                    return false;
                state.Return();
                return true;
            }
        }

        private sealed class ServerContinuationState
        {
            private const int MaxRetained = 4096;
            private const int ShardCount = 32;
            private static readonly Shard[] Shards = CreateShards();

            private ServerPipelineFacts _owner;
            private bool _hasOwner;
            private int _nextIndex;
            private ValueTask _completion;
            private int _completionAvailable;

            public static ServerContinuationState Rent(ServerPipelineFacts owner, int nextIndex)
            {
                var shard = Shards[Thread.CurrentThread.ManagedThreadId & (ShardCount - 1)];
                ServerContinuationState state;
                lock (shard.Gate)
                {
                    if (shard.Stack.TryPop(out state!))
                    {
                        shard.Retained--;
                    }
                    else
                    {
                        state = new ServerContinuationState();
                    }
                }
                state._owner = owner;
                state._hasOwner = true;
                state._nextIndex = nextIndex;
                return state;
            }

            public ValueTask InvokeAsync(SharpLinkServerInvocationContext context)
            {
                var invocation = _hasOwner
                    ? _owner.InvokeNextAsync(_nextIndex, context)
                    : throw new InvalidOperationException("The interceptor continuation has expired.");
                _completion = invocation;
                Volatile.Write(ref _completionAvailable, 1);
                return invocation;
            }

            public bool IsSameInvocation(ValueTask invocation)
                => Volatile.Read(ref _completionAvailable) != 0 && _completion.Equals(invocation);

            public ValueTask JoinAndReturnAsync()
            {
                if (Volatile.Read(ref _completionAvailable) == 0 || _completion.IsCompleted)
                {
                    Return();
                    return ValueTask.CompletedTask;
                }
                return AwaitCompletionAndReturnAsync(this, _completion);
            }

            public void Return()
            {
                _owner = default;
                _hasOwner = false;
                _nextIndex = 0;
                _completion = default;
                Volatile.Write(ref _completionAvailable, 0);

                var returnShard = Shards[Thread.CurrentThread.ManagedThreadId & (ShardCount - 1)];
                lock (returnShard.Gate)
                {
                    if (returnShard.Retained < returnShard.Max)
                    {
                        returnShard.Retained++;
                        returnShard.Stack.Push(this);
                    }
                }
            }

            private static Shard[] CreateShards()
            {
                var shards = new Shard[ShardCount];
                var perShard = MaxRetained / ShardCount;
                for (var index = 0; index < ShardCount; index++)
                    shards[index] = new Shard(perShard);
                return shards;
            }

            private sealed class Shard(int max)
            {
                public readonly int Max = max;
                public readonly Lock Gate = new();
                public readonly Stack<ServerContinuationState> Stack = new(4);
                public int Retained;
            }

            private static async ValueTask AwaitCompletionAndReturnAsync(
                ServerContinuationState state,
                ValueTask completion)
            {
                try
                {
                    await completion.ConfigureAwait(false);
                }
                finally
                {
                    state.Return();
                }
            }
        }

        private async ValueTask InvokeTerminalTrackedAsync(SharpLinkServerInvocationContext context)
        {
            try
            {
                if (_output is null)
                {
                    await _stub.InvokeNoReturnCancellableAsync(
                        _service, _generatedBridge, _methodId, _requestId, _arguments, _cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    await _stub.InvokeCancellableAsync(
                        _service, _generatedBridge, _methodId, _requestId, _arguments, _output, _cancellationToken)
                        .ConfigureAwait(false);
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
                context.Elapsed = _timeProvider.GetElapsedTime(_started);
            }
        }
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
