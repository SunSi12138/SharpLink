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
        return new InterceptedAsyncEnumerable<TResponse>(invocation);
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
        return new InterceptedAsyncEnumerable<TResponse>(invocation);
    }

    private abstract class ClientInterceptorState
    {
        private readonly SharpLinkClient _client;
        private readonly SharpLinkClientInvocationContext _context;
        private readonly SharpLinkClientInvocationDelegate _next;
        private int _index;
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
            _next = InvokeNextAsync;
        }

        protected SharpLinkClient Client => _client;
        protected SharpLinkClientInvocationContext Context => _context;

        public async ValueTask<SharpLinkClientInvocationResult> InvokeAsync()
        {
            _started = Stopwatch.GetTimestamp();
            try
            {
                var result = await InvokeNextAsync(_context).ConfigureAwait(false);
                if (_context.Status == SharpLinkInvocationStatus.Pending)
                    _context.Status = SharpLinkInvocationStatus.Succeeded;
                return result;
            }
            catch (OperationCanceledException exception)
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
            SharpLinkClientInvocationContext context)
        {
            var index = _index++;
            return index < _client._clientInterceptors.Length
                ? _client._clientInterceptors[index].InvokeAsync(context, _next)
                : InvokeTerminalTrackedAsync(context);
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
                context.Elapsed = Stopwatch.GetElapsedTime(_started);
            }
        }

        protected abstract ValueTask<SharpLinkClientInvocationResult> InvokeTerminalAsync(
            SharpLinkClientInvocationContext context);
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
    }

    private sealed class InterceptedAsyncEnumerable<T>(
        ValueTask<SharpLinkClientInvocationResult> invocation) : IAsyncEnumerable<T>
    {
        private int _enumerated;

        public async IAsyncEnumerator<T> GetAsyncEnumerator(
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _enumerated, 1) != 0)
                throw new InvalidOperationException("An intercepted RPC stream can only be enumerated once.");

            var stream = (await invocation.ConfigureAwait(false)).GetValue<IAsyncEnumerable<T>>();
            await foreach (var item in stream.WithCancellation(cancellationToken).ConfigureAwait(false))
                yield return item;
        }
    }
}
