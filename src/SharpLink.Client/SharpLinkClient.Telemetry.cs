namespace SharpLink.Client;

internal sealed partial class SharpLinkClient
{
    private ValueTask<TResponse> InvokeUnaryWithTelemetryAsync<TRequest, TResponse>(
        RpcMethodDescriptor method,
        TRequest request,
        IRpcCodec<TRequest> requestCodec,
        IRpcCodec<TResponse> responseCodec,
        SharpLinkCallOptions options,
        CancellationToken cancellationToken)
    {
        var scope = SharpLinkTelemetry.StartClientCall(method);
        try
        {
            ValueTask<TResponse> invocation;
            if (_clientInterceptors.Length != 0)
            {
                invocation = InvokeUnaryInterceptedAsync(
                    method, request, requestCodec, responseCodec, options, cancellationToken);
            }
            else
            {
                var control = ResolveCallControl(
                    options, true, method.HasMethodTimeout, method.MethodTimeout);
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
        SharpLinkCallOptions options,
        CancellationToken cancellationToken)
        where TStreams : struct, IRpcClientStreamWriter
    {
        var scope = SharpLinkTelemetry.StartClientCall(method);
        try
        {
            ValueTask invocation;
            if (_clientInterceptors.Length != 0)
            {
                invocation = InvokeOneWayInterceptedAsync(
                    method, request, requestCodec, streams, options, cancellationToken);
            }
            else
            {
                var control = ResolveCallControl(
                    options, false, method.HasMethodTimeout, method.MethodTimeout);
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
        SharpLinkCallOptions options,
        CancellationToken cancellationToken)
        where TStreams : struct, IRpcClientStreamWriter
    {
        var scope = SharpLinkTelemetry.StartClientCall(method);
        try
        {
            ValueTask<TResponse> invocation;
            if (_clientInterceptors.Length != 0)
            {
                invocation = InvokeClientStreamingInterceptedAsync(
                    method, request, requestCodec, responseCodec, streams, options, cancellationToken);
            }
            else
            {
                var control = ResolveCallControl(
                    options, false, method.HasMethodTimeout, method.MethodTimeout);
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
        SharpLinkCallOptions options,
        CancellationToken cancellationToken)
    {
        var stream = _clientInterceptors.Length != 0
            ? InvokeServerStreamingIntercepted(
                method, request, requestCodec, responseCodec, options, cancellationToken)
            : InvokeServerStreamingCore(
                method, request, requestCodec, responseCodec, options, cancellationToken);
        return ObserveStream(method, stream);
    }

    private IAsyncEnumerable<TResponse> InvokeDuplexStreamingWithTelemetry<TRequest, TResponse, TStreams>(
        RpcMethodDescriptor method,
        TRequest request,
        IRpcCodec<TRequest> requestCodec,
        IRpcCodec<TResponse> responseCodec,
        TStreams streams,
        SharpLinkCallOptions options,
        CancellationToken cancellationToken)
        where TStreams : struct, IRpcClientStreamWriter
    {
        var stream = _clientInterceptors.Length != 0
            ? InvokeDuplexStreamingIntercepted(
                method, request, requestCodec, responseCodec, streams, options, cancellationToken)
            : InvokeDuplexStreamingCore(
                method, request, requestCodec, responseCodec, streams, options, cancellationToken);
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

    private static async IAsyncEnumerable<T> ObserveStream<T>(
        RpcMethodDescriptor method,
        IAsyncEnumerable<T> stream,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var scope = SharpLinkTelemetry.StartClientCall(method);
        await using var enumerator = stream.GetAsyncEnumerator(cancellationToken);
        try
        {
            while (true)
            {
                T item;
                try
                {
                    if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                    {
                        scope.Complete();
                        yield break;
                    }
                    item = enumerator.Current;
                }
                catch (Exception exception)
                {
                    scope.Complete(exception);
                    throw;
                }
                yield return item;
            }
        }
        finally
        {
            scope.Complete();
        }
    }
}
