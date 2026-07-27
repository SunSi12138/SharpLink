namespace SharpLink.Client;

internal sealed partial class SharpLinkClient
{
    private ValueTask<TResponse> InvokeUnaryInterceptedAsync<TRequest, TResponse>(
        RpcMethodDescriptor method,
        TRequest request,
        IRpcCodec<TRequest> requestCodec,
        IRpcCodec<TResponse> responseCodec,
        SharpLinkCallOptions options,
        CancellationToken cancellationToken)
        => new UnaryInterceptorState<TRequest, TResponse>(
            this, method, request, requestCodec, responseCodec, options, cancellationToken).InvokeTypedAsync();

    private ValueTask InvokeOneWayInterceptedAsync<TRequest, TStreams>(
        RpcMethodDescriptor method,
        TRequest request,
        IRpcCodec<TRequest> requestCodec,
        TStreams streams,
        SharpLinkCallOptions options,
        CancellationToken cancellationToken)
        where TStreams : struct, IRpcClientStreamWriter
        => new OneWayInterceptorState<TRequest, TStreams>(
            this, method, request, requestCodec, streams, options, cancellationToken).InvokeVoidAsync();

    private ValueTask<TResponse> InvokeClientStreamingInterceptedAsync<TRequest, TResponse, TStreams>(
        RpcMethodDescriptor method,
        TRequest request,
        IRpcCodec<TRequest> requestCodec,
        IRpcCodec<TResponse> responseCodec,
        TStreams streams,
        SharpLinkCallOptions options,
        CancellationToken cancellationToken)
        where TStreams : struct, IRpcClientStreamWriter
        => new ClientStreamingInterceptorState<TRequest, TResponse, TStreams>(
            this, method, request, requestCodec, responseCodec, streams, options, cancellationToken).InvokeTypedAsync();

    private IAsyncEnumerable<TResponse> InvokeServerStreamingIntercepted<TRequest, TResponse>(
        RpcMethodDescriptor method,
        TRequest request,
        IRpcCodec<TRequest> requestCodec,
        IRpcCodec<TResponse> responseCodec,
        SharpLinkCallOptions options,
        CancellationToken cancellationToken)
    {
        var invocation = new ServerStreamingInterceptorState<TRequest, TResponse>(
            this, method, request, requestCodec, responseCodec, options, cancellationToken).InvokeAsync();
        return new InterceptedAsyncEnumerable<TResponse>(invocation, method.ResponseNullable);
    }

    private IAsyncEnumerable<TResponse> InvokeDuplexStreamingIntercepted<TRequest, TResponse, TStreams>(
        RpcMethodDescriptor method,
        TRequest request,
        IRpcCodec<TRequest> requestCodec,
        IRpcCodec<TResponse> responseCodec,
        TStreams streams,
        SharpLinkCallOptions options,
        CancellationToken cancellationToken)
        where TStreams : struct, IRpcClientStreamWriter
    {
        var invocation = new DuplexStreamingInterceptorState<TRequest, TResponse, TStreams>(
            this, method, request, requestCodec, responseCodec, streams, options, cancellationToken).InvokeAsync();
        return new InterceptedAsyncEnumerable<TResponse>(invocation, method.ResponseNullable);
    }

    private abstract class ClientInterceptorState
    {
        private readonly SharpLinkClient _client;
        private readonly SharpLinkClientInvocationContext _context;
        private long _started;

        protected ClientInterceptorState(
            SharpLinkClient client,
            RpcMethodDescriptor method,
            object? request,
            SharpLinkCallOptions options,
            CancellationToken cancellationToken)
        {
            _client = client;
            _context = new SharpLinkClientInvocationContext(method, request, options, cancellationToken);
        }

        protected SharpLinkClient Client => _client;
        protected SharpLinkClientInvocationContext Context => _context;

