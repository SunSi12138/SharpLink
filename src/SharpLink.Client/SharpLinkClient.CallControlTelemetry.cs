namespace SharpLink.Client;

internal sealed partial class SharpLinkClient
{
    private ResolvedCallControl ResolveCallControlForInvocation(
        RpcMethodDescriptor method,
        SharpLinkMetadata? metadata,
        bool includeClientDefault)
    {
        var lifetimeSource = ClientCallLifetimeSource.None;
        try
        {
            return ResolveCallControl(
                metadata,
                includeClientDefault,
                method.HasMethodTimeout,
                method.MethodTimeout,
                ref lifetimeSource);
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
