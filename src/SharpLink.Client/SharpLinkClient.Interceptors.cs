namespace SharpLink.Client;

internal sealed partial class SharpLinkClient
{
    private ValueTask<TResponse> InvokeUnaryInterceptedAsync<TRequest, TResponse>(
        RpcMethodDescriptor method,
        TRequest request,
        IRpcCodec<TRequest> requestCodec,
        IRpcCodec<TResponse> responseCodec,
        ISharpLinkClientInterceptor[] interceptors,
        SharpLinkMetadata? metadata,
        CancellationToken cancellationToken)
        => new UnaryInterceptorState<TRequest, TResponse>(
            this, method, request, requestCodec, responseCodec, interceptors, metadata, cancellationToken).InvokeTypedAsync();

    private ValueTask InvokeOneWayInterceptedAsync<TRequest, TStreams>(
        RpcMethodDescriptor method,
        TRequest request,
        IRpcCodec<TRequest> requestCodec,
        TStreams streams,
        ISharpLinkClientInterceptor[] interceptors,
        SharpLinkMetadata? metadata,
        CancellationToken cancellationToken)
        where TStreams : struct, IRpcClientStreamWriter
        => new OneWayInterceptorState<TRequest, TStreams>(
            this, method, request, requestCodec, streams, interceptors, metadata, cancellationToken).InvokeVoidAsync();

    private ValueTask<TResponse> InvokeClientStreamingInterceptedAsync<TRequest, TResponse, TStreams>(
        RpcMethodDescriptor method,
        TRequest request,
        IRpcCodec<TRequest> requestCodec,
        IRpcCodec<TResponse> responseCodec,
        TStreams streams,
        ISharpLinkClientInterceptor[] interceptors,
        SharpLinkMetadata? metadata,
        CancellationToken cancellationToken)
        where TStreams : struct, IRpcClientStreamWriter
        => new ClientStreamingInterceptorState<TRequest, TResponse, TStreams>(
            this, method, request, requestCodec, responseCodec, streams, interceptors, metadata, cancellationToken).InvokeTypedAsync();

    private IAsyncEnumerable<TResponse> InvokeServerStreamingIntercepted<TRequest, TResponse>(
        RpcMethodDescriptor method,
        TRequest request,
        IRpcCodec<TRequest> requestCodec,
        IRpcCodec<TResponse> responseCodec,
        ISharpLinkClientInterceptor[] interceptors,
        ResolvedCallControl control,
        CancellationToken cancellationToken)
    {
        var state = new ServerStreamingInterceptorState<TRequest, TResponse>(
            this, method, request, requestCodec, responseCodec, interceptors, control, cancellationToken);
        return new InterceptedAsyncEnumerable<TResponse>(
            state.InvokeAsync(), method.ResponseNullable, state.Deadline,
            _runtimeContext.TimeProvider, state.InvocationCancellation);
    }

    private IAsyncEnumerable<TResponse> InvokeDuplexStreamingIntercepted<TRequest, TResponse, TStreams>(
        RpcMethodDescriptor method,
        TRequest request,
        IRpcCodec<TRequest> requestCodec,
        IRpcCodec<TResponse> responseCodec,
        TStreams streams,
        ISharpLinkClientInterceptor[] interceptors,
        ResolvedCallControl control,
        CancellationToken cancellationToken)
        where TStreams : struct, IRpcClientStreamWriter
    {
        var state = new DuplexStreamingInterceptorState<TRequest, TResponse, TStreams>(
            this, method, request, requestCodec, responseCodec, streams, interceptors, control, cancellationToken);
        return new InterceptedAsyncEnumerable<TResponse>(
            state.InvokeAsync(), method.ResponseNullable, state.Deadline,
            _runtimeContext.TimeProvider, state.InvocationCancellation);
    }

    private abstract class ClientInterceptorState
    {
        private readonly SharpLinkClient _client;
        private readonly ISharpLinkClientInterceptor[] _interceptors;
        private readonly SharpLinkClientInvocationContext _context;
        private readonly ResolvedCallControl _control;
        private long _started;

        protected ClientInterceptorState(
            SharpLinkClient client,
            RpcMethodDescriptor method,
            object? request,
            ISharpLinkClientInterceptor[] interceptors,
            SharpLinkMetadata? metadata,
            CancellationToken cancellationToken,
            ResolvedCallControl? resolvedControl = null)
        {
            _client = client;
            _interceptors = interceptors;
            _control = resolvedControl ?? client.ResolveCallControl(
                metadata,
                method.Kind == RpcMethodKind.Unary,
                method.HasMethodTimeout,
                method.MethodTimeout);
            _context = new SharpLinkClientInvocationContext(
                method, request, _control.Metadata, cancellationToken);
        }

