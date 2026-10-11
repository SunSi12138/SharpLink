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
        IRpcStub stub,
        long methodId,
        int clientStreamCount,
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
            DrainCompletedOneWayStreams(session, requestId, clientStreamCount);
            TryClaimCallCompletion(callState);
        }
        catch (Exception ex)
        {
            DrainCompletedOneWayStreams(session, requestId, clientStreamCount);
            if (TryClaimCallCompletion(callState))
            {
                LogOnewayRpcDispatchFailed(_logger, MapServiceException(
                    ex, callContext, session, stub, methodId, requestId, cancellationToken));
            }
        }
        finally
        {
            DrainCompletedOneWayStreams(session, requestId, clientStreamCount);
            ReleaseOneWayDispatchResources(
                callState,
                requestId,
                requestCancellationMap,
                connection,
                requestPermit);
        }
    }
}
