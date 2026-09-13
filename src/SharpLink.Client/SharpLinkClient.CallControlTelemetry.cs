namespace SharpLink.Client;

internal sealed partial class SharpLinkClient
{
    internal ResolvedCallControl ResolveCallControlForInvocation(
        RpcMethodDescriptor method,
        SharpLinkMetadata? metadata,
        bool includeClientDefault)
    {
        var lifetimeSource = ClientCallLifetimeSource.None;
        try
        {
            var control = ResolveCallControl(
                metadata,
                includeClientDefault,
                method.HasMethodTimeout,
                method.MethodTimeout,
                ref lifetimeSource,
                allocateSharedLogicalCall: MayObserveSharedLogicalCallAcrossParticipants(method));
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
    // interceptor. Plain unary and oneway invocations are single-participant, so their deadline is
    // re-checked directly from the control instead of allocating one object per call.
    private bool MayObserveSharedLogicalCallAcrossParticipants(RpcMethodDescriptor method)
        => method.Kind != RpcMethodKind.Unary
           || method.HasClientStreams
           || Volatile.Read(ref _clientInterceptorGeneration).Count != 0;
}