        protected SharpLinkClient Client => _client;
        protected SharpLinkClientInvocationContext Context => _context;
        internal RpcDeadline Deadline => _control.Deadline;
        internal CancellationToken InvocationCancellation => _context.CancellationToken;

        public ValueTask<SharpLinkClientInvocationResult> InvokeAsync()
            => RunChainAsync();

        private async ValueTask<SharpLinkClientInvocationResult> RunChainAsync()
        {
            _started = _client._runtimeContext.TimeProvider.GetTimestamp();
            try
            {
                var result = await InvokeNextAsync(0, _context).ConfigureAwait(false);
                ThrowIfFrozenDeadlineExpired();
                ValidateResult(result);
                MarkChainSucceeded(_context);
                return result;
            }
            catch (Exception exception)
            {
                MarkTerminalFailed(_context, exception);
                throw;
            }
            finally
            {
                MarkTerminalElapsed(_context);
            }
        }

        protected async ValueTask<TResult> RunTypedChainAsync<TResult>()
        {
            _started = _client._runtimeContext.TimeProvider.GetTimestamp();
            try
            {
                var result = await InvokeNextAsync(0, _context).ConfigureAwait(false);
                ThrowIfFrozenDeadlineExpired();
                ValidateResult(result);
                MarkChainSucceeded(_context);
                return result.GetValue<TResult>();
            }
            catch (Exception exception)
            {
                MarkTerminalFailed(_context, exception);
                throw;
            }
            finally
            {
                MarkTerminalElapsed(_context);
            }
        }

        protected void MarkChainSucceeded(SharpLinkClientInvocationContext context)
        {
            if (context.Status == SharpLinkInvocationStatus.Pending)
                context.Status = SharpLinkInvocationStatus.Succeeded;
        }

        protected async ValueTask RunVoidChainAsync()
        {
            _started = _client._runtimeContext.TimeProvider.GetTimestamp();
            try
            {
                var result = await InvokeNextAsync(0, _context).ConfigureAwait(false);
                ThrowIfFrozenDeadlineExpired();
                ValidateResult(result);
                MarkChainSucceeded(_context);
            }
            catch (Exception exception)
            {
                MarkTerminalFailed(_context, exception);
                throw;
            }
            finally
            {
                MarkTerminalElapsed(_context);
            }
        }

