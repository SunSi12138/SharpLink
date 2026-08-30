namespace SharpLink.Client;

internal sealed partial class SharpLinkClient
{
    private ValueTask<TResponse> InvokeUnaryWithTelemetryAsync<TRequest, TResponse>(
        RpcMethodDescriptor method,
        TRequest request,
        IRpcCodec<TRequest> requestCodec,
        IRpcCodec<TResponse> responseCodec,
        ISharpLinkClientInterceptor[] interceptors,
        ResolvedCallControl control,
        CancellationToken cancellationToken)
    {
        var scope = SharpLinkTelemetry.StartClientCall(method);
        TagLifetimeSource(scope, control.LifetimeSource);
        try
        {
            ValueTask<TResponse> invocation;
            if (interceptors.Length != 0)
            {
                invocation = InvokeUnaryInterceptedAsync(
                    method, request, requestCodec, responseCodec, interceptors, control, cancellationToken);
            }
            else
            {
                invocation = InvokeUnaryWithOptionalRetryAsync(
                    method, request, requestCodec, responseCodec, control, cancellationToken);
            }
            return ObserveCallAsync(invocation, scope);
        }
        catch (Exception exception)
        {
            scope.Complete(exception);
            throw;
        }
    }

    private ValueTask InvokeOneWayWithTelemetryAsync<TRequest, TStreams>(
        RpcMethodDescriptor method,
        TRequest request,
        IRpcCodec<TRequest> requestCodec,
        TStreams streams,
        ISharpLinkClientInterceptor[] interceptors,
        ResolvedCallControl control,
        CancellationToken cancellationToken)
        where TStreams : struct, IRpcClientStreamWriter
    {
        var scope = SharpLinkTelemetry.StartClientCall(method);
        TagLifetimeSource(scope, control.LifetimeSource);
        try
        {
            ValueTask invocation;
            if (interceptors.Length != 0)
            {
                invocation = InvokeOneWayInterceptedAsync(
                    method, request, requestCodec, streams, interceptors, control, cancellationToken);
            }
            else
            {
                invocation = InvokeOneWayCoreAsync(
                    method,
                    request, requestCodec, streams, control, cancellationToken);
            }
            return ObserveCallAsync(invocation, scope);
        }
        catch (Exception exception)
        {
            scope.Complete(exception);
            throw;
        }
    }

    private ValueTask<TResponse> InvokeClientStreamingWithTelemetryAsync<TRequest, TResponse, TStreams>(
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
        var scope = SharpLinkTelemetry.StartClientCall(method);
        TagLifetimeSource(scope, control.LifetimeSource);
        try
        {
            ValueTask<TResponse> invocation;
            if (interceptors.Length != 0)
            {
                invocation = InvokeClientStreamingInterceptedAsync(
                    method, request, requestCodec, responseCodec, streams, interceptors, control, cancellationToken);
            }
            else
            {
                invocation = InvokeClientStreamingCoreAsync(
                    method, request, requestCodec, responseCodec, streams, control, cancellationToken);
            }
            return ObserveCallAsync(invocation, scope);
        }
        catch (Exception exception)
        {
            scope.Complete(exception);
            throw;
        }
    }

    private IAsyncEnumerable<TResponse> InvokeServerStreamingWithTelemetry<TRequest, TResponse>(
        RpcMethodDescriptor method,
        TRequest request,
        IRpcCodec<TRequest> requestCodec,
        IRpcCodec<TResponse> responseCodec,
        ISharpLinkClientInterceptor[] interceptors,
        ResolvedCallControl control,
        CancellationToken cancellationToken)
    {
        var stream = interceptors.Length != 0
            ? InvokeServerStreamingIntercepted(
                method, request, requestCodec, responseCodec, interceptors, control, cancellationToken)
            : InvokeServerStreamingCore(
                method, request, requestCodec, responseCodec, control, cancellationToken);
        return ObserveStream(method, stream, control.LifetimeSource);
    }

