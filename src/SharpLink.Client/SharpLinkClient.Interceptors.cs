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
                var failure = CreateDeadlineExceededException();
                SharpLinkPhysicalWork.TrackAbandonedUserWork(failure, invocationTask);
                throw failure;
            }
            return await invocationTask.ConfigureAwait(false);
        }

        internal ValueTask<SharpLinkClientInvocationResult> InvokeComposedInterceptorAsync(
            ISharpLinkClientInterceptor interceptor,
            SharpLinkClientInvocationDelegate next,
            SharpLinkClientInvocationContext context)
        {
            if (_control.LogicalCall is { } logicalCall &&
                !logicalCall.TryEnterProgress(_control.Deadline, _client._runtimeContext.TimeProvider))
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
            if (_control.LogicalCall is { } logicalCall &&
                !logicalCall.TryEnterProgress(_control.Deadline, _client._runtimeContext.TimeProvider))
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
            // The terminal boundary only replaces the metadata observed by the pipeline. Every
            // other captured field (lifetime source, telemetry detail, retry generation, time
            // provider, shared logical call) must stay exactly as the invocation captured it, so
            // this copies the control instead of rebuilding it field by field.
            => _control with
            {
                Metadata = context.Metadata is { Count: > 0 } ? context.Metadata : null
            };

        private void ThrowIfFrozenDeadlineExpired()
        {
            if (_control.LogicalCall is { } logicalCall)
            {
                if (!logicalCall.TryEnterProgress(_control.Deadline, _client._runtimeContext.TimeProvider))
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

        public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default)
            => new Enumerator(this, cancellationToken);

        private ValueTask<SharpLinkClientInvocationResult> Invocation => invocation;
        private bool ResponseNullable => responseNullable;
        private RpcDeadline Deadline => deadline;
        private TimeProvider TimeProvider => timeProvider;
        private ClientLogicalCallState? LogicalCall => logicalCall;
        private CancellationToken InvocationCancellation => invocationCancellation;

        // Experimental phase-specialized equivalent of the compiler's async iterator.
        // Keep the runtime iterator builder: it provides ExecutionContext/SyncContext
        // isolation and continuation capture, not merely a scheduling optimization.
        private sealed class Enumerator(InterceptedAsyncEnumerable<T> owner, CancellationToken cancellationToken)
            : IAsyncEnumerator<T>, IAsyncStateMachine,
              System.Threading.Tasks.Sources.IValueTaskSource<bool>,
              System.Threading.Tasks.Sources.IValueTaskSource
        {
            private const int Finished = -2, Initial = -3, Ready = -4, ExecutingSetup = -1;
            private const int Moving = 5;
            private const int InvocationWait = 0, MoveWait = 1, TimerWait = 2, MoveTaskWait = 3, DisposeWait = 4;
            private int _phase = Initial;
            private bool _disposeMode;
            private bool _ownershipTransferred;
            private AsyncIteratorMethodBuilder _builder = AsyncIteratorMethodBuilder.Create();
            private System.Threading.Tasks.Sources.ManualResetValueTaskSourceCore<bool> _promise;
            private IAsyncEnumerator<T>? _enumerator;
            private CancellationTokenSource? _lifetimeCancellation;
            private Task<bool>? _pendingMove;
            private Exception? _failure;
            private T _current = default!;
            private ConfiguredValueTaskAwaitable<SharpLinkClientInvocationResult>.ConfiguredValueTaskAwaiter _invocationAwaiter;
            private ConfiguredValueTaskAwaitable<bool>.ConfiguredValueTaskAwaiter _moveAwaiter;
            private ConfiguredTaskAwaitable<bool>.ConfiguredTaskAwaiter _taskAwaiter;
            private ConfiguredValueTaskAwaitable.ConfiguredValueTaskAwaiter _disposeAwaiter;

            public T Current => _current;

            public ValueTask<bool> MoveNextAsync()
            {
                if (_phase == Finished)
                    return default;
                _promise.Reset();
                var self = this;
                _builder.MoveNext(ref self);
                var token = _promise.Version;
                return _promise.GetStatus(token) == System.Threading.Tasks.Sources.ValueTaskSourceStatus.Succeeded
                    ? new ValueTask<bool>(_promise.GetResult(token))
                    : new ValueTask<bool>(this, token);
            }

            public ValueTask DisposeAsync()
            {
                // Match the async-iterator contract: disposal cannot race an active MoveNext.
                if (_phase >= ExecutingSetup)
                    throw new NotSupportedException();
                if (_phase == Finished)
                    return default;
                _disposeMode = true;
                _promise.Reset();
                var self = this;
                _builder.MoveNext(ref self);
                return new ValueTask(this, _promise.Version);
            }

            void IAsyncStateMachine.MoveNext()
            {
                try
                {
                    // Steady-state entry is independent of setup/await/disposal resumption.
                    if (_phase == Ready)
                    {
                        // Compiler iterators clear the yielded value on resumption,
                        // including a MoveNext that suspends and early disposal.
                        _current = default!;
                        if (_disposeMode)
                            BeginDispose();
                        else
                            BeginMove();
                        return;
                    }
                    Resume();
                }
                catch (OperationCanceledException exception) when (
                    (_phase is Moving or MoveWait or TimerWait or MoveTaskWait) && !_ownershipTransferred &&
                    (_lifetimeCancellation!.IsCancellationRequested || SharpLinkPhysicalWork.GetAbandonedUserWork(exception) is not null))
                {
                    TransferCancellation(exception);
                }
                catch (SharpLinkException exception) when (
                    (_phase is Moving or MoveWait or TimerWait or MoveTaskWait) && !_ownershipTransferred &&
                    exception.Code == SharpLinkErrorCode.DeadlineExceeded &&
                    (owner.Deadline.IsExpired(owner.TimeProvider) || SharpLinkPhysicalWork.GetAbandonedUserWork(exception) is not null))
                {
                    TransferDeadline(exception);
                }
                catch (Exception exception)
                {
                    if (_phase is Moving or MoveWait or TimerWait or MoveTaskWait)
                        FailMove(exception);
                    else if (_phase == DisposeWait)
                        FailDispose(exception);
                    else
                        Complete(exception);
                }
            }

            private void Resume()
            {
                switch (_phase)
                {
                    case Initial:
                        if (_disposeMode)
                        {
                            Complete(null);
                            return;
                        }
                        _phase = ExecutingSetup;
                        if (Interlocked.Exchange(ref owner._enumerated, 1) != 0)
                            throw new InvalidOperationException("An intercepted RPC stream can only be enumerated once.");
                        _invocationAwaiter = owner.Invocation.ConfigureAwait(false).GetAwaiter();
                        if (!_invocationAwaiter.IsCompleted)
                        {
                            _phase = InvocationWait;
                            var self = this;
                            _builder.AwaitUnsafeOnCompleted(ref _invocationAwaiter, ref self);
                            return;
                        }
                        FinishInvocation();
                        return;
                    case InvocationWait:
                        FinishInvocation();
                        return;
                    case MoveWait:
                        var move = _moveAwaiter;
                        _moveAwaiter = default;
                        FinishMove(move.GetResult());
                        return;
                    case TimerWait:
                        FinishTimer();
                        return;
                    case MoveTaskWait:
                        var task = _taskAwaiter;
                        _taskAwaiter = default;
                        var taskResult = task.GetResult();
                        _pendingMove = null;
                        FinishMove(taskResult);
                        return;
                    case DisposeWait:
                        var disposal = _disposeAwaiter;
                        _disposeAwaiter = default;
                        disposal.GetResult();
                        _lifetimeCancellation!.Dispose();
                        Complete(_failure);
                        return;
                }
            }

            private void FinishInvocation()
            {
                _phase = ExecutingSetup;
                var awaiter = _invocationAwaiter;
                _invocationAwaiter = default;
                var invocationResult = awaiter.GetResult();
                owner.ThrowIfDeadlineExpired();
                var stream = invocationResult.GetValue<IAsyncEnumerable<T>>();
                _lifetimeCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                    owner.InvocationCancellation, cancellationToken);
                try
                {
                    _enumerator = stream.GetAsyncEnumerator(_lifetimeCancellation.Token);
                }
                catch (Exception exception)
                {
                    if (SharpLinkPhysicalWork.TransferLifetime(exception, _lifetimeCancellation) is { } terminal)
                        throw terminal;
                    _lifetimeCancellation.Dispose();
                    throw;
                }
                BeginMove();
            }

            private void BeginMove()
            {
                _phase = Moving;
                owner.ThrowIfDeadlineExpired();
                var moveNext = _enumerator!.MoveNextAsync();
                if (!owner.Deadline.HasValue)
                {
                    _moveAwaiter = moveNext.ConfigureAwait(false).GetAwaiter();
                    if (!_moveAwaiter.IsCompleted)
                    {
                        _phase = MoveWait;
                        var self = this;
                        _builder.AwaitUnsafeOnCompleted(ref _moveAwaiter, ref self);
                        return;
                    }
                    var awaiter = _moveAwaiter;
                    _moveAwaiter = default;
                    FinishMove(awaiter.GetResult());
                    return;
                }
                if (moveNext.IsCompletedSuccessfully)
                {
                    var hasNext = moveNext.Result;
                    owner.ThrowIfDeadlineExpired();
                    FinishMove(hasNext);
                    return;
                }
                _pendingMove = moveNext.AsTask();
                _moveAwaiter = SharpLinkTimer.WaitAsync(
                    _pendingMove, owner.Deadline, owner.TimeProvider, _lifetimeCancellation!.Token)
                    .ConfigureAwait(false).GetAwaiter();
                _phase = TimerWait;
                if (!_moveAwaiter.IsCompleted)
                {
                    var self = this;
                    _builder.AwaitUnsafeOnCompleted(ref _moveAwaiter, ref self);
                    return;
                }
                FinishTimer();
            }

            private void FinishTimer()
            {
                bool completed;
                try
                {
                    var awaiter = _moveAwaiter;
                    _moveAwaiter = default;
                    completed = awaiter.GetResult();
                }
                catch (OperationCanceledException exception) when (_lifetimeCancellation!.IsCancellationRequested)
                {
                    _ownershipTransferred = true;
                    var failure = SharpLinkPhysicalWork.CreateCancellationTerminal(exception);
                    var physical = SharpLinkPhysicalWork.CreateCarrierOwner(failure, exception, _lifetimeCancellation);
                    physical.Track(CompleteAbandonedStreamAsync(_pendingMove!, _enumerator!, _lifetimeCancellation, physical));
                    throw failure;
                }
                if (!completed)
                {
                    owner.LogicalCall?.TryClaimDeadline();
                    _ownershipTransferred = true;
                    var failure = CreateDeadlineExceededException();
                    var physical = SharpLinkPhysicalWork.CreateOwner(failure, _lifetimeCancellation!);
                    physical.Track(CompleteAbandonedStreamAsync(_pendingMove!, _enumerator!, _lifetimeCancellation!, physical));
                    throw failure;
                }
                _taskAwaiter = _pendingMove!.ConfigureAwait(false).GetAwaiter();
                _phase = MoveTaskWait;
                if (!_taskAwaiter.IsCompleted)
                {
                    var self = this;
                    _builder.AwaitUnsafeOnCompleted(ref _taskAwaiter, ref self);
                    return;
                }
                var taskAwaiter = _taskAwaiter;
                _taskAwaiter = default;
                var taskResult = taskAwaiter.GetResult();
                _pendingMove = null;
                FinishMove(taskResult);
            }

            private void FinishMove(bool hasNext)
            {
                // Keep Current and null validation inside the move-failure boundary.
                _phase = Moving;
                var item = hasNext ? _enumerator!.Current : default!;
                if (hasNext && !owner.ResponseNullable && default(T) is null && item is null)
                    throw new InvalidCastException("A non-nullable intercepted RPC stream response was null.");
                if (!hasNext)
                {
                    BeginDispose();
                    return;
                }
                _current = item;
                _phase = Ready;
                _promise.SetResult(true);
            }

            private void TransferCancellation(OperationCanceledException exception)
            {
                _ownershipTransferred = true;
                try
                {
                    var failure = SharpLinkPhysicalWork.CreateCancellationTerminal(exception);
                    var physical = SharpLinkPhysicalWork.CreateCarrierOwner(failure, exception, _lifetimeCancellation!);
                    physical.Track(CompleteAbandonedStreamAsync(
                        Task.FromException(exception), _enumerator!, _lifetimeCancellation!, physical));
                    Complete(failure);
                }
                catch (Exception failure) { Complete(failure); }
            }

            private void TransferDeadline(SharpLinkException exception)
            {
                _ownershipTransferred = true;
                try
                {
                    var failure = new SharpLinkException(exception.Code, exception.DetailCode, exception.Message, exception);
                    var physical = SharpLinkPhysicalWork.CreateCarrierOwner(failure, exception, _lifetimeCancellation!);
                    physical.Track(CompleteAbandonedStreamAsync(
                        Task.FromException(exception), _enumerator!, _lifetimeCancellation!, physical));
                    Complete(failure);
                }
                catch (Exception failure) { Complete(failure); }
            }

            private void FailMove(Exception exception)
            {
                // The original first-pass exception filters live on MoveNext above.
                // Do not re-evaluate cancellation/deadline after user finally blocks run.
                _failure = exception;
                if (_ownershipTransferred)
                {
                    Complete(exception);
                    return;
                }
                try { BeginDispose(); }
                catch (Exception disposalFailure) { FailDispose(disposalFailure); }
            }

            private void BeginDispose()
            {
                _phase = DisposeWait;
                TryCancelLifetime(_lifetimeCancellation!);
                _disposeAwaiter = _enumerator!.DisposeAsync().ConfigureAwait(false).GetAwaiter();
                if (!_disposeAwaiter.IsCompleted)
                {
                    var self = this;
                    _builder.AwaitUnsafeOnCompleted(ref _disposeAwaiter, ref self);
                    return;
                }
                var awaiter = _disposeAwaiter;
                _disposeAwaiter = default;
                awaiter.GetResult();
                _lifetimeCancellation!.Dispose();
                Complete(_failure);
            }

            private void FailDispose(Exception exception)
            {
                try
                {
                    if (SharpLinkPhysicalWork.TransferLifetime(exception, _lifetimeCancellation!) is { } terminal)
                    {
                        Complete(terminal);
                        return;
                    }
                    _lifetimeCancellation!.Dispose();
                    Complete(exception);
                }
                catch (Exception cleanupFailure) { Complete(cleanupFailure); }
            }

            private void Complete(Exception? failure)
            {
                _phase = Finished;
                _current = default!;
                _enumerator = null;
                _lifetimeCancellation = null;
                _pendingMove = null;
                _failure = null;
                _invocationAwaiter = default;
                _moveAwaiter = default;
                _taskAwaiter = default;
                _disposeAwaiter = default;
                _builder.Complete();
                if (failure is null)
                    _promise.SetResult(false);
                else
                    _promise.SetException(failure);
            }

            void IAsyncStateMachine.SetStateMachine(IAsyncStateMachine stateMachine) { }
            bool System.Threading.Tasks.Sources.IValueTaskSource<bool>.GetResult(short token) => _promise.GetResult(token);
            void System.Threading.Tasks.Sources.IValueTaskSource.GetResult(short token) => _promise.GetResult(token);
            System.Threading.Tasks.Sources.ValueTaskSourceStatus System.Threading.Tasks.Sources.IValueTaskSource<bool>.GetStatus(short token) => _promise.GetStatus(token);
            System.Threading.Tasks.Sources.ValueTaskSourceStatus System.Threading.Tasks.Sources.IValueTaskSource.GetStatus(short token) => _promise.GetStatus(token);
            void System.Threading.Tasks.Sources.IValueTaskSource<bool>.OnCompleted(Action<object?> continuation, object? state, short token, System.Threading.Tasks.Sources.ValueTaskSourceOnCompletedFlags flags)
                => _promise.OnCompleted(continuation, state, token, flags);
            void System.Threading.Tasks.Sources.IValueTaskSource.OnCompleted(Action<object?> continuation, object? state, short token, System.Threading.Tasks.Sources.ValueTaskSourceOnCompletedFlags flags)
                => _promise.OnCompleted(continuation, state, token, flags);
        }

        private static async Task CompleteAbandonedStreamAsync(
            Task pendingMove,
            IAsyncEnumerator<T> enumerator,
            CancellationTokenSource lifetimeCancellation,
            SharpLinkPhysicalWork.PhysicalOwner physical)
        {
            await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
            TryCancelLifetime(lifetimeCancellation);
            try { await pendingMove.ConfigureAwait(false); }
            catch (Exception exception) { await physical.WaitForDescendantAsync(exception).ConfigureAwait(false); }
            try { await enumerator.DisposeAsync().ConfigureAwait(false); }
            catch (Exception exception) { await physical.WaitForDescendantAsync(exception).ConfigureAwait(false); }
        }

        private void ThrowIfDeadlineExpired()
        {
            if (logicalCall is not null)
            {
                if (!logicalCall.TryEnterProgress(deadline, timeProvider))
                    throw CreateDeadlineExceededException();
                return;
            }
            if (deadline.IsExpired(timeProvider))
                throw CreateDeadlineExceededException();
        }

        private static void TryCancelLifetime(CancellationTokenSource cancellation)
        {
            try { cancellation.Cancel(); }
            catch
            {
                // User callbacks cannot replace the terminal outcome selected by the call.
            }
        }
    }
}