        private ValueTask<SharpLinkClientInvocationResult> InvokeNextAsync(
            int index,
            SharpLinkClientInvocationContext context)
        {
            if (index >= _interceptors.Length)
                return InvokeTerminalTrackedAsync(context);

            var continuation = new ClientInterceptorContinuation(
                ClientContinuationState.Rent(this, index + 1));
            ValueTask<SharpLinkClientInvocationResult> invocation;
            try
            {
                invocation = _interceptors[index].InvokeAsync(context, continuation.InvokeAsync);
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
            private const int ShardCount = 32;
            private static readonly Shard[] Shards = CreateShards();

            private ClientInterceptorState? _owner;
            private int _nextIndex;
            private ValueTask<SharpLinkClientInvocationResult> _completion;
            private int _completionAvailable;

            public static ClientContinuationState Rent(ClientInterceptorState owner, int nextIndex)
            {
                var shard = Shards[Thread.CurrentThread.ManagedThreadId & (ShardCount - 1)];
                ClientContinuationState state;
                lock (shard.Gate)
                {
                    if (shard.Stack.TryPop(out state!))
                    {
                        shard.Retained--;
                    }
                    else
                    {
                        state = new ClientContinuationState();
                    }
                }
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
                public readonly Stack<ClientContinuationState> Stack = new(4);
                public int Retained;
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

        private ValueTask<SharpLinkClientInvocationResult> InvokeTerminalTrackedAsync(
            SharpLinkClientInvocationContext context)
            => InvokeTerminalAsync(context);

        protected ResolvedCallControl GetTerminalControl(SharpLinkClientInvocationContext context)
            => new(
                _control.Deadline,
                context.Metadata is { Count: > 0 } ? context.Metadata : null);

        private void ThrowIfFrozenDeadlineExpired()
        {
            if (_control.Deadline.IsExpired(_client._runtimeContext.TimeProvider))
                throw CreateDeadlineExceededException();
        }

        protected void MarkTerminalSucceeded(SharpLinkClientInvocationContext context)
            => context.Status = SharpLinkInvocationStatus.Succeeded;

        protected void MarkTerminalFailed(SharpLinkClientInvocationContext context, Exception exception)
        {
            if (IsCancellationException(exception))
            {
                context.Status = SharpLinkInvocationStatus.Cancelled;
                context.ErrorCode = SharpLinkErrorCode.Cancelled;
            }
            else
            {
                context.Status = SharpLinkInvocationStatus.Failed;
                context.ErrorCode = exception is SharpLinkException sharpLinkException
                    ? sharpLinkException.Code
                    : SharpLinkErrorCode.Internal;
            }
            context.Exception = exception;
        }

        protected void MarkTerminalElapsed(SharpLinkClientInvocationContext context)
            => context.Elapsed = _client._runtimeContext.TimeProvider.GetElapsedTime(_started);

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
            ISharpLinkClientInterceptor[] interceptors,
            SharpLinkMetadata? metadata,
            CancellationToken cancellationToken)
            : base(client, method, request, interceptors, metadata, cancellationToken)
        {
            _method = method;
            _request = request;
            _requestCodec = requestCodec;
            _responseCodec = responseCodec;
        }

        public ValueTask<TResponse> InvokeTypedAsync()
            => RunTypedChainAsync<TResponse>();

        protected override void ValidateResult(SharpLinkClientInvocationResult result)
        {
            var value = result.GetValue<TResponse>();
            if (!Context.Method.ResponseNullable && default(TResponse) is null && value is null)
                throw new InvalidCastException("A non-nullable intercepted RPC response was null.");
        }

        protected override async ValueTask<SharpLinkClientInvocationResult> InvokeTerminalAsync(
            SharpLinkClientInvocationContext context)
        {
            try
            {
                var control = GetTerminalControl(context);
                var response = await Client.InvokeUnaryWithOptionalRetryAsync(
                    _method, _request, _requestCodec, _responseCodec, control, context.CancellationToken).ConfigureAwait(false);
                MarkTerminalSucceeded(context);
                return new SharpLinkClientInvocationResult(response);
            }
            catch (Exception exception)
            {
                MarkTerminalFailed(context, exception);
                throw;
            }
            finally
            {
                MarkTerminalElapsed(context);
            }
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
            ISharpLinkClientInterceptor[] interceptors,
            SharpLinkMetadata? metadata,
            CancellationToken cancellationToken)
            : base(client, method, request, interceptors, metadata, cancellationToken)
        {
            _method = method;
            _request = request;
            _requestCodec = requestCodec;
            _streams = streams;
        }

        public ValueTask InvokeVoidAsync()
            => RunVoidChainAsync();

        protected override void ValidateResult(SharpLinkClientInvocationResult result)
        {
            if (result.Value is not null)
                throw new InvalidCastException("An intercepted OneWay result must be null.");
        }

        protected override async ValueTask<SharpLinkClientInvocationResult> InvokeTerminalAsync(
            SharpLinkClientInvocationContext context)
        {
            try
            {
                var control = GetTerminalControl(context);
                await Client.InvokeOneWayCoreAsync(
                    _method,
                    _request, _requestCodec, _streams, control, context.CancellationToken).ConfigureAwait(false);
                MarkTerminalSucceeded(context);
                return default;
            }
            catch (Exception exception)
            {
                MarkTerminalFailed(context, exception);
                throw;
            }
            finally
            {
                MarkTerminalElapsed(context);
            }
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
            ISharpLinkClientInterceptor[] interceptors,
            SharpLinkMetadata? metadata,
            CancellationToken cancellationToken)
            : base(client, method, request, interceptors, metadata, cancellationToken)
        {
            _method = method;
            _request = request;
            _requestCodec = requestCodec;
            _responseCodec = responseCodec;
            _streams = streams;
        }

        public ValueTask<TResponse> InvokeTypedAsync()
            => RunTypedChainAsync<TResponse>();

        protected override void ValidateResult(SharpLinkClientInvocationResult result)
        {
            var value = result.GetValue<TResponse>();
            if (!Context.Method.ResponseNullable && default(TResponse) is null && value is null)
                throw new InvalidCastException("A non-nullable intercepted RPC response was null.");
        }

        protected override async ValueTask<SharpLinkClientInvocationResult> InvokeTerminalAsync(
            SharpLinkClientInvocationContext context)
        {
            try
            {
                var control = GetTerminalControl(context);
                var response = await Client.InvokeClientStreamingCoreAsync(
                    _method,
                    _request, _requestCodec, _responseCodec, _streams, control,
                    context.CancellationToken).ConfigureAwait(false);
                MarkTerminalSucceeded(context);
                return new SharpLinkClientInvocationResult(response);
            }
            catch (Exception exception)
            {
                MarkTerminalFailed(context, exception);
                throw;
            }
            finally
            {
                MarkTerminalElapsed(context);
            }
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
            ISharpLinkClientInterceptor[] interceptors,
            ResolvedCallControl control,
            CancellationToken cancellationToken)
            : base(client, method, request, interceptors, control.Metadata, cancellationToken, control)
        {
            _method = method;
            _request = request;
            _requestCodec = requestCodec;
            _responseCodec = responseCodec;
        }

        protected override ValueTask<SharpLinkClientInvocationResult> InvokeTerminalAsync(
            SharpLinkClientInvocationContext context)
        {
            try
            {
                var stream = Client.InvokeServerStreamingCore(
                    _method, _request, _requestCodec, _responseCodec,
                    GetTerminalControl(context), context.CancellationToken);
                MarkTerminalSucceeded(context);
                return ValueTask.FromResult(new SharpLinkClientInvocationResult(stream));
            }
            catch (Exception exception)
            {
                MarkTerminalFailed(context, exception);
                throw;
            }
            finally
            {
                MarkTerminalElapsed(context);
            }
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
            ISharpLinkClientInterceptor[] interceptors,
            ResolvedCallControl control,
            CancellationToken cancellationToken)
            : base(client, method, request, interceptors, control.Metadata, cancellationToken, control)
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
            try
            {
                var stream = Client.InvokeDuplexStreamingCore(
                    _method, _request, _requestCodec, _responseCodec, _streams,
                    GetTerminalControl(context), context.CancellationToken);
                MarkTerminalSucceeded(context);
                return ValueTask.FromResult(new SharpLinkClientInvocationResult(stream));
            }
            catch (Exception exception)
            {
                MarkTerminalFailed(context, exception);
                throw;
            }
            finally
            {
                MarkTerminalElapsed(context);
            }
        }

        protected override void ValidateResult(SharpLinkClientInvocationResult result)
        {
            if (result.Value is not IAsyncEnumerable<TResponse>)
                throw new InvalidCastException($"The intercepted result is not {typeof(IAsyncEnumerable<TResponse>).FullName}.");
        }
    }