        public async ValueTask<SharpLinkClientInvocationResult> InvokeAsync()
        {
            _started = Stopwatch.GetTimestamp();
            try
            {
                var result = await InvokeNextAsync(0, _context).ConfigureAwait(false);
                ValidateResult(result);
                if (_context.Status == SharpLinkInvocationStatus.Pending)
                    _context.Status = SharpLinkInvocationStatus.Succeeded;
                return result;
            }
            catch (Exception exception) when (IsCancellationException(exception))
            {
                _context.Status = SharpLinkInvocationStatus.Cancelled;
                _context.ErrorCode = SharpLinkErrorCode.Cancelled;
                _context.Exception = exception;
                throw;
            }
            catch (Exception exception)
            {
                _context.Status = SharpLinkInvocationStatus.Failed;
                _context.ErrorCode = exception is SharpLinkException sharpLinkException
                    ? sharpLinkException.Code
                    : SharpLinkErrorCode.Internal;
                _context.Exception = exception;
                throw;
            }
            finally
            {
                _context.Elapsed = Stopwatch.GetElapsedTime(_started);
            }
        }

        private ValueTask<SharpLinkClientInvocationResult> InvokeNextAsync(
            int index,
            SharpLinkClientInvocationContext context)
        {
            if (index >= _client._clientInterceptors.Length)
                return InvokeTerminalTrackedAsync(context);

            var continuation = new ClientInterceptorContinuation(
                ClientContinuationState.Rent(this, index + 1));
            ValueTask<SharpLinkClientInvocationResult> invocation;
            try
            {
                invocation = _client._clientInterceptors[index].InvokeAsync(context, continuation.InvokeAsync);
            }
            catch (Exception exception)
            {
                invocation = ValueTask.FromException<SharpLinkClientInvocationResult>(exception);
            }
            if (!invocation.IsCompletedSuccessfully)
            {
                if (continuation.IsSameInvocation(invocation))
                    return invocation;
                return AwaitInterceptorAndContinuationAsync(invocation, continuation);
            }
            var result = invocation.Result;
            var continuationCompletion = continuation.JoinAsync();
            return continuationCompletion.IsCompletedSuccessfully
                ? ValueTask.FromResult(result)
                : AwaitContinuationAsync(result, continuationCompletion);
        }

        private static async ValueTask<SharpLinkClientInvocationResult> AwaitContinuationAsync(
            SharpLinkClientInvocationResult result,
            ValueTask continuationCompletion)
        {
            await continuationCompletion.ConfigureAwait(false);
            return result;
        }

        private static async ValueTask<SharpLinkClientInvocationResult> AwaitInterceptorAndContinuationAsync(
            ValueTask<SharpLinkClientInvocationResult> invocation,
            ClientInterceptorContinuation continuation)
        {
            SharpLinkClientInvocationResult result = default;
            Exception? invocationException = null;
            try
            {
                result = await invocation.ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                invocationException = exception;
            }

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
            return result;
        }

        private sealed class ClientInterceptorContinuation(ClientContinuationState state)
        {
            private int _invoked;
            private ClientContinuationState? _state = state;

