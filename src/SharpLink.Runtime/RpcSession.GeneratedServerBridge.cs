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

                await SendGeneratedStreamChunkAsync(
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

    // Keep the generated-server path concrete and codec-bound. The public IRpcSession
    // extension remains a separate client hot path so one stream item does not cross an
    // extra generic async wrapper merely to select its codec.
    private ValueTask SendGeneratedStreamChunkAsync<T>(
        long requestId,
        ushort streamId,
        T item,
        IRpcCodec<T> codec,
        CancellationToken cancellationToken)
    {
        var writer = RentFrameWriter();
        var ownsWriter = true;
        try
        {
            using (writer.BeginPacketScope(
                       ProtocolV2FrameType.StreamData,
                       ProtocolV2FrameFlags.None,
                       unchecked((ulong)requestId)))
            {
                var idSpan = writer.GetSpan(sizeof(ushort));
                BinaryPrimitives.WriteUInt16LittleEndian(idSpan, streamId);
                writer.Advance(sizeof(ushort));
                codec.Serialize(item, writer);
            }
            var encodedBytes = Math.Max(
                1,
                writer.WrittenCount - ProtocolV2Constants.HeaderBytes - sizeof(ushort));
            var pendingCredit = AcquireStreamSendCreditAsync(
                requestId,
                streamId,
                encodedBytes,
                cancellationToken);
            if (!pendingCredit.IsCompletedSuccessfully)
            {
                ownsWriter = false;
                return AwaitGeneratedStreamCreditAndSendAsync(
                    pendingCredit,
                    writer,
                    requestId,
                    streamId,
                    encodedBytes);
            }

            pendingCredit.GetAwaiter().GetResult();
            try
            {
                ownsWriter = false;
                SendPacket(writer);
            }
            catch
            {
                ReturnUnsentStreamCredit(requestId, streamId, encodedBytes);
                throw;
            }
            return ValueTask.CompletedTask;
        }
        finally
        {
            if (ownsWriter)
                RuntimeContext.Buffers.Return(writer);
        }
    }

    private async ValueTask AwaitGeneratedStreamCreditAndSendAsync(
        ValueTask pendingCredit,
        IRpcByteBufferWriter writer,
        long requestId,
        ushort streamId,
        int encodedBytes)
    {
        var ownsWriter = true;
        var creditAcquired = false;
        try
        {
            await pendingCredit.ConfigureAwait(false);
            creditAcquired = true;
            ownsWriter = false;
            SendPacket(writer);
        }
        catch
        {
            if (creditAcquired)
                ReturnUnsentStreamCredit(requestId, streamId, encodedBytes);
            throw;
        }
        finally
        {
            if (ownsWriter)
                RuntimeContext.Buffers.Return(writer);
        }
    }
}
