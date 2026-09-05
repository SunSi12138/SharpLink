namespace SharpLink.Client;

internal sealed partial class SharpLinkClient
{
    private ValueTask<TResponse> InvokeUnaryInterceptedAsync<TRequest, TResponse>(
        RpcMethodDescriptor method,
        TRequest request,
        IRpcCodec<TRequest> requestCodec,
        IRpcCodec<TResponse> responseCodec,
        ClientInterceptorGeneration interceptors,
        ResolvedCallControl control,
        CancellationToken cancellationToken)
        => new UnaryInterceptorState<TRequest, TResponse>(
            this, method, request, requestCodec, responseCodec, interceptors, control, cancellationToken).InvokeTypedAsync();

    private ValueTask InvokeOneWayInterceptedAsync<TRequest, TStreams>(
        RpcMethodDescriptor method,
        TRequest request,
        IRpcCodec<TRequest> requestCodec,
        TStreams streams,
        ClientInterceptorGeneration interceptors,
        ResolvedCallControl control,
        CancellationToken cancellationToken)
        where TStreams : struct, IRpcClientStreamWriter
        => new OneWayInterceptorState<TRequest, TStreams>(
            this, method, request, requestCodec, streams, interceptors, control, cancellationToken).InvokeVoidAsync();

    private ValueTask<TResponse> InvokeClientStreamingInterceptedAsync<TRequest, TResponse, TStreams>(
        RpcMethodDescriptor method,
        TRequest request,
        IRpcCodec<TRequest> requestCodec,
        IRpcCodec<TResponse> responseCodec,
        TStreams streams,
        ClientInterceptorGeneration interceptors,
        ResolvedCallControl control,
        CancellationToken cancellationToken)
        where TStreams : struct, IRpcClientStreamWriter
        => new ClientStreamingInterceptorState<TRequest, TResponse, TStreams>(
            this, method, request, requestCodec, responseCodec, streams, interceptors, control, cancellationToken).InvokeTypedAsync();

    private IAsyncEnumerable<TResponse> InvokeServerStreamingIntercepted<TRequest, TResponse>(
        RpcMethodDescriptor method,
        TRequest request,
        IRpcCodec<TRequest> requestCodec,
        IRpcCodec<TResponse> responseCodec,
        ClientInterceptorGeneration interceptors,
        ResolvedCallControl control,
        CancellationToken cancellationToken)
    {
        var state = new ServerStreamingInterceptorState<TRequest, TResponse>(
            this, method, request, requestCodec, responseCodec, interceptors, control, cancellationToken);
        return new InterceptedAsyncEnumerable<TResponse>(
            state.InvokeAsync(), method.ResponseNullable, state.Deadline,
            _runtimeContext.TimeProvider, state.LogicalCall, state.InvocationCancellation);
    }

    private IAsyncEnumerable<TResponse> InvokeDuplexStreamingIntercepted<TRequest, TResponse, TStreams>(
        RpcMethodDescriptor method,
        TRequest request,
        IRpcCodec<TRequest> requestCodec,
        IRpcCodec<TResponse> responseCodec,
        TStreams streams,
        ClientInterceptorGeneration interceptors,
        ResolvedCallControl control,
        CancellationToken cancellationToken)
        where TStreams : struct, IRpcClientStreamWriter
    {
        var state = new DuplexStreamingInterceptorState<TRequest, TResponse, TStreams>(
            this, method, request, requestCodec, responseCodec, streams, interceptors, control, cancellationToken);
        return new InterceptedAsyncEnumerable<TResponse>(
            state.InvokeAsync(), method.ResponseNullable, state.Deadline,
            _runtimeContext.TimeProvider, state.LogicalCall, state.InvocationCancellation);
    }

    private abstract class ClientInterceptorState
    {
        private readonly SharpLinkClient _client;
        private readonly ClientInterceptorGeneration _interceptors;
        private readonly SharpLinkClientInvocationContext _context;
        private readonly ResolvedCallControl _control;
        private long _started;

        protected ClientInterceptorState(
            SharpLinkClient client,
            RpcMethodDescriptor method,
            object? request,
            ClientInterceptorGeneration interceptors,
            ResolvedCallControl control,
            CancellationToken cancellationToken)
        {
            _client = client;
            _interceptors = interceptors;
            _control = control;
            _context = new SharpLinkClientInvocationContext(
                method, request, _control.Metadata, cancellationToken);
            _context.InterceptorPipelineState = this;
        }

