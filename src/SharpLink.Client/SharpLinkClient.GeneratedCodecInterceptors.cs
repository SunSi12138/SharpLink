namespace SharpLink.Client;

internal sealed partial class SharpLinkClient
{
    private ValueTask<TResponse> InvokeGeneratedUnaryInterceptedAsync<TRequest, TResponse, TRequestCodec, TResponseCodec>(
        RpcMethodDescriptor method,
        TRequest request,
        TRequestCodec requestCodec,
        TResponseCodec responseCodec,
        ClientInterceptorGeneration interceptors,
        ResolvedCallControl control,
        CancellationToken cancellationToken)
        where TRequestCodec : IRpcCodec<TRequest>
        where TResponseCodec : IRpcCodec<TResponse>
        => new GeneratedUnaryInterceptorState<TRequest, TResponse, TRequestCodec, TResponseCodec>(
            this, method, request, requestCodec, responseCodec, interceptors, control, cancellationToken).InvokeTypedAsync();

    private ValueTask InvokeGeneratedOneWayInterceptedAsync<TRequest, TRequestCodec, TStreams>(
        RpcMethodDescriptor method,
        TRequest request,
        TRequestCodec requestCodec,
        TStreams streams,
        ClientInterceptorGeneration interceptors,
        ResolvedCallControl control,
        CancellationToken cancellationToken)
        where TRequestCodec : IRpcCodec<TRequest>
        where TStreams : struct, IRpcClientStreamWriter
        => new GeneratedOneWayInterceptorState<TRequest, TRequestCodec, TStreams>(
            this, method, request, requestCodec, streams, interceptors, control, cancellationToken).InvokeVoidAsync();

    private ValueTask<TResponse> InvokeGeneratedClientStreamingInterceptedAsync<TRequest, TResponse, TRequestCodec, TResponseCodec, TStreams>(
        RpcMethodDescriptor method,
        TRequest request,
        TRequestCodec requestCodec,
        TResponseCodec responseCodec,
        TStreams streams,
        ClientInterceptorGeneration interceptors,
        ResolvedCallControl control,
        CancellationToken cancellationToken)
        where TRequestCodec : IRpcCodec<TRequest>
        where TResponseCodec : IRpcCodec<TResponse>
        where TStreams : struct, IRpcClientStreamWriter
        => new GeneratedClientStreamingInterceptorState<TRequest, TResponse, TRequestCodec, TResponseCodec, TStreams>(
            this, method, request, requestCodec, responseCodec, streams, interceptors, control, cancellationToken).InvokeTypedAsync();

    private IAsyncEnumerable<TResponse> InvokeGeneratedServerStreamingIntercepted<TRequest, TResponse, TRequestCodec, TResponseCodec>(
        RpcMethodDescriptor method,
        TRequest request,
        TRequestCodec requestCodec,
        TResponseCodec responseCodec,
        ClientInterceptorGeneration interceptors,
        ResolvedCallControl control,
        CancellationToken cancellationToken)
        where TRequestCodec : IRpcCodec<TRequest>
        where TResponseCodec : IRpcCodec<TResponse>
    {
        var state = new GeneratedServerStreamingInterceptorState<TRequest, TResponse, TRequestCodec, TResponseCodec>(
            this, method, request, requestCodec, responseCodec, interceptors, control, cancellationToken);
        return new InterceptedAsyncEnumerable<TResponse>(
            state.InvokeAsync(), method.ResponseNullable, state.Deadline,
            _runtimeContext.TimeProvider, state.LogicalCall, state.InvocationCancellation);
    }

    private IAsyncEnumerable<TResponse> InvokeGeneratedDuplexStreamingIntercepted<TRequest, TResponse, TRequestCodec, TResponseCodec, TStreams>(
        RpcMethodDescriptor method,
        TRequest request,
        TRequestCodec requestCodec,
        TResponseCodec responseCodec,
        TStreams streams,
        ClientInterceptorGeneration interceptors,
        ResolvedCallControl control,
        CancellationToken cancellationToken)
        where TRequestCodec : IRpcCodec<TRequest>
        where TResponseCodec : IRpcCodec<TResponse>
        where TStreams : struct, IRpcClientStreamWriter
    {
        var state = new GeneratedDuplexStreamingInterceptorState<TRequest, TResponse, TRequestCodec, TResponseCodec, TStreams>(
            this, method, request, requestCodec, responseCodec, streams, interceptors, control, cancellationToken);
        return new InterceptedAsyncEnumerable<TResponse>(
            state.InvokeAsync(), method.ResponseNullable, state.Deadline,
            _runtimeContext.TimeProvider, state.LogicalCall, state.InvocationCancellation);
    }