            public ValueTask<SharpLinkClientInvocationResult> InvokeAsync(
                SharpLinkClientInvocationContext context)
            {
                if (Interlocked.Exchange(ref _invoked, 1) != 0)
                {
                    return ValueTask.FromException<SharpLinkClientInvocationResult>(
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

            public bool IsSameInvocation(ValueTask<SharpLinkClientInvocationResult> invocation)
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

        private sealed class ClientContinuationState
        {
            private const int MaxRetained = 4096;
            private static ClientContinuationState? s_cached;
            private static int s_retained;

            private ClientInterceptorState? _owner;
            private ClientContinuationState? _nextCached;
            private int _nextIndex;
            private ValueTask<SharpLinkClientInvocationResult> _completion;
            private int _completionAvailable;

            public static ClientContinuationState Rent(ClientInterceptorState owner, int nextIndex)
            {
                ClientContinuationState state;
                while (true)
                {
                    state = Volatile.Read(ref s_cached)!;
                    if (state is null)
                    {
                        state = new ClientContinuationState();
                        break;
                    }
                    if (ReferenceEquals(
                            Interlocked.CompareExchange(ref s_cached, state._nextCached, state),
                            state))
                    {
                        Interlocked.Decrement(ref s_retained);
                        break;
                    }
                }
                state._nextCached = null;
                state._owner = owner;
                state._nextIndex = nextIndex;
                return state;
            }

            public ValueTask<SharpLinkClientInvocationResult> InvokeAsync(
                SharpLinkClientInvocationContext context)
            {
                var invocation = (_owner ?? throw new InvalidOperationException("The interceptor continuation has expired."))
                    .InvokeNextAsync(_nextIndex, context);
                _completion = invocation;
                Volatile.Write(ref _completionAvailable, 1);
                return invocation;
            }

            public bool IsSameInvocation(ValueTask<SharpLinkClientInvocationResult> invocation)
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
                _owner = null;
                _nextIndex = 0;
                _completion = default;
                Volatile.Write(ref _completionAvailable, 0);
                if (Interlocked.Increment(ref s_retained) > MaxRetained)
                {
                    Interlocked.Decrement(ref s_retained);
                    return;
                }
                ClientContinuationState? head;
                do
                {
                    head = Volatile.Read(ref s_cached);
                    _nextCached = head;
                } while (!ReferenceEquals(
                    Interlocked.CompareExchange(ref s_cached, this, head),
                    head));
            }

            private static async ValueTask AwaitCompletionAndReturnAsync(
                ClientContinuationState state,
                ValueTask<SharpLinkClientInvocationResult> completion)
            {
                try
                {
                    _ = await completion.ConfigureAwait(false);
                }
                finally
                {
                    state.Return();
                }
            }
        }

        private async ValueTask<SharpLinkClientInvocationResult> InvokeTerminalTrackedAsync(
            SharpLinkClientInvocationContext context)
        {
            try
            {
                var result = await InvokeTerminalAsync(context).ConfigureAwait(false);
                context.Status = SharpLinkInvocationStatus.Succeeded;
                return result;
            }
            catch (Exception exception) when (IsCancellationException(exception))
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
                context.Elapsed = Stopwatch.GetElapsedTime(_started);
            }
        }

        protected abstract ValueTask<SharpLinkClientInvocationResult> InvokeTerminalAsync(
            SharpLinkClientInvocationContext context);

        protected abstract void ValidateResult(SharpLinkClientInvocationResult result);

        private static bool IsCancellationException(Exception exception)
            => exception is OperationCanceledException or
               SharpLinkException { Code: SharpLinkErrorCode.Cancelled };
    }

    private sealed class UnaryInterceptorState<TRequest, TResponse> : ClientInterceptorState
    {
        private readonly RpcMethodDescriptor _method;
        private readonly TRequest _request;
        private readonly IRpcCodec<TRequest> _requestCodec;
        private readonly IRpcCodec<TResponse> _responseCodec;

        public UnaryInterceptorState(
            SharpLinkClient client,
            RpcMethodDescriptor method,
            TRequest request,
            IRpcCodec<TRequest> requestCodec,
            IRpcCodec<TResponse> responseCodec,
            SharpLinkCallOptions options,
            CancellationToken cancellationToken)
            : base(client, method, request, options, cancellationToken)
        {
            _method = method;
            _request = request;
            _requestCodec = requestCodec;
            _responseCodec = responseCodec;
        }

        public async ValueTask<TResponse> InvokeTypedAsync()
            => (await InvokeAsync().ConfigureAwait(false)).GetValue<TResponse>();

        protected override void ValidateResult(SharpLinkClientInvocationResult result)
        {
            var value = result.GetValue<TResponse>();
            if (!Context.Method.ResponseNullable && default(TResponse) is null && value is null)
                throw new InvalidCastException("A non-nullable intercepted RPC response was null.");
        }

