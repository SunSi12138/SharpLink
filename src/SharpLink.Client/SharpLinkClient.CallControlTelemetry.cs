namespace SharpLink.Client;

internal sealed partial class SharpLinkClient
{
    // Every entry point captures the interceptor generation exactly once and uses that same
    // snapshot for both the shared-state allocation decision and the executed pipeline, so a
    // runtime interceptor replacement cannot select an intercepted pipeline without shared state.
    internal ClientInterceptorGeneration CaptureInterceptorGenerationForInvocation()
        => Volatile.Read(ref _clientInterceptorGeneration);

    internal ResolvedCallControl ResolveCallControlForInvocation(
        RpcMethodDescriptor method,
        SharpLinkMetadata? metadata,
        bool includeClientDefault,
        ClientInterceptorGeneration interceptors)
    {
        ArgumentNullException.ThrowIfNull(interceptors);
        var lifetimeSource = ClientCallLifetimeSource.None;
        try
        {
            var control = ResolveCallControl(
                metadata,
                includeClientDefault,
                method.HasMethodTimeout,
                method.MethodTimeout,
                ref lifetimeSource,
                allocateSharedLogicalCall: MayObserveSharedLogicalCallAcrossParticipants(method, interceptors));
            return CaptureRetryGenerationForInvocation(method, control);
        }
        catch (SharpLinkException exception)
        {
            if (SharpLinkTelemetry.ClientCallsEnabled)
            {
                var detailMode = CaptureTelemetryDetailGeneration().Mode;
                var scope = SharpLinkTelemetry.StartClientCall(method);
                TagLifetimeSource(scope, lifetimeSource, detailMode);
                scope.Complete(exception);
            }
            throw;
        }
    }

    // The logical-call object only publishes observable state when more than one participant can
    // observe the deadline claim: a streaming dispatcher, a client-stream producer, or a client
    // interceptor. Plain unary and plain oneway invocations are single-participant, so their
    // deadline is re-checked directly from the frozen control instead of allocating an object per
    // call. The interceptor generation must be the one captured by the calling entry point: an
    // interceptor published after that capture belongs to the next logical call, and re-reading
    // the live generation here could select an intercepted pipeline with no shared state.
    private static bool MayObserveSharedLogicalCallAcrossParticipants(
        RpcMethodDescriptor method,
        ClientInterceptorGeneration interceptors)
        => method.Kind is RpcMethodKind.ClientStreaming
            or RpcMethodKind.ServerStreaming
            or RpcMethodKind.DuplexStreaming
           || method.HasClientStreams
           || interceptors.Count != 0;
}