    private sealed class GeneratedUnaryInterceptorState<TRequest, TResponse, TRequestCodec, TResponseCodec> : ClientInterceptorState
        where TRequestCodec : IRpcCodec<TRequest>
        where TResponseCodec : IRpcCodec<TResponse>
    {
        private readonly RpcMethodDescriptor _method;
        private readonly TRequest _request;
        private readonly TRequestCodec _requestCodec;
        private readonly TResponseCodec _responseCodec;

        internal GeneratedUnaryInterceptorState(
            SharpLinkClient client,
            RpcMethodDescriptor method,
            TRequest request,
            TRequestCodec requestCodec,
            TResponseCodec responseCodec,
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

        internal ValueTask<TResponse> InvokeTypedAsync() => RunTypedChainAsync<TResponse>();

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
                var response = await Client.InvokeGeneratedUnaryWithOptionalRetryAsync(
                    _method, _request, _requestCodec, _responseCodec,
                    GetTerminalControl(context), context.CancellationToken).ConfigureAwait(false);
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

    private sealed class GeneratedOneWayInterceptorState<TRequest, TRequestCodec, TStreams> : ClientInterceptorState
        where TRequestCodec : IRpcCodec<TRequest>
        where TStreams : struct, IRpcClientStreamWriter
    {
        private readonly RpcMethodDescriptor _method;
        private readonly TRequest _request;
        private readonly TRequestCodec _requestCodec;
        private readonly TStreams _streams;

        internal GeneratedOneWayInterceptorState(
            SharpLinkClient client,
            RpcMethodDescriptor method,
            TRequest request,
            TRequestCodec requestCodec,
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

        internal ValueTask InvokeVoidAsync() => RunVoidChainAsync();

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
                await Client.InvokeOneWayCoreAsync(
                    _method, _request, _requestCodec, _streams,
                    GetTerminalControl(context), context.CancellationToken).ConfigureAwait(false);
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

    private sealed class GeneratedClientStreamingInterceptorState<TRequest, TResponse, TRequestCodec, TResponseCodec, TStreams> : ClientInterceptorState
        where TRequestCodec : IRpcCodec<TRequest>
        where TResponseCodec : IRpcCodec<TResponse>
        where TStreams : struct, IRpcClientStreamWriter
    {
        private readonly RpcMethodDescriptor _method;
        private readonly TRequest _request;
        private readonly TRequestCodec _requestCodec;
        private readonly TResponseCodec _responseCodec;
        private readonly TStreams _streams;

        internal GeneratedClientStreamingInterceptorState(
            SharpLinkClient client,
            RpcMethodDescriptor method,
            TRequest request,
            TRequestCodec requestCodec,
            TResponseCodec responseCodec,
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

        internal ValueTask<TResponse> InvokeTypedAsync() => RunTypedChainAsync<TResponse>();

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
                var response = await Client.InvokeGeneratedClientStreamingCoreAsync(
                    _method, _request, _requestCodec, _responseCodec, _streams,
                    GetTerminalControl(context), context.CancellationToken).ConfigureAwait(false);
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

    private sealed class GeneratedServerStreamingInterceptorState<TRequest, TResponse, TRequestCodec, TResponseCodec> : ClientInterceptorState
        where TRequestCodec : IRpcCodec<TRequest>
        where TResponseCodec : IRpcCodec<TResponse>
    {
        private readonly RpcMethodDescriptor _method;
        private readonly TRequest _request;
        private readonly TRequestCodec _requestCodec;
        private readonly TResponseCodec _responseCodec;

        internal GeneratedServerStreamingInterceptorState(
            SharpLinkClient client,
            RpcMethodDescriptor method,
            TRequest request,
            TRequestCodec requestCodec,
            TResponseCodec responseCodec,
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
                var stream = Client.InvokeGeneratedServerStreamingCore(
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

    private sealed class GeneratedDuplexStreamingInterceptorState<TRequest, TResponse, TRequestCodec, TResponseCodec, TStreams> : ClientInterceptorState
        where TRequestCodec : IRpcCodec<TRequest>
        where TResponseCodec : IRpcCodec<TResponse>
        where TStreams : struct, IRpcClientStreamWriter
    {
        private readonly RpcMethodDescriptor _method;
        private readonly TRequest _request;
        private readonly TRequestCodec _requestCodec;
        private readonly TResponseCodec _responseCodec;
        private readonly TStreams _streams;

        internal GeneratedDuplexStreamingInterceptorState(
            SharpLinkClient client,
            RpcMethodDescriptor method,
            TRequest request,
            TRequestCodec requestCodec,
            TResponseCodec responseCodec,
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
                var stream = Client.InvokeGeneratedDuplexStreamingCore(
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
}