        protected SharpLinkClient Client => _client;
        protected SharpLinkClientInvocationContext Context => _context;
        internal RpcDeadline Deadline => _control.Deadline;
        internal ClientLogicalCallState? LogicalCall => _control.LogicalCall;
        internal CancellationToken InvocationCancellation => _context.CancellationToken;

        public ValueTask<SharpLinkClientInvocationResult> InvokeAsync()
            => RunChainAsync();

        private async ValueTask<SharpLinkClientInvocationResult> RunChainAsync()
        {
            _started = _client._runtimeContext.TimeProvider.GetTimestamp();
            try
            {
                var result = await AwaitInvocationWithinFrozenDeadlineAsync(
                    _interceptors.Entry(_context)).ConfigureAwait(false);
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
                var result = await AwaitInvocationWithinFrozenDeadlineAsync(
                    _interceptors.Entry(_context)).ConfigureAwait(false);
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
                var result = await AwaitInvocationWithinFrozenDeadlineAsync(
                    _interceptors.Entry(_context)).ConfigureAwait(false);
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

        private async ValueTask<SharpLinkClientInvocationResult> AwaitInvocationWithinFrozenDeadlineAsync(
            ValueTask<SharpLinkClientInvocationResult> invocation)
        {
            if (!_control.Deadline.HasValue)
                return await invocation.ConfigureAwait(false);
            if (invocation.IsCompletedSuccessfully)
            {
                var result = invocation.Result;
                ThrowIfFrozenDeadlineExpired();
                return result;
            }

            var invocationTask = invocation.AsTask();
            if (!await SharpLinkTimer.WaitAsync(
                    invocationTask,
                    _control.Deadline,
                    _client._runtimeContext.TimeProvider,
                    CancellationToken.None).ConfigureAwait(false))
            {
                _control.LogicalCall?.TryClaimDeadline();
                _ = ObserveAbandonedInvocationAsync(invocationTask);
                throw CreateDeadlineExceededException();
            }
            return await invocationTask.ConfigureAwait(false);
        }

        private static async Task ObserveAbandonedInvocationAsync(
            Task<SharpLinkClientInvocationResult> invocationTask)
        {
            try { _ = await invocationTask.ConfigureAwait(false); }
            catch { }
        }

        internal ValueTask<SharpLinkClientInvocationResult> InvokeComposedInterceptorAsync(
            ISharpLinkClientInterceptor interceptor,
            SharpLinkClientInvocationDelegate next,
            SharpLinkClientInvocationContext context)
        {
            if (_control.LogicalCall is { } logicalCall && !logicalCall.TryEnterProgress())
            {
                return ValueTask.FromException<SharpLinkClientInvocationResult>(
                    CreateDeadlineExceededException());
            }

            try
            {
                return interceptor.InvokeAsync(context, next);
            }
            catch (Exception exception)
            {
                return ValueTask.FromException<SharpLinkClientInvocationResult>(exception);
            }
        }

        internal ValueTask<SharpLinkClientInvocationResult> InvokeComposedTerminalAsync(
            SharpLinkClientInvocationContext context)
        {
            if (_control.LogicalCall is { } logicalCall && !logicalCall.TryEnterProgress())
            {
                return ValueTask.FromException<SharpLinkClientInvocationResult>(
                    CreateDeadlineExceededException());
            }

            return InvokeTerminalTrackedAsync(context);
        }

        private ValueTask<SharpLinkClientInvocationResult> InvokeTerminalTrackedAsync(
            SharpLinkClientInvocationContext context)
            => InvokeTerminalAsync(context);

        protected ResolvedCallControl GetTerminalControl(SharpLinkClientInvocationContext context)
            => new(
                _control.Deadline,
                context.Metadata is { Count: > 0 } ? context.Metadata : null,
                _control.LogicalCall);

        private void ThrowIfFrozenDeadlineExpired()
        {
            if (_control.LogicalCall is { } logicalCall)
            {
                if (!logicalCall.TryEnterProgress())
                    throw CreateDeadlineExceededException();
                return;
            }
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
            ClientInterceptorGeneration interceptors,
            ResolvedCallControl control,
            CancellationToken cancellationToken)
            : base(client, method, request, interceptors, control, cancellationToken)
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
            ClientInterceptorGeneration interceptors,
            ResolvedCallControl control,
            CancellationToken cancellationToken)
            : base(client, method, request, interceptors, control, cancellationToken)
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
            ClientInterceptorGeneration interceptors,
            ResolvedCallControl control,
            CancellationToken cancellationToken)
            : base(client, method, request, interceptors, control, cancellationToken)
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
            ClientInterceptorGeneration interceptors,
            ResolvedCallControl control,
            CancellationToken cancellationToken)
            : base(client, method, request, interceptors, control, cancellationToken)
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
            ClientInterceptorGeneration interceptors,
            ResolvedCallControl control,
            CancellationToken cancellationToken)
            : base(client, method, request, interceptors, control, cancellationToken)
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
        ClientLogicalCallState? logicalCall,
        CancellationToken invocationCancellation) : IAsyncEnumerable<T>
    {
        private int _enumerated;

        public async IAsyncEnumerator<T> GetAsyncEnumerator(
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _enumerated, 1) != 0)
                throw new InvalidOperationException("An intercepted RPC stream can only be enumerated once.");

            var invocationResult = await AwaitInvocationWithinDeadlineAsync(invocation).ConfigureAwait(false);
            var stream = invocationResult.GetValue<IAsyncEnumerable<T>>();

            using var lifetimeCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                invocationCancellation, cancellationToken);
            ThrowIfDeadlineExpired();
            var enumerator = stream.GetAsyncEnumerator(lifetimeCancellation.Token);
            var deadlineWon = false;
            try
            {
                while (true)
                {
                    ThrowIfDeadlineExpired();
                    var moveNext = enumerator.MoveNextAsync();
                    bool hasNext;
                    if (!deadline.HasValue)
                    {
                        hasNext = await moveNext.ConfigureAwait(false);
                    }
                    else if (moveNext.IsCompletedSuccessfully)
                    {
                        hasNext = moveNext.Result;
                        ThrowIfDeadlineExpired();
                    }
                    else
                    {
                        var moveNextTask = moveNext.AsTask();
                        if (!await SharpLinkTimer.WaitAsync(
                                moveNextTask, deadline, timeProvider, lifetimeCancellation.Token).ConfigureAwait(false))
                        {
                            logicalCall?.TryClaimDeadline();
                            deadlineWon = true;
                            TryCancelLifetime(lifetimeCancellation);
                            _ = ObserveAbandonedMoveNextAsync(moveNextTask);
                            throw CreateDeadlineExceededException();
                        }
                        hasNext = await moveNextTask.ConfigureAwait(false);
                    }

                    if (!hasNext)
                        yield break;
                    var item = enumerator.Current;
                    if (!responseNullable && default(T) is null && item is null)
                        throw new InvalidCastException("A non-nullable intercepted RPC stream response was null.");
                    yield return item;
                }
            }
            finally
            {
                TryCancelLifetime(lifetimeCancellation);
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

        private static void TryCancelLifetime(CancellationTokenSource cancellation)
        {
            try
            {
                cancellation.Cancel();
            }
            catch
            {
                // Cancellation is cleanup after the logical call has selected its terminal path.
                // User callbacks cannot replace that terminal outcome.
            }
        }

        private async ValueTask<SharpLinkClientInvocationResult> AwaitInvocationWithinDeadlineAsync(
            ValueTask<SharpLinkClientInvocationResult> pendingInvocation)
        {
            if (!deadline.HasValue)
                return await pendingInvocation.ConfigureAwait(false);
            if (pendingInvocation.IsCompletedSuccessfully)
            {
                var result = pendingInvocation.Result;
                ThrowIfDeadlineExpired();
                return result;
            }

            var invocationTask = pendingInvocation.AsTask();
            if (!await SharpLinkTimer.WaitAsync(
                    invocationTask,
                    deadline,
                    timeProvider,
                    CancellationToken.None).ConfigureAwait(false))
            {
                logicalCall?.TryClaimDeadline();
                _ = ObserveAbandonedInvocationAsync(invocationTask);
                throw CreateDeadlineExceededException();
            }
            return await invocationTask.ConfigureAwait(false);
        }

        private static async Task ObserveAbandonedInvocationAsync(
            Task<SharpLinkClientInvocationResult> task)
        {
            try { _ = await task.ConfigureAwait(false); }
            catch { }
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
            if (logicalCall is not null)
            {
                if (!logicalCall.TryEnterProgress())
                    throw CreateDeadlineExceededException();
                return;
            }
            if (deadline.IsExpired(timeProvider))
                throw CreateDeadlineExceededException();
        }
    }
}