    private IAsyncEnumerable<TResponse> InvokeDuplexStreamingWithTelemetry<TRequest, TResponse, TStreams>(
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
        var stream = interceptors.Length != 0
            ? InvokeDuplexStreamingIntercepted(
                method, request, requestCodec, responseCodec, streams, interceptors, control, cancellationToken)
            : InvokeDuplexStreamingCore(
                method, request, requestCodec, responseCodec, streams, control, cancellationToken);
        return ObserveStream(method, stream, control.LifetimeSource);
    }

    private static async ValueTask<T> ObserveCallAsync<T>(
        ValueTask<T> invocation,
        SharpLinkTelemetry.CallScope scope)
    {
        try
        {
            var result = await invocation.ConfigureAwait(false);
            scope.Complete();
            return result;
        }
        catch (Exception exception)
        {
            scope.Complete(exception);
            throw;
        }
    }

    private static async ValueTask ObserveCallAsync(
        ValueTask invocation,
        SharpLinkTelemetry.CallScope scope)
    {
        try
        {
            await invocation.ConfigureAwait(false);
            scope.Complete();
        }
        catch (Exception exception)
        {
            scope.Complete(exception);
            throw;
        }
    }

    private static IAsyncEnumerable<T> ObserveStream<T>(
        RpcMethodDescriptor method,
        IAsyncEnumerable<T> stream,
        ClientCallLifetimeSource lifetimeSource)
        => new TelemetryAsyncEnumerable<T>(method, stream, lifetimeSource);

    private sealed class TelemetryAsyncEnumerable<T>(
        RpcMethodDescriptor method,
        IAsyncEnumerable<T> stream,
        ClientCallLifetimeSource lifetimeSource) : IAsyncEnumerable<T>
    {
        public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            var scope = SharpLinkTelemetry.StartClientCall(method);
            TagLifetimeSource(scope, lifetimeSource);
            try
            {
                return new TelemetryAsyncEnumerator<T>(
                    stream.GetAsyncEnumerator(cancellationToken),
                    scope);
            }
            catch (Exception exception)
            {
                scope.Complete(exception);
                throw;
            }
        }
    }

    private sealed class TelemetryAsyncEnumerator<T>(
        IAsyncEnumerator<T> enumerator,
        SharpLinkTelemetry.CallScope scope) : IAsyncEnumerator<T>
    {
        private int _terminalObserved;

        public T Current => enumerator.Current;

        public ValueTask<bool> MoveNextAsync()
        {
            try
            {
                var move = enumerator.MoveNextAsync();
                if (!move.IsCompletedSuccessfully)
                    return AwaitMoveNextAsync(move);
                if (!move.Result)
                    Complete();
                return move;
            }
            catch (Exception exception)
            {
                Complete(exception);
                throw;
            }
        }

        public ValueTask DisposeAsync()
        {
            CompleteAbandoned();
            return enumerator.DisposeAsync();
        }

        private async ValueTask<bool> AwaitMoveNextAsync(ValueTask<bool> move)
        {
            try
            {
                var hasNext = await move.ConfigureAwait(false);
                if (!hasNext)
                    Complete();
                return hasNext;
            }
            catch (Exception exception)
            {
                Complete(exception);
                throw;
            }
        }

        private void Complete(Exception? exception = null)
        {
            if (Interlocked.Exchange(ref _terminalObserved, 1) == 0)
                scope.Complete(exception);
        }

        private void CompleteAbandoned()
        {
            if (Interlocked.Exchange(ref _terminalObserved, 1) != 0)
                return;

            scope.Complete(new OperationCanceledException(
                "The response stream consumer stopped before remote completion."));
            SharpLinkTelemetry.RecordAbandonedCall("client", "consumer_abandoned");
        }
    }

    private static void TagLifetimeSource(
        SharpLinkTelemetry.CallScope scope,
        ClientCallLifetimeSource lifetimeSource)
    {
        var value = lifetimeSource.ToTelemetryValue();
        if (value is not null)
            scope.SetTag("rpc.sharplink.lifetime_source", value);
    }
}
