namespace SharpLink.Runtime;

internal sealed partial class RpcSession
{
    internal IAsyncEnumerable<T> CreateGeneratedInboundStream<T>(
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

    internal async ValueTask PumpGeneratedOutboundStreamAsync<T>(
        long requestId,
        ushort streamId,
        IAsyncEnumerable<T> stream,
        IRpcCodec<T> codec,
        bool payloadNullable,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(codec);

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

        this.SendStreamCompleteAsync(requestId, streamId);
    }

    // Keep the generated-server path concrete and codec-bound. The internal Runtime helper
    // remains a separate client hot path so one stream item does not cross an extra generic
    // async wrapper merely to select its codec.
    private ValueTask SendGeneratedStreamChunkAsync<T>(
        long requestId,
        ushort streamId,
        T item,
        IRpcCodec<T> codec,
        CancellationToken cancellationToken)
    {
        if (codec is IRpcSizedCodec<T> sizedCodec &&
            sizedCodec.CanExactSize &&
            sizedCodec.TryGetEncodedSize(item, out var knownEncodedBytes))
        {
            return SendStreamChunkKnownSizeAsync(
                requestId,
                streamId,
                item,
                codec,
                Math.Max(1, knownEncodedBytes),
                cancellationToken);
        }

        IRpcByteBufferWriter? writer = null;
        var ownsWriter = true;
        try
        {
            writer = RentFrameWriter();
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
            if (ownsWriter && writer is not null)
                RuntimeContext.Buffers.Return(writer);
        }
    }

    internal async ValueTask SendStreamChunkKnownSizeAsync<T>(
        long requestId,
        ushort streamId,
        T item,
        IRpcCodec<T> codec,
        int encodedBytes,
        CancellationToken cancellationToken)
    {
        await AcquireStreamSendCreditAsync(
            requestId,
            streamId,
            encodedBytes,
            cancellationToken).ConfigureAwait(false);

        IRpcByteBufferWriter? writer = null;
        var ownsWriter = true;
        try
        {
            writer = RentFrameWriter();
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

            var actualEncodedBytes = Math.Max(
                1,
                writer.WrittenCount - ProtocolV2Constants.HeaderBytes - sizeof(ushort));
            if (actualEncodedBytes != encodedBytes)
            {
                throw new InvalidOperationException(
                    "Generated stream item size differed after credit was acquired.");
            }

            ownsWriter = false;
            SendPacket(writer);
        }
        catch
        {
            ReturnUnsentStreamCredit(requestId, streamId, encodedBytes);
            throw;
        }
        finally
        {
            if (ownsWriter && writer is not null)
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

/// <summary>
/// Exposes only generated stream protocol operations. Business exception policy belongs to the
/// Server invocation bridge that composes this adapter.
/// </summary>
internal sealed class RpcSessionGeneratedServerBridge(RpcSession session) : IRpcGeneratedServerBridge
{
    public IAsyncEnumerable<T> CreateInboundStream<T>(
        long requestId,
        ushort streamId,
        IRpcCodec<T> codec,
        bool payloadNullable,
        CancellationToken cancellationToken)
        => session.CreateGeneratedInboundStream(
            requestId,
            streamId,
            codec,
            payloadNullable,
            cancellationToken);

    public ValueTask PumpOutboundStreamAsync<T>(
        long requestId,
        ushort streamId,
        IAsyncEnumerable<T> stream,
        IRpcCodec<T> codec,
        bool payloadNullable,
        long contractId,
        long methodId,
        CancellationToken cancellationToken)
        => session.PumpGeneratedOutboundStreamAsync(
            requestId,
            streamId,
            stream,
            codec,
            payloadNullable,
            cancellationToken);
}
