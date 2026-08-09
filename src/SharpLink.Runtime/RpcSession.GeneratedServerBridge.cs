namespace SharpLink.Runtime;

public sealed partial class RpcSession : IRpcGeneratedServerBridge
{
    IAsyncEnumerable<T> IRpcGeneratedServerBridge.CreateInboundStream<T>(
        long requestId,
        ushort streamId,
        IRpcCodec<T> codec,
        bool payloadNullable,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(codec);
        var dispatcher = PooledAsyncStreamDispatcher<T>.Rent(
            cancellationToken,
            codec,
            payloadNullable);
        try
        {
            StreamManager.Register(requestId, streamId, dispatcher);
            return dispatcher;
        }
        catch (Exception registrationException)
        {
            dispatcher.Complete(registrationException);
            SharpLinkAsyncCleanup.DisposeSynchronously(dispatcher);
            throw;
        }
    }

    async ValueTask IRpcGeneratedServerBridge.PumpOutboundStreamAsync<T>(
        long requestId,
        ushort streamId,
        IAsyncEnumerable<T> stream,
        IRpcCodec<T> codec,
        bool payloadNullable,
        long contractId,
        long methodId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(codec);

        Exception? terminalError = null;
        try
        {
            await foreach (var item in stream
                               .WithCancellation(cancellationToken)
                               .ConfigureAwait(false))
            {
                if (!payloadNullable && default(T) is null && item is null)
                {
                    throw new SharpLinkException(
                        SharpLinkErrorCode.Internal,
                        "A non-nullable RPC stream response was null.");
                }

                await ((IRpcSession)this).SendStreamChunkAsync(
                    requestId,
                    streamId,
                    item,
                    codec,
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            terminalError = exception;
        }

        if (terminalError is null)
        {
            ((IRpcSession)this).SendStreamCompleteAsync(requestId, streamId);
            return;
        }

        ((IRpcSession)this).SendStreamErrorAsync(
            requestId,
            streamId,
            terminalError,
            contractId,
            methodId);
    }
}
