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

    private static void TryCancelGeneratedLifetime(CancellationTokenSource cancellation)
    {
        try
        {
            cancellation.Cancel();
        }
        catch
        {
            // Cancellation is cleanup after the call has already selected its terminal path.
            // User callbacks cannot replace that terminal outcome.
        }
    }

    private ValueTask SendGeneratedStreamCompleteAsync(
        long requestId,
        ushort streamId,
        RpcDeadline deadline,
        TimeProvider? deadlineTimeProvider,
        CancellationToken cancellationToken)
    {
        if (UsesWriterOwnedStreams)
            return SendWriterOwnedTerminalAsync(requestId, streamId, null,
                deadline, deadlineTimeProvider, cancellationToken);
        SendGeneratedStreamComplete(requestId, streamId, deadline, deadlineTimeProvider);
        return ValueTask.CompletedTask;
    }

    private void SendGeneratedStreamComplete(
        long requestId,
        ushort streamId,
        RpcDeadline deadline,
        TimeProvider? deadlineTimeProvider)
    {
        var writer = RentFrameWriter();
        var ownsWriter = true;
        try
        {
            using (writer.BeginPacketScope(
                       ProtocolV2FrameType.StreamComplete,
                       ProtocolV2FrameFlags.None,
                       unchecked((ulong)requestId)))
            {
                var idSpan = writer.GetSpan(sizeof(ushort));
                BinaryPrimitives.WriteUInt16LittleEndian(idSpan, streamId);
                writer.Advance(sizeof(ushort));
            }

            // The enqueue is the publication commit. Re-check after frame construction so clean
            // EOF cannot publish after the frozen RPC deadline merely because the pump checked
            // before building the terminal frame.
            ThrowIfGeneratedStreamDeadlineExpired(deadline, deadlineTimeProvider);
            ownsWriter = false;
            try
            {
                SendPacket(writer);
            }
            finally
            {
                CompleteSendStream(requestId, streamId);
            }
        }
        finally
        {
            if (ownsWriter)
                RuntimeContext.Buffers.Return(writer);
        }
    }

    private static void ThrowIfGeneratedStreamDeadlineExpired(
        RpcDeadline deadline,
        TimeProvider? timeProvider)
    {
        if (timeProvider is not null && deadline.IsExpired(timeProvider))
            throw CreateGeneratedStreamDeadlineExceededException();
    }

    private static SharpLinkException CreateGeneratedStreamDeadlineExceededException()
        => new(
            SharpLinkErrorCode.DeadlineExceeded,
            "RPC deadline exceeded during server stream production.");

    private static async Task ObserveAbandonedGeneratedMoveNextAsync(Task<bool> task)
    {
        try { _ = await task.ConfigureAwait(false); }
        catch { }
    }

    private static async Task ObserveAbandonedGeneratedSendAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch { }
    }

    private static async Task ObserveAbandonedGeneratedDisposeAsync(ValueTask dispose)
    {
        try { await dispose.ConfigureAwait(false); }
        catch { }
    }

    // Legacy exact-size sending retains credit-before-serialization. In the explicitly
    // selected writer-owned mode, producer serialization feeds the bounded preparation queue;
    // the single writer alone debits protocol credit before accepting a frame into its batch.
    private ValueTask<StreamFlowController.ResolvedSendCreditLease>
        SendGeneratedStreamChunkAsync<T>(
            long requestId,
            ushort streamId,
            T item,
            IRpcCodec<T> codec,
            IRpcSizedCodec<T>? exactSizeCodec,
            RpcDeadline deadline,
            TimeProvider? deadlineTimeProvider,
            CancellationToken cancellationToken,
            StreamFlowController.ResolvedSendCreditLease creditLease)
    {
        if (UsesWriterOwnedStreams)
            return SendWriterOwnedChunkAsync(requestId, streamId, item, codec, exactSizeCodec,
                cancellationToken, creditLease, deadline, deadlineTimeProvider);
        if (exactSizeCodec is not null &&
            exactSizeCodec.TryGetEncodedSize(item, out var knownEncodedBytes, out var sizedSnapshot))
        {
            return SendStreamChunkKnownSizeWithCreditLeaseAsync(
                requestId,
                streamId,
                item,
                exactSizeCodec,
                knownEncodedBytes,
                sizedSnapshot,
                cancellationToken,
                creditLease,
                deadline,
                deadlineTimeProvider);
        }

        return SendUnsizedStreamChunkWithCreditLeaseAsync(
            requestId,
            streamId,
            item,
            codec,
            cancellationToken,
            creditLease,
            deadline,
            deadlineTimeProvider);
    }

    private async ValueTask<StreamFlowController.ResolvedSendCreditLease>
        SendStreamChunkKnownSizeWithCreditLeaseAsync<T>(
            long requestId,
            ushort streamId,
            T item,
            IRpcSizedCodec<T> sizedCodec,
            int encodedBytes,
            IRpcSizedCodecSnapshot? sizedSnapshot,
            CancellationToken cancellationToken,
            StreamFlowController.ResolvedSendCreditLease creditLease,
            RpcDeadline deadline = default,
            TimeProvider? deadlineTimeProvider = null)
    {
        var creditBytes = Math.Max(1, encodedBytes);
        var creditAcquired = false;
        IRpcByteBufferWriter? writer = null;
        var ownsWriter = true;
        try
        {
            creditLease = await AcquireStreamSendCreditAsync(
                creditLease,
                requestId,
                streamId,
                creditBytes,
                cancellationToken).ConfigureAwait(false);
            creditAcquired = true;

            writer = RuntimeContext.Buffers.Rent(
                checked(ProtocolV2Constants.HeaderBytes + NegotiatedMaxFramePayloadBytes + 4));
            using (writer.BeginPacketScope(
                       ProtocolV2FrameType.StreamData,
                       ProtocolV2FrameFlags.None,
                       unchecked((ulong)requestId)))
            {
                writer.GetSpan(sizeof(ushort) + encodedBytes + 4);
                writer.Advance(0);
                var idSpan = writer.GetSpan(sizeof(ushort));
                BinaryPrimitives.WriteUInt16LittleEndian(idSpan, streamId);
                writer.Advance(sizeof(ushort));
                sizedCodec.SerializeSized(item, writer, encodedBytes, sizedSnapshot);
                if (sizedSnapshot is not null)
                {
                    sizedCodec.ReleaseSnapshot(sizedSnapshot);
                    sizedSnapshot = null;
                }
            }

            var actualEncodedBytes = writer.WrittenCount - ProtocolV2Constants.HeaderBytes - sizeof(ushort);
            if (actualEncodedBytes != encodedBytes)
            {
                throw new InvalidOperationException(
                    "Generated stream item size differed after credit was acquired.");
            }

            ThrowIfGeneratedStreamDeadlineExpired(deadline, deadlineTimeProvider);
            ownsWriter = false;
            SendPacket(writer);
            return creditLease;
        }
        catch
        {
            if (creditAcquired)
                ReturnUnsentStreamCredit(creditLease, requestId, streamId, creditBytes);
            throw;
        }
        finally
        {
            if (sizedSnapshot is not null)
                sizedCodec.ReleaseSnapshot(sizedSnapshot);
            if (ownsWriter && writer is not null)
                RuntimeContext.Buffers.Return(writer);
        }
    }

    internal async ValueTask SendStreamChunkKnownSizeAsync<T>(
        long requestId,
        ushort streamId,
        T item,
        IRpcSizedCodec<T> sizedCodec,
        int encodedBytes,
        IRpcSizedCodecSnapshot? sizedSnapshot,
        CancellationToken cancellationToken,
        RpcDeadline deadline = default,
        TimeProvider? deadlineTimeProvider = null)
    {
        var creditBytes = Math.Max(1, encodedBytes);
        var creditAcquired = false;
        IRpcByteBufferWriter? writer = null;
        var ownsWriter = true;
        try
        {
            await AcquireStreamSendCreditAsync(
                requestId,
                streamId,
                creditBytes,
                cancellationToken).ConfigureAwait(false);
            creditAcquired = true;

            writer = RuntimeContext.Buffers.Rent(
                checked(ProtocolV2Constants.HeaderBytes + NegotiatedMaxFramePayloadBytes + 4));
            using (writer.BeginPacketScope(
                       ProtocolV2FrameType.StreamData,
                       ProtocolV2FrameFlags.None,
                       unchecked((ulong)requestId)))
            {
                writer.GetSpan(sizeof(ushort) + encodedBytes + 4);
                writer.Advance(0);
                var idSpan = writer.GetSpan(sizeof(ushort));
                BinaryPrimitives.WriteUInt16LittleEndian(idSpan, streamId);
                writer.Advance(sizeof(ushort));
                sizedCodec.SerializeSized(item, writer, encodedBytes, sizedSnapshot);
                if (sizedSnapshot is not null)
                {
                    sizedCodec.ReleaseSnapshot(sizedSnapshot);
                    sizedSnapshot = null;
                }
            }

            var actualEncodedBytes = writer.WrittenCount - ProtocolV2Constants.HeaderBytes - sizeof(ushort);
            if (actualEncodedBytes != encodedBytes)
            {
                throw new InvalidOperationException(
                    "Generated stream item size differed after credit was acquired.");
            }

            ThrowIfGeneratedStreamDeadlineExpired(deadline, deadlineTimeProvider);
            ownsWriter = false;
            SendPacket(writer);
        }
        catch
        {
            if (creditAcquired)
                ReturnUnsentStreamCredit(requestId, streamId, creditBytes);
            throw;
        }
        finally
        {
            if (sizedSnapshot is not null)
                sizedCodec.ReleaseSnapshot(sizedSnapshot);
            if (ownsWriter && writer is not null)
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
    public void EnsureUserCodeEntry(long requestId)
    {
        if (SharpLinkCallContext.Current is { } context &&
            context.DeadlineTimeProvider is { } timeProvider &&
            context.LocalRpcDeadline.IsExpired(timeProvider))
        {
            throw new SharpLinkException(
                SharpLinkErrorCode.DeadlineExceeded,
                "Request deadline exceeded.");
        }
    }

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
