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
                ref lifetimeSource);
            return CaptureRetryGenerationForInvocation(method, control);
        }
        catch (SharpLinkException exception)
        {
            if (SharpLinkTelemetry.ClientCallsEnabled)
            {
                var scope = SharpLinkTelemetry.StartClientCall(method);
                TagLifetimeSource(scope, lifetimeSource);
                scope.Complete(exception);
            }
            throw;
        }
    }
}
