namespace SharpLink.Server;

internal sealed partial class SharpLinkServer
{
    private async Task AwaitOneWayDispatchAsync(
        ValueTask invokeTask,
        ServerCallCancellationState callState,
        long requestId,
        StripedLongMap<ServerCallCancellationState> requestCancellationMap,
        ServerConnectionState connection,
        SharpLinkCallContextSnapshot callContext,
        RpcSession session,
        RpcMethodDescriptor method,
        CancellationToken cancellationToken,
        ServerRequestPermit requestPermit)
    {
        try
        {
            await invokeTask.ConfigureAwait(false);
            if (callContext is SharpLinkServerInvocationContext
                {
                    Status: SharpLinkInvocationStatus.Pending
                } interceptorContext)
                interceptorContext.Status = SharpLinkInvocationStatus.Succeeded;
            DrainCompletedOneWayStreams(session, requestId, method.ClientStreamCount);
            TryClaimCallCompletion(callState);
        }
        catch (Exception ex)
        {
            DrainCompletedOneWayStreams(session, requestId, method.ClientStreamCount);
            if (TryClaimCallCompletion(callState))
            {
                LogOnewayRpcDispatchFailed(_logger, MapServiceException(
                    ex, callContext, session, method, requestId, cancellationToken));
            }
        }
        finally
        {
            DrainCompletedOneWayStreams(session, requestId, method.ClientStreamCount);
            ReleaseOneWayDispatchResources(
                callState,
                requestId,
                requestCancellationMap,
                connection,
                requestPermit);
        }
    }
}
