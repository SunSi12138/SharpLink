namespace SharpLink.Server;

internal sealed partial class SharpLinkServer
{
    internal Func<long, RpcMethodDescriptor, CancellationToken, ValueTask>? BeforeStreamAcceptanceTestHook;

    private static ValueTask InvokeBoundStubAsync(IRpcStub stub, object service, IRpcGeneratedServerBridge bridge,
        RpcMethodDescriptor method, long requestId, ReadOnlySequence<byte> args, IRpcByteBufferWriter? output, CancellationToken token)
    {
        if (method.Kind is RpcMethodKind.ClientStreaming or RpcMethodKind.ServerStreaming or RpcMethodKind.DuplexStreaming)
            return InvokeAcceptedBoundStubAsync(stub, service, (ServerGeneratedBridge)bridge, method, requestId, args, output, token);
        return output is null ? stub.InvokeNoReturnCancellableAsync(service, bridge, method, requestId, args, token)
            : stub.InvokeCancellableAsync(service, bridge, method, requestId, args, output, token);
    }

    private static async ValueTask InvokeAcceptedBoundStubAsync(IRpcStub stub, object service, ServerGeneratedBridge bridge,
        RpcMethodDescriptor method, long requestId, ReadOnlySequence<byte> args, IRpcByteBufferWriter? output, CancellationToken token)
    {
        await bridge.AcceptBoundRequestAsync(requestId, method, token).ConfigureAwait(false);
        if (output is null) await stub.InvokeNoReturnCancellableAsync(service, bridge, method, requestId, args, token).ConfigureAwait(false);
        else await stub.InvokeCancellableAsync(service, bridge, method, requestId, args, output, token).ConfigureAwait(false);
    }

    private static async ValueTask JoinBoundInputStreamsAsync(ValueTask invocation, RpcSession session, long requestId, RpcMethodDescriptor method)
    {
        Exception? failure = null;
        try { await invocation.ConfigureAwait(false); }
        catch (Exception error) { failure = error; }
        try
        {
            if (method.Kind == RpcMethodKind.OneWay)
                await session.StreamManager.AbandonOneWayInputsAfterDispatchesAsync(requestId, method.ClientStreamCount).ConfigureAwait(false);
            else await CompleteDynamicRequestStreamsAsync(session, requestId, true).ConfigureAwait(false);
        }
        catch (Exception error) { failure = CombineTerminalExceptions(failure, error); }
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
