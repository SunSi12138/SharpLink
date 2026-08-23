namespace SharpLink.Client;

internal sealed partial class SharpLinkClient
{
    private ValueTask<TResponse> InvokeUnaryWithTelemetryAsync<TRequest, TResponse>(
        RpcMethodDescriptor method,
        TRequest request,
        IRpcCodec<TRequest> requestCodec,
        IRpcCodec<TResponse> responseCodec,
        ISharpLinkClientInterceptor[] interceptors,
        SharpLinkMetadata? metadata,
        CancellationToken cancellationToken)
    {
        var scope = SharpLinkTelemetry.StartClientCall(method);
        try
        {
            ValueTask<TResponse> invocation;
            if (interceptors.Length != 0)
            {
                invocation = InvokeUnaryInterceptedAsync(
                    method, request, requestCodec, responseCodec, interceptors, metadata, cancellationToken);
            }
            else
            {
                var control = ResolveCallControl(
                    metadata, true, method.HasMethodTimeout, method.MethodTimeout);
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
        SharpLinkMetadata? metadata,
        CancellationToken cancellationToken)
        where TStreams : struct, IRpcClientStreamWriter
    {
        var scope = SharpLinkTelemetry.StartClientCall(method);
        try
        {
            ValueTask invocation;
            if (interceptors.Length != 0)
            {
                invocation = InvokeOneWayInterceptedAsync(
                    method, request, requestCodec, streams, interceptors, metadata, cancellationToken);
            }
            else
            {
                var control = ResolveCallControl(
                    metadata, false, method.HasMethodTimeout, method.MethodTimeout);
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
        SharpLinkMetadata? metadata,
        CancellationToken cancellationToken)
        where TStreams : struct, IRpcClientStreamWriter
    {
        var scope = SharpLinkTelemetry.StartClientCall(method);
        try
        {
            ValueTask<TResponse> invocation;
            if (interceptors.Length != 0)
            {
                invocation = InvokeClientStreamingInterceptedAsync(
                    method, request, requestCodec, responseCodec, streams, interceptors, metadata, cancellationToken);
            }
            else
            {
                var control = ResolveCallControl(
                    metadata, false, method.HasMethodTimeout, method.MethodTimeout);
                invocation = InvokeClientStreamingCoreAsync(
                    method,
                    request, requestCodec, responseCodec, streams, control, cancellationToken);
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
        SharpLinkMetadata? metadata,
        CancellationToken cancellationToken)
    {
        var stream = interceptors.Length != 0
            ? InvokeServerStreamingIntercepted(
                method, request, requestCodec, responseCodec, interceptors, metadata, cancellationToken)
            : InvokeServerStreamingCore(
                method, request, requestCodec, responseCodec,
                ResolveCallControl(metadata, false, method.HasMethodTimeout, method.MethodTimeout),
                cancellationToken);
        return ObserveStream(method, stream);
    }

    private IAsyncEnumerable<TResponse> InvokeDuplexStreamingWithTelemetry<TRequest, TResponse, TStreams>(
        RpcMethodDescriptor method,
        TRequest request,
        IRpcCodec<TRequest> requestCodec,
        IRpcCodec<TResponse> responseCodec,
        TStreams streams,
        ISharpLinkClientInterceptor[] interceptors,
        SharpLinkMetadata? metadata,
        CancellationToken cancellationToken)
        where TStreams : struct, IRpcClientStreamWriter
    {
        var stream = interceptors.Length != 0
            ? InvokeDuplexStreamingIntercepted(
                method, request, requestCodec, responseCodec, streams, interceptors, metadata, cancellationToken)
            : InvokeDuplexStreamingCore(
                method, request, requestCodec, responseCodec, streams,
                ResolveCallControl(metadata, false, method.HasMethodTimeout, method.MethodTimeout),
                cancellationToken);
        return ObserveStream(method, stream);
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
        IAsyncEnumerable<T> stream)
        => new TelemetryAsyncEnumerable<T>(method, stream);

    private sealed class TelemetryAsyncEnumerable<T>(
        RpcMethodDescriptor method,
        IAsyncEnumerable<T> stream) : IAsyncEnumerable<T>
    {
        public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            var scope = SharpLinkTelemetry.StartClientCall(method);
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
}
