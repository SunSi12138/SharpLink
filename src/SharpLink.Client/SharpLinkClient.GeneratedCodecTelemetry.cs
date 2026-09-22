namespace SharpLink.Client;

internal sealed partial class SharpLinkClient
{
    private ValueTask<TResponse> InvokeGeneratedUnaryWithTelemetryAsync<TRequest, TResponse, TRequestCodec, TResponseCodec>(
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
        var detailMode = control.TelemetryDetailMode;
        var scope = SharpLinkTelemetry.StartClientCall(method);
        TagLifetimeSource(scope, control.LifetimeSource, detailMode);
        try
        {
            var invocation = interceptors.Count != 0
                ? InvokeGeneratedUnaryInterceptedAsync(
                    method, request, requestCodec, responseCodec, interceptors, control, cancellationToken)
                : InvokeGeneratedUnaryWithOptionalRetryAsync(
                    method, request, requestCodec, responseCodec, control, cancellationToken);
            return ObserveCallAsync(invocation, scope);
        }
        catch (Exception exception)
        {
            scope.Complete(exception);
            throw;
        }
    }

    private ValueTask InvokeGeneratedOneWayWithTelemetryAsync<TRequest, TRequestCodec, TStreams>(
        RpcMethodDescriptor method,
        TRequest request,
        TRequestCodec requestCodec,
        TStreams streams,
        ClientInterceptorGeneration interceptors,
        ResolvedCallControl control,
        CancellationToken cancellationToken)
        where TRequestCodec : IRpcCodec<TRequest>
        where TStreams : struct, IRpcClientStreamWriter
    {
        var detailMode = control.TelemetryDetailMode;
        var scope = SharpLinkTelemetry.StartClientCall(method);
        TagLifetimeSource(scope, control.LifetimeSource, detailMode);
        try
        {
            var invocation = interceptors.Count != 0
                ? InvokeGeneratedOneWayInterceptedAsync(
                    method, request, requestCodec, streams, interceptors, control, cancellationToken)
                : InvokeOneWayCoreAsync(
                    method, request, requestCodec, streams, control, cancellationToken);
            return ObserveCallAsync(invocation, scope);
        }
        catch (Exception exception)
        {
            scope.Complete(exception);
            throw;
        }
    }

    private ValueTask<TResponse> InvokeGeneratedClientStreamingWithTelemetryAsync<TRequest, TResponse, TRequestCodec, TResponseCodec, TStreams>(
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
        var detailMode = control.TelemetryDetailMode;
        var scope = SharpLinkTelemetry.StartClientCall(method);
        TagLifetimeSource(scope, control.LifetimeSource, detailMode);
        try
        {
            var invocation = interceptors.Count != 0
                ? InvokeGeneratedClientStreamingInterceptedAsync(
                    method, request, requestCodec, responseCodec, streams, interceptors, control, cancellationToken)
                : InvokeGeneratedClientStreamingCoreAsync(
                    method, request, requestCodec, responseCodec, streams, control, cancellationToken);
            return ObserveCallAsync(invocation, scope);
        }
        catch (Exception exception)
        {
            scope.Complete(exception);
            throw;
        }
    }

    private IAsyncEnumerable<TResponse> InvokeGeneratedServerStreamingWithTelemetry<TRequest, TResponse, TRequestCodec, TResponseCodec>(
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
        var detailMode = control.TelemetryDetailMode;
        var stream = interceptors.Count != 0
            ? InvokeGeneratedServerStreamingIntercepted(
                method, request, requestCodec, responseCodec, interceptors, control, cancellationToken)
            : InvokeGeneratedServerStreamingCore(
                method, request, requestCodec, responseCodec, control, cancellationToken);
        return ObserveStream(method, stream, control.LifetimeSource, detailMode);
    }

    private IAsyncEnumerable<TResponse> InvokeGeneratedDuplexStreamingWithTelemetry<TRequest, TResponse, TRequestCodec, TResponseCodec, TStreams>(
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
        var detailMode = control.TelemetryDetailMode;
        var stream = interceptors.Count != 0
            ? InvokeGeneratedDuplexStreamingIntercepted(
                method, request, requestCodec, responseCodec, streams, interceptors, control, cancellationToken)
            : InvokeGeneratedDuplexStreamingCore(
                method, request, requestCodec, responseCodec, streams, control, cancellationToken);
        return ObserveStream(method, stream, control.LifetimeSource, detailMode);
    }
}