        protected override async ValueTask<SharpLinkClientInvocationResult> InvokeTerminalAsync(
            SharpLinkClientInvocationContext context)
        {
            var control = Client.ResolveCallControl(
                context.Options, true, _method.HasMethodTimeout, _method.MethodTimeout);
            var response = await Client.InvokeUnaryWithOptionalRetryAsync(
                _method, _request, _requestCodec, _responseCodec, control, context.CancellationToken).ConfigureAwait(false);
            return new SharpLinkClientInvocationResult(response);
        }
    }

    private sealed class OneWayInterceptorState<TRequest, TStreams> : ClientInterceptorState
        where TStreams : struct, IRpcClientStreamWriter
    {
        private readonly RpcMethodDescriptor _method;
        private readonly TRequest _request;
        private readonly IRpcCodec<TRequest> _requestCodec;
        private readonly TStreams _streams;

        public OneWayInterceptorState(
            SharpLinkClient client,
            RpcMethodDescriptor method,
            TRequest request,
            IRpcCodec<TRequest> requestCodec,
            TStreams streams,
            SharpLinkCallOptions options,
            CancellationToken cancellationToken)
            : base(client, method, request, options, cancellationToken)
        {
            _method = method;
            _request = request;
            _requestCodec = requestCodec;
            _streams = streams;
        }

        public async ValueTask InvokeVoidAsync()
            => _ = await InvokeAsync().ConfigureAwait(false);

        protected override void ValidateResult(SharpLinkClientInvocationResult result)
        {
            if (result.Value is not null)
                throw new InvalidCastException("An intercepted OneWay result must be null.");
        }

        protected override async ValueTask<SharpLinkClientInvocationResult> InvokeTerminalAsync(
            SharpLinkClientInvocationContext context)
        {
            var control = Client.ResolveCallControl(
                context.Options, false, _method.HasMethodTimeout, _method.MethodTimeout);
            await Client.InvokeOneWayCoreAsync(
                _method,
                _request, _requestCodec, _streams, control, context.CancellationToken).ConfigureAwait(false);
            return default;
        }
    }

    private sealed class ClientStreamingInterceptorState<TRequest, TResponse, TStreams> : ClientInterceptorState
        where TStreams : struct, IRpcClientStreamWriter
    {
        private readonly RpcMethodDescriptor _method;
        private readonly TRequest _request;
        private readonly IRpcCodec<TRequest> _requestCodec;
        private readonly IRpcCodec<TResponse> _responseCodec;
        private readonly TStreams _streams;

        public ClientStreamingInterceptorState(
            SharpLinkClient client,
            RpcMethodDescriptor method,
            TRequest request,
            IRpcCodec<TRequest> requestCodec,
            IRpcCodec<TResponse> responseCodec,
            TStreams streams,
            SharpLinkCallOptions options,
            CancellationToken cancellationToken)
            : base(client, method, request, options, cancellationToken)
        {
            _method = method;
            _request = request;
            _requestCodec = requestCodec;
            _responseCodec = responseCodec;
            _streams = streams;
        }

        public async ValueTask<TResponse> InvokeTypedAsync()
            => (await InvokeAsync().ConfigureAwait(false)).GetValue<TResponse>();

        protected override void ValidateResult(SharpLinkClientInvocationResult result)
        {
            var value = result.GetValue<TResponse>();
            if (!Context.Method.ResponseNullable && default(TResponse) is null && value is null)
                throw new InvalidCastException("A non-nullable intercepted RPC response was null.");
        }

        protected override async ValueTask<SharpLinkClientInvocationResult> InvokeTerminalAsync(
            SharpLinkClientInvocationContext context)
        {
            var control = Client.ResolveCallControl(
                context.Options, false, _method.HasMethodTimeout, _method.MethodTimeout);
            var response = await Client.InvokeClientStreamingCoreAsync(
                _method,
                _request, _requestCodec, _responseCodec, _streams, control,
                context.CancellationToken).ConfigureAwait(false);
            return new SharpLinkClientInvocationResult(response);
        }
    }

