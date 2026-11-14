namespace SharpLink.Client;

internal sealed partial class SharpLinkClient
{
    internal ValueTask<TResponse> InvokeStaticUnaryAsync<TRequest, TResponse>(
        StaticBindingCallSite bindingSite,
        RpcMethodDescriptor method, in TRequest request, IRpcCodec<TRequest> requestCodec,
        IRpcCodec<TResponse> responseCodec, SharpLinkMetadata? metadata,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requestCodec);
        ArgumentNullException.ThrowIfNull(responseCodec);
        bindingSite.Validate(method, RpcMethodKind.Unary);
        cancellationToken.ThrowIfCancellationRequested();
        var interceptors = CaptureInterceptorGenerationForInvocation();
        var control = ResolveCallControlForInvocation(
            method, metadata, includeClientDefault: true, interceptors);
        control = control with { BindingSite = bindingSite };
        Interlocked.Increment(ref _activeLogicalInvocations);
        try
        {
            ValueTask<TResponse> invocation;
            if (SharpLinkTelemetry.ClientCallsEnabled)
                invocation = InvokeUnaryWithTelemetryAsync(method, request, requestCodec, responseCodec, interceptors, control, cancellationToken);
            else if (interceptors.Count != 0)
                invocation = InvokeUnaryInterceptedAsync(method, request, requestCodec, responseCodec, interceptors, control, cancellationToken);
            else
                invocation = InvokeUnaryWithOptionalRetryAsync(method, request, requestCodec, responseCodec, control, cancellationToken);
            return CompleteLogicalInvocation(invocation);
        }
        catch
        {
            Interlocked.Decrement(ref _activeLogicalInvocations);
            throw;
        }
    }

    internal ValueTask InvokeStaticOneWayAsync<TRequest, TStreams>(
        StaticBindingCallSite bindingSite,
        RpcMethodDescriptor method,
        in TRequest request,
        IRpcCodec<TRequest> requestCodec,
        in TStreams streams,
        SharpLinkMetadata? metadata,
        CancellationToken cancellationToken = default)
        where TStreams : struct, IRpcClientStreamWriter
    {
        ArgumentNullException.ThrowIfNull(requestCodec);
        bindingSite.Validate(method, RpcMethodKind.OneWay);
        cancellationToken.ThrowIfCancellationRequested();
        var interceptors = CaptureInterceptorGenerationForInvocation();
        var control = ResolveCallControlForInvocation(
            method, metadata, includeClientDefault: false, interceptors);
        control = control with { BindingSite = bindingSite };
        Interlocked.Increment(ref _activeLogicalInvocations);
        try
        {
            ValueTask invocation;
            if (SharpLinkTelemetry.ClientCallsEnabled)
                invocation = InvokeOneWayWithTelemetryAsync(method, request, requestCodec, streams, interceptors, control, cancellationToken);
            else if (interceptors.Count != 0)
                invocation = InvokeOneWayInterceptedAsync(method, request, requestCodec, streams, interceptors, control, cancellationToken);
            else
                invocation = InvokeOneWayCoreAsync(method, request, requestCodec, streams, control, cancellationToken);
            return CompleteLogicalInvocation(invocation);
        }
        catch
        {
            Interlocked.Decrement(ref _activeLogicalInvocations);
            throw;
        }
    }

    internal ValueTask<TResponse> InvokeStaticClientStreamingAsync<TRequest, TResponse, TStreams>(
        StaticBindingCallSite bindingSite,
        RpcMethodDescriptor method,
        in TRequest request,
        IRpcCodec<TRequest> requestCodec,
        IRpcCodec<TResponse> responseCodec,
        in TStreams streams,
        SharpLinkMetadata? metadata,
        CancellationToken cancellationToken = default)
        where TStreams : struct, IRpcClientStreamWriter
    {
        ArgumentNullException.ThrowIfNull(requestCodec);
        ArgumentNullException.ThrowIfNull(responseCodec);
        bindingSite.Validate(method, RpcMethodKind.ClientStreaming);
        cancellationToken.ThrowIfCancellationRequested();
        var interceptors = CaptureInterceptorGenerationForInvocation();
        var control = ResolveCallControlForInvocation(
            method, metadata, includeClientDefault: false, interceptors);
        control = control with { BindingSite = bindingSite };
        Interlocked.Increment(ref _activeLogicalInvocations);
        try
        {
            ValueTask<TResponse> invocation;
            if (SharpLinkTelemetry.ClientCallsEnabled)
                invocation = InvokeClientStreamingWithTelemetryAsync(method, request, requestCodec, responseCodec, streams, interceptors, control, cancellationToken);
            else if (interceptors.Count != 0)
                invocation = InvokeClientStreamingInterceptedAsync(method, request, requestCodec, responseCodec, streams, interceptors, control, cancellationToken);
            else
                invocation = InvokeClientStreamingCoreAsync(method, request, requestCodec, responseCodec, streams, control, cancellationToken);
            return CompleteLogicalInvocation(invocation);
        }
        catch
        {
            Interlocked.Decrement(ref _activeLogicalInvocations);
            throw;
        }
    }

    internal IAsyncEnumerable<TResponse> InvokeStaticServerStreamingAsync<TRequest, TResponse>(
        StaticBindingCallSite bindingSite,
        RpcMethodDescriptor method,
        in TRequest request,
        IRpcCodec<TRequest> requestCodec,
        IRpcCodec<TResponse> responseCodec,
        SharpLinkMetadata? metadata,
        CancellationToken cancellationToken = default)
    {
        var interceptors = CaptureInterceptorGenerationForInvocation();
        var control = ResolveCallControlForInvocation(
            method, metadata, includeClientDefault: false, interceptors);
        control = control with { BindingSite = bindingSite };
        return InvokeServerStreamingResolved(
            method, request, requestCodec, responseCodec, interceptors, control, cancellationToken);
    }

    internal IAsyncEnumerable<TResponse> InvokeServerStreamingResolved<TRequest, TResponse>(
        RpcMethodDescriptor method,
        in TRequest request,
        IRpcCodec<TRequest> requestCodec,
        IRpcCodec<TResponse> responseCodec,
        ClientInterceptorGeneration interceptors,
        ResolvedCallControl control,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requestCodec);
        ArgumentNullException.ThrowIfNull(responseCodec);
        ArgumentNullException.ThrowIfNull(interceptors);
        (control.BindingSite ?? throw StaticBindingWire.Error(StaticBindingFailure.NoBoundRoute)).Validate(method, RpcMethodKind.ServerStreaming);
        EnsureLogicalCallProgress(control);
        Interlocked.Increment(ref _activeLogicalInvocations);
        try
        {
            IAsyncEnumerable<TResponse> invocation;
            if (SharpLinkTelemetry.ClientCallsEnabled)
                invocation = InvokeServerStreamingWithTelemetry(method, request, requestCodec, responseCodec, interceptors, control, cancellationToken);
            else if (interceptors.Count != 0)
                invocation = InvokeServerStreamingIntercepted(method, request, requestCodec, responseCodec, interceptors, control, cancellationToken);
            else
                invocation = InvokeServerStreamingCore(method, request, requestCodec, responseCodec, control, cancellationToken);
            return CompleteLogicalInvocation(invocation);
        }
        catch
        {
            Interlocked.Decrement(ref _activeLogicalInvocations);
            throw;
        }
    }

    private IAsyncEnumerable<TResponse> InvokeServerStreamingCore<TRequest, TResponse>(
        RpcMethodDescriptor method,
        TRequest request,
        IRpcCodec<TRequest> requestCodec,
        IRpcCodec<TResponse> responseCodec,
        ResolvedCallControl control,
        CancellationToken cancellationToken)
    {
        var dispatcher = PooledAsyncStreamDispatcher<TResponse>.Rent(cancellationToken, responseCodec, method.ResponseNullable);
        var registrationLease = dispatcher.RetainForRegistration();
        var registrationOwner = SharpLinkClientModuleCallContext.Current?.Retain();
        try { _ = TrackBoundStreamWork(() => StartServerStreamingInvokerAsync(dispatcher, method, request, requestCodec, control, cancellationToken, registrationLease, registrationOwner), "ServerStreamingInvoker"); }
        catch (Exception error)
        { dispatcher.Complete(error); dispatcher.ReleaseRegistrationRetention(registrationLease); registrationOwner?.Dispose(); }
        return dispatcher;
    }

    internal IAsyncEnumerable<TResponse> InvokeStaticDuplexStreamingAsync<TRequest, TResponse, TStreams>(
        StaticBindingCallSite bindingSite,
        RpcMethodDescriptor method,
        in TRequest request,
        IRpcCodec<TRequest> requestCodec,
        IRpcCodec<TResponse> responseCodec,
        in TStreams streams,
        SharpLinkMetadata? metadata,
        CancellationToken cancellationToken = default)
        where TStreams : struct, IRpcClientStreamWriter
    {
        var interceptors = CaptureInterceptorGenerationForInvocation();
        var control = ResolveCallControlForInvocation(
            method, metadata, includeClientDefault: false, interceptors);
        control = control with { BindingSite = bindingSite };
        return InvokeDuplexStreamingResolved(
            method, request, requestCodec, responseCodec, streams, interceptors, control, cancellationToken);
    }

    internal IAsyncEnumerable<TResponse> InvokeDuplexStreamingResolved<TRequest, TResponse, TStreams>(
        RpcMethodDescriptor method,
        in TRequest request,
        IRpcCodec<TRequest> requestCodec,
        IRpcCodec<TResponse> responseCodec,
        in TStreams streams,
        ClientInterceptorGeneration interceptors,
        ResolvedCallControl control,
        CancellationToken cancellationToken = default)
        where TStreams : struct, IRpcClientStreamWriter
    {
        ArgumentNullException.ThrowIfNull(requestCodec);
        ArgumentNullException.ThrowIfNull(responseCodec);
        ArgumentNullException.ThrowIfNull(interceptors);
        (control.BindingSite ?? throw StaticBindingWire.Error(StaticBindingFailure.NoBoundRoute)).Validate(method, RpcMethodKind.DuplexStreaming);
        EnsureLogicalCallProgress(control);
        Interlocked.Increment(ref _activeLogicalInvocations);
        try
        {
            IAsyncEnumerable<TResponse> invocation;
            if (SharpLinkTelemetry.ClientCallsEnabled)
                invocation = InvokeDuplexStreamingWithTelemetry(method, request, requestCodec, responseCodec, streams, interceptors, control, cancellationToken);
            else if (interceptors.Count != 0)
                invocation = InvokeDuplexStreamingIntercepted(method, request, requestCodec, responseCodec, streams, interceptors, control, cancellationToken);
            else
                invocation = InvokeDuplexStreamingCore(method, request, requestCodec, responseCodec, streams, control, cancellationToken);
            return CompleteLogicalInvocation(invocation);
        }
        catch
        {
            Interlocked.Decrement(ref _activeLogicalInvocations);
            throw;
        }
    }

}
