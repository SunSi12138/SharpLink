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

        var callContext = SharpLinkCallContext.Current;
        var deadline = callContext?.LocalRpcDeadline ?? default;
        var deadlineTimeProvider = callContext?.DeadlineTimeProvider;
        using var lifetimeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var enumerator = stream.GetAsyncEnumerator(lifetimeCancellation.Token);
        var deadlineWon = false;
        try
        {
            while (true)
            {
                var moveNext = enumerator.MoveNextAsync();
                bool hasNext;
                if (!deadline.HasValue || deadlineTimeProvider is null || moveNext.IsCompletedSuccessfully)
                {
                    hasNext = await moveNext.ConfigureAwait(false);
                }
                else
                {
                    var moveNextTask = moveNext.AsTask();
                    if (!await SharpLinkTimer.WaitAsync(
                            moveNextTask,
                            deadline,
                            deadlineTimeProvider,
                            lifetimeCancellation.Token).ConfigureAwait(false))
                    {
                        deadlineWon = true;
                        TryCancelGeneratedLifetime(lifetimeCancellation);
                        _ = ObserveAbandonedGeneratedMoveNextAsync(moveNextTask);
                        throw CreateGeneratedStreamDeadlineExceededException();
                    }
                    hasNext = await moveNextTask.ConfigureAwait(false);
                }

                if (!hasNext)
                    break;

                var item = enumerator.Current;
                if (!payloadNullable && default(T) is null && item is null)
                {
                    throw new SharpLinkException(
                        SharpLinkErrorCode.Internal,
                        "A non-nullable RPC stream response was null.");
                }

                var send = SendGeneratedStreamChunkAsync(
                    requestId,
                    streamId,
                    item,
                    codec,
                    deadline,
                    deadlineTimeProvider,
                    lifetimeCancellation.Token);
                if (!deadline.HasValue || deadlineTimeProvider is null || send.IsCompletedSuccessfully)
                {
                    await send.ConfigureAwait(false);
                }
                else
                {
                    var sendTask = send.AsTask();
                    if (!await SharpLinkTimer.WaitAsync(
                            sendTask,
                            deadline,
                            deadlineTimeProvider,
                            lifetimeCancellation.Token).ConfigureAwait(false))
                    {
                        deadlineWon = true;
                        TryCancelGeneratedLifetime(lifetimeCancellation);
                        _ = ObserveAbandonedGeneratedSendAsync(sendTask);
                        throw CreateGeneratedStreamDeadlineExceededException();
                    }
                    await sendTask.ConfigureAwait(false);
                }
            }

            ThrowIfGeneratedStreamDeadlineExpired(deadline, deadlineTimeProvider);
            SendGeneratedStreamComplete(requestId, streamId, deadline, deadlineTimeProvider);
        }
        finally
        {
            TryCancelGeneratedLifetime(lifetimeCancellation);
            try
            {
                var dispose = enumerator.DisposeAsync();
                if (deadlineWon && !dispose.IsCompletedSuccessfully)
                    _ = ObserveAbandonedGeneratedDisposeAsync(dispose);
                else
                    await dispose.ConfigureAwait(false);
            }
            catch when (deadlineWon)
            {
                // The monotonic deadline is already terminal; a user enumerator that ignores
                // cancellation cannot delay the RPC while its disposal completes.
            }
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

    // Keep the generated-server path concrete and codec-bound. Exact-size codecs retain the
    // credit-before-serialize path; only the universal unsized fallback enters the session-owned
    // pre-credit serialized-memory admission helper.
    private ValueTask SendGeneratedStreamChunkAsync<T>(
        long requestId,
        ushort streamId,
        T item,
        IRpcCodec<T> codec,
        RpcDeadline deadline,
        TimeProvider? deadlineTimeProvider,
        CancellationToken cancellationToken)
    {
        if (codec is IRpcSizedCodec<T> sizedCodec &&
            sizedCodec.CanExactSize &&
            sizedCodec.TryGetEncodedSize(item, out var knownEncodedBytes, out var sizedSnapshot))
        {
            return SendStreamChunkKnownSizeAsync(
                requestId,
                streamId,
                item,
                sizedCodec,
                knownEncodedBytes,
                sizedSnapshot,
                cancellationToken,
                deadline,
                deadlineTimeProvider);
        }

        return SendUnsizedStreamChunkAsync(
            requestId,
            streamId,
            item,
            codec,
            cancellationToken,
            deadline,
            deadlineTimeProvider);
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