    private sealed class ServerStreamingInterceptorState<TRequest, TResponse> : ClientInterceptorState
    {
        private readonly RpcMethodDescriptor _method;
        private readonly TRequest _request;
        private readonly IRpcCodec<TRequest> _requestCodec;
        private readonly IRpcCodec<TResponse> _responseCodec;

        public ServerStreamingInterceptorState(
            SharpLinkClient client,
            RpcMethodDescriptor method,
            TRequest request,
            IRpcCodec<TRequest> requestCodec,
            IRpcCodec<TResponse> responseCodec,
            SharpLinkCallOptions options,
            CancellationToken cancellationToken)
            : base(client, method, request, options, cancellationToken)
        {
            _method = method;
            _request = request;
            _requestCodec = requestCodec;
            _responseCodec = responseCodec;
        }

        protected override ValueTask<SharpLinkClientInvocationResult> InvokeTerminalAsync(
            SharpLinkClientInvocationContext context)
        {
            var stream = Client.InvokeServerStreamingCore(
                _method, _request, _requestCodec, _responseCodec, context.Options, context.CancellationToken);
            return ValueTask.FromResult(new SharpLinkClientInvocationResult(stream));
        }

        protected override void ValidateResult(SharpLinkClientInvocationResult result)
        {
            if (result.Value is not IAsyncEnumerable<TResponse>)
                throw new InvalidCastException($"The intercepted result is not {typeof(IAsyncEnumerable<TResponse>).FullName}.");
        }
    }

    private sealed class DuplexStreamingInterceptorState<TRequest, TResponse, TStreams> : ClientInterceptorState
        where TStreams : struct, IRpcClientStreamWriter
    {
        private readonly RpcMethodDescriptor _method;
        private readonly TRequest _request;
        private readonly IRpcCodec<TRequest> _requestCodec;
        private readonly IRpcCodec<TResponse> _responseCodec;
        private readonly TStreams _streams;

        public DuplexStreamingInterceptorState(
            SharpLinkClient client,
            RpcMethodDescriptor method,
            TRequest request,
            IRpcCodec<TRequest> requestCodec,
            IRpcCodec<TResponse> responseCodec,
            TStreams streams,
            SharpLinkCallOptions options,
            CancellationToken cancellationToken)
            : base(client, method, request, options, cancellationToken)
        {
            _method = method;
            _request = request;
            _requestCodec = requestCodec;
            _responseCodec = responseCodec;
            _streams = streams;
        }

        protected override ValueTask<SharpLinkClientInvocationResult> InvokeTerminalAsync(
            SharpLinkClientInvocationContext context)
        {
            var stream = Client.InvokeDuplexStreamingCore(
                _method, _request, _requestCodec, _responseCodec, _streams,
                context.Options, context.CancellationToken);
            return ValueTask.FromResult(new SharpLinkClientInvocationResult(stream));
        }

        protected override void ValidateResult(SharpLinkClientInvocationResult result)
        {
            if (result.Value is not IAsyncEnumerable<TResponse>)
                throw new InvalidCastException($"The intercepted result is not {typeof(IAsyncEnumerable<TResponse>).FullName}.");
        }
    }

    private sealed class InterceptedAsyncEnumerable<T>(
        ValueTask<SharpLinkClientInvocationResult> invocation,
        bool responseNullable) : IAsyncEnumerable<T>
    {
        private int _enumerated;

        public async IAsyncEnumerator<T> GetAsyncEnumerator(
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _enumerated, 1) != 0)
                throw new InvalidOperationException("An intercepted RPC stream can only be enumerated once.");

            var stream = (await invocation.ConfigureAwait(false)).GetValue<IAsyncEnumerable<T>>();
            await foreach (var item in stream.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                if (!responseNullable && default(T) is null && item is null)
                    throw new InvalidCastException("A non-nullable intercepted RPC stream response was null.");
                yield return item;
            }
        }
    }
}