    private sealed class InterceptedAsyncEnumerable<T>(
        ValueTask<SharpLinkClientInvocationResult> invocation,
        bool responseNullable,
        RpcDeadline deadline,
        TimeProvider timeProvider,
        CancellationToken invocationCancellation) : IAsyncEnumerable<T>
    {
        private int _enumerated;

        public async IAsyncEnumerator<T> GetAsyncEnumerator(
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _enumerated, 1) != 0)
                throw new InvalidOperationException("An intercepted RPC stream can only be enumerated once.");

            ThrowIfDeadlineExpired();
            var stream = (await invocation.ConfigureAwait(false)).GetValue<IAsyncEnumerable<T>>();
            ThrowIfDeadlineExpired();

            using var lifetimeCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                invocationCancellation, cancellationToken);
            var enumerator = stream.GetAsyncEnumerator(lifetimeCancellation.Token);
            var deadlineWon = false;
            try
            {
                while (true)
                {
                    ThrowIfDeadlineExpired();
                    var moveNext = enumerator.MoveNextAsync();
                    bool hasNext;
                    if (!deadline.HasValue || moveNext.IsCompletedSuccessfully)
                    {
                        hasNext = await moveNext.ConfigureAwait(false);
                    }
                    else
                    {
                        var moveNextTask = moveNext.AsTask();
                        if (!await SharpLinkTimer.WaitAsync(
                                moveNextTask, deadline, timeProvider, lifetimeCancellation.Token).ConfigureAwait(false))
                        {
                            deadlineWon = true;
                            lifetimeCancellation.Cancel();
                            _ = ObserveAbandonedMoveNextAsync(moveNextTask);
                            throw CreateDeadlineExceededException();
                        }
                        hasNext = await moveNextTask.ConfigureAwait(false);
                    }

                    if (!hasNext)
                        yield break;
                    ThrowIfDeadlineExpired();
                    var item = enumerator.Current;
                    if (!responseNullable && default(T) is null && item is null)
                        throw new InvalidCastException("A non-nullable intercepted RPC stream response was null.");
                    yield return item;
                }
            }
            finally
            {
                lifetimeCancellation.Cancel();
                try
                {
                    var dispose = enumerator.DisposeAsync();
                    if (deadlineWon && !dispose.IsCompletedSuccessfully)
                        _ = ObserveAbandonedDisposeAsync(dispose);
                    else
                        await dispose.ConfigureAwait(false);
                }
                catch when (deadlineWon)
                {
                    // The deadline is already the terminal result. Disposal is best-effort for
                    // a short-circuited local enumerator that may ignore cancellation.
                }
            }
        }

        private static async Task ObserveAbandonedMoveNextAsync(Task<bool> task)
        {
            try { _ = await task.ConfigureAwait(false); }
            catch { }
        }

        private static async Task ObserveAbandonedDisposeAsync(ValueTask dispose)
        {
            try { await dispose.ConfigureAwait(false); }
            catch { }
        }

        private void ThrowIfDeadlineExpired()
        {
            if (deadline.IsExpired(timeProvider))
                throw CreateDeadlineExceededException();
        }
    }
}
